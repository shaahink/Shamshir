using Microsoft.Extensions.Logging;

namespace TradingEngine.Services;

public sealed class EntryPlanner(
    ISymbolInfoRegistry symbolRegistry,
    ILogger<EntryPlanner> logger)
{
    public TradeIntent Plan(TradeIntent intent, OrderEntryOptions entry, decimal signalPrice)
    {
        if (entry.Method == OrderEntryMethod.Market)
            return intent;

        if (entry.Method == OrderEntryMethod.MarketWithSlippage)
            return intent with { Entry = entry };

        if (entry.Method == OrderEntryMethod.LimitOffset)
            return PlanLimitOffset(intent, entry, signalPrice);

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

        var originalSlDistance = intent.Direction == TradeDirection.Long
            ? signalPrice - intent.StopLoss.Value
            : intent.StopLoss.Value - signalPrice;

        var newSl = intent.Direction == TradeDirection.Long
            ? new Price(limitPrice - originalSlDistance)
            : new Price(limitPrice + originalSlDistance);

        Price? newTp = null;
        if (intent.TakeProfit is not null)
        {
            var originalTpDistance = intent.Direction == TradeDirection.Long
                ? intent.TakeProfit.Value.Value - signalPrice
                : signalPrice - intent.TakeProfit.Value.Value;
            newTp = intent.Direction == TradeDirection.Long
                ? new Price(limitPrice + originalTpDistance)
                : new Price(limitPrice - originalTpDistance);
        }

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
}
