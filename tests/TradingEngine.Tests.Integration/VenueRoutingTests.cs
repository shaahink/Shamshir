using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration;

/// <summary>
/// iter-38 D6 / P0-B1: the default dev backtest venue is now the credential-free REPLAY path, and cTrader is
/// EXPLICIT opt-in only. Before this, a no-venue run fell through to <c>CTrader:UseForBacktest</c> (true in
/// dev), silently routing default runs through the wall-clock-buggy in-process cTrader path (the root of
/// T1/T2/T6/T7/T11/T12). These assertions pin the new routing so it can't regress.
/// </summary>
[Trait("Category", "VenueRouting")]
public sealed class VenueRoutingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("replay")]
    [InlineData("sim")]
    [InlineData("simulated")]
    [InlineData("REPLAY")]
    [InlineData("nonsense")]
    public void Default_unknown_and_replay_route_to_replay(string? venue)
        => BacktestOrchestrator.ResolveUseCtrader(venue).Should().BeFalse();

    [Theory]
    [InlineData("ctrader")]
    [InlineData("CTrader")]
    [InlineData("CTRADER")]
    public void Ctrader_is_explicit_opt_in(string venue)
        => BacktestOrchestrator.ResolveUseCtrader(venue).Should().BeTrue();
}
