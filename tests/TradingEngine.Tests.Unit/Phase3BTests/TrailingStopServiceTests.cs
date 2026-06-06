namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Services")]
public sealed class TrailingStopServicePhase3BTests
{
    [Fact]
    public void Evaluate_NoLongerThrows()
    {
        var svc = new TrailingStopService();
        var pos = new Position(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long,
            0.1m, new Price(1.08500m), new Price(1.08300m), null, DateTime.UtcNow, "test");
        var tick = new Tick(Symbol.Parse("EURUSD"), 1.08650m, 1.08660m, DateTime.UtcNow);

        var result = svc.Evaluate(pos, tick, new TrailingConfig(TrailingMethod.StepPips, 10, 0, 0), []);
        Assert.NotNull(result); // may fire or not, but should not throw
    }
}
