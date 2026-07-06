using TradingEngine.Services.AddOns;
using TradingEngine.Domain.Interfaces;

namespace TradingEngine.Services;

/// <summary>
/// iter-quant-model P2.6 (D9, units doctrine). PURE conversions between a normalized unit (ATR-multiple,
/// spread-multiple, ATR-fraction) and a concrete pip distance for a given (symbol, timeframe). This is the
/// single place a raw-pip config field gets overridden by its normalized companion — everything downstream
/// (SlTpResolver, PreTradeGate, EntryPlanner, PositionManager) keeps reading the SAME existing raw-pip
/// fields (MaxPips, OffsetPips, StepPips, LimitOffsetPips, MaxSlippagePips, RiskProfile.MaxSlPips)
/// unchanged; only the VALUE bound into those fields differs once a normalized companion is set.
///
/// Reference scale: prefers measured <see cref="ReferenceScales"/> via <see cref="IReferenceScaleLookup"/>
/// when available; falls back to the <see cref="AddOnAutoTuner.ReferenceAtrPips"/> spread-guess heuristic.
/// </summary>
public static class UnitConversion
{
    public static double ReferenceAtrPips(Timeframe tf, SymbolInfo symbol, IReferenceScaleLookup? lookup = null)
    {
        if (lookup is not null)
        {
            var measured = lookup.GetMedianAtrPips(symbol.Symbol, tf);
            if (measured.HasValue && measured.Value > 0) return measured.Value;
        }
        return AddOnAutoTuner.ReferenceAtrPips(tf, SpreadPips(symbol));
    }

    public static double SpreadPips(SymbolInfo symbol) =>
        symbol.PipSize > 0 ? (double)(symbol.TypicalSpread / symbol.PipSize) : 0;

    public static PositionManagementOptions ResolvePips(
        this PositionManagementOptions pm, Timeframe tf, SymbolInfo symbol, IReferenceScaleLookup? lookup = null)
    {
        var refAtr = ReferenceAtrPips(tf, symbol, lookup);
        var spreadPips = SpreadPips(symbol);

        return pm with
        {
            StopLoss = refAtr > 0 && pm.StopLoss.MaxSlAtrMultiple is { } slMult
                ? pm.StopLoss with { MaxPips = slMult * refAtr }
                : pm.StopLoss,
            Breakeven = spreadPips > 0 && pm.Breakeven.OffsetSpreadMultiple is { } beMult
                ? pm.Breakeven with { OffsetPips = beMult * spreadPips }
                : pm.Breakeven,
            Trailing = refAtr > 0 && pm.Trailing.StepAtrFraction is { } stepFrac
                ? pm.Trailing with { StepPips = stepFrac * refAtr }
                : pm.Trailing,
        };
    }

    public static OrderEntryOptions ResolvePips(
        this OrderEntryOptions oe, Timeframe tf, SymbolInfo symbol, IReferenceScaleLookup? lookup = null)
    {
        var refAtr = ReferenceAtrPips(tf, symbol, lookup);
        var spreadPips = SpreadPips(symbol);

        return oe with
        {
            LimitOffsetPips = refAtr > 0 && oe.LimitOffsetAtrFraction is { } loFrac
                ? loFrac * refAtr
                : oe.LimitOffsetPips,
            MaxSlippagePips = spreadPips > 0 && oe.MaxSlippageSpreadMultiple is { } msMult
                ? msMult * spreadPips
                : oe.MaxSlippagePips,
        };
    }

    public static RiskProfile ResolveMaxSlPips(this RiskProfile profile, Timeframe tf, SymbolInfo symbol,
        IReferenceScaleLookup? lookup = null)
    {
        var refAtr = ReferenceAtrPips(tf, symbol, lookup);
        return refAtr > 0 && profile.MaxSlAtrMultiple is { } mult
            ? profile with { MaxSlPips = mult * refAtr }
            : profile;
    }
}
