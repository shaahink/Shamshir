namespace TradingEngine.Risk;

public static class PositionSizer
{
    public static decimal Calculate(
        decimal equity,
        RiskPercent riskPercent,
        Pips stopLossDistance,
        decimal pipValue,
        decimal drawdownScaleFactor,
        decimal maxLots,
        decimal brokerMinLots,
        decimal brokerLotStep)
    {
        var riskAmount = equity * (decimal)riskPercent.Value;
        var rawLots = riskAmount / ((decimal)stopLossDistance.Value * pipValue);
        var scaledLots = rawLots * drawdownScaleFactor;
        var clampedLots = Math.Min(scaledLots, maxLots);
        var steppedLots = Math.Floor(clampedLots / brokerLotStep) * brokerLotStep;
        return Math.Max(steppedLots, brokerMinLots);
    }

    public static decimal Calculate(
        decimal equity,
        RiskProfile profile,
        Pips stopLossDistance,
        decimal pipValue,
        decimal drawdownScaleFactor,
        decimal maxLots,
        decimal brokerMinLots,
        decimal brokerLotStep,
        double currentAtr = 0)
    {
        return profile.LotSizingMethod switch
        {
            LotSizingMethod.FixedLots => Math.Max(Math.Min(profile.FixedLots, maxLots), brokerMinLots),
            LotSizingMethod.FixedDollarRisk => CalculateFixedDollar(profile.FixedDollarRisk, stopLossDistance, pipValue, brokerMinLots, brokerLotStep, maxLots),
            LotSizingMethod.KellyFraction => CalculateKelly(equity, profile, stopLossDistance, pipValue, drawdownScaleFactor, brokerMinLots, brokerLotStep, maxLots),
            _ => CalculatePercentRisk(equity, profile, stopLossDistance, pipValue, drawdownScaleFactor, brokerMinLots, brokerLotStep, maxLots),
        };
    }

    private static decimal CalculatePercentRisk(
        decimal equity, RiskProfile profile, Pips slDistance, decimal pipValue,
        decimal scale, decimal minLots, decimal lotStep, decimal maxLots)
    {
        var riskAmount = equity * (decimal)profile.RiskPerTradePercent;
        var rawLots = riskAmount / ((decimal)slDistance.Value * pipValue);
        var scaledLots = rawLots * scale;
        var clampedLots = Math.Min(scaledLots, maxLots);
        var steppedLots = Math.Floor(clampedLots / lotStep) * lotStep;
        return Math.Max(steppedLots, minLots);
    }

    private static decimal CalculateFixedDollar(
        decimal fixedDollarRisk, Pips slDistance, decimal pipValue,
        decimal minLots, decimal lotStep, decimal maxLots)
    {
        var rawLots = fixedDollarRisk / ((decimal)slDistance.Value * pipValue);
        var clampedLots = Math.Min(rawLots, maxLots);
        var steppedLots = Math.Floor(clampedLots / lotStep) * lotStep;
        return Math.Max(steppedLots, minLots);
    }

    private static decimal CalculateKelly(
        decimal equity, RiskProfile profile, Pips slDistance, decimal pipValue,
        decimal scale, decimal minLots, decimal lotStep, decimal maxLots)
    {
        var kellyFraction = (decimal)profile.KellyFraction;
        var riskAmount = equity * (decimal)profile.RiskPerTradePercent * kellyFraction;
        var rawLots = riskAmount / ((decimal)slDistance.Value * pipValue);
        var scaledLots = rawLots * scale;
        var clampedLots = Math.Min(scaledLots, maxLots);
        var steppedLots = Math.Floor(clampedLots / lotStep) * lotStep;
        return Math.Max(steppedLots, minLots);
    }
}
