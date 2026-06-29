using FluentAssertions;
using TradingEngine.Web.Dtos.Runs;
using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.Api;

/// <summary>
/// iter-strategy-system P1 (D3): the row-based builder. A row is a (strategy × symbol × timeframe × pack);
/// these pin the pure translation into a RunPlan + per-(symbol,tf) execution passes, including the
/// load-bearing case the per-pass config exists for — the SAME strategy carrying DIFFERENT packs on
/// different rows.
/// </summary>
public sealed class RunPlanBuilderTests
{
    private static RunRowRequest Row(string sid, string sym, string tf, string? pack = null, bool enabled = true)
        => new() { StrategyId = sid, Symbol = sym, Timeframe = tf, PackId = pack, Enabled = enabled };

    [Fact]
    public void FromRows_keeps_only_enabled_rows_and_normalises_case()
    {
        var plan = RunPlanBuilder.FromRows(new[]
        {
            Row("trend-breakout", "eurusd", "h1", pack: "aggressive"),
            Row("super-trend", "GBPUSD", "H4", enabled: false),   // dropped
            Row("", "EURUSD", "H1"),                              // dropped (blank strategy)
        });

        plan.Entries.Should().ContainSingle();
        var e = plan.Entries[0];
        e.StrategyId.Should().Be("trend-breakout");
        e.Symbol.Should().Be("EURUSD");
        e.Timeframe.Should().Be("H1");
        e.PackId.Should().Be("aggressive");
    }

    [Fact]
    public void FromRows_treats_blank_pack_as_null_and_dedupes()
    {
        var plan = RunPlanBuilder.FromRows(new[]
        {
            Row("trend-breakout", "EURUSD", "H1", pack: "  "),
            Row("trend-breakout", "EURUSD", "H1", pack: null),   // duplicate of the above (both null pack)
        });

        plan.Entries.Should().ContainSingle();
        plan.Entries[0].PackId.Should().BeNull();
    }

    [Fact]
    public void IntoPasses_groups_by_symbol_and_timeframe_with_per_strategy_packs()
    {
        var plan = RunPlanBuilder.FromRows(new[]
        {
            Row("trend-breakout", "EURUSD", "H1", pack: "packA"),
            Row("super-trend", "EURUSD", "H1", pack: "packB"),
            Row("trend-breakout", "GBPUSD", "H1", pack: "packC"),
        });

        var passes = RunPlanBuilder.IntoPasses(plan);

        passes.Should().HaveCount(2); // (EURUSD,H1) and (GBPUSD,H1)

        var eur = passes.Single(p => p.Symbol == "EURUSD" && p.Timeframe == "H1");
        eur.StrategyPacks.Should().HaveCount(2);
        eur.StrategyPacks["trend-breakout"].Should().Be("packA");
        eur.StrategyPacks["super-trend"].Should().Be("packB");

        var gbp = passes.Single(p => p.Symbol == "GBPUSD" && p.Timeframe == "H1");
        gbp.StrategyPacks.Should().ContainSingle();
        gbp.StrategyPacks["trend-breakout"].Should().Be("packC"); // SAME strategy, DIFFERENT pack per pass
    }

    [Fact]
    public void IntoPasses_isolates_packs_so_one_strategy_can_differ_across_passes()
    {
        var plan = RunPlanBuilder.FromRows(new[]
        {
            Row("trend-breakout", "EURUSD", "H1", pack: "tight"),
            Row("trend-breakout", "EURUSD", "H4", pack: "wide"),
        });

        var passes = RunPlanBuilder.IntoPasses(plan);

        passes.Single(p => p.Timeframe == "H1").StrategyPacks["trend-breakout"].Should().Be("tight");
        passes.Single(p => p.Timeframe == "H4").StrategyPacks["trend-breakout"].Should().Be("wide");
    }
}
