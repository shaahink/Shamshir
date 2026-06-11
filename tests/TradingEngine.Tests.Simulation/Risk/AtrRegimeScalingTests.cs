namespace TradingEngine.Tests.Simulation.Risk;

[Trait("Category", "Simulation")]
public sealed class AtrRegimeScalingTests
{
    [Fact]
    public void HighAtrRegime_ReducesLotSize()
    {
        var mod = new AtrRegimeSizeModifier();
        var ctx = new SizeModifierContext
        {
            Equity = new EquitySnapshot(DateTime.UtcNow, 100_000, 0, 100_000, 100_000, 100_000, 0, 0, EngineMode.Backtest),
            Profile = new RiskProfile("s", "S", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo")
            {
                SizeModifiers = new SizeModifierOptions
                {
                    AtrRegime = new AtrScalingOptions { Enabled = true, HighAtrMultiple = 1.5, HighAtrSizeScale = 0.7 }
                }
            },
            Intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null, new Price(1.08m), new Price(1.09m), "t", "s", "r", DateTime.UtcNow),
            CurrentAtr = 0.0020,
            AtrBaseline = [0.0010]
        };

        mod.ComputeScale(ctx).Should().Be(0.7);
    }
}
