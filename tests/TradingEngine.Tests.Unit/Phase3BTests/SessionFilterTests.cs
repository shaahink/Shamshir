namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Risk")]
public sealed class SessionFilterPhase3BTests
{
    [Fact]
    public void IsWeekend_Saturday_ReturnsTrue()
    {
        var filter = new SessionFilter();
        filter.IsWeekend(new DateTime(2024, 1, 6)).Should().BeTrue(); // Saturday
    }

    [Fact]
    public void IsWeekend_Sunday_ReturnsTrue()
    {
        var filter = new SessionFilter();
        filter.IsWeekend(new DateTime(2024, 1, 7)).Should().BeTrue(); // Sunday
    }

    [Fact]
    public void IsWeekend_Monday_ReturnsFalse()
    {
        var filter = new SessionFilter();
        filter.IsWeekend(new DateTime(2024, 1, 8)).Should().BeFalse(); // Monday
    }
}
