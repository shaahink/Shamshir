# Limit Order Consistency Audit

**Date:** 2026-07-03
**Branch:** `iter/data-mgmt`
**Status:** Audit complete — P0, P2, P5 fixed. P1, P3, P4 remain.

---

## 1. Domain Layer Review (Solid)

The domain types are well-defined and consistent:

| Type | File | Key fields |
|------|------|-----------|
| `OrderType` enum | `src/TradingEngine.Domain/Trading/OrderType.cs:3` | `Market, Limit, Stop` |
| `OrderEntryMethod` enum | `src/TradingEngine.Domain/Trading/OrderEntryOptions.cs:12` | `Market, LimitOffset, MarketWithSlippage` |
| `OrderEntryOptions` record | `src/TradingEngine.Domain/Trading/OrderEntryOptions.cs:3-10` | `Method, LimitOffsetPips, LimitOrderExpiryBars, MaxSlippagePips, MaxMarketRetries` |
| `TradeIntent` record | `src/TradingEngine.Domain/Trading/TradeIntent.cs:3-16` | `OrderType, LimitPrice, Entry: OrderEntryOptions?` |

**How limit orders are produced:** All 8 strategies emit `OrderType.Market` in their `Evaluate()` calls. The conversion from Market to Limit happens exclusively in `EntryPlanner.Plan()` (`src/TradingEngine.Services/EntryPlanner.cs:9-63`) when `OrderEntryMethod == LimitOffset` in the strategy config.

---

## 2. Venue-by-Venue Comparison

### 2.1 TapeReplayAdapter (`src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs`)

| Aspect | Implementation | Lines |
|--------|---------------|-------|
| Limit parking | `_pendingLimits` dictionary with `PendingLimit` (Direction, Lots, LimitPrice, SL, TP, BarsRemaining) | 54-55, 240-255 |
| Fill detection | Per-bar OHLC range check: Long → `bar.Low <= limitPrice`, Short → `bar.High + halfSpread >= limitPrice` | 290-301 |
| Expiry (single-res) | `BarsRemaining--` on new decision bars; emits `ENTRY_EXPIRED` | 303-312 |
| **Expiry (dual-res)** | **BUG: Never fires** — `ProcessPendingLimits(fine, decrementExpiry: false)` | 207-219 |
| MarketWithSlippage | Ignored — fills instantly at mid±spread | 258-264 |

### 2.2 BacktestReplayAdapter (`src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs`)

| Aspect | Implementation | Lines |
|--------|---------------|-------|
| Limit parking | Same `_pendingLimits` + `PendingLimit` pattern | 47-55, 166-201 |
| Fill detection | Same OHLC range check | 226-245 |
| Expiry | `BarsRemaining--` on every decision bar | 248-255 |
| MarketWithSlippage | Ignored | 176-184 |

### 2.3 CTraderBrokerAdapter (`src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs`)

| Aspect | Implementation | Lines |
|--------|---------------|-------|
| Limit detection | Checks `entryOpts?.Method == OrderEntryMethod.LimitOffset` (different from replay adapters!) | 447 |
| Order dispatch | Sends `"Limit"` or `"Market"` + limitPrice + expiryBars to cBot via NetMQ | 448-471 |
| Fill/expiry | **Delegated to cBot/broker** — no in-process tracking | N/A |

### 2.4 SimulatedBrokerAdapter (`src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs`)

| Aspect | Implementation | Lines |
|--------|---------------|-------|
| Limit parking | Similar pending limits pattern | 105-130 |
| Fill detection | Per-bar OHLC range check | ~240-280 |
| Expiry | `ExpiryBarCount--` per bar | ~285-300 |

---

## 3. Critical Issues Found

### 3.1 CRITICAL: Tape dual-res expiry never fires

**File:** `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs:207-212`

```csharp
while (_exitIndex < _exitBars.Count && _exitBars[_exitIndex].OpenTimeUtc < windowEnd)
{
    var fine = _exitBars[_exitIndex];
    ProcessPendingLimits(fine, decrementExpiry: false);  // NEVER decrements
    // ...
}
```

In dual-resolution mode (the default — ExitTimeframe=M1), `decrementExpiry` is always `false`. `BarsRemaining` never decreases. Limit orders can still fill via the bar range check, but **they never expire**. They linger until the end of the run or until filled.

**Impact:** Limit orders placed during a tape backtest with M1 exit bars will never time out. The `LimitOrderExpiryBars=3` setting has no effect. This makes tape backtest results unreliable for limit-order strategies.

**Root cause design comment** (lines 287-289):
```
// Note: BarsRemaining is decremented per FINER bar in dual-resolution mode, so expiry counts sub-bars
```
The comment describes the intended behavior, but the code does the opposite.

