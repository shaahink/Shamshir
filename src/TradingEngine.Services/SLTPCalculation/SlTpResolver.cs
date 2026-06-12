namespace TradingEngine.Services.SLTPCalculation;

public sealed class SlTpResolver
{
    public (Price Sl, Price? Tp) Resolve(
        Price entry, TradeDirection direction, double atr, SymbolInfo symbol,
        PositionManagementOptions opts, Price? strategySuppliedSl = null)
    {
        var sl = ResolveStopLoss(entry, direction, atr, symbol, opts.StopLoss, strategySuppliedSl);
        var tp = ResolveTakeProfit(entry, direction, atr, symbol, sl, opts.TakeProfit);
        return (sl, tp);
    }

    private static Price ResolveStopLoss(
        Price entry, TradeDirection direction, double atr, SymbolInfo symbol,
        SlOptions opts, Price? strategySl)
    {
        if (opts.Method == "SwingPoint" && strategySl is not null)
        {
            var atrDist = SlTpHelpers.AtrBased(entry, direction, atr, opts.AtrMultiple, symbol);
            var atrSlDistance = Math.Abs(entry.Value - atrDist.Value);
            var suppliedSlDistance = Math.Abs(entry.Value - strategySl.Value.Value);

            if ((decimal)suppliedSlDistance >= atrSlDistance)
                return SlTpHelpers.AtrBased(entry, direction, atr, opts.AtrMultiple, symbol);

            return strategySl.Value;
        }

        if (opts.Method == "FixedPips")
        {
            var t = SlTpHelpers.FixedPip(entry, direction, new Pips(opts.FixedPips), symbol);
            return CapMaxPips(t, entry, direction, opts.MaxPips, symbol);
        }

        // Default: AtrMultiple
        var sl = SlTpHelpers.AtrBased(entry, direction, atr, opts.AtrMultiple, symbol);
        return CapMaxPips(sl, entry, direction, opts.MaxPips, symbol);
    }

    private static Price? ResolveTakeProfit(
        Price entry, TradeDirection direction, double atr, SymbolInfo symbol,
        Price sl, TpOptions opts)
    {
        return opts.Method switch
        {
            "None" => null,
            "FixedPips" => SlTpHelpers.FixedPip(entry, direction, new Pips(opts.FixedPips), symbol),
            "AtrMultiple" => SlTpHelpers.AtrMultiple(entry, direction, atr, opts.AtrMultiple, symbol),
            _ => SlTpHelpers.RRMultiple(entry, sl, direction, opts.RrMultiple, symbol),
        };
    }

    private static Price CapMaxPips(Price sl, Price entry, TradeDirection direction, double maxPips, SymbolInfo symbol)
    {
        var dist = PipCalculator.Distance(entry, sl, symbol);
        if (dist.Value <= maxPips) return sl;

        return SlTpHelpers.FixedPip(entry, direction, new Pips(maxPips), symbol);
    }
}
