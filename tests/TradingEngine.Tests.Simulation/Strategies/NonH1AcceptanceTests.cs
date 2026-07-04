using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Risk.Filters;
using TradingEngine.Strategies.TrendBreakout;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Strategies;

/// <summary>
/// P1.2/P1.5.1 gate (PLAN.md §3 P1): "M15 tape run on EURUSD produces ≥1 proposal for trend-breakout."
/// Deferred during P1 (no M15 acceptance test — PROGRESS.md's own admitted gap) and, when finally
/// written during the P1.5 static review, found FAILING against the shipped P1 code: strategies'
/// RequiredIndicators still requested H1 (implicitly, via IndicatorRequest's default parameter) even
/// though their own bar lookups were correctly de-hardcoded to EntryTimeframe. Drives the REAL
/// IndicatorSnapshotService + BarEvaluator pipeline (not StrategyTestHelper, which bypasses
/// IndicatorSnapshotService entirely and — separately — hardcodes bars under the H1 key, so it could
/// never see this class of bug either).
/// </summary>
[Trait("Category", "KernelAcceptance")]
[Trait("Speed", "Fast")]
public sealed class NonH1AcceptanceTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");

    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static readonly RiskProfile StandardProfile = new(
        "standard", "Standard", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
        false, "ftmo-standard", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

    private static PropFirmRuleSet FtmoRuleSet() => new(
        "ftmo-standard", "ftmo-standard", "Fixed", 0.05, 0.10, 0.10, 0,
        "BalancePlusFloating", "22:00:00", "UTC",
        false, "High", 0, 0,
        false, "21:00:00", "20:00:00", "NextTradingDay", false);

    private static BarEvaluator BuildEvaluator(IReadOnlyList<IStrategy> strategies, ISymbolInfoRegistry symbolRegistry)
    {
        // Real SkenderIndicatorService — IndicatorSnapshotService casts to the concrete type internally,
        // so a strategy that actually requests indicators (unlike AlwaysSignalStrategy) needs the real thing.
        var realIndicators = new SkenderIndicatorService();
        var indicatorSnapshot = new IndicatorSnapshotService(realIndicators, strategies);

        var strategyBank = Substitute.For<IStrategyBank>();
        strategyBank.GetActive(Arg.Any<Symbol>(), Arg.Any<Timeframe>(), Arg.Any<MarketRegime>())
            .ReturnsForAnyArgs(strategies);

        var regimeDetector = Substitute.For<IRegimeDetector>();
        regimeDetector.Detect(Arg.Any<Symbol>(), Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<IReadOnlyDictionary<string, double>>())
            .ReturnsForAnyArgs(MarketRegime.Trending);

        var newsFilter = Substitute.For<INewsFilter>();
        var sessionFilter = new SessionFilter();

        var riskManager = Substitute.For<IRiskManager>();
        riskManager.ActiveRuleSet.Returns(FtmoRuleSet());
        riskManager.CheckComplianceBlock(Arg.Any<TradeIntent>(), Arg.Any<RiskProfile>()).Returns((string?)null);
        riskManager.Drawdown.Returns(TradingEngine.Engine.DrawdownReducer.CreateInitial(10_000m, "Fixed"));

        var riskProfileResolver = Substitute.For<IRiskProfileResolver>();
        riskProfileResolver.Resolve(Arg.Any<string>()).Returns(StandardProfile);

        return new BarEvaluator(
            indicatorSnapshot, strategyBank, regimeDetector, signalGate: null,
            new TradingEngine.Services.EntryPlanner(symbolRegistry, NullLogger<TradingEngine.Services.EntryPlanner>.Instance),
            symbolRegistry, getCrossRate: (_, _) => 1.0m,
            newsFilter, sessionFilter, riskManager, riskProfileResolver,
            governor: null, NullLogger.Instance, realIndicators);
    }

    [Fact]
    public async Task M15Run_TrendBreakout_ProducesAtLeastOneProposal()
    {
        // 80 M15 bars trending up, well past RequiredBarCount (max(20,50,14)+5=55) — a monotonic uptrend
        // where every bar's high exceeds the prior 20-bar lookback high, so trend-breakout should fire on
        // the first eligible bar once indicators are actually being computed on the M15 timeframe.
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = Bars.Trend(Eurusd, Timeframe.M15, start, 1.1000m, pips: 200, barCount: 80).Build();

        var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
        symbolRegistry.Get(Eurusd).Returns(EurusdInfo);

        var strategy = new TrendBreakoutStrategy(
            new TrendBreakoutConfig { EntryTimeframe = Timeframe.M15 },
            symbolRegistry,
            NullLogger<TrendBreakoutStrategy>.Instance);

        var evaluator = BuildEvaluator([strategy], symbolRegistry);
        var state = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            TradingEngine.Engine.DrawdownReducer.CreateInitial(10_000m, "Fixed"),
            0, ProtectionState.None, AccountView.Flat);

        var proposals = new List<OrderProposed>();
        foreach (var bar in bars)
        {
            var barClosed = new BarClosed(bar.Symbol, bar.Timeframe, bar.Open, bar.High, bar.Low, bar.Close, bar.OpenTimeUtc);
            var evaluation = await evaluator.EvaluateAsync(barClosed, state, CancellationToken.None);
            proposals.AddRange(evaluation.Proposals);
        }

        proposals.Should().NotBeEmpty(
            "trend-breakout on a clean M15 uptrend must fire at least once — if this is empty, the " +
            "strategy's indicator requests are silently missing (P1.5.1: IndicatorRequest.Timeframe " +
            "defaulting to H1 while bars are only loaded for M15)");
        proposals.Should().OnlyContain(p => p.EntryTimeframe == Timeframe.M15);
    }
}
