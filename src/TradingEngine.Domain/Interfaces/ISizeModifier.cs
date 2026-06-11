namespace TradingEngine.Domain;

public interface ISizeModifier
{
    string Name { get; }
    double ComputeScale(SizeModifierContext context);
}