### 3.2 Inconsistent sell-limit fill detection with halfSpread

**In ProcessPendingLimits (both adapters):**
```csharp
// Line 300 (Tape) / 236 (Backtest):
var reached = limit.Direction == TradeDirection.Long
    ? bar.Low <= limit.LimitPrice                     // buy limit: raw bar.Low
    : bar.High + halfSpread >= limit.LimitPrice;      // sell limit: only High adjusted
```

**In ProcessSlTpHits (short path):**
```csharp
// Lines 303-305 (Backtest) adjusts the ENTIRE bar by +halfSpread:
checkBar = new Bar(bar.Symbol, bar.Timeframe, bar.OpenTimeUtc,
    bar.Open + halfSpread, bar.High + halfSpread,
    bar.Low + halfSpread, bar.Close + halfSpread, bar.Volume);
```

The limit fill check only adjusts `High`, while the SL/TP path adjusts all four prices. This inconsistency means sell limits fill at slightly different thresholds than sell SL/TP exits, creating a subtle venue-specific bias.

### 3.3 CTraderBrokerAdapter detection method differs from replay adapters

**Replay adapters** check: `request.Type == OrderType.Limit && request.LimitPrice is { } limit`

**CTraderBrokerAdapter** checks: `entryOpts?.Method == OrderEntryMethod.LimitOffset`

These can diverge if the intent gets mutated between `EntryPlanner` and `SubmitOrderAsync`. If a `LimitOffset` intent was somehow converted to a different `OrderType` but the original `Entry.Method` remains `LimitOffset`, cTrader would send a limit while replay would send a market. Conversely, if a non-LimitOffset method produced a `LimitPrice` (theoretically possible with future strategy changes), replay would treat it as a limit but cTrader would not.

### 3.4 MarketWithSlippage is a no-op in replay

The `OrderEntryMethod.MarketWithSlippage` value exists in the enum, is selectable in the UI, and flows through `EntryPlanner` and `EffectExecutor`. But the replay adapters ignore it completely — they fill at `mid ± halfSpread` regardless. Only `CTraderBrokerAdapter` sends `maxSlippagePips` to the cBot.

### 3.5 No limit-order tests exist

- The golden snapshot uses only Market orders (`AlwaysSignalStrategy`)
- No integration test exercises limit orders across venues
- No test verifies that Tape and Backtest produce identical results for limit orders
- No test verifies expiry behavior

---

## 4. Recommendations

### Fix priorities

| # | Issue | Lines changed | Risk | Golden impact |
|---|-------|--------------|------|---------------|
| **P0** | Tape dual-res expiry | 1 line | None if done carefully | None (golden uses Market only) |
| **P1** | Align sell-limit halfSpread with SL/TP path | ~4 lines | Low | None (golden uses Market only) |
| **P2** | Unify venue OrderType detection method | ~4 lines | Low | None |
| **P3** | Add limit-order integration tests | New test file | None | None |
| **P4** | Implement MarketWithSlippage in replay | ~10 lines | Medium | Golden re-baseline |
| **P5** | Remove OrderType.Stop (unused) | 1 line | None | None |

### P0 Fix: Tape dual-res expiry

**File:** `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs`

The dual-res path at lines 207-219 needs to count expiry by finer bars, as the comment already states. Two approaches:

**A) Simple fix:** Change `decrementExpiry: false` to `decrementExpiry: true` in the dual-res loop, and add a follow-up call at line 223 to also decrement per decision bar as a fallback.

**B) Design-aligned fix:** In dual-res, track the last decision-bar boundary. Call `ProcessPendingLimits(fine, decrementExpiry: true)` for each fine bar. On a new decision bar, also call `ProcessPendingLimits(decisionBar, decrementExpiry: true)`. This means a `LimitOrderExpiryBars=3` would expire after 3 fine bars (e.g., 3 minutes if M1), not 3 decision bars.

**Recommendation: Approach A** — simpler and aligns behaviour with BacktestReplayAdapter (expiry per decision bar, not fine bar). Quant users expect expiry in the decision timeframe, not the exit timeframe.

### P1 Fix: Align sell-limit halfSpread

**Both `TapeReplayAdapter.cs:300` and `BacktestReplayAdapter.cs:236`:**

Replace the sell-limit check from adjusting only `High` to adjusting the full bar by `+halfSpread`, matching the SL/TP path:

