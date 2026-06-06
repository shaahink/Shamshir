namespace TradingEngine.Domain;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StrategyIdAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}
