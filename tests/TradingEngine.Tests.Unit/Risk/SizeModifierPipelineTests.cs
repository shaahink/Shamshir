namespace TradingEngine.Tests.Unit.Risk;

[Trait("Category", "Risk")]
public sealed class SizeModifierPipelineTests
{
    private static SizeModifierPipeline Make(IEnumerable<ISizeModifier> modifiers, SizeModifierOptions? opts = null)
    {
        var logger = Substitute.For<ILogger<SizeModifierPipeline>>();
        return new SizeModifierPipeline(modifiers, opts ?? new SizeModifierOptions(), logger);
    }

    private static SizeModifierContext MakeContext(EquitySnapshot? equity = null, RiskProfile? profile = null)
    {
        return new SizeModifierContext
        {
            Equity = equity ?? new EquitySnapshot(DateTime.UtcNow, 100_000, 0, 100_000, 100_000, 100_000, 0, 0, EngineMode.Backtest),
            Profile = profile ?? new RiskProfile("s", "S", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo"),
            Intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null, new Price(1.08000m), new Price(1.08500m), "test", "standard", "test", DateTime.UtcNow),
        };
    }

    [Fact]
    public void Pipeline_MultipliesEnabledModifiers_AndClamps()
    {
        var ctx = MakeContext();
        var m1 = Substitute.For<ISizeModifier>();
        m1.Name.Returns("M1");
        m1.ComputeScale(Arg.Any<SizeModifierContext>()).Returns(0.8);
        var m2 = Substitute.For<ISizeModifier>();
        m2.Name.Returns("M2");
        m2.ComputeScale(Arg.Any<SizeModifierContext>()).Returns(1.2);

        var pipeline = Make([m1, m2]);
        var result = pipeline.ComputeCombinedScale(ctx);
        result.Should().BeApproximately(0.96, 0.01);
    }

    [Fact]
    public void Pipeline_Clamps_ToMaxCombinedScale()
    {
        var ctx = MakeContext();
        var m = Substitute.For<ISizeModifier>();
        m.Name.Returns("M");
        m.ComputeScale(Arg.Any<SizeModifierContext>()).Returns(10.0);

        var pipeline = Make([m], new SizeModifierOptions { MaxCombinedScale = 1.5 });
        var result = pipeline.ComputeCombinedScale(ctx);
        result.Should().Be(1.5);
    }

    [Fact]
    public void Pipeline_Clamps_ToMinCombinedScale()
    {
        var ctx = MakeContext();
        var m = Substitute.For<ISizeModifier>();
        m.Name.Returns("M");
        m.ComputeScale(Arg.Any<SizeModifierContext>()).Returns(0.01);

        var pipeline = Make([m], new SizeModifierOptions { MinCombinedScale = 0.1 });
        var result = pipeline.ComputeCombinedScale(ctx);
        result.Should().Be(0.1);
    }

    [Fact]
    public void Pipeline_DrawdownOnly_MatchesLegacyDrawdownScalerOutput()
    {
        var equity = new EquitySnapshot(DateTime.UtcNow, 100_000, 0, 92_000, 100_000, 100_000, 0.05m, 0.08m, EngineMode.Backtest);
        var profile = new RiskProfile("s", "S", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo");
        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null, new Price(1.08000m), new Price(1.08500m), "test", "standard", "test", DateTime.UtcNow);
        var ctx = new SizeModifierContext { Equity = equity, Profile = profile, Intent = intent };

        var ddMod = new DrawdownSizeModifier();
        var pipeline = Make([ddMod]);

        var expected = (double)DrawdownScaler.ComputeScaleFactor(0.08m, (decimal)profile.MaxTotalDrawdownPercent, profile.DrawdownScaleThreshold, profile.DrawdownScaleFloor);
        var actual = pipeline.ComputeCombinedScale(ctx);
        actual.Should().BeApproximately(expected, 0.0001);
    }

    [Fact]
    public void AtrModifier_Disabled_ReturnsOne()
    {
        var ctx = MakeContext();
        var mod = new AtrRegimeSizeModifier();
        mod.ComputeScale(ctx).Should().Be(1.0);
    }

    [Fact]
    public void AtrModifier_HighAtr_ScalesDown()
    {
        var profile = new RiskProfile("s", "S", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo")
        {
            SizeModifiers = new SizeModifierOptions
            {
                AtrRegime = new AtrScalingOptions { Enabled = true, HighAtrMultiple = 1.5, HighAtrSizeScale = 0.7 },
            },
        };
        var ctx = MakeContext(profile: profile) with { CurrentAtr = 0.0020, AtrBaseline = [0.0010] };
        var mod = new AtrRegimeSizeModifier();
        mod.ComputeScale(ctx).Should().Be(0.7);
    }

    [Fact]
    public void ConfidenceModifier_LossStreak_ReducesSize()
    {
        var profile = new RiskProfile("s", "S", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo")
        {
            SizeModifiers = new SizeModifierOptions
            {
                Confidence = new ConfidenceScalingOptions { Enabled = true, LossStreakThreshold = 3, LossStreakScale = 0.5 },
            },
        };
        var ctx = MakeContext(profile: profile) with { StrategyLossStreak = 3 };
        var mod = new ConfidenceSizeModifier();
        mod.ComputeScale(ctx).Should().Be(0.5);
    }

    [Fact]
    public void TimeOfDayModifier_InsideWindow_AppliesScale()
    {
        var profile = new RiskProfile("s", "S", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo")
        {
            SizeModifiers = new SizeModifierOptions
            {
                TimeOfDay = new TimeOfDayScalingOptions
                {
                    Enabled = true,
                    Windows = [new TimeOfDayScaleWindow(new TimeSpan(20, 0, 0), new TimeSpan(22, 0, 0), 0.5)],
                },
            },
        };
        var ctx = MakeContext(profile: profile) with { UtcTimeOfDay = new TimeSpan(21, 0, 0) };
        var mod = new TimeOfDaySizeModifier();
        mod.ComputeScale(ctx).Should().Be(0.5);
    }
}
