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
    /// The sizing decision made observable (P0.1/R7): the resolved intermediate values behind the final
    /// clamped <see cref="Lots"/>, so a venue-parity divergence (F1) is visible in the journal instead of
    /// requiring DB archaeology. <see cref="RawLots"/> is the unclamped candidate; <see cref="Lots"/> is
    /// after step/min/max clamping; <see cref="RiskAmount"/> is the clamped worst-case stop loss in money.
    /// </summary>
    public readonly record struct SizingBreakdown(
        string Method,
        decimal EquityAtGate,
        double RiskPct,
        double KellyFraction,
        decimal DrawdownScale,
        decimal SlPips,
        decimal PipValuePerLot,
        decimal RawLots,
        decimal Lots,
        decimal RiskAmount);

    /// <summary>
    /// Size a candidate order. <paramref name="slPips"/> is the stop distance in pips and
    /// <paramref name="pipValuePerLot"/> the cross-rate-aware pip value (both supplied by the evaluator
    /// via <see cref="OrderProposed"/>). Returns lots clamped to the symbol's step/min/max.
    /// </summary>
    public static decimal Calculate(
        decimal equity, RiskProfile profile, decimal slPips, decimal pipValuePerLot,
        decimal drawdownScale, decimal maxLots, decimal minLots, decimal lotStep)
        => Explain(equity, profile, slPips, pipValuePerLot, drawdownScale, maxLots, minLots, lotStep).Lots;

    /// <summary>
    /// Same math as <see cref="Calculate"/> (the final <see cref="SizingBreakdown.Lots"/> is byte-identical),
    /// but also returns the resolved inputs+intermediates for the journal (R7). The kernel calls this so the
    /// <c>OrderProposed</c> DecisionRecord carries the sizing story; <see cref="Calculate"/> delegates here.
    /// </summary>
    public static SizingBreakdown Explain(
        decimal equity, RiskProfile profile, decimal slPips, decimal pipValuePerLot,
        decimal drawdownScale, decimal maxLots, decimal minLots, decimal lotStep)
    {
        var method = profile.LotSizingMethod.ToString();

        if (slPips <= 0 || pipValuePerLot <= 0)
        {
            return new SizingBreakdown(method, equity, profile.RiskPerTradePercent, profile.KellyFraction,
                drawdownScale, slPips, pipValuePerLot, 0m, 0m, 0m);
        }

        var (rawLots, lots) = profile.LotSizingMethod switch
        {
            // H6: apply the drawdown scale to fixed methods too.
            LotSizingMethod.FixedLots => RawAndClamped(profile.FixedLots * drawdownScale, maxLots, minLots, lotStep),
            LotSizingMethod.FixedDollarRisk => RiskRawAndClamped(profile.FixedDollarRisk * drawdownScale, slPips, pipValuePerLot, maxLots, minLots, lotStep),
            LotSizingMethod.KellyFraction => RiskRawAndClamped(equity * (decimal)profile.RiskPerTradePercent * (decimal)profile.KellyFraction * drawdownScale, slPips, pipValuePerLot, maxLots, minLots, lotStep),
            // H5: AntiMartingale is a real, EXPLICIT branch (no silent fall-through). TODO(deepseek): drive
            // the multiplier/steps off the recent-trade streak (profile.AntiMartingaleMultiplier /
            // AntiMartingaleMaxSteps) instead of treating it as plain PercentRisk.
            LotSizingMethod.AntiMartingale => RiskRawAndClamped(equity * (decimal)profile.RiskPerTradePercent * drawdownScale, slPips, pipValuePerLot, maxLots, minLots, lotStep),
            _ => RiskRawAndClamped(equity * (decimal)profile.RiskPerTradePercent * drawdownScale, slPips, pipValuePerLot, maxLots, minLots, lotStep),
        };

        return new SizingBreakdown(method, equity, profile.RiskPerTradePercent, profile.KellyFraction,
            drawdownScale, slPips, pipValuePerLot, rawLots, lots, lots * slPips * pipValuePerLot);
    }

    public static decimal FromRiskAmount(decimal riskAmount, decimal slPips, decimal pipValuePerLot, decimal maxLots, decimal minLots, decimal lotStep)
    {
        return Clamp(riskAmount / (slPips * pipValuePerLot), maxLots, minLots, lotStep);
    }

    private static (decimal Raw, decimal Clamped) RawAndClamped(decimal rawLots, decimal maxLots, decimal minLots, decimal lotStep)
        => (rawLots, Clamp(rawLots, maxLots, minLots, lotStep));

    private static (decimal Raw, decimal Clamped) RiskRawAndClamped(decimal riskAmount, decimal slPips, decimal pipValuePerLot, decimal maxLots, decimal minLots, decimal lotStep)
    {
        var raw = riskAmount / (slPips * pipValuePerLot);
        return (raw, Clamp(raw, maxLots, minLots, lotStep));
    }

    private static decimal Clamp(decimal rawLots, decimal maxLots, decimal minLots, decimal lotStep)
    {
        var clamped = Math.Min(rawLots, maxLots);
        var stepped = lotStep > 0 ? Math.Floor(clamped / lotStep) * lotStep : clamped;
        return Math.Max(stepped, minLots);
    }
}
