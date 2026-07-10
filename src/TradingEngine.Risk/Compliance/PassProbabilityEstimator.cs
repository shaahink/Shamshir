namespace TradingEngine.Risk.Compliance;

public sealed class PassProbabilityEstimator : IPassProbabilityEstimator
{
    public PassProbabilityEstimate Estimate(PassProbabilityInput input)
    {
        var history = input.HistoricalDailyPnL;
        if (history.Count == 0)
        {
            return new PassProbabilityEstimate
            {
                ProbabilityOfPass = 0,
                Recommendation = "Insufficient history",
            };
        }

        var rng = new Random(42);
        var passCount = 0;
        var dailyBreachCount = 0;
        var maxBreachCount = 0;
        var projectedEquities = new List<decimal>();

        var targetEquity = input.InitialBalance * (1m + (decimal)input.ProfitTargetPercent);
        var dailyLossLimit = input.InitialBalance * (decimal)input.MaxDailyLossPercent;
        var maxLossLimit = input.InitialBalance * (1m - (decimal)input.MaxTotalLossPercent);
        var isBalanceBased = input.DailyDdBase == DailyDdBase.InitialBalance;

        for (int run = 0; run < input.MonteCarloRuns; run++)
        {
            var equity = input.CurrentEquity;
            var dailyStart = equity;
            var breacheDDaily = false;
            var breachedMax = false;
            var reachedTarget = false;

            for (int day = 0; day < input.DaysRemaining; day++)
            {
                var sample = history[rng.Next(history.Count)];
                equity += sample;

                if (equity >= targetEquity) { reachedTarget = true; break; }
                if (equity <= maxLossLimit) { breachedMax = true; break; }

                if (isBalanceBased)
                {
                    var dailyLossAmount = dailyStart - equity;
                    if (dailyLossAmount >= dailyLossLimit) { breacheDDaily = true; break; }
                }
                else
                {
                    var dailyDD = (dailyStart - equity) / dailyStart;
                    if (dailyDD >= (decimal)input.MaxDailyLossPercent) { breacheDDaily = true; break; }
                }

                dailyStart = equity;
            }

            if (reachedTarget) passCount++;
            if (breacheDDaily) dailyBreachCount++;
            if (breachedMax) maxBreachCount++;
            projectedEquities.Add(equity);
        }

        var total = input.MonteCarloRuns;
        projectedEquities.Sort();
        var medianEquity = projectedEquities[total / 2];

        return new PassProbabilityEstimate
        {
            ProbabilityOfPass = (double)passCount / total,
            ProbabilityOfDailyBreach = (double)dailyBreachCount / total,
            ProbabilityOfMaxBreach = (double)maxBreachCount / total,
            ExpectedDaysToTarget = passCount > 0 ? input.DaysRemaining / 2 : input.DaysRemaining,
            ProjectedFinalEquity = medianEquity,
            Recommendation = (passCount / (double)total) switch
            {
                >= 0.7 => "On Track",
                >= 0.4 => "Warning — reduce risk",
                _ => "At Risk — P(pass) < 40%. Consider stopping."
            },
        };
    }
}
