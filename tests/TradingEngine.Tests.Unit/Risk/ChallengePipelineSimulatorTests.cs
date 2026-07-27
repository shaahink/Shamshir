namespace TradingEngine.Tests.Unit.Risk;

/// <summary>
/// iter-pass-economics E1 — pins the pipeline walker's arithmetic on degenerate streams where
/// every quantity is hand-computable (a constant stream resamples to itself, so the MC is
/// deterministic regardless of seed), and pins seed determinism on a noisy stream.
/// </summary>
[Trait("Category", "Risk")]
public sealed class ChallengePipelineSimulatorTests
{
    private static readonly PropFirmRuleSet SwingP1 = new(
        "ftmo-swing", "FTMO Swing", "Fixed",
        0.05, 0.10, 0.10, 4,
        "BalancePlusFloatingMinusFeesAndSwaps", "00:00:00", "Europe/Prague",
        true, "High", 0, 0, true, "21:00:00", "20:00:00", "NextTradingDay", false);

    private static readonly PropFirmRuleSet SwingP2 = SwingP1 with { Id = "ftmo-verification", ProfitTargetPercent = 0.05 };

    private static readonly PropFirmRuleSet OneStep = new(
        "ftmo-1step", "FTMO 1-Step", ChallengeSimulator.TrailingEodDrawdownType,
        0.03, 0.10, 0.10, 0,
        "BalancePlusFloatingMinusFeesAndSwaps", "00:00:00", "Europe/Prague",
        true, "High", 0, 0, true, "21:00:00", "20:00:00", "NextTradingDay", false)
    { BestDayMaxShare = 0.5 };

    private static ChallengePipelineSpec Swing2Step(int maxAttempts = 1, int? stopAfter = null) => new(
        "swing-2step", [SwingP1, SwingP2], SwingP1, 540m, 0.80, true, maxAttempts, stopAfter);

    private static ChallengePipelineSpec Standard1Step(int maxAttempts = 1, int? stopAfter = null) => new(
        "standard-1step", [OneStep], OneStep, 499m, 0.90, true, maxAttempts, stopAfter);

    private static ChallengePipelineMcOptions FastOptions(int reps = 8) => new()
    {
        Replicates = reps,
        PathTradingDays = 600,
        MeanBlockTradingDays = 10,
        Seed = 20260727,
    };

    private static List<PipelineDay> Constant(decimal pnl, int days = 100) =>
        Enumerable.Repeat(new PipelineDay(pnl, 1), days).ToList();

    [Fact]
    public void ConstantWinningStream_Swing2Step_PipelineArithmeticIsExact()
    {
        // +500/day: P1 passes day 20, P2 day 10 (fresh 100k each), funded pays every 21 days:
        // profit 10,500 x 80% = 8,400; 520-day horizon -> 24 payouts; refund cancels the fee.
        var result = ChallengePipelineSimulator.Run(Constant(500m), Swing2Step(), FastOptions());

        result.PFunded.Should().Be(1.0);
        result.FirstAttemptPassRate.Should().Be(1.0);
        result.PhasePassRates.Should().Equal(1.0, 1.0);
        result.MeanTradingDaysToFunded.Should().Be(30);
        result.MeanPayoutCount.Should().Be(24);
        result.PFundedBust.Should().Be(0);
        result.MeanPipelineNet.Should().Be(24 * 8_400m);
        result.CensoredReplicates.Should().Be(0);
        result.PipelineNetSd.Should().Be(0);
    }

    [Fact]
    public void ConstantWinningStream_Standard1Step_PipelineArithmeticIsExact()
    {
        // Single phase (no min trading days; +500 equal days keep Best Day at 1/N of positives):
        // pass day 20; funded pays 90% of 10,500 = 9,450 x 24 payouts; refund cancels the fee.
        var result = ChallengePipelineSimulator.Run(Constant(500m), Standard1Step(), FastOptions());

        result.PFunded.Should().Be(1.0);
        result.MeanTradingDaysToFunded.Should().Be(20);
        result.MeanPayoutCount.Should().Be(24);
        result.MeanPipelineNet.Should().Be(24 * 9_450m);
    }

    [Fact]
    public void ConstantLosingStream_RetryPolicies_ChargeExactFees()
    {
        // -400/day: every attempt busts on the 10% max loss; no funding ever.
        var stream = Constant(-400m);

        var single = ChallengePipelineSimulator.Run(stream, Swing2Step(maxAttempts: 1), FastOptions());
        single.PFunded.Should().Be(0);
        single.AttemptPassRate.Should().Be(0);
        single.MeanAttempts.Should().Be(1);
        single.MeanPipelineNet.Should().Be(-540m);

        var persistent = ChallengePipelineSimulator.Run(stream, Swing2Step(maxAttempts: 6), FastOptions());
        persistent.MeanAttempts.Should().Be(6);
        persistent.MeanPipelineNet.Should().Be(-6 * 540m);

        var twoStrikes = ChallengePipelineSimulator.Run(
            stream, Swing2Step(maxAttempts: 6, stopAfter: 2), FastOptions());
        twoStrikes.MeanAttempts.Should().Be(2);
        twoStrikes.MeanPipelineNet.Should().Be(-2 * 540m);
    }

    [Fact]
    public void NoisyStream_SameSeed_IsDeterministic()
    {
        // Fixed pseudo-noise (no wall-clock, no Random in test data construction).
        var stream = Enumerable.Range(0, 250)
            .Select(i => new PipelineDay(((i * 7919) % 1601) - 800m, 1 + i % 3))
            .ToList();

        var a = ChallengePipelineSimulator.Run(stream, Swing2Step(maxAttempts: 6, stopAfter: 2), FastOptions(reps: 50));
        var b = ChallengePipelineSimulator.Run(stream, Swing2Step(maxAttempts: 6, stopAfter: 2), FastOptions(reps: 50));

        b.MeanPipelineNet.Should().Be(a.MeanPipelineNet);
        b.PFunded.Should().Be(a.PFunded);
        b.SourceDailyNetCiLow.Should().Be(a.SourceDailyNetCiLow);
        b.SourceDailyNetMde.Should().Be(a.SourceDailyNetMde);
    }

    [Fact]
    public void TwoSidedCompanion_ConstantStream_CiCollapsesToTheMean()
    {
        var result = ChallengePipelineSimulator.Run(Constant(500m), Standard1Step(), FastOptions());

        result.SourceMeanDailyNet.Should().Be(500m);
        result.SourceDailyNetCiLow.Should().Be(500m);
        result.SourceDailyNetCiHigh.Should().Be(500m);
        result.SourceDailyNetMde.Should().Be(0m);
    }
}
