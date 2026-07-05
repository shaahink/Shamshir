using TradingEngine.Services.AddOns;

namespace TradingEngine.Services;

/// <summary>
/// iter-quant-model P2.6 (D9, units doctrine). PURE conversions between a normalized unit (ATR-multiple,
/// spread-multiple, ATR-fraction) and a concrete pip distance for a given (symbol, timeframe). This is the
/// single place a raw-pip config field gets overridden by its normalized companion — everything downstream
/// (SlTpResolver, PreTradeGate, EntryPlanner, PositionManager) keeps reading the SAME existing raw-pip
/// fields (MaxPips, OffsetPips, StepPips, LimitOffsetPips, MaxSlippagePips, RiskProfile.MaxSlPips)
/// unchanged; only the VALUE bound into those fields differs once a normalized companion is set.
///
/// Reference scale is the existing <see cref="AddOnAutoTuner.ReferenceAtrPips"/> heuristic (spread × a
/// per-TF factor) — the same "typical ATR" used elsewhere for auto-tuning. P3.4b upgrades this to a
/// measured (rolling-median) reference table; P2.6 does not need that to fix the flat-pip-cap bug, because
/// the SAME reference is used both when migrating an old value and when resolving a new one.
/// </summary>
public static class UnitConversion
{
    public static double ReferenceAtrPips(Timeframe tf, SymbolInfo symbol) =>
        AddOnAutoTuner.ReferenceAtrPips(tf, SpreadPips(symbol));

    public static double SpreadPips(SymbolInfo symbol) =>
        symbol.PipSize > 0 ? (double)(symbol.TypicalSpread / symbol.PipSize) : 0;

    public static PositionManagementOptions ResolvePips(
        this PositionManagementOptions pm, Timeframe tf, SymbolInfo symbol)
    {
        var refAtr = ReferenceAtrPips(tf, symbol);
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
        this OrderEntryOptions oe, Timeframe tf, SymbolInfo symbol)
    {
        var refAtr = ReferenceAtrPips(tf, symbol);
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

    public static RiskProfile ResolveMaxSlPips(this RiskProfile profile, Timeframe tf, SymbolInfo symbol)
    {
        var refAtr = ReferenceAtrPips(tf, symbol);
        return refAtr > 0 && profile.MaxSlAtrMultiple is { } mult
            ? profile with { MaxSlPips = mult * refAtr }
            : profile;
    }
}
