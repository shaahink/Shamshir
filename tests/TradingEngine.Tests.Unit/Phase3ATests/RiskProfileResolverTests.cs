namespace TradingEngine.Tests.Unit.Phase3ATests;

[Trait("Category", "Services")]
public sealed class RiskProfileResolverTests
{
    [Fact] // T-3
    public void UnknownProfile_Throws()
    {
        var resolver = new RiskProfileResolver([]);
        var act = () => resolver.Resolve("nonexistent");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact] // T-4
    public void ResolvesFromLoadedConfig()
    {
        var profiles = new List<RiskProfile>
        {
            new("standard", "Standard", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo-standard"),
        };
        var resolver = new RiskProfileResolver(profiles);
        var result = resolver.Resolve("standard");
        result.Id.Should().Be("standard");
        result.RiskPerTradePercent.Should().Be(0.01);
    }
}