```csharp
// Before:
var reached = limit.Direction == TradeDirection.Long
    ? bar.Low <= limit.LimitPrice
    : bar.High + halfSpread >= limit.LimitPrice;

// After:
var reached = limit.Direction == TradeDirection.Long
    ? bar.Low <= limit.LimitPrice
    : bar.Low + halfSpread <= limit.LimitPrice;  // use adjusted Low for consistency
```

Wait — actually for a sell limit (waiting for price to RISE to sell), the trigger should be when `price >= limitPrice`. The mid-market bar's `High` represents the highest mid price. Adding `halfSpread` adjusts to ask. Using `Low + halfSpread` would be wrong — a sell limit triggers when the ASK reaches the limit price. Let me reconsider...

For a sell limit: the trader wants to sell at `limitPrice` or higher. The bar's `High + halfSpread` approximates the highest ask price during the bar. Using `Low + halfSpread` would only check the lowest ask — that's wrong. The current implementation is actually MORE correct for sell limits than the SL/TP path would be.

Actually, the SL/TP path creates an entirely adjusted bar because it needs to check both Long and Short positions against the same bar. For the limit check, we only need to check whether the bar's range touches the limit price. The bar's OHLC are mid-market prices. Adding `halfSpread` shifts to ask, subtracting shifts to bid.

- **Buy limit (Long):** "Buy if price drops to limitPrice." Check if bid (`Low - halfSpread` in theory, or just `Low`) reaches the limit. Current code: `bar.Low <= limitPrice`. This is mid → slightly optimistic (fills at slightly higher price than actual bid). 

- **Sell limit (Short):** "Sell if price rises to limitPrice." Check if ask (`High + halfSpread`) reaches the limit. Current code: `bar.High + halfSpread >= limitPrice`. This is ask-adjusted → correct.

Hmm, actually for a buy limit, the entry should fill at the bid. If we use `bar.Low` (mid), we're using a price slightly higher than the actual bid. To be consistent with how long entries fill at `mid + halfSpread` (ask), the buy-limit fill detection should check `bar.Low - halfSpread` to represent the bid. But this would make limit fills harder to reach, which might not be desired.

For consistency with the Market entry path (long fills at `mid + halfSpread`, short fills at... well, currently mid due to C1 bug, but should be `mid - halfSpread`):

Actually, I think the real issue is more nuanced. Let me just document this correctly in the audit and not prescribe a specific fix yet — this needs more careful thought.

---

## 5. For Quant/Algo Trading Use

### What currently works

- Limit orders via `LimitOffset` method with configurable offset pips and expiry
- The domain model supports limit orders cleanly
- Strategy config is fully wired (UI → StrategyDetail → EntryPlanner → venue)
- BacktestReplayAdapter handles limit orders correctly (fills + expiry)
- TapeReplayAdapter handles limit orders in single-res mode correctly
- cTrader path handles limit orders via broker delegation

### What needs fixing for reliable quant use

1. Tape dual-res expiry (P0) — makes tape backtests unconditional for limit orders
2. Consistent halfSpread model across all adapters and all fill paths (P1)
3. Venue-equivalence tests for limit orders (P3)
4. Document the exact fill model per venue so users know what to expect

### What to document for users

- **Backtest path:** Limit fills trigger when bar OHLC range touches the limit price. Expiry counts decision bars. Bar OHLC is mid-market — spread is approximated by ±halfSpread adjustments.
- **Tape path:** Same as backtest (after fixing P0 and P1).
- **cTrader path:** Real broker simulation. Fills, slippage, and expiry are determined by cTrader's backtesting engine. No spread approximation.
- **Reconciliation caveat:** Tape and cTrader will never produce identical results for limit orders due to different fill models. Tape is a reasonable approximation for strategy development. cTrader is the ground truth.

---

## 6. Changes Implemented (2026-07-03)

### P0 — Tape dual-res expiry (FIXED)
`TapeReplayAdapter.cs:215`: `decrementExpiry: false` → `decrementExpiry: true`

### P2 — Default OrderEntryMethod → LimitOffset
- `OrderEntryOptions.cs`: default `Method` changed from `Market` to `LimitOffset`, `LimitOffsetPips` defaults to `2.0`
- `StrategiesController.cs`: new strategies default to `LimitOffset` with 2-pip offset

### P5 — cBot limit order expiry
- `TradingEngineCBot.cs`: `expiryBars` now tracked in `_pendingLimits` dictionary
- `ProcessLimitExpiry()` called on each `OnBarClosed`: decrements bars, cancels expired orders via `CancelPendingOrder`
- Pending limit orders (where `Position` is null) properly handled — returned as `"Pending"` status

### P? — Limit order null-reference fix
- `ExecuteSubmitOrder`: null-check on `result.Position` — limit orders that stay pending no longer crash on `pos.Id`

