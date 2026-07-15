using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Host;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Ftmo;

/// <summary>
/// Phase 0c gate + template. Proves the iter-24 testability seam: <see cref="TradingLoop"/> can be
/// driven directly — no IHost, no IHost 5s floor, no orphan processes — with a fake broker and the
/// real OrderDispatcher/PositionTracker. Runs in well under a second. The FTMO constraint suite
/// (Phase 5) reuses this exact wiring, swapping the substitute risk manager for a real one.
/// </summary>
public sealed class TradingLoopDirectTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static readonly RiskProfile StandardProfile = new(
        "standard", "Standard", 1.0, 5.0, 10.0, 100.0, 10.0, 0.5, 0.1, 5,
        false, "ftmo-standard",
        LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

    [Fact]
    public async Task TradingLoop_DrivenDirectly_ProducesAnOrder()
    {
        var strategy = new AlwaysSignalStrategy();
        IReadOnlyList<IStrategy> strategies = [strategy];

        // -- Fake broker: accepts orders, empty execution stream --
        var broker = Substitute.For<IBrokerAdapter>();
        broker.SubmitOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Guid.NewGuid()));
        broker.ExecutionStream.Returns(Channel.CreateUnbounded<ExecutionEvent>().Reader);

        var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
        symbolRegistry.Get(Eurusd).Returns(EurusdInfo);

        Func<string, string, decimal> crossRate = (_, _) => 1.0m;

        // -- Risk: allow everything, fixed lot (real RiskManager swaps in for the FTMO suite) --
        var risk = Substitute.For<IRiskManager>();
        risk.CalculateLotSize(Arg.Any<TradeIntent>(), Arg.Any<EquitySnapshot>(), Arg.Any<RiskProfile>(), Arg.Any<decimal>())
            .Returns(0.01m);
        risk.ValidateOrder(
                Arg.Any<TradeIntent>(), Arg.Any<EquitySnapshot>(), Arg.Any<RiskProfile>(), Arg.Any<decimal>(),
                Arg.Any<SymbolInfo>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
                Arg.Any<IReadOnlyList<ProjectedPosition>>(), out Arg.Any<decimal>())
            .Returns(ci => { ci[9] = 0.01m; return Array.Empty<RiskViolation>(); });

        var resolver = Substitute.For<IRiskProfileResolver>();
        resolver.Resolve(Arg.Any<string>()).Returns(StandardProfile);

        var clock = Substitute.For<IEngineClock>();
        clock.UtcNow.Returns(_ => T0);

        var eventBus = Substitute.For<IEventBus>();

        var positionManager = Substitute.For<IPositionManager>();
        var decisionJournal = Substitute.For<IDecisionJournal>();
        var indicators = Substitute.For<IIndicatorService>();
        var runContext = new EngineRunContext("trading-loop-direct");

        var regimeDetector = Substitute.For<IRegimeDetector>();
        var strategyBank = Substitute.For<IStrategyBank>();
        strategyBank.GetActive(Arg.Any<Symbol>(), Arg.Any<Timeframe>(), Arg.Any<MarketRegime>())
            .ReturnsForAnyArgs(strategies);

        var indicatorSnapshot = new IndicatorSnapshotService(indicators, strategies);
        var positionTracker = new PositionTracker(
            symbolRegistry, crossRate, risk, positionManager, eventBus, clock,
            NullLogger<PositionTracker>.Instance);
        var dispatcher = new OrderDispatcher(
            risk, resolver, symbolRegistry, crossRate, decisionJournal, runContext,
            NullLogger<OrderDispatcher>.Instance);

        var equity = new EquitySnapshot(T0, 10_000m, 0m, 10_000m, 10_000m, 10_000m, 0m, 0m, EngineMode.Backtest);

        var loop = new TradingLoop(
            broker, indicatorSnapshot, dispatcher, positionTracker,
            strategyBank, regimeDetector, signalGate: null, governor: null, symbolRegistry,
            eventBus, clock, runContext, (_, _) => 1m, () => equity, progress: null, journal: null,
            new TradingEngine.Services.EntryPlanner(symbolRegistry, NullLogger<TradingEngine.Services.EntryPlanner>.Instance),
            NullLogger.Instance);

        // AlwaysSignalStrategy fires a Long once bar count exceeds 5. Drive 8 bars.
        for (var i = 0; i < 8; i++)
        {
            var bar = new Bar(Eurusd, Timeframe.H1, T0.AddHours(i),
                1.1000m, 1.1010m, 1.0990m, 1.1000m, 1000);
            await loop.ProcessBarAsync(bar, CancellationToken.None);
        }

        loop.BarCount.Should().Be(8);
        await broker.Received().SubmitOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }
}
