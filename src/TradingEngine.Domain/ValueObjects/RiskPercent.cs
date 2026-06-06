namespace TradingEngine.Domain;

public readonly record struct RiskPercent(double Value)
{
    public static RiskPercent Parse(double value)
    {
        if (value is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Risk percent must be between 0 and 1");
        return new RiskPercent(value);
    }
}
