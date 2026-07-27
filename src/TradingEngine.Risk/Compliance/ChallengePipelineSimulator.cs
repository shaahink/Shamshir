namespace TradingEngine.Risk.Compliance;

/// <summary>
/// One trading day of a candidate's pooled position stream: net closed PnL (CE(S)T daily
/// bucketing done by the extractor) and the count of trades OPENED that day (V0 trading-day
/// semantics). This is the resampling unit — days, not trades — so intra-day clustering is
/// preserved by construction (PR-E1-1).
/// </summary>
public sealed record PipelineDay(decimal NetPnL, int TradesOpened);

/// <summary>
/// One product pipeline (iter-pass-economics E1, PR-E1-1/PR-E1-2): evaluation phase rule sets
/// in order (2-Step = [P1, P2]; 1-Step = [single]), funded-phase loss rules (no target),
/// fee/split/refund economics, and the retry policy. All dollar semantics at
/// <see cref="ChallengePipelineMcOptions.InitialBalance"/> per phase (FTMO: fresh account each
/// phase; balance resets to initial after each funded payout).
/// </summary>
public sealed record ChallengePipelineSpec(
    string ProductId,
    IReadOnlyList<PropFirmRuleSet> EvaluationPhases,
    PropFirmRuleSet FundedRules,
    decimal AttemptFee,
    double PayoutSplit,
    bool RefundFeeOnFirstPayout,
    int MaxAttempts,
    int? StopAfterConsecutiveFails)
{
    public int PayoutCycleTradingDays { get; init; } = 21;
    public int FundedHorizonTradingDays { get; init; } = 520;
}

public sealed record ChallengePipelineMcOptions
{
    public int Replicates { get; init; } = 2000;
    public int PathTradingDays { get; init; } = 2500;
    public int MeanBlockTradingDays { get; init; } = 10;
    public int Seed { get; init; } = 20260727;
    public decimal InitialBalance { get; init; } = 100_000m;
}

public sealed record ChallengePipelineMcResult(
    string ProductId,
    int Replicates,
    double PFunded,
    double AttemptPassRate,
    double FirstAttemptPassRate,
    IReadOnlyList<double> PhasePassRates,
    double MeanAttempts,
    double? MeanTradingDaysToFunded,
    double? PFundedBust,
    double MeanPayoutCount,
    decimal MeanPipelineNet,
    decimal MedianPipelineNet,
    decimal P5PipelineNet,
    decimal P95PipelineNet,
    decimal PipelineNetSd,
    decimal McStandardError,
    int CensoredReplicates,
    int FundedHorizonTruncated,
    decimal SourceMeanDailyNet,
    decimal SourceDailyNetCiLow,
    decimal SourceDailyNetCiHigh,
    decimal SourceDailyNetMde);

