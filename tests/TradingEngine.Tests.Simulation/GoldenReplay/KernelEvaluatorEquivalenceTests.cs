using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Engine;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// K1 gate (iter-36 cutover): proves the new <see cref="BarEvaluator"/> stage — the event producer that
/// replaces <c>TradingLoop.ProcessBarAsync</c>'s imperative dispatch — drives the kernel to the SAME
/// order decision as the golden baseline. Per bar the evaluator emits <see cref="OrderProposed"/> events;
/// the pure kernel gate (<c>PreTradeGate</c>) sizes/accepts them. The first accepted order must match
/// <c>golden-snapshot.json</c>'s first trade (0.20 lots, Long), and there must be exactly one
/// <see cref="SubmitOrder"/> per accepted proposal (no double-submit).
///
/// Also pins the K1 requirement that the impure verdicts (news/weekend/compliance/governor) are FROZEN
/// onto the proposal at sim-time and applied by the pure kernel — so no protection is silently dropped
/// and a replay is date-independent.
/// </summary>
[Trait("Category", "KernelAcceptance")]
[Trait("Speed", "Fast")]
public sealed class KernelEvaluatorEquivalenceTests
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

    private static KernelConfig BuildConfig()
    {
        var ruleSet = FtmoRuleSet();
        var constraints = ConstraintSet.Resolve(StandardProfile, ruleSet);
        var sizing = new SizingPolicyOptions { FlattenAtFraction = 0.9 };

        return new KernelConfig(
            constraints, StandardProfile, sizing,
            ResolveSymbol: _ => EurusdInfo,
            ProjectOpenPositions: state =>
            {
                var open = new List<ProjectedPosition>();
                foreach (var (_, ps) in state.Positions)
                {
                    if (ps.Phase != PositionPhase.Open) continue;
                    var slPips = ps.Direction == TradeDirection.Long
                        ? (ps.EntryPrice.Value - ps.CurrentStopLoss.Value) / EurusdInfo.PipSize
                        : (ps.CurrentStopLoss.Value - ps.EntryPrice.Value) / EurusdInfo.PipSize;
                    open.Add(new ProjectedPosition(slPips, ps.Lots, EurusdInfo.ContractSize * EurusdInfo.PipSize));
                }
                return open;
            },
            Seed: 42);
    }

    private static BarEvaluator BuildEvaluator(IReadOnlyList<IStrategy> strategies)
    {
        var indicators = Substitute.For<IIndicatorService>();
        var indicatorSnapshot = new IndicatorSnapshotService(indicators, strategies);

        var strategyBank = Substitute.For<IStrategyBank>();
        strategyBank.GetActive(Arg.Any<Symbol>(), Arg.Any<Timeframe>(), Arg.Any<MarketRegime>())
            .ReturnsForAnyArgs(strategies);

        var regimeDetector = Substitute.For<IRegimeDetector>();
        regimeDetector.Detect(Arg.Any<Symbol>(), Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<IReadOnlyDictionary<string, double>>())
            .ReturnsForAnyArgs(MarketRegime.Trending);

        var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
        symbolRegistry.Get(Arg.Any<Symbol>()).Returns(EurusdInfo);

        var newsFilter = Substitute.For<INewsFilter>();
        var sessionFilter = new SessionFilter();

        var riskManager = Substitute.For<IRiskManager>();
        riskManager.ActiveRuleSet.Returns(FtmoRuleSet());
        riskManager.CheckComplianceBlock(Arg.Any<TradeIntent>(), Arg.Any<RiskProfile>()).Returns((string?)null);
        riskManager.Drawdown.Returns(DrawdownReducer.CreateInitial(10_000m, "Fixed"));

        var riskProfileResolver = Substitute.For<IRiskProfileResolver>();
        riskProfileResolver.Resolve(Arg.Any<string>()).Returns(StandardProfile);

        return new BarEvaluator(
            indicatorSnapshot, strategyBank, regimeDetector, signalGate: null,
            new EntryPlanner(symbolRegistry, NullLogger<EntryPlanner>.Instance),
            symbolRegistry, getCrossRate: (_, _) => 1.0m,
            newsFilter, sessionFilter, riskManager, riskProfileResolver,
            governor: null, NullLogger.Instance);
    }

    private static EngineState InitialState() => new(
        new Dictionary<Guid, PositionState>(),
        new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
        DrawdownReducer.CreateInitial(10_000m, "Fixed"),
        0,
        ProtectionState.None,
        AccountView.Flat);

    [Fact]
    public async Task Evaluator_DrivesKernel_ToGoldenFirstOrder()
    {
        var bars = GoldenBarFixture.Create();
        var kernel = new Kernel(BuildConfig());
        var evaluator = BuildEvaluator([new AlwaysSignalStrategy()]);
        var queue = new InMemoryEngineEventQueue();

        var state = InitialState();
        var allProposals = new List<OrderProposed>();
        var submitEffects = new List<SubmitOrder>();

        foreach (var bar in bars)
        {
            var barClosed = new BarClosed(bar.Symbol, bar.Timeframe, bar.Open, bar.High, bar.Low, bar.Close, bar.OpenTimeUtc);
            var evaluation = await evaluator.EvaluateAsync(barClosed, state, CancellationToken.None);
            allProposals.AddRange(evaluation.Proposals);

            foreach (var proposal in evaluation.Proposals) queue.Enqueue(proposal);
            queue.Enqueue(barClosed);

            while (queue.TryDequeue(out var evt))
            {
                var decision = kernel.Decide(state, evt);
                state = decision.State;
                foreach (var effect in decision.Effects)
                {
                    if (effect is SubmitOrder so)
                    {
                        submitEffects.Add(so);
                        // Venue feedback: the proposal already created the Submitted position in the
                        // kernel; the fill moves it to Open.
                        queue.Enqueue(new OrderFilled(so.OrderId, so.Symbol, so.Lots,
                            new Price(bar.Close), bar.OpenTimeUtc.AddSeconds(1)));
                    }
                }
            }

            var equity = 10_000m + state.Positions.Values
                .Where(p => p.Phase == PositionPhase.Open)
                .Sum(p => (bar.Close - p.EntryPrice.Value) * EurusdInfo.ContractSize * p.Lots
                    * (p.Direction == TradeDirection.Long ? 1 : -1));
            queue.Enqueue(new EquityObserved(10_000m, equity, equity - 10_000m, bar.OpenTimeUtc));
            while (queue.TryDequeue(out var evt))
            {
                var decision = kernel.Decide(state, evt);
                state = decision.State;
            }
        }

        // --- The evaluator produced a real proposal carrying the gate-relevant market context ---
        allProposals.Should().NotBeEmpty("the AlwaysSignalStrategy fires once on the golden down-leg fixture");
        var firstProposal = allProposals[0];
        firstProposal.Symbol.Should().Be(Eurusd);
        firstProposal.Direction.Should().Be(TradeDirection.Long);
        firstProposal.StrategyId.Should().Be("always-signal");
        firstProposal.SlPips.Should().Be(50m, "AlwaysSignalStrategy sets SL 50 pips from entry");
        firstProposal.PipValuePerLot.Should().Be(10m, "EURUSD pip value per lot = ContractSize × PipSize = 10");

        // --- The kernel gate sized/accepted it identically to the golden baseline ---
        var golden = GoldenSnapshotLoader.Load();
        var goldenFirstTrade = golden.Trades.Should().NotBeEmpty().And.Subject.First();

        submitEffects.Should().HaveCount(1, "exactly one SubmitOrder per accepted proposal — no double-submit");
        submitEffects[0].Lots.Should().Be(goldenFirstTrade.Lots,
            "the evaluator-driven kernel must size the first order identically to golden-snapshot.json (0.20)");
        submitEffects[0].Direction.ToString().Should().Be(goldenFirstTrade.Direction);
        submitEffects[0].Symbol.Should().Be(Eurusd);

        state.Positions.Should().NotBeEmpty("the accepted+filled order leaves a position open");
    }

    [Fact]
    public void Kernel_AppliesExternalWeekendVerdict_CarriedOnProposal()
    {
        var kernel = new Kernel(BuildConfig());
        var state = InitialState() with { Account = new AccountView(10_000m, 10_000m, 0m) };
        var simTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        OrderProposed Propose(ExternalVerdicts external) => new(
            new Guid("11111111-1111-1111-1111-111111111111"), Eurusd, TradeDirection.Long, OrderType.Market,
            null, new Price(1.0920m), new Price(1.1100m), "always-signal", 1.0970m, 50m, 10m, simTime, external);

        // Control: no blocking verdict ⇒ the gate accepts (proves the proposal is otherwise valid).
        var accepted = kernel.Decide(state, Propose(default));
        accepted.Effects.OfType<SubmitOrder>().Should().ContainSingle(
            "with no external verdict the gate accepts the proposal");

        // A weekend verdict frozen on the proposal ⇒ the pure kernel rejects it with the right reason.
        var rejected = kernel.Decide(state, Propose(new ExternalVerdicts(WeekendRestricted: true)));
        rejected.Effects.OfType<SubmitOrder>().Should().BeEmpty(
            "a weekend-restricted proposal must not reach the venue");
        var reject = rejected.Effects.OfType<RecordDecisionEvent>().Should().ContainSingle().Subject;
        reject.Decision.Reason.Should().Be("WEEKEND_RESTRICTION",
            "the kernel must apply the external verdict carried on the proposal — no protection silently dropped");
    }

    [Fact]
    public async Task Evaluator_FreezesWeekendVerdict_AtBarSimTime()
    {
        // Six H1 bars on a Saturday (2024-01-06). AlwaysSignalStrategy fires on bar 6; the evaluator must
        // freeze WeekendRestricted=true onto the proposal because the BAR's sim-time is a weekend and the
        // ftmo rule-set forbids weekend holding — date-dependence resolved at evaluation, not wall-clock.
        var saturday = new DateTime(2024, 1, 6, 0, 0, 0, DateTimeKind.Utc);
        var bars = Bars.Trend(Eurusd, Timeframe.H1, saturday, 1.1000m, -50, 6).Build();

        var evaluator = BuildEvaluator([new AlwaysSignalStrategy()]);
        var state = InitialState();

        var proposals = new List<OrderProposed>();
        foreach (var bar in bars)
        {
            var barClosed = new BarClosed(bar.Symbol, bar.Timeframe, bar.Open, bar.High, bar.Low, bar.Close, bar.OpenTimeUtc);
            var evaluation = await evaluator.EvaluateAsync(barClosed, state, CancellationToken.None);
            proposals.AddRange(evaluation.Proposals);
        }

        proposals.Should().ContainSingle("the strategy fires once over six bars");
        proposals[0].External.WeekendRestricted.Should().BeTrue(
            "the evaluator computed the weekend verdict from the bar's sim-time (a Saturday)");
        proposals[0].OccurredAtUtc.DayOfWeek.Should().Be(DayOfWeek.Saturday);
    }
}
