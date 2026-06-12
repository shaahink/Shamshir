using TradingEngine.Domain.Experiments;

namespace TradingEngine.Host.Experiments;

public static class WalkForwardSplitter
{
    public static IReadOnlyList<(DateOnly TrainFrom, DateOnly TrainTo, DateOnly TestFrom, DateOnly TestTo)> Split(
        DateOnly from, DateOnly to, WalkForwardSpec spec)
    {
        var totalDays = to.DayNumber - from.DayNumber + 1;
        if (totalDays <= 0 || spec.Folds <= 0)
            return [];

        var foldSize = totalDays / spec.Folds;
        var folds = new List<(DateOnly, DateOnly, DateOnly, DateOnly)>();

        for (var f = 0; f < spec.Folds; f++)
        {
            var foldStart = from.AddDays(f * foldSize);
            var foldEnd = f == spec.Folds - 1 ? to : from.AddDays((f + 1) * foldSize - 1);
            var foldDays = foldEnd.DayNumber - foldStart.DayNumber + 1;

            var trainDays = (int)(foldDays * spec.TrainFraction);
            if (trainDays < 1) trainDays = 1;

            var trainFrom = foldStart;
            var trainTo = foldStart.AddDays(trainDays - 1);
            var testFrom = trainTo.AddDays(1);
            var testTo = foldEnd;

            if (testFrom > testTo)
                testFrom = testTo;

            folds.Add((trainFrom, trainTo, testFrom, testTo));
        }

        return folds;
    }
}