/// <summary>
/// E1's challenge-pipeline-EV objective (PLAN D1): Monte-Carlo over stationary-block-bootstrap
/// resampled trading-day paths, walking fee → evaluation attempt(s) (via the V0-verified
/// <see cref="ChallengeSimulator"/> semantics) → funded phase (payout cycles) under a stated
/// retry policy. Every result carries the source stream's pooled daily-$ bootstrap CI + MDE
/// next to the pipeline EV — two-sided reporting is law (D1); the EV never appears alone.
/// Daily-close granularity: intraday floating troughs are invisible, so breach detection stays
/// OPTIMISTIC until the V6 intraday envelope lands (same caveat as ChallengeSimulator).
/// Deterministic: replicate r uses Random(Seed + r); no wall-clock anywhere.
/// </summary>
public static class ChallengePipelineSimulator
{
    public static ChallengePipelineMcResult Run(
        IReadOnlyList<PipelineDay> source, ChallengePipelineSpec spec, ChallengePipelineMcOptions opt)
    {
        if (source.Count < 2) throw new ArgumentException("Source stream needs at least 2 trading days.", nameof(source));

        var phaseCount = spec.EvaluationPhases.Count;
        var pipelineNets = new decimal[opt.Replicates];
        var fundedCount = 0;
        var fundedBusts = 0;
        var totalAttempts = 0;
        var totalPasses = 0;
        var firstAttemptPasses = 0;
        var phaseStarts = new int[phaseCount];
        var phasePasses = new int[phaseCount];
        var daysToFundedSum = 0L;
        var payoutCountSum = 0L;
        var censored = 0;
        var truncated = 0;

        for (var rep = 0; rep < opt.Replicates; rep++)
        {
            var rng = new Random(opt.Seed + rep);
            var (path, cum) = Resample(source, opt.PathTradingDays, opt.MeanBlockTradingDays, rng);

            var idx = 0;
            var attempts = 0;
            var consecFails = 0;
            var feesPaid = 0m;
            var received = 0m;
            var repCensored = false;

            while (attempts < spec.MaxAttempts)
            {
                attempts++;
                totalAttempts++;
                feesPaid += spec.AttemptFee;

                var passedAll = true;
                for (var p = 0; p < phaseCount; p++)
                {
                    if (idx >= path.Length) { repCensored = true; break; }
                    phaseStarts[p]++;
                    var window = new PathWindow(path, cum, idx, path.Length - idx, opt.InitialBalance);
                    var result = ChallengeSimulator.SimulateWindow(window, spec.EvaluationPhases[p]);
                    if (result.Verdict == ChallengeVerdict.Incomplete) { repCensored = true; break; }
                    idx += result.DayResolved!.Value;
                    if (result.Verdict == ChallengeVerdict.Fail) { passedAll = false; break; }
                    phasePasses[p]++;
                }

                if (repCensored) break;

                if (passedAll)
                {
                    totalPasses++;
                    if (attempts == 1) firstAttemptPasses++;
                    fundedCount++;
                    daysToFundedSum += idx;

                    var funded = WalkFunded(path, ref idx, spec, opt);
                    payoutCountSum += funded.Payouts;
                    received += funded.Paid;
                    if (funded.Bust) fundedBusts++;
                    if (funded.Truncated) truncated++;
                    if (spec.RefundFeeOnFirstPayout && funded.Payouts > 0) received += spec.AttemptFee;
                    break;
                }

                consecFails++;
                if (spec.StopAfterConsecutiveFails is { } k && consecFails >= k) break;
            }

            if (repCensored) censored++;
            pipelineNets[rep] = received - feesPaid;
        }

        var sorted = (decimal[])pipelineNets.Clone();
        Array.Sort(sorted);
        var mean = pipelineNets.Average();
        var sd = StdDev(pipelineNets, mean);
        var (srcMean, ciLo, ciHi, mde) = DailyMeanBootstrap(source, opt);

        return new ChallengePipelineMcResult(
            ProductId: spec.ProductId,
            Replicates: opt.Replicates,
            PFunded: fundedCount / (double)opt.Replicates,
            AttemptPassRate: totalAttempts > 0 ? totalPasses / (double)totalAttempts : 0,
            FirstAttemptPassRate: firstAttemptPasses / (double)opt.Replicates,
            PhasePassRates: Enumerable.Range(0, phaseCount)
                .Select(p => phaseStarts[p] > 0 ? phasePasses[p] / (double)phaseStarts[p] : 0.0).ToList(),
            MeanAttempts: totalAttempts / (double)opt.Replicates,
            MeanTradingDaysToFunded: fundedCount > 0 ? daysToFundedSum / (double)fundedCount : null,
            PFundedBust: fundedCount > 0 ? fundedBusts / (double)fundedCount : null,
            MeanPayoutCount: fundedCount > 0 ? payoutCountSum / (double)opt.Replicates : 0,
            MeanPipelineNet: mean,
            MedianPipelineNet: Percentile(sorted, 0.50),
            P5PipelineNet: Percentile(sorted, 0.05),
            P95PipelineNet: Percentile(sorted, 0.95),
            PipelineNetSd: sd,
            McStandardError: sd / (decimal)Math.Sqrt(opt.Replicates),
            CensoredReplicates: censored,
            FundedHorizonTruncated: truncated,
            SourceMeanDailyNet: srcMean,
            SourceDailyNetCiLow: ciLo,
            SourceDailyNetCiHigh: ciHi,
            SourceDailyNetMde: mde);
    }

    private readonly record struct FundedOutcome(int Payouts, decimal Paid, bool Bust, bool Truncated);

    /// <summary>
    /// Funded phase at daily-close granularity: no profit target; MDL floor hangs off the
    /// previous close balance (V0 semantics, amount fixed off initial); ML static or
    /// EOD-trailing per the funded rule set; payout every PayoutCycleTradingDays — trader
    /// receives split × profit and balance/HWM reset to initial (FTMO reset-after-payout,
    /// stated modeling assumption in PR-E1-1).
    /// </summary>
    private static FundedOutcome WalkFunded(
        PipelineDay[] path, ref int idx, ChallengePipelineSpec spec, ChallengePipelineMcOptions opt)
    {
        var rules = spec.FundedRules;
        var initial = opt.InitialBalance;
        var trailing = string.Equals(rules.DrawdownType, ChallengeSimulator.TrailingEodDrawdownType,
            StringComparison.OrdinalIgnoreCase);
        var mlAmount = initial * (decimal)rules.MaxTotalLossPercent;
        var dailyLimit = initial * (decimal)rules.MaxDailyLossPercent;

        var bal = initial;
        var prevClose = initial;
        var hwm = initial;
        var payouts = 0;
        var paid = 0m;
        var bust = false;
        var truncated = false;
        var cycle = 0;
        var day = 0;

        for (; day < spec.FundedHorizonTradingDays; day++)
        {
            if (idx + day >= path.Length) { truncated = true; break; }

            var newBal = bal + path[idx + day].NetPnL;
            var floor = trailing ? Math.Max(hwm, initial) - mlAmount : initial - mlAmount;
            if (newBal <= floor) { bust = true; day++; break; }
            if (newBal <= prevClose - dailyLimit) { bust = true; day++; break; }

            bal = newBal;
            prevClose = newBal;
            if (newBal > hwm) hwm = newBal;

            cycle++;
            if (cycle >= spec.PayoutCycleTradingDays)
            {
                cycle = 0;
                var profit = bal - initial;
                if (profit > 0)
                {
                    payouts++;
                    paid += (decimal)spec.PayoutSplit * profit;
                    bal = initial;
                    prevClose = initial;
                    hwm = initial;
                }
            }
        }

        idx += day;
        return new FundedOutcome(payouts, paid, bust, truncated);
    }

