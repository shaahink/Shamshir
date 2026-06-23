using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.LiveMonitorChain;

[Trait("Category", "LiveMonitor")]
[Trait("Speed", "Fast")]
public sealed class TallyEventTests
{
    [Fact]
    public void TallyEventCounters()
    {
        var s = new BacktestOrchestrator.BacktestRunState { RunId = "test" };
        BacktestOrchestrator.TallyEvent(s, new("t", "SIGNAL", "", DateTime.UtcNow));
        BacktestOrchestrator.TallyEvent(s, new("t", "ORDER", "", DateTime.UtcNow));
        BacktestOrchestrator.TallyEvent(s, new("t", "EXEC", "", DateTime.UtcNow));
        BacktestOrchestrator.TallyEvent(s, new("t", "CLOSE", "", DateTime.UtcNow));
        BacktestOrchestrator.TallyEvent(s, new("t", "REJECTED", "", DateTime.UtcNow));
        BacktestOrchestrator.TallyEvent(s, new("t", "BREACH", "", DateTime.UtcNow));

        s.Signals.Should().Be(1);
        s.Orders.Should().Be(1);
        s.Fills.Should().Be(1);
        s.Closes.Should().Be(1);
        s.Rejections.Should().Be(1);
        s.Breaches.Should().Be(1);
    }
}
