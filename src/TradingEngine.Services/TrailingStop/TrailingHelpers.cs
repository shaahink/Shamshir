namespace TradingEngine.Services.TrailingStop;

public static class TrailingHelpers
{
    public static Price? StepTrail(
        Position position,
        decimal currentBid,
        decimal currentAsk,
        Pips stepPips,
        SymbolInfo symbol)
    {
        var step = (decimal)stepPips.Value * symbol.PipSize;

        if (position.Direction == TradeDirection.Long)
        {
            var newSl = currentBid - step;
            return newSl > position.CurrentStopLoss.Value
                ? new Price(RoundToTickSize(newSl, symbol.TickSize))
                : null;
        }
        else
        {
            var newSl = currentAsk + step;
            return newSl < position.CurrentStopLoss.Value
                ? new Price(RoundToTickSize(newSl, symbol.TickSize))
                : null;
        }
    }

    public static Price? AtrTrail(
        Position position,
        decimal highestBidSinceEntry,
        decimal lowestAskSinceEntry,
        double currentAtr,
        double multiplier,
        SymbolInfo symbol)
    {
        var offset = (decimal)(currentAtr * multiplier);

        if (position.Direction == TradeDirection.Long)
        {
            var newSl = highestBidSinceEntry - offset;
            return newSl > position.CurrentStopLoss.Value
                ? new Price(RoundToTickSize(newSl, symbol.TickSize))
                : null;
        }
        else
        {
            var newSl = lowestAskSinceEntry + offset;
            return newSl < position.CurrentStopLoss.Value
                ? new Price(RoundToTickSize(newSl, symbol.TickSize))
                : null;
        }
    }

    public static Price? Breakeven(
        Position position,
        decimal currentBid,
        decimal currentAsk,
        double triggerRMultiple,
        Pips bufferPips,
        SymbolInfo symbol)
    {
        var slDistance = Math.Abs(position.EntryPrice.Value - position.CurrentStopLoss.Value);
        var triggerDistance = slDistance * (decimal)triggerRMultiple;
        var buffer = (decimal)bufferPips.Value * symbol.PipSize;

        if (position.Direction == TradeDirection.Long)
        {
            var inProfit = currentBid - position.EntryPrice.Value;
            if (inProfit < triggerDistance) return null;

            var beSl = position.EntryPrice.Value + buffer;
            return beSl > position.CurrentStopLoss.Value
                ? new Price(RoundToTickSize(beSl, symbol.TickSize))
                : null;
        }
        else
        {
            var inProfit = position.EntryPrice.Value - currentAsk;
            if (inProfit < triggerDistance) return null;

            var beSl = position.EntryPrice.Value - buffer;
            return beSl < position.CurrentStopLoss.Value
                ? new Price(RoundToTickSize(beSl, symbol.TickSize))
                : null;
        }
    }

    private static decimal RoundToTickSize(decimal price, decimal tickSize)
        => Math.Round(price / tickSize) * tickSize;
}
