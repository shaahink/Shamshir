namespace TradingEngine.Tests.Simulation.Risk;

[Trait("Category", "Simulation")]
public sealed class CurrencyExposureTests
{
    [Fact]
    public void LongEurUsd_Plus_LongGbpUsd_Exceeds_UsdLimit()
    {
        var tracker = new CurrencyExposureTracker();
        tracker.Open(Guid.NewGuid(), "EUR", "USD", TradeDirection.Long, 4000);
        var result = tracker.WouldExceedLimit("GBP", "USD", TradeDirection.Long, 4000, 0.05, 100_000);
        result.Should().BeTrue();
    }

    [Fact]
    public void LongEurUsd_Plus_LongUsdJpy_OffsettingUsdDirection_DoesNotExceed()
    {
        var tracker = new CurrencyExposureTracker();
        tracker.Open(Guid.NewGuid(), "EUR", "USD", TradeDirection.Long, 3000);
        // LONG USDJPY = long USD, short JPY => USD direction opposite to short-USD of LONG EURUSD
        var result = tracker.WouldExceedLimit("USD", "JPY", TradeDirection.Long, 3000, 0.05, 100_000);
        // USD: -3000 (from EURUSD) + 3000 (from USDJPY) = 0 => does not exceed
        result.Should().BeFalse();
    }
}
