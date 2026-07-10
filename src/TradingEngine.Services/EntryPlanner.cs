using Microsoft.Extensions.Logging;

namespace TradingEngine.Services;

public sealed class EntryPlanner(
    ISymbolInfoRegistry symbolRegistry,
    ILogger<EntryPlanner> logger)
{
    // P2.7: StopConfirm needs the signal bar's High/Low (the level the stop trigger sits beyond), which
    // signalPrice alone (the tick mid) can't provide. bar is optional so every pre-existing call/test that
    // only cares about Market/LimitOffset/MarketWithSlippage stays source-compatible; StopConfirm falls
    // back to treating signalPrice as the bar extreme if bar is omitted (defensive, shouldn't happen from
    // the real evaluator call sites, which always have the bar in scope).
    public TradeIntent Plan(TradeIntent intent, OrderEntryOptions entry, decimal signalPrice, Bar? bar = null)
    {
        if (entry.Method == OrderEntryMethod.Market)
            return intent;

        if (entry.Method == OrderEntryMethod.MarketWithSlippage)
            return intent with { Entry = entry };

        if (entry.Method == OrderEntryMethod.LimitOffset)
            return PlanLimitOffset(intent, entry, signalPrice);

        if (entry.Method == OrderEntryMethod.StopConfirm)
            return PlanStopConfirm(intent, entry, signalPrice, bar);

        return intent;
    }

    private TradeIntent PlanLimitOffset(TradeIntent intent, OrderEntryOptions entry, decimal signalPrice)
    {
        var symbolInfo = symbolRegistry.Get(intent.Symbol);
        var pipSize = symbolInfo.PipSize;
        var offsetAmount = (decimal)entry.LimitOffsetPips * pipSize;

        var limitPrice = intent.Direction == TradeDirection.Long
            ? signalPrice - offsetAmount
            : signalPrice + offsetAmount;

        var (newSl, newTp) = ShiftSlTp(intent, signalPrice, limitPrice);

        logger.LogDebug("ENTRY_PLAN|{Strategy}|{Dir}|signal={Signal:F5}|limit={Limit:F5}|offsetPips={Offset}",
            intent.StrategyId, intent.Direction, signalPrice, limitPrice, entry.LimitOffsetPips);

        return intent with
        {
            OrderType = OrderType.Limit,
            LimitPrice = new Price(limitPrice),
            StopLoss = newSl,
            TakeProfit = newTp,
            Entry = entry,
        };
    }

    // P2.7: buy stop at the signal bar's High + a spread-multiple buffer (sell stop mirrors on the Low) —
    // a resting confirmation order that fills only once price actually breaks past the bar that produced
    // the signal, instead of filling immediately at the signal's own (possibly mid-range) price.
    private TradeIntent PlanStopConfirm(TradeIntent intent, OrderEntryOptions entry, decimal signalPrice, Bar? bar)
    {
        var symbolInfo = symbolRegistry.Get(intent.Symbol);
        var buffer = (decimal)entry.StopConfirmBufferSpreadMultiple * symbolInfo.TypicalSpread;
        var barExtreme = intent.Direction == TradeDirection.Long
            ? bar?.High ?? signalPrice
            : bar?.Low ?? signalPrice;

        var stopPrice = intent.Direction == TradeDirection.Long
            ? barExtreme + buffer
            : barExtreme - buffer;

        var (newSl, newTp) = ShiftSlTp(intent, signalPrice, stopPrice);

        logger.LogDebug("ENTRY_PLAN|{Strategy}|{Dir}|signal={Signal:F5}|stop={Stop:F5}|barExtreme={Extreme:F5}|bufferMult={Mult}",
            intent.StrategyId, intent.Direction, signalPrice, stopPrice, barExtreme, entry.StopConfirmBufferSpreadMultiple);

        return intent with
        {
            OrderType = OrderType.Stop,
            LimitPrice = new Price(stopPrice),
            StopLoss = newSl,
            TakeProfit = newTp,
            Entry = entry,
        };
    }

    // Shifts SL/TP by the SAME distance the entry price moved (signalPrice → newEntryPrice), so a resting
    // order's risk/reward shape matches what the strategy originally intended at signalPrice, not a
    // distorted one anchored to the (possibly quite different) resting trigger price.
    private static (Price StopLoss, Price? TakeProfit) ShiftSlTp(TradeIntent intent, decimal signalPrice, decimal newEntryPrice)
    {
        var originalSlDistance = intent.Direction == TradeDirection.Long
            ? signalPrice - intent.StopLoss.Value
            : intent.StopLoss.Value - signalPrice;

        var newSl = intent.Direction == TradeDirection.Long
            ? new Price(Math.Max(newEntryPrice - originalSlDistance, 0.00001m))
            : new Price(Math.Max(newEntryPrice + originalSlDistance, 0.00001m));

        Price? newTp = null;
        if (intent.TakeProfit is not null)
        {
            var originalTpDistance = intent.Direction == TradeDirection.Long
                ? intent.TakeProfit.Value.Value - signalPrice
                : signalPrice - intent.TakeProfit.Value.Value;
            var tpPrice = intent.Direction == TradeDirection.Long
                ? newEntryPrice + originalTpDistance
                : newEntryPrice - originalTpDistance;
            newTp = tpPrice > 0 ? new Price(tpPrice) : null;
        }

        return (newSl, newTp);
    }
}
