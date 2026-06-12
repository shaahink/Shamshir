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

    public static Price? StructureTrail(
        Position position,
        IReadOnlyList<Bar> recentBars,
        int lookbackBars,
        double atrValue,
        double atrMultiple,
        SymbolInfo symbol)
    {
        var offset = (decimal)(atrValue * atrMultiple);
        var window = recentBars.TakeLast(Math.Min(lookbackBars + 2, recentBars.Count)).ToList();
        if (window.Count < 3) return null;

        decimal? swingLevel = null;

        if (position.Direction == TradeDirection.Long)
        {
            for (var i = window.Count - 2; i >= 1; i--)
            {
                if (window[i].Low < window[i - 1].Low && window[i].Low < window[i + 1].Low)
                {
                    swingLevel = window[i].Low;
                    break;
                }
            }
            if (swingLevel.HasValue)
            {
                var newSl = RoundToTickSize(swingLevel.Value - offset, symbol.TickSize);
                return newSl > position.CurrentStopLoss.Value ? new Price(newSl) : null;
            }
        }
        else
        {
            for (var i = window.Count - 2; i >= 1; i--)
            {
                if (window[i].High > window[i - 1].High && window[i].High > window[i + 1].High)
                {
                    swingLevel = window[i].High;
                    break;
                }
            }
            if (swingLevel.HasValue)
            {
                var newSl = RoundToTickSize(swingLevel.Value + offset, symbol.TickSize);
                return newSl < position.CurrentStopLoss.Value ? new Price(newSl) : null;
            }
        }

        return null;
    }

    public static Price? SteppedRTrail(
        Position position,
        decimal currentBid,
        decimal currentAsk,
        double[] rLevels,
        SymbolInfo symbol)
    {
        var slDistance = Math.Abs(position.EntryPrice.Value - position.CurrentStopLoss.Value);

        if (position.Direction == TradeDirection.Long)
        {
            var currentProfit = currentBid - position.EntryPrice.Value;
            for (var i = rLevels.Length - 1; i >= 0; i--)
            {
                if (currentProfit >= slDistance * (decimal)rLevels[i])
                {
                    var newSl = i == 0
                        ? position.EntryPrice.Value
                        : position.EntryPrice.Value + slDistance * (decimal)rLevels[i - 1];
                    newSl = RoundToTickSize(newSl, symbol.TickSize);
                    return newSl > position.CurrentStopLoss.Value ? new Price(newSl) : null;
                }
            }
        }
        else
        {
            var currentProfit = position.EntryPrice.Value - currentAsk;
            for (var i = rLevels.Length - 1; i >= 0; i--)
            {
                if (currentProfit >= slDistance * (decimal)rLevels[i])
                {
                    var newSl = i == 0
                        ? position.EntryPrice.Value
                        : position.EntryPrice.Value - slDistance * (decimal)rLevels[i - 1];
                    newSl = RoundToTickSize(newSl, symbol.TickSize);
                    return newSl < position.CurrentStopLoss.Value ? new Price(newSl) : null;
                }
            }
        }

        return null;
    }
}
