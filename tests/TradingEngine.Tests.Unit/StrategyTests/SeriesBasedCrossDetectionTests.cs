using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Strategies.BollingerSqueeze;
using TradingEngine.Strategies.MacdMomentum;
using TradingEngine.Strategies.MtfTrend;
using TradingEngine.Strategies.SuperTrend;

namespace TradingEngine.Tests.Unit.StrategyTests;

/// <summary>
/// P2.1 gate: proves the 4 strategies ported from a private "previous value" field to
/// MarketContext.IndicatorSeries still detect the same cross/flip they did before — a refactor with no
/// intended behavior change needs a positive assertion, not just "doesn't throw". Hand-constructs the
/// IndicatorValues/IndicatorSeries directly (bypassing Skender) so each fixture is a worked, verifiable
/// example rather than something fished out of random trending data.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class SeriesBasedCrossDetectionTests
{
    private static readonly ISymbolInfoRegistry Registry = BuildRegistry();

    private static ISymbolInfoRegistry BuildRegistry()
    {
        var reg = new SymbolInfoRegistry();
        reg.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return reg;
    }

    private static List<Bar> FlatBars(int count, decimal close, Timeframe tf = Timeframe.H1)
    {
        var bars = new List<Bar>();
        var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            bars.Add(new Bar(Symbol.Parse("EURUSD"), tf, time, close, close + 0.0005m, close - 0.0005m, close, 100));
            time = time.AddHours(1);
        }
        return bars;
    }

    [Fact]
    public void MacdMomentum_FiresLong_OnHistogramCrossAboveZero_WithPriceAboveSma()
    {
        var strategy = new MacdMomentumStrategy(new MacdMomentumConfig(), Registry, NullLogger<MacdMomentumStrategy>.Instance);
        var bars = FlatBars(strategy.RequiredBarCount, 1.0900m);
        bars[^1] = bars[^1] with { Close = 1.0900m };

        var values = new Dictionary<string, double>
        {
            ["SMA_200"] = 1.0800, // price above SMA
            ["ADX_14"] = 25,      // above AdxMinThreshold(20)
            ["ATR_14"] = 0.0010,
        };
        var series = new Dictionary<string, IReadOnlyList<double>>
        {
            ["MACD_12_26_9_Histogram"] = [-0.5, 0.3], // prev negative, curr positive: crosses above zero
        };
        var context = new MarketContext(Symbol.Parse("EURUSD"),
            new Tick(Symbol.Parse("EURUSD"), 1.0900m, 1.0902m, DateTime.UtcNow),
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            values, DateTime.UtcNow, series);

        var intent = strategy.Evaluate(context);

        intent.Should().NotBeNull("histogram crossed above zero with price above SMA200");
        intent!.Direction.Should().Be(TradeDirection.Long);
    }

    [Fact]
    public void MacdMomentum_DoesNotFire_WhenSeriesHasFewerThanTwoPoints()
    {
        var strategy = new MacdMomentumStrategy(new MacdMomentumConfig(), Registry, NullLogger<MacdMomentumStrategy>.Instance);
        var bars = FlatBars(strategy.RequiredBarCount, 1.0900m);
        var values = new Dictionary<string, double> { ["SMA_200"] = 1.0800, ["ADX_14"] = 25, ["ATR_14"] = 0.0010 };
        var series = new Dictionary<string, IReadOnlyList<double>> { ["MACD_12_26_9_Histogram"] = [0.3] };
        var context = new MarketContext(Symbol.Parse("EURUSD"),
            new Tick(Symbol.Parse("EURUSD"), 1.0900m, 1.0902m, DateTime.UtcNow),
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            values, DateTime.UtcNow, series);

        strategy.Evaluate(context).Should().BeNull("a single-point series has no 'previous' value to cross from yet");
    }

    [Fact]
    public void SuperTrend_FiresLong_OnDirectionFlipFromBearishToBullish()
    {
        var strategy = new SuperTrendStrategy(new SuperTrendConfig(), Registry, NullLogger<SuperTrendStrategy>.Instance);
        var bars = FlatBars(strategy.RequiredBarCount, 1.0900m);

        var values = new Dictionary<string, double>
        {
            ["ST_10_3"] = 1.0850,
            ["ADX_14"] = 25,
            ["ATR_14"] = 0.0010,
        };
        var series = new Dictionary<string, IReadOnlyList<double>>
        {
            ["ST_10_3_Direction"] = [-1, -1, 1], // was bearish for a while, just flipped bullish
        };
        var context = new MarketContext(Symbol.Parse("EURUSD"),
            new Tick(Symbol.Parse("EURUSD"), 1.0900m, 1.0902m, DateTime.UtcNow),
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            values, DateTime.UtcNow, series);

        var intent = strategy.Evaluate(context);

        intent.Should().NotBeNull("direction flipped from -1 to 1");
        intent!.Direction.Should().Be(TradeDirection.Long);
    }

    [Fact]
    public void SuperTrend_SkipsInvalidWarmupReadings_WhenScanningForPreviousDirection()
    {
        var strategy = new SuperTrendStrategy(new SuperTrendConfig(), Registry, NullLogger<SuperTrendStrategy>.Instance);
        var bars = FlatBars(strategy.RequiredBarCount, 1.0900m);

        var values = new Dictionary<string, double> { ["ST_10_3"] = 1.0850, ["ADX_14"] = 25, ["ATR_14"] = 0.0010 };
        // 0 = invalid/warmup reading (Skender emits this before the indicator stabilizes); the last valid
        // reading before the current one is -1, two positions back.
        var series = new Dictionary<string, IReadOnlyList<double>>
        {
            ["ST_10_3_Direction"] = [-1, 0, 1],
        };
        var context = new MarketContext(Symbol.Parse("EURUSD"),
            new Tick(Symbol.Parse("EURUSD"), 1.0900m, 1.0902m, DateTime.UtcNow),
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            values, DateTime.UtcNow, series);

        var intent = strategy.Evaluate(context);

        intent.Should().NotBeNull("the strategy must scan back past the invalid 0 reading to find the last valid direction (-1)");
        intent!.Direction.Should().Be(TradeDirection.Long);
    }

    [Fact]
    public void MtfTrend_FiresLong_OnRsiCrossAboveBullishPullback_WhenH4Bullish()
    {
        var strategy = new MtfTrendStrategy(new MtfTrendConfig(), Registry, NullLogger<MtfTrendStrategy>.Instance);
        var bars = FlatBars(strategy.RequiredBarCount, 1.0900m);

        var values = new Dictionary<string, double>
        {
            ["EMA_200"] = 1.0800, // H4 EMA below close => H4 bullish
            ["ATR_14"] = 0.0010,
        };
        var series = new Dictionary<string, IReadOnlyList<double>>
        {
            ["RSI_14"] = [40, 46], // crosses above RsiBullishPullback(45)
        };
        var context = new MarketContext(Symbol.Parse("EURUSD"),
            new Tick(Symbol.Parse("EURUSD"), 1.0900m, 1.0902m, DateTime.UtcNow),
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            values, DateTime.UtcNow, series);

        var intent = strategy.Evaluate(context);

        intent.Should().NotBeNull("H4 bullish and RSI crossed above the bullish pullback threshold");
        intent!.Direction.Should().Be(TradeDirection.Long);
    }

    [Fact]
    public void BollingerSqueeze_FiresLong_OnSqueezeThenBreakoutAboveUpperBand()
    {
        var strategy = new BollingerSqueezeStrategy(new BollingerSqueezeConfig(), Registry, NullLogger<BollingerSqueezeStrategy>.Instance);
        var bars = FlatBars(strategy.RequiredBarCount, 1.0100m);

        var values = new Dictionary<string, double>
        {
            ["BB_20_2"] = 1.0000,
            ["BB_20_2_Upper"] = 1.0050,
            ["BB_20_2_Lower"] = 0.9950,
            ["ATR_14"] = 0.0010,
        };

        // 10 prior bars with a WIDE band (width 0.10), then the current bar's band has contracted to 0.01 —
        // well below 0.8x the prior average (0.08) — arming the squeeze latch on this same bar; the current
        // close (1.0100, set on the last bar above) breaks above the now-narrow upper band (1.0050).
        var upper = Enumerable.Repeat(1.0500, 10).Append(1.0050).ToList();
        var lower = Enumerable.Repeat(0.9500, 10).Append(0.9950).ToList();
        var middle = Enumerable.Repeat(1.0000, 11).ToList();
        var series = new Dictionary<string, IReadOnlyList<double>>
        {
            ["BB_20_2_Upper"] = upper,
            ["BB_20_2_Lower"] = lower,
            ["BB_20_2"] = middle,
        };

        var context = new MarketContext(Symbol.Parse("EURUSD"),
            new Tick(Symbol.Parse("EURUSD"), 1.0100m, 1.0102m, DateTime.UtcNow),
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            values, DateTime.UtcNow, series);

        var intent = strategy.Evaluate(context);

        intent.Should().NotBeNull("bandwidth contracted well below 0.8x its prior average and price broke above the upper band");
        intent!.Direction.Should().Be(TradeDirection.Long);
    }
}
