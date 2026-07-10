using System.Diagnostics.CodeAnalysis;
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
///
/// P7.6 (F6-R): when a crashed cTrader teardown leaves close-fill OrderFilled events in the journal
/// without matching PublishTradeClosed effects, the barrier now attempts to RECONSTRUCT the
/// PublishTradeClosed by pairing the close fill with its open fill (for entry data) and the original
/// OrderProposed/OrderSubmitted (for StrategyId, Direction, StopLoss, TakeProfit). Successfully
/// reconstructed closes are backfilled just like normal PublishTradeClosed effects; unreconstructable
/// ones (missing open fill or proposal in the journal) remain counted as JournalCloseFills for the
/// Unreconstructable warning.
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
            var (journalCloses, unreconstructedCloseFills) = await CollectAsync(runId, ct);
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
                return new TradePersistenceReconciliation(expected, persisted, 0) { JournalCloseFills = unreconstructedCloseFills };

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

            return new TradePersistenceReconciliation(expected, persisted, backfilled) { JournalCloseFills = unreconstructedCloseFills };
        }
        catch (Exception ex)
        {
            // The barrier must never break finalization — a failure here is itself a warning-worthy anomaly
            // but the run's engine result stays intact.
            logger.LogError(ex, "TRADE_BARRIER_FAILED|run={RunId}", runId);
            return new TradePersistenceReconciliation(0, 0, 0) { Failed = true, FailureDetail = ex.Message };
        }
    }

    private async Task<(List<PublishTradeClosed> Closes, int UnreconstructedCloseFills)> CollectAsync(string runId, CancellationToken ct)
    {
        var closes = new List<PublishTradeClosed>();
        var totalCloseFills = 0;

        // F6-R: collect open fills and proposals for orphan-close reconstruction.
        var openFills = new Dictionary<Guid, OpenFillSnapshot>();
        var proposals = new Dictionary<Guid, ProposalSnapshot>();
        var orphanCloses = new List<CloseFillSnapshot>();

        await foreach (var step in journal.StreamByRunAsync(runId, afterSeq: null, ct))
        {
            // ── Collect open fills (OrderFilled without CloseReason) for orphan pairing ──
            if (step.EventKind == "OrderFilled" && !string.IsNullOrEmpty(step.EventJson))
            {
                if (HasCloseReason(step.EventJson))
                {
                    totalCloseFills++;
                    // Close fills WITH a PublishTradeClosed effect in the same step are handled by the
                    // existing effect deserialization path below; close fills WITHOUT one are orphans.
                    if (!step.EffectKinds.Contains(nameof(PublishTradeClosed)))
                    {
                        if (TryParseCloseFill(step.EventJson, out var cf))
                            orphanCloses.Add(cf);
                    }
                }
                else
                {
                    if (TryParseOpenFill(step.EventJson, out var of))
                        openFills[of.OrderId] = of;
                }
            }

            // ── Collect proposals for StrategyId/Direction/StopLoss/TakeProfit ──
            if (step.EventKind is "OrderProposed" or "OrderSubmitted" && !string.IsNullOrEmpty(step.EventJson))
            {
                if (TryParseProposal(step.EventJson, out var prop))
                    proposals[prop.OrderId] = prop;
            }

            // ── Existing PublishTradeClosed effect collection (unchanged) ──
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

        // ── F6-R: reconstruct PublishTradeClosed from orphan close fills ──
        var unreconstructed = totalCloseFills;
        foreach (var orphan in orphanCloses)
        {
            if (openFills.TryGetValue(orphan.OrderId, out var open) &&
                proposals.TryGetValue(orphan.OrderId, out var prop))
            {
                var ptc = new PublishTradeClosed(
                    PositionId: orphan.OrderId,
                    Symbol: open.Symbol,
                    Direction: prop.Direction,
                    Lots: open.Lots,
                    EntryPrice: new Price(open.EntryPrice),
                    ExitPrice: new Price(orphan.ExitPrice),
                    StopLoss: new Price(prop.StopLoss),
                    TakeProfit: prop.TakeProfit is { } tp ? new Price(tp) : null,
                    StrategyId: prop.StrategyId,
                    ExitReason: orphan.CloseReason,
                    ClosedAtUtc: orphan.CloseTime,
                    OpenedAtUtc: open.EntryTime,
                    OrderId: orphan.OrderId,
                    OrderEntryMethod: "Market",
                    GrossProfit: orphan.GrossProfit,
                    NetProfit: orphan.NetProfit,
                    Commission: orphan.Commission,
                    Swap: orphan.Swap
                );
                closes.Add(ptc);
                unreconstructed--;
                logger.LogInformation(
                    "F6R_RECONSTRUCTED|run={RunId}|position={PositionId}|reason={Reason}|net={Net}",
                    runId, orphan.OrderId, orphan.CloseReason, orphan.NetProfit);
            }
        }

        return (closes, unreconstructed);
    }

    // A close fill carries a non-null, non-empty CloseReason (SL/TP/STOPOUT/CLOSED); entry fills leave it
    // null. The journal writes OrderFilled EventJson PascalCase (verified against the audit DB), so a
    // case-sensitive property probe is correct and avoids a full deserialize of the event.
    private static bool HasCloseReason(string? eventJson)
    {
        if (string.IsNullOrEmpty(eventJson) || eventJson == "{}")
            return false;
        try
        {
            using var doc = JsonDocument.Parse(eventJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("CloseReason", out var v)
                && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(v.GetString());
        }
        catch
        {
            return false;
        }
    }

    private static string Key(Guid positionId, DateTime closedAtUtc, string exitReason, decimal lots) =>
        $"{positionId}|{closedAtUtc.Ticks}|{exitReason}|{lots}";

    // ── F6-R: snapshot POCOs used to pair journal events for orphan-close reconstruction ──

    private sealed record OpenFillSnapshot(Guid OrderId, Symbol Symbol, decimal EntryPrice, decimal Lots, DateTime EntryTime);
    private sealed record ProposalSnapshot(Guid OrderId, string StrategyId, TradeDirection Direction, decimal StopLoss, decimal? TakeProfit);
    private sealed record CloseFillSnapshot(Guid OrderId, string Symbol, decimal ExitPrice, decimal Lots, DateTime CloseTime, string CloseReason, decimal? GrossProfit, decimal? NetProfit, decimal? Commission, decimal? Swap);

    private static bool TryParseOpenFill(string eventJson, [NotNullWhen(true)] out OpenFillSnapshot? result)
    {
        result = null;
        if (string.IsNullOrEmpty(eventJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(eventJson);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return false;

            var orderId = r.GetProperty("OrderId").GetGuid();
            var symbolStr = r.GetProperty("Symbol").GetProperty("Value").GetString()!;
            var entryPrice = r.GetProperty("FillPrice").GetProperty("Value").GetDecimal();
            var lots = r.GetProperty("FilledLots").GetDecimal();
            var time = r.GetProperty("OccurredAtUtc").GetDateTime();

            result = new OpenFillSnapshot(orderId, Symbol.Parse(symbolStr), entryPrice, lots, time);
            return true;
        }
        catch { return false; }
    }

    private static bool TryParseProposal(string eventJson, [NotNullWhen(true)] out ProposalSnapshot? result)
    {
        result = null;
        if (string.IsNullOrEmpty(eventJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(eventJson);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return false;

            var orderId = r.GetProperty("OrderId").GetGuid();
            var strategyId = r.GetProperty("StrategyId").GetString()!;
            var direction = Enum.Parse<TradeDirection>(r.GetProperty("Direction").GetString()!, ignoreCase: true);
            var stopLoss = r.GetProperty("StopLoss").GetProperty("Value").GetDecimal();

            decimal? takeProfit = null;
            if (r.TryGetProperty("TakeProfit", out var tpEl) && tpEl.ValueKind == JsonValueKind.Object)
                takeProfit = tpEl.GetProperty("Value").GetDecimal();

            result = new ProposalSnapshot(orderId, strategyId, direction, stopLoss, takeProfit);
            return true;
        }
        catch { return false; }
    }

    private static bool TryParseCloseFill(string eventJson, [NotNullWhen(true)] out CloseFillSnapshot? result)
    {
        result = null;
        if (string.IsNullOrEmpty(eventJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(eventJson);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return false;

            var orderId = r.GetProperty("OrderId").GetGuid();
            var symbolStr = r.GetProperty("Symbol").GetProperty("Value").GetString()!;
            var exitPrice = r.GetProperty("FillPrice").GetProperty("Value").GetDecimal();
            var lots = r.GetProperty("FilledLots").GetDecimal();
            var time = r.GetProperty("OccurredAtUtc").GetDateTime();
            var closeReason = r.GetProperty("CloseReason").GetString()!;

            decimal? gross = r.TryGetProperty("GrossProfit", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetDecimal() : null;
            decimal? net = r.TryGetProperty("NetProfit", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDecimal() : null;
            decimal? commission = r.TryGetProperty("Commission", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
            decimal? swap = r.TryGetProperty("Swap", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDecimal() : null;

            result = new CloseFillSnapshot(orderId, symbolStr, exitPrice, lots, time, closeReason, gross, net, commission, swap);
            return true;
        }
        catch { return false; }
    }
}

public sealed record TradePersistenceReconciliation(int Expected, int Persisted, int Backfilled)
{
    public int Missing => Expected - Persisted;

    // A count mismatch (some trades were lost and had to be backfilled, or could not be) is the F6 signal
    // that must surface as a run warning → completed-with-warnings, never a silent lower TotalTrades.
    public bool HasLoss => Failed || Persisted < Expected;

    // F6-R (P7.6): the venue journalled close fills (OrderFilled with a CloseReason) but a crashed
    // teardown lost the close→PublishTradeClosed→TradeResult path. The barrier now attempts to
    // RECONSTRUCT PublishTradeClosed by pairing each close fill with its open fill + proposal.
    // JournalCloseFills counts only the close fills that COULD NOT be reconstructed (missing open fill
    // or proposal in the journal). When JournalCloseFills > 0 and Persisted + Backfilled == 0, the
    // barrier signals Unreconstructable — ALL close fills lacked the journal data needed for recovery.
    // Partial recovery (some reconstructed, some not) produces Expected > 0 + Backfilled > 0 → not
    // Unreconstructable, but the unreconstructed count is still available in JournalCloseFills.
    public bool Unreconstructable => !Failed && Persisted + Backfilled == 0 && JournalCloseFills > 0;

    // Count of venue-authoritative close fills that COULD NOT be reconstructed by F6-R (missing open
    // fill or proposal data in the journal). Zero when all close fills were either already represented
    // by PublishTradeClosed effects or successfully reconstructed from paired journal entries.
    public int JournalCloseFills { get; init; }

    public bool Failed { get; init; }
    public string? FailureDetail { get; init; }
}
