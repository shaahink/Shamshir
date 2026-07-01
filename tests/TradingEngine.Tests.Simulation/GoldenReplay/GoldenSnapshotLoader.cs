using System.Text.Json;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// Loads the committed <c>golden-snapshot.json</c> baseline (the OLD engine's output over
/// <see cref="GoldenBarFixture"/>) so kernel tests can assert equivalence against a REAL recorded
/// value instead of a magic constant. Reads from the source tree (same resolution as
/// <see cref="GoldenReplayTests"/>), so it does not depend on the file being copied to bin/.
///
/// K0 (iter-36 cutover): the equivalence gate must compare the kernel against this baseline.
/// Today the kernel acceptance test asserts the first order's sizing/direction here; the FULL-run
/// trade+risk equivalence lands at K3, once the kernel backtest loop (evaluator + real fills) exists.
/// </summary>
public static class GoldenSnapshotLoader
{
    private static readonly string SnapshotPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "GoldenReplay", "golden-snapshot.json"));

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static GoldenSnapshot Load()
    {
        if (!File.Exists(SnapshotPath))
        {
            throw new FileNotFoundException(
                $"golden-snapshot.json not found at {SnapshotPath} — the equivalence gate cannot run. " +
                "Run GoldenReplayTests once to (re)generate the baseline.");
        }

        var json = File.ReadAllText(SnapshotPath);
        return JsonSerializer.Deserialize<GoldenSnapshot>(json, Options)
            ?? throw new InvalidOperationException("golden-snapshot.json deserialized to null.");
    }
}
