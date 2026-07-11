namespace TradingEngine.Services.Helpers;

/// <summary>
/// P0.3 (F6): the single place that turns a <see cref="PublishTradeClosed"/> effect into a
/// <see cref="TradeResult"/>. Extracted verbatim from <c>EffectExecutor.HandlePublishTradeClosed</c> so
/// the LIVE persistence path and the journal-based BACKFILL path (the F6 integrity barrier) reconstruct
/// trades identically — a trade recovered from the journal is byte-for-byte what the live close would
/// have written. Pure: no I/O, no clock. <paramref name="timeframe"/> is the only caller-supplied field
/// (live path resolves it from the strategy registry; backfill leaves it null — the journal has no TF).
/// </summary>
public static class TradeResultFactory
{
    public static TradeResult FromClose(
        PublishTradeClosed effect,
        SymbolInfo symbolInfo,
        Func<string, string, decimal> crossRateProvider,
        Guid id,
        string? timeframe = null)
    {
        var recomputedGross = PipCalculator.GrossPnL(effect.Direction, effect.EntryPrice, effect.ExitPrice,
            effect.Lots, symbolInfo, crossRateProvider);

        // Prefer the venue-authoritative PnL (commission/swap-inclusive) when the live venue reported
        // it; only fall back to the price-recomputed gross for the simulated venue.
        var currency = recomputedGross.Currency;
        var gross = effect.GrossProfit is { } g ? new Money(g, currency) : recomputedGross;
        var commission = new Money(effect.Commission ?? 0m, currency);
        var swap = new Money(effect.Swap ?? 0m, currency);
        var net = effect.NetProfit is { } n ? new Money(n, currency) : gross.Add(commission).Add(swap);

        var pipSize = symbolInfo.PipSize;
        var entry = effect.EntryPrice.Value;
        var exit = effect.ExitPrice.Value;
        var isLong = effect.Direction == TradeDirection.Long;
        var signedMove = isLong ? exit - entry : entry - exit;

        var pnlPips = new Pips((double)(signedMove / pipSize));

        var rMultiple = PipCalculator.RMultiple(effect.Direction, effect.EntryPrice, effect.ExitPrice, effect.InitialStopLoss);

        var hi = Math.Max(entry, exit);
        if (effect.HighWater > 0) hi = Math.Max(hi, effect.HighWater);
        var lo = Math.Min(entry, exit);
        if (effect.LowWater > 0) lo = Math.Min(lo, effect.LowWater);

        var mfePips = new Pips((double)((isLong ? hi - entry : entry - lo) / pipSize));
        var maePips = new Pips((double)((isLong ? entry - lo : hi - entry) / pipSize));

        return new TradeResult(id, effect.PositionId, effect.Symbol, effect.Direction,
            effect.Lots, effect.EntryPrice, effect.ExitPrice, effect.StopLoss, effect.TakeProfit,
            effect.OpenedAtUtc, effect.ClosedAtUtc, gross, commission, swap,
            net, pnlPips, rMultiple, maePips, mfePips,
            effect.ExitReason, effect.StrategyId, effect.RiskProfileId ?? "standard",
            OrderEntryMethod: effect.OrderEntryMethod,
            OrderId: effect.OrderId,
            EntryReason: effect.EntryReason,
            EntryRegime: effect.EntryRegime,
            Timeframe: timeframe,
            InitialStopLoss: effect.InitialStopLoss,
            EntrySnapshotJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                reason = effect.EntryReason,
                regime = effect.EntryRegime,
                direction = effect.Direction.ToString(),
                entryPrice = effect.EntryPrice.Value,
                stopLoss = effect.InitialStopLoss.Value,
                takeProfit = effect.TakeProfit?.Value,
                lots = effect.Lots
            }));
    }
}
