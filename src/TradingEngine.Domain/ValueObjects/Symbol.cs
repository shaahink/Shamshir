namespace TradingEngine.Domain;

public readonly record struct Symbol(string Value)
{
    public static Symbol Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new Symbol(value.ToUpperInvariant().Trim());
    }

    public override string ToString() => Value;
}
