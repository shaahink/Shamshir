using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.LiveMonitorChain;

[Trait("Category", "LiveMonitor")]
[Trait("Speed", "Fast")]
public sealed class TallyEventTests
{
    [Fact]
    public void TallyEventCounters()
    {
        var s = new BacktestRunState { RunId = "test" };
        RunProgressProjector.TallyEvent(s, new("t", "SIGNAL", "", DateTime.UtcNow));
        RunProgressProjector.TallyEvent(s, new("t", "ORDER", "", DateTime.UtcNow));
        RunProgressProjector.TallyEvent(s, new("t", "EXEC", "", DateTime.UtcNow));
        RunProgressProjector.TallyEvent(s, new("t", "CLOSE", "", DateTime.UtcNow));
        RunProgressProjector.TallyEvent(s, new("t", "REJECTED", "", DateTime.UtcNow));
        RunProgressProjector.TallyEvent(s, new("t", "BREACH", "", DateTime.UtcNow));

        s.Signals.Should().Be(1);
        s.Orders.Should().Be(1);
        s.Fills.Should().Be(1);
        s.Closes.Should().Be(1);
        s.Rejections.Should().Be(1);
        s.Breaches.Should().Be(1);
    }
}
