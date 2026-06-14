using TradingEngine.Infrastructure.Indicators;

namespace TradingEngine.Tests.Unit.Infrastructure;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class IndicatorCacheKeyTests
{
    [Fact]
    public void BuildKey_same_key_different_timeframes_produce_unique_signatures()
    {
        var reqA = new IndicatorRequest("atr", IndicatorType.Atr, 14, Timeframe: Timeframe.H1);
        var reqB = new IndicatorRequest("atr", IndicatorType.Atr, 14, Timeframe: Timeframe.M15);

        var keyA = IndicatorCache.BuildKey(Symbol.Parse("EURUSD"), reqA);
        var keyB = IndicatorCache.BuildKey(Symbol.Parse("EURUSD"), reqB);

        keyA.Should().NotBe(keyB,
            "same Key string with different Timeframes must produce distinct cache keys");
    }

    [Fact]
    public void BuildKey_same_key_different_periods_produce_unique_signatures()
    {
        var reqA = new IndicatorRequest("atr", IndicatorType.Atr, 14);
        var reqB = new IndicatorRequest("atr", IndicatorType.Atr, 50);

        var keyA = IndicatorCache.BuildKey(Symbol.Parse("EURUSD"), reqA);
        var keyB = IndicatorCache.BuildKey(Symbol.Parse("EURUSD"), reqB);

        keyA.Should().NotBe(keyB,
            "same Key string with different Periods must produce distinct cache keys");
    }

    [Fact]
    public void BuildKey_same_key_different_types_produce_unique_signatures()
    {
        var reqA = new IndicatorRequest("atr", IndicatorType.Atr, 14);
        var reqB = new IndicatorRequest("atr", IndicatorType.Ema, 14);

        var keyA = IndicatorCache.BuildKey(Symbol.Parse("EURUSD"), reqA);
        var keyB = IndicatorCache.BuildKey(Symbol.Parse("EURUSD"), reqB);

        keyA.Should().NotBe(keyB,
            "same Key string with different Types must produce distinct cache keys");
    }

    [Fact]
    public void BuildKey_same_key_different_param1_produce_unique_signatures()
    {
        var reqA = new IndicatorRequest("macd", IndicatorType.Macd, 12) { Param1 = 26 };
        var reqB = new IndicatorRequest("macd", IndicatorType.Macd, 12) { Param1 = 30 };

        var keyA = IndicatorCache.BuildKey(Symbol.Parse("EURUSD"), reqA);
        var keyB = IndicatorCache.BuildKey(Symbol.Parse("EURUSD"), reqB);

        keyA.Should().NotBe(keyB,
            "same Key string with different Param1 must produce distinct cache keys");
    }

    [Fact]
    public void BuildKey_identical_requests_produce_same_key()
    {
        var reqA = new IndicatorRequest("atr", IndicatorType.Atr, 14, Timeframe: Timeframe.H1);
        var reqB = new IndicatorRequest("atr", IndicatorType.Atr, 14, Timeframe: Timeframe.H1);

        var keyA = IndicatorCache.BuildKey(Symbol.Parse("EURUSD"), reqA);
        var keyB = IndicatorCache.BuildKey(Symbol.Parse("EURUSD"), reqB);

        keyA.Should().Be(keyB,
            "identical requests must produce the same cache key for dedup");
    }

    [Fact]
    public void Cache_does_not_collide_same_key_different_signatures()
    {
        var cache = new IndicatorCache();
        var symbol = Symbol.Parse("EURUSD");
        var reqA = new IndicatorRequest("atr", IndicatorType.Atr, 14, Timeframe: Timeframe.H1);
        var reqB = new IndicatorRequest("atr", IndicatorType.Atr, 50, Timeframe: Timeframe.M15);

        var keyA = IndicatorCache.BuildKey(symbol, reqA);
        var keyB = IndicatorCache.BuildKey(symbol, reqB);

        cache.Set(keyA, 1.2345);
        cache.Set(keyB, 2.3456);

        cache.Get(keyA).Should().Be(1.2345);
        cache.Get(keyB).Should().Be(2.3456);
    }
}
