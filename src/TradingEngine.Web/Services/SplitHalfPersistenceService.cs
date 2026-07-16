using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Web.Services;

/// <summary>
/// iter-structural-edge S0: the F64 split-half selection test as a committed, parameterized
/// service — a line-faithful port of the research script <c>tools/research/split_half.py</c>
/// (§1 of iter-structural-edge/RESEARCH.md). Question answered: if we had picked "positive
/// cells" using only H1 (before <paramref name="split"/>), what would H2 have paid? Selection
/// is the only fitted step being evaluated — exactly the step a portfolio-of-cells build would
/// perform. Exposed as `GET api/experiments/persistence` and the `research persistence` CLI
/// verb so every F64 number is one command away from re-verification (PLAN §5).
/// </summary>
public sealed class SplitHalfPersistenceService
{
    private readonly TradingDbContext _db;

    public SplitHalfPersistenceService(TradingDbContext db) => _db = db;

    public async Task<PersistenceReport> ComputeAsync(
        string experimentIdOrPrefix, DateOnly split, double baseAmount, CancellationToken ct)
    {
        var experiments = await _db.Experiments.AsNoTracking()
            .Select(e => new { e.Id, e.Name })
            .ToListAsync(ct);
        var matches = experiments
            .Where(e => e.Id.ToString("D").StartsWith(experimentIdOrPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            return PersistenceReport.Failed($"no experiment matches '{experimentIdOrPrefix}'");
        }

        if (matches.Count > 1)
        {
            return PersistenceReport.Failed(
                $"'{experimentIdOrPrefix}' is ambiguous ({matches.Count} experiments) — use more digits");
        }

        var experiment = matches[0];

        // Scored cells only (Composite non-null) — the same pool the research script used.
        var rows = await _db.ExperimentRuns.AsNoTracking()
            .Where(er => er.ExperimentId == experiment.Id)
            .Select(er => new { er.BacktestRunId, er.VariantLabel, er.ScoreJson })
            .ToListAsync(ct);
        var scored = rows.Where(r => HasComposite(r.ScoreJson))
            .Select(r => (RunId: r.BacktestRunId!, Label: r.VariantLabel ?? ""))
            .ToList();
        if (scored.Count == 0)
        {
            return PersistenceReport.Failed($"experiment {experiment.Id} has no scored (non-null Composite) runs");
        }

        var runIds = scored.Select(r => r.RunId).ToList();
        var windows = await _db.BacktestRuns.AsNoTracking()
            .Where(b => b.RunId != null && runIds.Contains(b.RunId))
            .Select(b => new { b.BacktestFrom, b.BacktestTo })
            .ToListAsync(ct);
        var from = DateOnly.FromDateTime(windows.Min(w => w.BacktestFrom));
        var to = DateOnly.FromDateTime(windows.Max(w => w.BacktestTo));
        var h2Days = to.DayNumber - split.DayNumber;
        if (h2Days <= 0)
        {
            return PersistenceReport.Failed($"split {split:yyyy-MM-dd} is not inside the window {from:yyyy-MM-dd} -> {to:yyyy-MM-dd}");
        }

        var trades = await _db.Trades.AsNoTracking()
            .Where(t => t.RunId != null && runIds.Contains(t.RunId))
            .Select(t => new { RunId = t.RunId!, t.ClosedAtUtc, t.NetPnLAmount })
            .ToListAsync(ct);

        // Per-cell H1 PnL and H2 daily PnL, keyed by variant label (cells are unique per label).
        var labelOf = scored.ToDictionary(r => r.RunId, r => r.Label);
        var h1Pnl = scored.ToDictionary(r => r.Label, _ => 0.0);
        var h2Daily = scored.ToDictionary(r => r.Label, _ => new Dictionary<DateOnly, double>());
        foreach (var t in trades)
        {
            var label = labelOf[t.RunId];
            var day = DateOnly.FromDateTime(t.ClosedAtUtc);
            var pnl = (double)t.NetPnLAmount;
            if (day < split)
            {
                h1Pnl[label] += pnl;
            }
            else
            {
                var daily = h2Daily[label];
                daily[day] = daily.GetValueOrDefault(day) + pnl;
            }
        }
        var h2Pnl = h2Daily.ToDictionary(kv => kv.Key, kv => kv.Value.Values.Sum());

        // --- the selection test ---
        var selection = h1Pnl.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        var h1OfSel = selection.Sum(l => h1Pnl[l]);
        var h2OfSel = selection.Sum(l => h2Pnl[l]);
        var persisted = selection.Count(l => h2Pnl[l] > 0);

        var top8 = selection.OrderByDescending(l => h1Pnl[l]).Take(8).ToList();
        var top8H1 = top8.Sum(l => h1Pnl[l]);
        var top8H2 = top8.Sum(l => h2Pnl[l]);

        var reverseSel = h2Pnl.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        var reverseH2 = reverseSel.Sum(l => h2Pnl[l]);
        var reverseH1 = reverseSel.Sum(l => h1Pnl[l]);

        // --- H2 rolling 30d challenge windows for the H1-selected portfolio ---
        var allDates = selection.SelectMany(l => h2Daily[l].Keys).Distinct().OrderBy(d => d).ToList();
        var agg = allDates.ToDictionary(d => d, d => selection.Sum(l => h2Daily[l].GetValueOrDefault(d)));
        var challenge = new List<ChallengeWindowCounts>();
        foreach (var scale in new[] { 1, 2, 3 })
        {
            var (p, f, i) = RollWindows(allDates, agg, scale, baseAmount);
            var worst = allDates.Count == 0 ? 0.0 : allDates.Min(d => agg[d]) * scale / baseAmount * 100;
            challenge.Add(new ChallengeWindowCounts(scale, p, f, i, worst));
        }

        var report = new PersistenceReport
        {
            ExperimentId = experiment.Id,
            ExperimentName = experiment.Name,
            From = from,
            To = to,
            Split = split,
            H2Days = h2Days,
            BaseAmount = baseAmount,
            ScoredCells = scored.Count,
            H1PositiveCells = selection.Count,
            H1PnlOfSelection = h1OfSel,
            H2PnlOfSelection = h2OfSel,
            PersistedCells = persisted,
            Top8 = top8.Select(l => new PersistenceCell(l, h1Pnl[l], h2Pnl[l])).ToList(),
            Top8H1Pnl = top8H1,
            Top8H2Pnl = top8H2,
            H2PositiveCells = reverseSel.Count,
            ReverseH2Pnl = reverseH2,
            ReverseH1Pnl = reverseH1,
            ChallengeWindows = challenge,
        };
        return report with { Text = Render(report) };
    }

    // Exact port of split_half.py windows(): every H2 trade-date is a window start; the window
    // spans 30 CALENDAR days; a day fails on (daily loss > cap of the fixed base) OR (equity
    // below base×(1−floor)); passes on reaching base×(1+target); otherwise incomplete.
    private static (int Pass, int Fail, int Incomplete) RollWindows(
        IReadOnlyList<DateOnly> allDates, IReadOnlyDictionary<DateOnly, double> agg, int scale,
        double baseAmount, double target = 0.10, double cap = 0.05, double floor = 0.10, int spanDays = 30)
    {
        int p = 0, f = 0, i = 0;
        for (var si = 0; si < allDates.Count; si++)
        {
            var end = allDates[si].AddDays(spanDays);
            if (end > allDates[^1]) break;
            var eq = baseAmount;
            var verdict = 'i';
            for (var di = si; di < allDates.Count; di++)
            {
                var d = allDates[di];
                if (d > end) break;
                var delta = agg[d] * scale;
                eq += delta;
                if (delta < -cap * baseAmount || eq < baseAmount * (1 - floor)) { verdict = 'f'; break; }
                if (eq >= baseAmount * (1 + target)) { verdict = 'p'; break; }
            }
            if (verdict == 'p') p++;
            else if (verdict == 'f') f++;
            else i++;
        }
        return (p, f, i);
    }

    private static bool HasComposite(string? scoreJson)
    {
        if (string.IsNullOrEmpty(scoreJson) || scoreJson == "{}") return false;
        try
        {
            using var doc = JsonDocument.Parse(scoreJson);
            return doc.RootElement.TryGetProperty("Composite", out var c)
                && c.ValueKind is JsonValueKind.Number;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Renders the F64 block in the research script's own layout so the output diffs cleanly
    // against RESEARCH.md §1. Numbers, not alignment, are the contract (G0: ±$1 on sums).
    private static string Render(PersistenceReport r)
    {
        var inv = CultureInfo.InvariantCulture;
        string Money(double v) => "$" + v.ToString("#,0", inv);
        string Signed(double v) => v.ToString("+0.00;-0.00", inv);
        var sb = new StringBuilder();
        sb.AppendLine($"experiment {r.ExperimentName} ({r.ExperimentId.ToString("D")[..8]})");
        sb.AppendLine(string.Format(inv, "census {0:yyyy-MM-dd} -> {1:yyyy-MM-dd}, split {2:yyyy-MM-dd}, H2 span {3}d",
            r.From, r.To, r.Split, r.H2Days));
        sb.AppendLine();
        sb.AppendLine("=== SPLIT-HALF SELECTION TEST (F64) ===");
        sb.AppendLine($"cells positive in H1: {r.H1PositiveCells}/{r.ScoredCells}  (H1 PnL of selection: {Money(r.H1PnlOfSelection)})");
        var haircut = r.H1PnlOfSelection == 0 ? 0 : r.H2PnlOfSelection / r.H1PnlOfSelection;
        sb.AppendLine($"same cells in H2:     {Money(r.H2PnlOfSelection)}   -> haircut factor {haircut.ToString("0.00", inv)}");
        var persistencePct = r.H1PositiveCells == 0 ? 0 : 100.0 * r.PersistedCells / r.H1PositiveCells;
        sb.AppendLine($"persistence: {r.PersistedCells}/{r.H1PositiveCells} H1-positive cells stayed positive in H2 ({persistencePct.ToString("0", inv)}%)");
        sb.AppendLine($"H2 return of H1-selected portfolio at 1x: {Signed(Per30d(r.H2PnlOfSelection, r))}%/30d");
        sb.AppendLine($"top-8 by H1 PnL -> H2: {Money(r.Top8H2Pnl)} = {Signed(Per30d(r.Top8H2Pnl, r))}%/30d (H1 was {Money(r.Top8H1Pnl)})");
        foreach (var cell in r.Top8)
            sb.AppendLine($"   {cell.VariantLabel,-44} H1={cell.H1Pnl.ToString("#,0", inv),8}  H2={cell.H2Pnl.ToString("#,0", inv),8}");
        var reverseFactor = r.ReverseH2Pnl == 0 ? 0 : r.ReverseH1Pnl / r.ReverseH2Pnl;
        sb.AppendLine();
        sb.AppendLine($"reverse check: H2-positive cells ({r.H2PositiveCells}) earned {Money(r.ReverseH2Pnl)} in H2, {Money(r.ReverseH1Pnl)} in H1 -> factor {reverseFactor.ToString("0.00", inv)}");
        sb.AppendLine();
        sb.AppendLine(string.Format(inv, "H1-selected portfolio, H2 rolling 30d challenge windows (fresh ${0:#,0} each):", r.BaseAmount));
        foreach (var w in r.ChallengeWindows)
            sb.AppendLine($" k={w.Scale}x: {w.Pass,2} pass / {w.Fail,2} fail / {w.Incomplete,2} incomplete   worstDay={Signed(w.WorstDayPercent)}%");
        return sb.ToString();

        static double Per30d(double pnl, PersistenceReport r) => pnl / r.H2Days * 30 / r.BaseAmount * 100;
    }
}

public sealed record PersistenceCell(string VariantLabel, double H1Pnl, double H2Pnl);

public sealed record ChallengeWindowCounts(int Scale, int Pass, int Fail, int Incomplete, double WorstDayPercent);

public sealed record PersistenceReport
{
    public Guid ExperimentId { get; init; }
    public string ExperimentName { get; init; } = "";
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public DateOnly Split { get; init; }
    public int H2Days { get; init; }
    public double BaseAmount { get; init; }
    public int ScoredCells { get; init; }
    public int H1PositiveCells { get; init; }
    public double H1PnlOfSelection { get; init; }
    public double H2PnlOfSelection { get; init; }
    public int PersistedCells { get; init; }
    public List<PersistenceCell> Top8 { get; init; } = [];
    public double Top8H1Pnl { get; init; }
    public double Top8H2Pnl { get; init; }
    public int H2PositiveCells { get; init; }
    public double ReverseH2Pnl { get; init; }
    public double ReverseH1Pnl { get; init; }
    public List<ChallengeWindowCounts> ChallengeWindows { get; init; } = [];
    /// <summary>The rendered F64 block (research-script layout) — what the CLI prints.</summary>
    public string Text { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    public static PersistenceReport Failed(string error) => new() { Error = error };
}
