# PR3 — Entry/Exit Playbook Audit

**Date**: 2026-06-12  
**Status**: COMPLETE

---

## Strategy Audit Table

| Strategy | SL Source | TP Source | Trailing | Uses SlTpResolver? | Config Has positionManagement? | Inline Math? | Action |
|----------|-----------|-----------|----------|--------------------|-------------------------------|--------------|--------|
| **trend-breakout** | `SlTpHelpers.AtrBased` via `_config.PositionManagement` | `SlTpHelpers.RRMultiple` | `AtrMultiple(1.0)` in config | Yes (partial) | YES | No | Adopt fully. No changes needed. |
| **super-trend** | ST indicator line (code L92) | Hardcoded `2.0R` (code L94) | None | No | NO (missing) | Yes | **FIX**: Supply ST line as `strategySuppliedSl` via `SlTpResolver` with `SwingPoint` method. Add `positionManagement` to config. |
| **session-breakout** | Inline ATR math (L75-78) | Inline RR math (L79-82) | None | No | YES (partial) | Yes | **FIX**: Replace inline math with `SlTpResolver.Resolve()` call. |
| **rsi-divergence** | `SlTpHelpers.AtrBased` (L73) | `SlTpHelpers.RRMultiple` (L74) | None | Yes (partial) | NO (missing) | No | **FIX**: Add `positionManagement` to config. Already uses helpers. |
| **mtf-trend** | Dual: `SwingBased` + `AtrBased`, picks wider (L106-114) | `SlTpHelpers.RRMultiple` (L116) | None | Yes (partial) | NO (missing) | Yes (custom) | **FIX**: Use `SlTpResolver` with `strategySuppliedSl` for swing stop. Add `positionManagement` to config. |
| **mean-reversion** | Inline ATR math (L67-70) | Inline RR math (L71-74) | None (playbook: MR = no trail) | No | YES | Yes | **FIX**: Replace with `SlTpResolver`. MR targets the mean — no trailing, no ride mode per playbook. |
| **macd-momentum** | `SlTpHelpers.AtrBased` (L110) | `SlTpHelpers.RRMultiple` (L111) | None | Yes (partial) | NO (missing) | No | **FIX**: Add `positionManagement` to config. Already uses helpers. |
| **ema-alignment** | Inline ATR math (L70-73) | Inline RR math (L74-77) | `AtrMultiple(1.0)` in config | No | YES (partial) | Yes | **FIX**: Replace inline math with `SlTpResolver`. Adds ADX for RideOptions. |
| **bb-squeeze** | Band+buffer inline (L102-113) | `SlTpHelpers.RRMultiple` (L123) | None | No (partial) | NO (missing) | Yes (custom SL) | **FIX**: Supply band-derived SL via `strategySuppliedSl`. Add `positionManagement` to config with custom SL method. |

---

## Configuration Gaps

### Strategies missing `positionManagement` block: 5

1. **super-trend** — needs ATR-based SL (per playbook) + R-multiple TP
2. **rsi-divergence** — needs config surfacing (already code-compliant)
3. **mtf-trend** — needs dual SL method config
4. **macd-momentum** — needs config surfacing (already code-compliant)
5. **bb-squeeze** — needs band-based SL config

### Strategies with inline exit math: 4

1. **super-trend** — ST line for SL, hardcoded TP
2. **session-breakout** — duplicated ATR + RR math
3. **mean-reversion** — duplicated ATR + RR math
4. **ema-alignment** — duplicated ATR + RR math

### Strategies already compliant: 1

1. **trend-breakout** — uses SlTpHelpers, config-driven, proper positionManagement

---

## SlTpResolver Issues Found

1. **`FixedPips` TP path bug**: Line 34 of `SlTpResolver.cs` — `FixedPips` case calls `SlTpHelpers.AtrMultiple(...)` instead of `SlTpHelpers.FixedPip(...)`. This is a copy-paste bug from early development. Fix: call `SlTpHelpers.FixedPip`.

2. **Missing methods**: `Structure` and `SteppedR` SL methods not yet implemented (deferred to T2).

3. **No partial-TP awareness**: `Resolve()` doesn't return a partial TP price. Need optional output for T3.

---

## Playbook Baseline

Per the iter-19 PLAN §T0, baked-in best practices:

### Per Asset Class × Timeframe

| Class | TF | SL Method | SL Value | TP Method | TP Value | Trailing | BE |
|-------|-----|----------|----------|-----------|----------|----------|-----|
| FX Majors | H1 | AtrMultiple | 1.5× | RrMultiple | 2.0R | AtrMultiple(1.0) | +1R |
| FX Majors | H4 | AtrMultiple | 2.0× | RrMultiple | 2.5R | AtrMultiple(1.5) | +1R |
| FX Majors | D1 | AtrMultiple | 2.5× | RrMultiple | 3.0R | AtrMultiple(2.0) | +1R |
| JPY Crosses | H1 | AtrMultiple | 2.0× | RrMultiple | 2.0R | AtrMultiple(1.5) | +1R |
| JPY Crosses | H4 | AtrMultiple | 2.5× | RrMultiple | 2.5R | AtrMultiple(2.0) | +1R |
| JPY Crosses | D1 | AtrMultiple | 2.5× | RrMultiple | 3.0R | AtrMultiple(2.5) | +1R |

### Per Strategy Overrides

| Strategy | Class | Deviation | Reason |
|----------|-------|-----------|--------|
| **mean-reversion** | MR | TP = 1.5R, NO trailing, NO ride | MR has natural target (the mean). Ride mode + trailing do not apply. |
| **super-trend** | Trend | SL = ST line (swing-point), TP = 2.0R | ST line is the defining signal — it IS the stop. ATR backup if ST line is too far. |
| **bb-squeeze** | Breakout | SL = Band ± buffer | Band defines breakout boundaries. ATR backup for width cap. |
| **mtf-trend** | Trend | Dual SL (swing + ATR), pick wider | Multi-timeframe trend uses structural swing points + ATR volatility guard. |

### Entry Hygiene (all strategies)

- ATR(14) ≥ 25th percentile over trailing 90-day window (dead market guard)
- Spread ≤ 10% of SL distance
- Intraday session windows (M30, H1, H4): Majors = London+NY; JPY = Tokyo+London overlap; D1 exempt
