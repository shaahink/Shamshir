using FluentAssertions;
using TradingEngine.Domain;
using TradingEngine.Host;
using TradingEngine.Web.Services;
using Xunit;

namespace TradingEngine.Tests.Integration.Iter27;

/// <summary>
/// Regression tests for the iter-27 UI/data fixes that need the Web assembly
/// (see docs/iterations/iter-27/PLAN.md). These are fast, in-memory unit-style tests; they live in
/// the Integration project only because it is the suite that references TradingEngine.Web.
/// </summary>
public sealed class Iter27FixTests
{
    // ---- F-Funnel: live Monitor counters map the event-type strings the ENGINE actually emits ----

    [Fact]
    public void TallyEvent_maps_engine_event_types_to_the_right_counters()
    {
        var state = new BacktestRunState { RunId = "r" };

        void Tally(string type) => RunProgressProjector.TallyEvent(
            state, new BacktestProgressEvent("r", type, $"{type} msg", DateTime.UtcNow));

        Tally("SIGNAL");
        Tally("ORDER"); Tally("ORDER");
        Tally("EXEC");
        Tally("CLOSE"); Tally("CLOSE"); Tally("CLOSE");
        Tally("REJECTED");
        Tally("BREACH");
        Tally("BAR");   // noise — must not move any funnel counter

        state.Signals.Should().Be(1);
        state.Orders.Should().Be(2);
        state.Fills.Should().Be(1, "engine emits 'EXEC' for fills, not 'FILL'");
        state.Closes.Should().Be(3);
        state.Rejections.Should().Be(1);
        state.Breaches.Should().Be(1);
    }

    // ---- F-Funnel2: per-strategy Report funnel counts intents, not per-bar BAR_EVAL noise ----

    [Fact]
    public void BuildFunnel_signals_are_orders_plus_rejects_not_the_bar_count()
    {
        var t = new List<DecisionRecordView>();
        long seq = 0;
        DecisionRecordView Rec(string evt, string? reason) => new(
            ++seq, DateTime.UtcNow, "EURUSD", "trend-breakout", evt, null, null, null, reason, "{}");

        // 100 per-bar evaluations (noise) — these must NOT inflate Signals.
        for (var i = 0; i < 100; i++) t.Add(Rec("BAR_EVAL", null));

        // The dispatcher also writes OrderSubmitted; only the lifecycle "Accepted" should count.
        t.Add(Rec("OrderSubmitted", "Accepted"));
        t.Add(Rec("OrderSubmitted", "Accepted"));
        t.Add(Rec("OrderSubmitted", null));        // dispatcher dupe — ignored
        t.Add(Rec("OrderRejected", "PreTradeGate"));
        t.Add(Rec("OrderFilled", "Filled"));
        t.Add(Rec("OrderFilled", "TP"));           // a winning close

        var funnel = RunFunnel.BuildFunnel(t);

        var row = funnel.Should().ContainSingle().Subject;
        row.StrategyId.Should().Be("trend-breakout");
        row.Orders.Should().Be(2, "only lifecycle OrderSubmitted(Accepted) counts");
        row.Signals.Should().Be(3, "Signals = accepted orders (2) + rejects (1), not the 100 bar evals");
        row.Fills.Should().Be(1);
        row.Closes.Should().Be(1);   // the TP close
    }

    // ---- Strategy picker: a run honours the selected strategy IDs (else runs all configured) ----

    [Fact]
    public void SelectActiveIds_empty_selection_runs_all_configured()
    {
        var configured = new[] { "trend-breakout", "ema-alignment", "mean-reversion" };

        StrategyRegistry.SelectActiveIds(configured, [])
            .Should().BeEquivalentTo(configured, o => o.WithStrictOrdering());
    }

    [Fact]
    public void SelectActiveIds_filters_configured_to_the_selection()
    {
        var configured = new[] { "trend-breakout", "ema-alignment", "mean-reversion" };

        StrategyRegistry.SelectActiveIds(configured, new[] { "mean-reversion" })
            .Should().Equal("mean-reversion");
    }

    [Fact]
    public void SelectActiveIds_drops_selections_that_are_not_configured()
    {
        var configured = new[] { "trend-breakout", "ema-alignment" };

        StrategyRegistry.SelectActiveIds(configured, new[] { "ema-alignment", "does-not-exist" })
            .Should().Equal("ema-alignment");
    }
}
