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
    public void LongEurUsd_Plus_ShortUsdJpy_DoesNotExceed()
    {
        var tracker = new CurrencyExposureTracker();
        tracker.Open(Guid.NewGuid(), "EUR", "USD", TradeDirection.Long, 3000);
        var result = tracker.WouldExceedLimit("USD", "JPY", TradeDirection.Short, 3000, 0.05, 100_000);
        // Opposite USD direction partially offsets
        result.Should().BeFalse();
    }
}
