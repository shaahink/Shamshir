using System.Text.Json;
using System.Threading.Channels;
using TradingEngine.Engine;
using TradingEngine.Host;

namespace TradingEngine.Tests.Simulation.VenueParity;

/// <summary>
/// P0.1 / P0.5 (R8) — the venue-parity equivalence tier that would have caught F1 the day it shipped.
///
/// F1 (AUDIT): the cTrader leg sized every order at exactly ¼ of the tape leg's risk for byte-identical
/// proposals ($125 vs $500 on 100,000 × 0.5%). Root cause (hypothesis 1, now proven here): EngineRunner's
/// startup reconciliation adopted the venue's hello-reported balance (a cTrader demo account's real ~25k)
/// over the run's configured 100k — so the pure gate sized off equity 25k on the cTrader path and 100k on
/// the tape path. 25,000 / 100,000 = 0.25.
///
/// These tests need NO cTrader credentials — they drive the pure gate directly and the CTraderBrokerAdapter
/// over a fake in-memory transport. The suite is tagged Category=VenueParity so it rides the standard gate
/// filter (see PLAN P0.5).
/// </summary>
[Trait("Category", "VenueParity")]
[Trait("Speed", "Fast")]
public sealed class VenueSizingParityTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");

    // Mirrors the audited EURUSD symbol (contract 100k, pip 0.0001 ⇒ pipValuePerLot = 10).
    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private const decimal PipValuePerLot = 10m;

    // The audited profile: PercentRisk 0.5% (RiskPerTradePercent 0.005), standard.
    private static RiskProfile Profile() => new(
        "standard", "Standard", 0.005, 0.05, 0.10, 0.0, 0.10, 0.5, 0.5, 5,
        false, "ftmo-standard", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

    private static (KernelConfig Config, EngineState State) Build(decimal equity)
    {
        var profile = Profile();
        var ruleSet = new PropFirmRuleSet(
            "ftmo-standard", "ftmo-standard", "Fixed", 0.05, 0.10, 0.10, 0,
            "BalancePlusFloating", "22:00:00", "UTC",
            false, "High", 0, 0,
            false, "21:00:00", "20:00:00", "NextTradingDay", false);
        var constraints = ConstraintSet.Resolve(profile, ruleSet);
        var sizing = new SizingPolicyOptions { FlattenAtFraction = 0.9 };

        var config = new KernelConfig(
            constraints, profile, sizing,
            ResolveSymbol: _ => EurusdInfo,
            ProjectOpenPositions: _ => [],
            Seed: 42);

        var state = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(equity, "Fixed"), 0, ProtectionState.None,
            new AccountView(equity, equity, 0m));

        return (config, state);
    }

    // The March trade-1 proposal: SL 26.0 pips (audit table row 1).
    private static OrderProposed Proposal() => new(
        Guid.NewGuid(), Eurusd, TradeDirection.Long, OrderType.Market,
        null, new Price(1.0894m), null, "trend-breakout", 1.0920m,
        26.0m, PipValuePerLot, new DateTime(2024, 3, 4, 6, 0, 0, DateTimeKind.Utc));

    // ── REPRO (R3, before-the-fix): equity 25k sizes at exactly ¼ of equity 100k. This is F1's mechanism. ──
    [Fact]
    public void Repro_QuarterEquity_ProducesQuarterLots()
    {
        var (cfg100k, state100k) = Build(100_000m);
        var (cfg25k, state25k) = Build(25_000m);

        var proposal = Proposal();
        var tape = PreTradeGate.Evaluate(state100k, proposal, cfg100k.Constraints, cfg100k.Profile, cfg100k.Sizing, EurusdInfo, []);
        var ctrader = PreTradeGate.Evaluate(state25k, proposal, cfg25k.Constraints, cfg25k.Profile, cfg25k.Sizing, EurusdInfo, []);

        tape.Accepted.Should().BeTrue();
        ctrader.Accepted.Should().BeTrue();

        // 100,000 × 0.005 / (26 × 10) = 1.923 → floor to 0.01 step = 1.92.
        tape.Lots.Should().Be(1.92m, "the tape leg sizes off the configured 100k");
        // 25,000 × 0.005 / (26 × 10) = 0.4807 → floor to 0.01 step = 0.48.
        ctrader.Lots.Should().Be(0.48m, "the cTrader leg sized off the adopted 25k hello balance — the F1 bug");

        // The audited ratio: exactly ¼ (modulo lot-step flooring noise), and RiskAmount ~ $500 vs ~$125.
        (tape.Lots / ctrader.Lots).Should().Be(4.0m);
        tape.RiskAmount.Should().Be(499.20m);
        ctrader.RiskAmount.Should().Be(124.80m);
    }

    // ── THE FIX (R3, after): with balance resolution corrected, both venues size off the SAME balance. ──
    [Fact]
    public void Fixed_BothVenues_ResolveToConfigBalance_AndSizeEqually()
    {
        // Tape: GetAccountStateAsync returns the configured 100k → no drift, no adoption.
        var tape = EngineRunner.ResolveInitialBalance(
            EngineMode.Backtest, configBalance: 100_000m, riskManagerBalance: 0m, venueBalance: 100_000m);
        // cTrader: the hello reports the demo account's 25k, but backtest must NOT adopt it.
        var ctrader = EngineRunner.ResolveInitialBalance(
            EngineMode.Backtest, configBalance: 100_000m, riskManagerBalance: 0m, venueBalance: 25_000m);

        tape.InitialBalance.Should().Be(100_000m);
        ctrader.InitialBalance.Should().Be(100_000m, "backtest sizing authority is the configured balance, never the venue hello");
        ctrader.HasDrift.Should().BeTrue("a 25k venue balance disagreeing with 100k config is recorded as drift");
        ctrader.AdoptVenueEquity.Should().BeFalse("backtest never adopts the venue equity");

        // With equal resolved balances, the pure gate produces byte-identical lots + risk on both venues.
        var (cfgT, stateT) = Build(tape.InitialBalance);
        var (cfgC, stateC) = Build(ctrader.InitialBalance);
        var proposal = Proposal();
        var gateT = PreTradeGate.Evaluate(stateT, proposal, cfgT.Constraints, cfgT.Profile, cfgT.Sizing, EurusdInfo, []);
        var gateC = PreTradeGate.Evaluate(stateC, proposal, cfgC.Constraints, cfgC.Profile, cfgC.Sizing, EurusdInfo, []);

        gateC.Lots.Should().Be(gateT.Lots, "same bars + same resolved balance ⇒ equal lots across venues (F1 closed)");
        gateC.RiskAmount.Should().Be(gateT.RiskAmount, "and equal RiskAmount");
        gateT.Lots.Should().Be(1.92m);
    }

    // ── Live mode is UNCHANGED: the venue balance IS the truth and is adopted (drift guard is backtest-only). ──
    [Fact]
    public void Live_AdoptsVenueBalance()
    {
        var live = EngineRunner.ResolveInitialBalance(
            EngineMode.Live, configBalance: 100_000m, riskManagerBalance: 0m, venueBalance: 25_000m);

        live.InitialBalance.Should().Be(25_000m, "live sizing must track the real account balance");
        live.AdoptVenueEquity.Should().BeTrue();
        live.HasDrift.Should().BeFalse("adoption is expected in live — not drift");
    }

    // ── R7 instrumentation: the kernel's accept-path DecisionRecord now carries the sizing story. ──
    [Fact]
    public void AcceptedProposal_JournalsSizingInputs()
    {
        var (config, state) = Build(100_000m);
        var kernel = new Kernel(config);

        var decision = kernel.Decide(state, Proposal());

        var record = decision.Effects.OfType<RecordDecisionEvent>().Should().ContainSingle().Subject;
        record.Decision.DetailJson.Should().NotBe("{}", "the accept path must carry the sizing inputs (R7)");

        using var doc = JsonDocument.Parse(record.Decision.DetailJson);
        var root = doc.RootElement;
        root.GetProperty("equityAtGate").GetDecimal().Should().Be(100_000m);
        root.GetProperty("lotSizingMethod").GetString().Should().Be("PercentRisk");
        root.GetProperty("riskPct").GetDouble().Should().Be(0.005);
        root.GetProperty("drawdownScale").GetDecimal().Should().Be(1m);
        root.GetProperty("slPips").GetDecimal().Should().Be(26.0m);
        root.GetProperty("pipValuePerLot").GetDecimal().Should().Be(10m);
        root.GetProperty("clampedLots").GetDecimal().Should().Be(1.92m);
        root.GetProperty("riskAmount").GetDecimal().Should().Be(499.20m);
        // rawLots is the unclamped candidate (pre lot-step floor): 1.923...
        root.GetProperty("rawLots").GetDecimal().Should().BeGreaterThan(1.92m).And.BeLessThan(1.93m);
    }

    // ── The mechanism SOURCE: a FakeTransport cTrader hello of balance=25000 surfaces via
    //    GetAccountStateAsync — proving where the stale 25k the gate consumed came from (no creds). ──
    [Fact]
    public async Task CtraderHello_SurfacesDemoBalance_ThatBacktestMustNotAdopt()
    {
        var transport = new FakeMessageTransport();
        var adapter = new CTraderBrokerAdapter(transport, Substitute.For<ILogger<CTraderBrokerAdapter>>());
        await adapter.ConnectAsync(CancellationToken.None);

        var hello = """{"type":"hello","v":1,"account":{"balance":25000.0,"equity":25000.0},"positions":[]}""";
        await transport.RouterWriter.WriteAsync((new byte[] { 1, 2, 3 }, hello));
        await Task.Delay(150);

        var venueState = await adapter.GetAccountStateAsync(CancellationToken.None);
        venueState.Balance.Should().Be(25_000m, "the cBot hello reports the demo account balance");

        // This is exactly the value EngineRunner receives — and, in backtest, must reject in favour of config.
        var resolution = EngineRunner.ResolveInitialBalance(
            EngineMode.Backtest, configBalance: 100_000m, riskManagerBalance: 0m, venueBalance: venueState.Balance);
        resolution.InitialBalance.Should().Be(100_000m);
        resolution.HasDrift.Should().BeTrue();

        await adapter.DisconnectAsync(CancellationToken.None);
    }

    private sealed class FakeMessageTransport : IMessageTransport
    {
        private readonly Channel<(string, string)> _subChannel =
            Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });
        private readonly Channel<(byte[], string)> _routerChannel =
            Channel.CreateBounded<(byte[], string)>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

        public ChannelReader<(string Topic, string Json)> SubMessages => _subChannel.Reader;
        public ChannelReader<(byte[] Identity, string Json)> RouterMessages => _routerChannel.Reader;
        public ChannelWriter<(byte[], string)> RouterWriter => _routerChannel.Writer;

        public bool IsConnected { get; set; } = true;
        public Action? OnConnected { get; set; }

        public void Send(byte[] identity, string json) { }
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct)
        {
            _subChannel.Writer.TryComplete();
            _routerChannel.Writer.TryComplete();
            return Task.CompletedTask;
        }
    }
}
