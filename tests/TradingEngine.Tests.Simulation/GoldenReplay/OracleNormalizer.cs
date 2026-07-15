using System.Globalization;
using System.Text.Json;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// Strips all nondeterminism from engine output so snapshots are bit-identical across runs.
///
/// Current engine nondeterminism sources:
///   1. DateTime.UtcNow / bar.OpenTimeUtc — timestamps vary by run.
///   2. Guid.NewGuid() — order IDs, position IDs are random.
///   3. DB auto-increment row IDs — not in captured output, but worth noting.
///
/// Strategy: replace timestamps with "T+index" relative to bar ordering, and GUIDs with "id-{seq}"
/// based on first-encounter order.
/// </summary>
public static class OracleNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(GoldenSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static GoldenSnapshot? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<GoldenSnapshot>(json, JsonOptions);
    }
}
