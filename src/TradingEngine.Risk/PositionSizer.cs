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
}
