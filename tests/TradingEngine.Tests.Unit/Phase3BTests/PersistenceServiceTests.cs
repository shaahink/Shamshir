using Microsoft.Extensions.DependencyInjection;

namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Services")]
public sealed class PersistenceServiceTests
{
    [Fact]
    public async Task SaveTradeAsync_DoesNotThrow()
    {
        var repo = Substitute.For<ITradeRepository>();
        repo.SaveAsync(Arg.Any<TradeResult>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.GetService(typeof(ITradeRepository)).Returns(repo);
        scope.ServiceProvider.GetRequiredService<ITradeRepository>().Returns(repo);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        scopeFactory.CreateAsyncScope().Returns(new AsyncServiceScope(scope));

        var logger = Substitute.For<ILogger<PersistenceService>>();
        var svc = new PersistenceService(scopeFactory, logger);

        var trade = new TradeResult(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"),
            TradeDirection.Long, 0.1m, new Price(1.0m), new Price(1.0m), new Price(0.99m), null,
            DateTime.UtcNow, DateTime.UtcNow,
            new Money(0, "USD"), new Money(0, "USD"), new Money(0, "USD"), new Money(0, "USD"),
            new Pips(0), 0, new Pips(0), new Pips(0), "TP", "test", "standard", EngineMode.Backtest);

        await svc.SaveTradeAsync(trade, CancellationToken.None);
        await repo.Received(1).SaveAsync(Arg.Any<TradeResult>(), Arg.Any<CancellationToken>());
    }
}
