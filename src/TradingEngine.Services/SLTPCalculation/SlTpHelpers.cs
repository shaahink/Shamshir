namespace TradingEngine.Services.SLTPCalculation;

public static class SlTpHelpers
{
    public static Price FixedPip(Price entry, TradeDirection direction, Pips distance, SymbolInfo symbol)
    {
        var offset = (decimal)distance.Value * symbol.PipSize;
        return direction == TradeDirection.Long
            ? new Price(entry.Value - offset)
            : new Price(entry.Value + offset);
    }

    public static Price AtrBased(Price entry, TradeDirection direction, double atrValue, double multiplier, SymbolInfo symbol)
    {
        var offset = (decimal)(atrValue * multiplier);
        var rawSl = direction == TradeDirection.Long
            ? entry.Value - offset
            : entry.Value + offset;
        return new Price(RoundToTickSize(rawSl, symbol.TickSize));
    }

    public static Price SwingBased(Price entry, TradeDirection direction, IReadOnlyList<Bar> recentBars, int lookbackBars, Pips bufferPips, SymbolInfo symbol)
    {
        var bufferOffset = (decimal)bufferPips.Value * symbol.PipSize;

        if (direction == TradeDirection.Long)
        {
            var swingLow = recentBars.TakeLast(lookbackBars).Min(b => b.Low);
            return new Price(RoundToTickSize(swingLow - bufferOffset, symbol.TickSize));
        }
        else
        {
            var swingHigh = recentBars.TakeLast(lookbackBars).Max(b => b.High);
            return new Price(RoundToTickSize(swingHigh + bufferOffset, symbol.TickSize));
        }
    }

    public static Price? RRMultiple(Price entry, Price stopLoss, TradeDirection direction, double rrRatio, SymbolInfo symbol)
    {
        if (rrRatio <= 0) return null;

        var slDistance = Math.Abs(entry.Value - stopLoss.Value);
        var tpDistance = slDistance * (decimal)rrRatio;

        var rawTp = direction == TradeDirection.Long
            ? entry.Value + tpDistance
            : entry.Value - tpDistance;

        return new Price(RoundToTickSize(rawTp, symbol.TickSize));
    }

    public static Price? AtrMultiple(Price entry, TradeDirection direction, double atrValue, double multiplier, SymbolInfo symbol)
    {
        var offset = (decimal)(atrValue * multiplier);
        var rawTp = direction == TradeDirection.Long
            ? entry.Value + offset
            : entry.Value - offset;
        return new Price(RoundToTickSize(rawTp, symbol.TickSize));
    }

    public static bool IsSlValid(Price entry, Price stopLoss, TradeDirection direction, SymbolInfo symbol, RiskProfile profile)
    {
        var correctSide = direction == TradeDirection.Long
            ? stopLoss.Value < entry.Value
            : stopLoss.Value > entry.Value;

        if (!correctSide) return false;

        var distance = PipCalculator.Distance(entry, stopLoss, symbol);
        if (distance.Value > profile.MaxSlPips) return false;
        if (distance.Value <= 0) return false;

        return true;
    }

    private static decimal RoundToTickSize(decimal price, decimal tickSize)
        => Math.Round(price / tickSize) * tickSize;
}