    /// <summary>
    /// Politis–Romano stationary bootstrap over day tuples (block_bootstrap.py conventions:
    /// uniform block start, geometric length with mean L, wrap-around), plus the cumulative-PnL
    /// prefix sums the lazy <see cref="PathWindow"/> needs.
    /// </summary>
    internal static (PipelineDay[] Path, decimal[] Cum) Resample(
        IReadOnlyList<PipelineDay> source, int length, int meanBlock, Random rng)
    {
        var n = source.Count;
        var p = 1.0 / meanBlock;
        var path = new PipelineDay[length];
        var i = rng.Next(n);
        for (var k = 0; k < length; k++)
        {
            path[k] = source[i];
            i = rng.NextDouble() < p ? rng.Next(n) : (i + 1) % n;
        }

        var cum = new decimal[length + 1];
        for (var k = 0; k < length; k++) cum[k + 1] = cum[k] + path[k].NetPnL;
        return (path, cum);
    }

    /// <summary>
    /// The D1 two-sided companion: stationary-bootstrap 95% CI on the source stream's mean
    /// daily net $, and MDE = 2.8016 × SE (block_bootstrap.py convention).
    /// </summary>
    internal static (decimal Mean, decimal CiLow, decimal CiHigh, decimal Mde) DailyMeanBootstrap(
        IReadOnlyList<PipelineDay> source, ChallengePipelineMcOptions opt)
    {
        var n = source.Count;
        var p = 1.0 / opt.MeanBlockTradingDays;
        var rng = new Random(opt.Seed);
        var means = new decimal[opt.Replicates];
        for (var rep = 0; rep < opt.Replicates; rep++)
        {
            var sum = 0m;
            var i = rng.Next(n);
            for (var k = 0; k < n; k++)
            {
                sum += source[i].NetPnL;
                i = rng.NextDouble() < p ? rng.Next(n) : (i + 1) % n;
            }
            means[rep] = sum / n;
        }
        Array.Sort(means);
        var overallMean = source.Sum(d => d.NetPnL) / n;
        var se = StdDev(means, means.Average());
        return (overallMean,
            means[(int)(0.025 * means.Length)],
            means[Math.Max(0, (int)(0.975 * means.Length) - 1)],
            2.8016m * se);
    }

    private sealed class PathWindow : IReadOnlyList<DailyEquityPoint>
    {
        private static readonly DateTime BaseDate = new(2020, 1, 1);
        private readonly PipelineDay[] _path;
        private readonly decimal[] _cum;
        private readonly int _offset;
        private readonly decimal _initialBalance;

        public PathWindow(PipelineDay[] path, decimal[] cum, int offset, int count, decimal initialBalance)
        {
            _path = path;
            _cum = cum;
            _offset = offset;
            Count = count;
            _initialBalance = initialBalance;
        }

        public int Count { get; }

        public DailyEquityPoint this[int index]
        {
            get
            {
                var start = _initialBalance + _cum[_offset + index] - _cum[_offset];
                var end = _initialBalance + _cum[_offset + index + 1] - _cum[_offset];
                return new DailyEquityPoint(
                    BaseDate.AddDays(_offset + index), start, end,
                    TradesClosed: 0, TradesOpened: _path[_offset + index].TradesOpened, EndBalance: end);
            }
        }

        public IEnumerator<DailyEquityPoint> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static decimal StdDev(decimal[] values, decimal mean)
    {
        if (values.Length < 2) return 0m;
        var ss = 0m;
        foreach (var v in values) ss += (v - mean) * (v - mean);
        return (decimal)Math.Sqrt((double)(ss / (values.Length - 1)));
    }

    private static decimal Percentile(decimal[] sorted, double q)
    {
        if (sorted.Length == 0) return 0m;
        var idx = (int)Math.Round(q * (sorted.Length - 1));
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
