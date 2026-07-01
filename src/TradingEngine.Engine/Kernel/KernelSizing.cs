using TradingEngine.Domain;

namespace TradingEngine.Engine;

/// <summary>
/// Pure position-sizing math for the kernel (iter-35 A2). Faithful port of
/// <c>TradingEngine.Risk.PositionSizer</c> + <c>DrawdownScaler</c> into the Engine project, because the
/// kernel (Engine) references only Domain and sizing must live inside the pure decision core.
///
/// TODO(deepseek): once this is the single sizing authority, DELETE TradingEngine.Risk.PositionSizer +
/// DrawdownScaler and point remaining callers here (Kill-List). Verify against the golden oracle.
///
/// Fixes vs the original: H5 — AntiMartingale gets a real (explicit) branch; H6 — drawdown scale is
/// applied to FixedLots / FixedDollarRisk too.
/// </summary>
public static class KernelSizing
{
    /// <summary>Linear drawdown scale-down factor (was DrawdownScaler.ComputeScaleFactor).</summary>
    public static double ComputeScaleFactor(
        decimal currentDrawdownFraction, decimal maxDrawdownLimit, double scaleThreshold, double scaleFloor)
    {
        if (maxDrawdownLimit <= 0)
        {
            return 1.0;
        }

        var ddRatio = (double)(currentDrawdownFraction / maxDrawdownLimit);
        if (ddRatio <= scaleThreshold)
        {
            return 1.0;
        }
        if (ddRatio >= 1.0)
        {
            return scaleFloor;
        }

        var range = 1.0 - scaleThreshold;
        var position = (ddRatio - scaleThreshold) / range;
        return 1.0 - (position * (1.0 - scaleFloor));
    }

    /// <summary>
    /// Size a candidate order. <paramref name="slPips"/> is the stop distance in pips and
    /// <paramref name="pipValuePerLot"/> the cross-rate-aware pip value (both supplied by the evaluator
    /// via <see cref="OrderProposed"/>). Returns lots clamped to the symbol's step/min/max.
    /// </summary>
    public static decimal Calculate(
        decimal equity, RiskProfile profile, decimal slPips, decimal pipValuePerLot,
        decimal drawdownScale, decimal maxLots, decimal minLots, decimal lotStep)
    {
        if (slPips <= 0 || pipValuePerLot <= 0)
        {
            return 0m;
        }

        return profile.LotSizingMethod switch
        {
            // H6: apply the drawdown scale to fixed methods too.
            LotSizingMethod.FixedLots => Clamp(profile.FixedLots * drawdownScale, maxLots, minLots, lotStep),
            LotSizingMethod.FixedDollarRisk => FromRiskAmount(profile.FixedDollarRisk * drawdownScale, slPips, pipValuePerLot, maxLots, minLots, lotStep),
            LotSizingMethod.KellyFraction => FromRiskAmount(equity * (decimal)profile.RiskPerTradePercent * (decimal)profile.KellyFraction * drawdownScale, slPips, pipValuePerLot, maxLots, minLots, lotStep),
            // H5: AntiMartingale is a real, EXPLICIT branch (no silent fall-through). TODO(deepseek): drive
            // the multiplier/steps off the recent-trade streak (profile.AntiMartingaleMultiplier /
            // AntiMartingaleMaxSteps) instead of treating it as plain PercentRisk.
            LotSizingMethod.AntiMartingale => FromRiskAmount(equity * (decimal)profile.RiskPerTradePercent * drawdownScale, slPips, pipValuePerLot, maxLots, minLots, lotStep),
            _ => FromRiskAmount(equity * (decimal)profile.RiskPerTradePercent * drawdownScale, slPips, pipValuePerLot, maxLots, minLots, lotStep),
        };
    }

    public static decimal FromRiskAmount(decimal riskAmount, decimal slPips, decimal pipValuePerLot, decimal maxLots, decimal minLots, decimal lotStep)
    {
        return Clamp(riskAmount / (slPips * pipValuePerLot), maxLots, minLots, lotStep);
    }

    private static decimal Clamp(decimal rawLots, decimal maxLots, decimal minLots, decimal lotStep)
    {
        var clamped = Math.Min(rawLots, maxLots);
        var stepped = lotStep > 0 ? Math.Floor(clamped / lotStep) * lotStep : clamped;
        return Math.Max(stepped, minLots);
    }
}
