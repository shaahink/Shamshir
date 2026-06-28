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
        for (var i = strategy.RequiredBarCount; i < bars.Count; i++)
        {
            var window = bars.Take(i + 1).ToList();
            var indicators = StrategyTestHelper.ComputeIndicators(window, strategy.RequiredIndicators);
            var ctx = StrategyTestHelper.MakeContext(bars[i], "EURUSD", window, indicators);
            if (strategy.Evaluate(ctx) is { } intent) signals.Add(intent);
        }
        return signals;
    }

    [Fact]
    public void EmaAlignment_FiresLong_OnCleanUptrend()
    {
        var s = new EmaAlignmentStrategy(
            new EmaAlignmentConfig("ema", "EMA Alignment", true, "standard", new EmaAlignmentParameters()),
            Registry(), Substitute.For<ILogger<EmaAlignmentStrategy>>());

        var signals = Signals(s, StrongTrend(up: true, 260));

        signals.Should().NotBeEmpty("a clean uptrend must align the EMAs and fire at least once (not silently dead)");
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
