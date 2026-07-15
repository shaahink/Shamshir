using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.Reconcile;

/// <summary>
/// P0.4 (F2, measure-first per Q4) — a pure entry-latency analyzer. The audit found cTrader entries
/// fill one full decision bar later than tape for byte-identical proposals; this quantifies that lag
/// per run so a reconcile can report it. It does NOT change any execution behavior — it only measures
/// the delay between an order's PROPOSAL (journalled <c>OrderProposed.OccurredAtUtc</c>, bar-open
/// convention) and its FILL (persisted <c>TradeResult.OpenedAtUtc</c>), joined on <c>OrderId</c>.
///
/// Kept free of EF/Web types so it stays deterministic and trivially unit-testable; the caller does the
/// journal/trade I/O and hands in already-parsed proposals + fills.
/// </summary>
public static class EntryLatencyAnalyzer
{
    public static EntryLatencyReport Analyze(
        IEnumerable<EntryLatencyProposal> proposals,
        IEnumerable<EntryLatencyFill> fills)
    {
        // First proposal wins per OrderId — a rejected proposal shares the OrderProposed shape but never
        // fills, so it drops out of the join anyway; a re-proposal on the same OrderId keeps the earliest.
        var byOrderId = new Dictionary<Guid, EntryLatencyProposal>();
        foreach (var p in proposals)
            byOrderId.TryAdd(p.OrderId, p);

        var matched = new List<EntryLatency>();
        var unmatched = 0;
        foreach (var f in fills)
        {
            if (!byOrderId.TryGetValue(f.OrderId, out var p))
            {
                unmatched++;
                continue;
            }

            // Both timestamps are UTC wall-clock; subtract on ticks (no timezone conversion) so a
            // trailing 'Z' on one side and none on the other still yield the true delta.
            var delaySeconds = (f.FilledAtUtc - p.ProposedAtUtc).TotalSeconds;
            var barSeconds = p.DecisionTimeframe.ToTimeSpan().TotalSeconds;
            var delayBars = barSeconds > 0 ? delaySeconds / barSeconds : 0d;
            matched.Add(new EntryLatency(
                f.OrderId, p.ProposedAtUtc, f.FilledAtUtc, delaySeconds, delayBars, p.DecisionTimeframe));
        }

        matched.Sort((a, b) => a.ProposedAtUtc.CompareTo(b.ProposedAtUtc));
        return new EntryLatencyReport(
            MatchedTrades: matched.Count,
            UnmatchedFills: unmatched,
            DelaySeconds: Summarize(matched.Select(m => m.DelaySeconds)),
            DelayBars: Summarize(matched.Select(m => m.DelayBars)),
            Trades: matched);
    }

    private static EntryLatencyStats Summarize(IEnumerable<double> values)
    {
        var v = values.ToList();
        if (v.Count == 0)
            return new EntryLatencyStats(0, 0, 0, 0);
        v.Sort();
        return new EntryLatencyStats(
            Median: Median(v),
            Mean: v.Average(),
            Min: v[0],
            Max: v[^1]);
    }

    // Precondition: sorted, non-empty.
    private static double Median(IReadOnlyList<double> sorted)
    {
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2d;
    }
}

/// <summary>An accepted order proposal: the decision instant (bar-open) + its decision timeframe.</summary>
public sealed record EntryLatencyProposal(Guid OrderId, DateTime ProposedAtUtc, Timeframe DecisionTimeframe);

/// <summary>A realized fill: when the position actually opened.</summary>
public sealed record EntryLatencyFill(Guid OrderId, DateTime FilledAtUtc);

/// <summary>Per-trade proposal→fill latency. <see cref="DelayBars"/> is in decision-timeframe units.</summary>
public sealed record EntryLatency(
    Guid OrderId,
    DateTime ProposedAtUtc,
    DateTime FilledAtUtc,
    double DelaySeconds,
    double DelayBars,
    Timeframe DecisionTimeframe);

public sealed record EntryLatencyStats(double Median, double Mean, double Min, double Max);

public sealed record EntryLatencyReport(
    int MatchedTrades,
    int UnmatchedFills,
    EntryLatencyStats DelaySeconds,
    EntryLatencyStats DelayBars,
    IReadOnlyList<EntryLatency> Trades);
