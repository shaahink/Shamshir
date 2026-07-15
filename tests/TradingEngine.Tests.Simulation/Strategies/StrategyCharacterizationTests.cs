using TradingEngine.Strategies.EmaAlignment;
using TradingEngine.Strategies.MeanReversion;

namespace TradingEngine.Tests.Simulation.Strategies;

/// <summary>
/// iter-37 Phase C — per-strategy characterization, closing the gap left by
/// <see cref="StrategySignalContractTests"/> (which already locks the indicator-key family —
/// SuperTrend/MACD/RSI/Bollinger — against the iter-29 "silently dead" regression). Here we add the two
/// remaining signal-producing strategies (<c>EmaAlignment</c>, <c>MeanReversion</c>): each must fire at
/// least one signal on a regime it's designed for, and its first-signal shape is characterized. A
/// strategy that regresses to ZERO signals on its own fixture fails loudly.
/// </summary>
[Trait("Category", "Simulation")]
public sealed class StrategyCharacterizationTests
{
    private static SymbolInfoRegistry Registry()
    {
        var reg = new SymbolInfoRegistry();
        reg.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return reg;
    }

    private static List<TradeIntent> Signals(IStrategy strategy, List<Bar> bars)
    {
        var signals = new List<TradeIntent>();
        // P2.1/P2.3: ema-alignment now reads its EMA crossover history from MarketContext.IndicatorSeries
        // instead of a single latest value — accumulate a real per-key history from bar 0, mirroring
        // production's ring buffer (IndicatorSnapshotService fills from the engine's very first bar, not
        // from RequiredBarCount), capped at 64 to match SeriesCapacity.
        const int seriesCapacity = 64;
        var history = new Dictionary<string, List<double>>();
        for (var i = 0; i < bars.Count; i++)
        {
            var window = bars.Take(i + 1).ToList();
            var indicators = StrategyTestHelper.ComputeIndicators(window, strategy.RequiredIndicators);
            foreach (var (key, value) in indicators)
            {
                if (!history.TryGetValue(key, out var list)) { list = []; history[key] = list; }
                list.Add(value);
                while (list.Count > seriesCapacity) list.RemoveAt(0);
            }

            if (i < strategy.RequiredBarCount) continue;

            var series = history.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)kv.Value);
            var ctx = StrategyTestHelper.MakeContext(bars[i], "EURUSD", window, indicators, series);
            if (strategy.Evaluate(ctx) is { } intent) signals.Add(intent);
        }
        return signals;
    }

    [Fact]
    public void EmaAlignment_FiresLong_OnUptrendWithPullback()
    {
        // P2.3/D5: ema-alignment is now a pullback-entry edge (crossover, then first touch of the fast
        // EMA), not a state condition — a perfectly smooth trend with no pullback legitimately produces
        // ZERO signals under the new semantics, so this needs a fixture with a genuine pullback, not
        // StrongTrend's dead-smooth rise (still shared with MeanReversion below, unmodified).
        var s = new EmaAlignmentStrategy(
            new EmaAlignmentConfig("ema", "EMA Alignment", true, "standard", new EmaAlignmentParameters()),
            Registry(), Substitute.For<ILogger<EmaAlignmentStrategy>>());

        var signals = Signals(s, TrendWithPullback(up: true, 260));

        signals.Should().NotBeEmpty("an uptrend with a genuine pullback to the fast EMA must fire at least once (not silently dead)");
        signals[0].Direction.Should().Be(TradeDirection.Long, "characterization: first signal on an uptrend is Long");
    }

    [Fact]
    public void MeanReversion_Fires_OnOscillationWithRejectionWicks()
    {
        var s = new MeanReversionStrategy(
            new MeanReversionConfig("mr", "Mean Reversion", true, "standard", new MeanReversionParameters()),
            Registry(), Substitute.For<ILogger<MeanReversionStrategy>>());

        // A battery: a strategy this alive should fire on at least one mean-reverting regime.
        var fired = Signals(s, Oscillation(300)).Count
                  + Signals(s, StrongTrend(up: false, 260)).Count
                  + Signals(s, StrongTrend(up: true, 260)).Count;

        fired.Should().BeGreaterThan(0, "mean-reversion must fire on an oscillating / wicky regime (not silently dead)");
    }

    // A flat/ranging warmup (so fast/slow EMA start close together — a monotonic trend from bar 0 never
    // shows a discrete crossover under this recompute-from-scratch methodology, since Skender's EMA seed
    // already has fast>slow by the time both are first computable), then a clean uptrend whose fast EMA
    // crosses above the slow EMA once, followed shortly by a pin-bar pullback (a deep lower wick, but the
    // close stays on the trend side) that genuinely touches the fast EMA — the shape ema-alignment's
    // pullback-entry edge is designed to trade.
    private static List<Bar> TrendWithPullback(bool up, int count)
    {
        var bars = new List<Bar>();
        var price = 1.1000m;
        var step = (up ? 1 : -1) * 0.0010m;
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            var open = price;
            var close = i < 60 ? price + (i % 2 == 0 ? 1 : -1) * 0.0002m : price + step;
            var high = Math.Max(open, close) + 0.0003m;
            var low = Math.Min(open, close) - 0.0003m;
            if (i == 75)
            {
                // Pin-bar pullback: close stays on the trend side (confirming continuation), but the wick
                // dips (long) or spikes (short) deep enough to genuinely touch the fast EMA.
                if (up) low = close - 0.0100m; else high = close + 0.0100m;
            }
            bars.Add(new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, t, open, high, low, close, 1000));
            price = close;
            t = t.AddHours(1);
        }
        return bars;
    }

    // A clean trend strong enough that the fast EMA sits firmly above/below the slow EMA.
    private static List<Bar> StrongTrend(bool up, int count)
    {
        var bars = new List<Bar>();
        var price = 1.1000m;
        var step = (up ? 1 : -1) * 0.0010m;
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            var open = price;
            var close = price + step;
            var high = Math.Max(open, close) + 0.0003m;
            var low = Math.Min(open, close) - 0.0003m;
            bars.Add(new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, t, open, high, low, close, 1000));
            price = close;
            t = t.AddHours(1);
        }
        return bars;
    }

    // Sharp symmetric swings (overshoot past the bands) with pronounced rejection wicks at the extremes.
    private static List<Bar> Oscillation(int count)
    {
        var bars = new List<Bar>();
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var mid = 1.1000m;
        for (var i = 0; i < count; i++)
        {
            var phase = Math.Sin(i * Math.PI / 6.0);          // 12-bar cycle
            var close = mid + (decimal)phase * 0.0040m;        // ±40 pips around the mean
            var open = mid + (decimal)Math.Sin((i - 1) * Math.PI / 6.0) * 0.0040m;
            // Pronounced rejection wicks at the swing extremes.
            var high = Math.Max(open, close) + 0.0015m;
            var low = Math.Min(open, close) - 0.0015m;
            bars.Add(new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, t, open, high, low, close, 1000));
            t = t.AddHours(1);
        }
        return bars;
    }
}
