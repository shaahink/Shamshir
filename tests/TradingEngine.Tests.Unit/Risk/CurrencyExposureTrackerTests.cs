namespace TradingEngine.Tests.Unit.Risk;

[Trait("Category", "Risk")]
public sealed class CurrencyExposureTrackerTests
{
    [Fact]
    public void EurUsd_Plus_GbpUsd_Long_IncreasesUsdExposure()
    {
        var tracker = new CurrencyExposureTracker();
        tracker.Open(Guid.NewGuid(), "EUR", "USD", TradeDirection.Long, 1000);

        var snap = tracker.GetSnapshot();
        snap.NetRiskByCurrency["USD"].Should().BeLessThan(0);
    }

    [Fact]
    public void Close_Decreases_CurrencyExposure()
    {
        var tracker = new CurrencyExposureTracker();
        var id = Guid.NewGuid();
        tracker.Open(id, "EUR", "USD", TradeDirection.Long, 5000);
        tracker.Close(id);

        var snap = tracker.GetSnapshot();
        snap.TotalCorrelatedRisk.Should().Be(0);
    }

    [Fact]
    public void EmptyTracker_ReturnsZeroExposure()
    {
        var tracker = new CurrencyExposureTracker();
        var snap = tracker.GetSnapshot();
        snap.TotalCorrelatedRisk.Should().Be(0);
    }

    [Fact]
    public void WouldExceedLimit_ReturnsTrue_WhenCorrelatedExposureTooHigh()
    {
        var tracker = new CurrencyExposureTracker();
        tracker.Open(Guid.NewGuid(), "EUR", "USD", TradeDirection.Long, 4000);

        var result = tracker.WouldExceedLimit("GBP", "USD", TradeDirection.Long, 5000, 0.05, 100_000);
        result.Should().BeTrue();
    }

    [Fact]
    public void WouldExceedLimit_ReturnsFalse_WhenBelowLimit()
    {
        var tracker = new CurrencyExposureTracker();
        tracker.Open(Guid.NewGuid(), "EUR", "USD", TradeDirection.Long, 1000);

        var result = tracker.WouldExceedLimit("GBP", "USD", TradeDirection.Long, 1000, 0.05, 100_000);
        result.Should().BeFalse();
    }
}
