namespace TradingEngine.Risk.Sizing;

public sealed class GovernorSizeModifier : ISizeModifier
{
    private readonly ITradingGovernor _governor;

    public GovernorSizeModifier(ITradingGovernor governor)
    {
        _governor = governor;
    }

    public string Name => "Governor";

    public double ComputeScale(SizeModifierContext context)
    {
        var snapshot = _governor.GetSnapshot();
        return (double)snapshot.SizeMultiplier;
    }
}
