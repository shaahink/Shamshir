using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TradingEngine.Services.Helpers;

namespace TradingEngine.Infrastructure.Persistence;

/// <summary>
/// P0.3 (F6): the trade-persistence integrity barrier. The audited BTC cTrader run journalled 12
/// proposals + 17 fills + 7 closes yet persisted 0 TradeResults and reported TotalTrades=0 — trades
/// vanished when the close→TradeResult path lost everything before finalization. Finalization has no
/// barrier that says "the journal says N closed positions, TradeResults says M — refuse to report M
/// silently".
///
/// This barrier reconciles the run's journalled <see cref="PublishTradeClosed"/> effects (the lossless
/// source of truth) against persisted TradeResults rows. On a shortfall it BACKFILLS the missing trades
/// by reconstructing them from the journal via <see cref="TradeResultFactory"/> (the same builder the
/// live path uses, so a recovered trade is byte-for-byte what the live close would have written) and
/// returns the count so the caller can attach a <c>TRADES_LOST</c> warning → <c>completed-with-warnings</c>.
/// </summary>
public sealed class TradePersistenceBarrier(
    IJournalQueryRepository journal,
    ITradeRepository trades,
    TradingDbContext db,
    ISymbolInfoRegistry symbols,
    Func<string, string, decimal> crossRateProvider,
    ILogger<TradePersistenceBarrier> logger)
{
    private static readonly JsonSerializerOptions EffectOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<TradePersistenceReconciliation> ReconcileAndBackfillAsync(string runId, CancellationToken ct)
    {
        try
        {
            var journalCloses = await CollectJournalClosesAsync(runId, ct);
            var expected = journalCloses.Count;

            // Multiset of already-persisted closes keyed by the close's identity, so partial closes
            // (same PositionId, different ClosedAtUtc/Lots) are counted independently and not double-inserted.
            var persistedRows = await db.Trades.Where(t => t.RunId == runId)
                .Select(t => new { t.PositionId, t.ClosedAtUtc, t.ExitReason, t.Lots })
                .ToListAsync(ct);
            var persisted = persistedRows.Count;

            var existingKeys = new Dictionary<string, int>();
            foreach (var r in persistedRows)
            {
                var k = Key(r.PositionId, r.ClosedAtUtc, r.ExitReason, r.Lots);
                existingKeys[k] = existingKeys.GetValueOrDefault(k) + 1;
            }

            if (expected <= persisted)
                return new TradePersistenceReconciliation(expected, persisted, 0);

            var backfilled = 0;
            foreach (var close in journalCloses)
            {
                var k = Key(close.PositionId, close.ClosedAtUtc, close.ExitReason, close.Lots);
                if (existingKeys.TryGetValue(k, out var n) && n > 0)
                {
                    existingKeys[k] = n - 1;
                    continue;
                }

                if (!symbols.TryGet(close.Symbol, out var symbolInfo))
                {
                    logger.LogWarning(
                        "TRADE_BACKFILL_SKIP|run={RunId}|position={PositionId}|symbol {Symbol} not registered",
                        runId, close.PositionId, close.Symbol);
                    continue;
                }

                var trade = TradeResultFactory.FromClose(close, symbolInfo, crossRateProvider, Guid.NewGuid());
                await trades.SaveAsync(trade, runId, ct);
                backfilled++;
                logger.LogWarning(
                    "TRADE_BACKFILLED|run={RunId}|position={PositionId}|closed={ClosedAtUtc:O}|net={Net}|reason={Reason}",
                    runId, close.PositionId, close.ClosedAtUtc, trade.NetPnL.Amount, close.ExitReason);
            }

            if (backfilled > 0)
            {
                logger.LogWarning("TRADES_LOST|run={RunId}|expected={Expected}|persisted={Persisted}|backfilled={Backfilled}",
                    runId, expected, persisted, backfilled);
            }

            return new TradePersistenceReconciliation(expected, persisted, backfilled);
        }
        catch (Exception ex)
        {
            // The barrier must never break finalization — a failure here is itself a warning-worthy anomaly
            // but the run's engine result stays intact.
            logger.LogError(ex, "TRADE_BARRIER_FAILED|run={RunId}", runId);
            return new TradePersistenceReconciliation(0, 0, 0) { Failed = true, FailureDetail = ex.Message };
        }
    }

    private async Task<List<PublishTradeClosed>> CollectJournalClosesAsync(string runId, CancellationToken ct)
    {
        var closes = new List<PublishTradeClosed>();
        await foreach (var step in journal.StreamByRunAsync(runId, afterSeq: null, ct))
        {
            if (step.EffectKinds.Count == 0 || !step.EffectKinds.Contains(nameof(PublishTradeClosed)))
                continue;
            if (string.IsNullOrEmpty(step.EffectsJson) || step.EffectsJson == "[]")
                continue;

            using var doc = JsonDocument.Parse(step.EffectsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                continue;

            var effects = doc.RootElement;
            var length = effects.GetArrayLength();
            for (var i = 0; i < step.EffectKinds.Count && i < length; i++)
            {
                if (step.EffectKinds[i] != nameof(PublishTradeClosed))
                    continue;
                var close = effects[i].Deserialize<PublishTradeClosed>(EffectOpts);
                if (close is not null)
                    closes.Add(close);
            }
        }
        return closes;
    }

    private static string Key(Guid positionId, DateTime closedAtUtc, string exitReason, decimal lots) =>
        $"{positionId}|{closedAtUtc.Ticks}|{exitReason}|{lots}";
}

public sealed record TradePersistenceReconciliation(int Expected, int Persisted, int Backfilled)
{
    public int Missing => Expected - Persisted;

    // A count mismatch (some trades were lost and had to be backfilled, or could not be) is the F6 signal
    // that must surface as a run warning → completed-with-warnings, never a silent lower TotalTrades.
    public bool HasLoss => Failed || Persisted < Expected;

    public bool Failed { get; init; }
    public string? FailureDetail { get; init; }
}
