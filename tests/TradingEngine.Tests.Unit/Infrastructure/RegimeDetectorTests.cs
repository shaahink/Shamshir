using TradingEngine.Infrastructure.Indicators;

namespace TradingEngine.Tests.Unit.Infrastructure;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class RegimeDetectorTests
{
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");

    private static AtrBasedRegimeDetector NewDetector()
        => new(new SkenderIndicatorService(), new RegimeOptions());

    // A clean, strong uptrend (trend >> noise) should classify as Trending. This is the case the old
    // detector got wrong: it read symbol-prefixed keys that were never populated and depended on a
    // strategy requesting ADX, so it returned Unknown on every bar.
    [Fact]
    public void Strong_clean_uptrend_is_Trending()
    {
        var bars = Trend(160, step: 0.0010m, noise: 0.0001m);
        var regime = NewDetector().Detect(Eur, bars, new Dictionary<string, double>());
        regime.Should().Be(MarketRegime.Trending);
    }

    // A tight oscillation with no directional bias should NOT be classified Trending.
    [Fact]
    public void Flat_oscillation_is_not_Trending()
    {
        var bars = Oscillate(160, mid: 1.1000m, amplitude: 0.0003m);
        var regime = NewDetector().Detect(Eur, bars, new Dictionary<string, double>());
        regime.Should().NotBe(MarketRegime.Trending);
    }

    [Fact]
    public void Too_few_bars_is_Unknown()
    {
        var bars = Trend(40, step: 0.0010m, noise: 0.0001m);
        NewDetector().Detect(Eur, bars, new Dictionary<string, double>())
            .Should().Be(MarketRegime.Unknown);
    }

    private static List<Bar> Trend(int count, decimal step, decimal noise)
    {
        var rng = new Random(7);
        var bars = new List<Bar>();
        var price = 1.1000m;
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
        {
            var open = price;
            var jitter = (decimal)((rng.NextDouble() - 0.5) * 2.0) * noise;
            var close = price + step + jitter;
            var high = Math.Max(open, close) + (decimal)rng.NextDouble() * noise;
            var low = Math.Min(open, close) - (decimal)rng.NextDouble() * noise;
            bars.Add(new Bar(Eur, Timeframe.H1, t, open, high, low, close, 1000));
            price = close;
            t = t.AddHours(1);
        }
        return bars;
    }

    private static List<Bar> Oscillate(int count, decimal mid, decimal amplitude)
    {
        var bars = new List<Bar>();
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
        {
            var phase = (decimal)Math.Sin(i * Math.PI / 6.0);
            var close = mid + phase * amplitude;
            var open = mid + (decimal)Math.Sin((i - 1) * Math.PI / 6.0) * amplitude;
            var high = Math.Max(open, close) + amplitude * 0.1m;
            var low = Math.Min(open, close) - amplitude * 0.1m;
            bars.Add(new Bar(Eur, Timeframe.H1, t, open, high, low, close, 1000));
            t = t.AddHours(1);
        }
        return bars;
    }
}
