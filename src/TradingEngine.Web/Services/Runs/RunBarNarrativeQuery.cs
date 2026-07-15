using TradingEngine.Web.Dtos.Runs;

namespace TradingEngine.Web.Services;

/// <summary>The bar-narrative read path: projects a run's journal (cache-first, streamed from the
/// DB otherwise) into per-bar narratives for the run timeline. Extracted verbatim from
/// RunQueryService.</summary>
public sealed class RunBarNarrativeQuery(
    IJournalQueryRepository journals,
    IRunDataCache? runDataCache)
{
    private readonly IJournalQueryRepository _journals = journals;
    private readonly IRunDataCache? _cache = runDataCache;

    private const int MaxBarEvents = 5000;

    public async Task<IReadOnlyList<BarNarrativeResponse>> GetRunBarsAsync(
        string runId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        if (_cache is not null && _cache.HasRun(runId))
        {
            var cachedJournal = _cache.GetJournal(runId, MaxBarEvents);
            if (cachedJournal.Count > 0)
            {
                return BuildBarNarratives(cachedJournal, from, to);
            }
        }

        var bars = new Dictionary<DateTime, List<StepRecord>>();
        var eventCount = 0;

        await foreach (var record in _journals.StreamByRunAsync(runId, null, ct))
        {
            if (eventCount >= MaxBarEvents)
            {
                break;
            }
            if (from.HasValue && record.SimTimeUtc < from.Value)
            {
                continue;
            }
            if (to.HasValue && record.SimTimeUtc > to.Value)
            {
                continue;
            }

            if (!bars.TryGetValue(record.SimTimeUtc, out var group))
            {
                group = [];
                bars[record.SimTimeUtc] = group;
            }
            group.Add(record);
            eventCount++;
        }

        var allRecords = bars.Values.SelectMany(g => g).ToList();
        return BuildBarNarratives(allRecords, from, to);
    }

    private static IReadOnlyList<BarNarrativeResponse> BuildBarNarratives(
        IReadOnlyList<StepRecord> records, DateTime? from, DateTime? to)
    {
        var bars = new Dictionary<DateTime, List<StepRecord>>();
        foreach (var record in records)
        {
            if (from.HasValue && record.SimTimeUtc < from.Value)
            {
                continue;
            }
            if (to.HasValue && record.SimTimeUtc > to.Value)
            {
                continue;
            }

            if (!bars.TryGetValue(record.SimTimeUtc, out var group))
            {
                group = [];
                bars[record.SimTimeUtc] = group;
            }
            group.Add(record);
        }

        var result = new List<BarNarrativeResponse>(bars.Count);
        foreach (var (simTime, group) in bars.OrderBy(kv => kv.Key))
        {
            var first = group[0];
            var barClosed = group.FirstOrDefault(r => r.EventKind == "BarClosed");
            var last = group[^1];

            result.Add(new BarNarrativeResponse
            {
                SimTimeUtc = simTime,
                FirstSeq = first.Seq,
                EventCount = group.Count,
                Regime = barClosed?.Regime ?? group.FirstOrDefault(r => r.Regime != null)?.Regime,
                Verdicts = (barClosed?.StrategyVerdicts ?? [])
                    .Select(v => new BarStrategyVerdictDto
                    {
                        StrategyId = v.StrategyId,
                        SignalFired = v.SignalFired,
                        Direction = v.Direction?.ToString() ?? (v.SignalFired ? "Long" : null),
                        Reason = v.Reason,
                    }).ToList(),
                ProposalCount = group.Count(r => r.EventKind == "OrderProposed"),
                GateRejections = group
                    .Where(r => r.DecisionReason is not null && IsRejection(r.DecisionReason))
                    .Select(r => r.DecisionReason!)
                    .Distinct()
                    .ToList(),
                Risk = last.Risk is { } risk ? new BarRiskSnapshotDto
                {
                    Equity = risk.Equity,
                    Balance = risk.Balance,
                    DailyDrawdown = risk.DailyDrawdown,
                    MaxDrawdown = risk.MaxDrawdown,
                    OpenPositions = risk.OpenPositions,
                    InProtectionMode = risk.InProtectionMode,
                    GovernorState = risk.GovernorState,
                } : null,
                FillCount = group.Count(r => r.EventKind == "OrderFilled"),
                CloseCount = group.Count(r => r.EffectKinds.Any(e => e == "PublishTradeClosed")),
                RejectionCount = group.Count(r => r.DecisionReason is not null && IsRejection(r.DecisionReason)),
            });
        }

        return result;
    }

    private static bool IsRejection(string reason) =>
        !reason.Equals("Accepted", StringComparison.Ordinal) &&
        !reason.Equals("Filled", StringComparison.Ordinal) &&
        !reason.Equals("BarUpdate", StringComparison.Ordinal) &&
        !reason.Equals("TickUpdate", StringComparison.Ordinal) &&
        !reason.Equals("PartialFill", StringComparison.Ordinal) &&
        !reason.Equals("PartialClose", StringComparison.Ordinal) &&
        !reason.Equals("StillReducing", StringComparison.Ordinal) &&
        !reason.Equals("PartialCloseWhileClosing", StringComparison.Ordinal);
}
