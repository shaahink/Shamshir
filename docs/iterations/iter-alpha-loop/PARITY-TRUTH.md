# Parity Truth — what actually diverges, and why

**Date:** 2026-07-11
**Branch:** `iter/alpha-loop`
**Status:** SUPERSEDES `R2-DIVERGENCE-INVESTIGATION.md` (that document's money analysis is void — see F1)
**Method:** code read + direct DB query against `src/TradingEngine.Web/data/trading.db`. Every claim below
carries the query or the file:line that produced it.

---

## 0. Verdict

**R2's parity PASS is not supported by its own evidence, and R1's scoreboard is not a valid census.**
Neither is a small error. Both must be redone after the parity work below.

The good news: the divergence is *not* mysterious. It is four concrete, fixable defects — a sign
bug, a fabricated data file, a wrong formula, and a broken experiment design. None of them require
a fudge factor to fix. The tape can be made to agree with cTrader to within a tick and a couple of
percent on costs, by making the venue **declare** its own economics instead of us guessing them.

---

## 1. F1 — The two venues store costs with OPPOSITE SIGNS, and the reconcile compares them raw

This single defect voids every money number in `R2-DIVERGENCE-INVESTIGATION.md`.

```sql
SELECT RunId,Venue,Symbol,TotalTrades,NetProfit,CommissionTotal,SwapTotal
FROM BacktestRuns WHERE RunId IN ('9f0ea5e5','197598ab','e29c5dfe','00aaba6a');
```
| RunId | Venue | Symbol | Trades | Net | Commission | Swap |
|---|---|---|---|---|---|---|
| 197598ab | ctrader | XAUUSD | 15 | 4625.82 | **−54.36** | **−10.10** |
| 9f0ea5e5 | tape | XAUUSD | 14 | 5773.29 | **+16.31** | **−33.10** |
| 00aaba6a | ctrader | USDCAD | 13 | −1399.82 | **−128.80** | **−246.20** |
| e29c5dfe | tape | USDCAD | 13 | 385.15 | **+194.04** | **+5.40** |

Both identities check out exactly — and they are **different identities**:

- **cTrader** (`TradingEngineCBot.cs:534-542`, passes `pos.Commissions` / `pos.Swap` through raw):
  costs are **negative**, `Net = Gross + Commission + Swap`.
  Verify: `4690.28 + (−54.36) + (−10.10) = 4625.82` ✓
- **Tape** (`TradeCostCalculator.cs:65`): costs are **positive**, `Net = Gross − Commission − Swap`.
  Verify: `5756.50 − 16.31 − (−33.10) = 5773.29` ✓

The reconcile endpoint subtracts these raw. So "Commission delta = $70.67" is `+16.31 − (−54.36)` —
a comparison of a cost against a credit. The real statement is: **tape charged $16.31 where cTrader
charged $54.36** (tape undercharges 3.3×).

Everything the R2 document built on top of this is fiction:
- "tape refund model vs cTrader charge model" — no. Sign convention.
- "tape credits rollover, cTrader debits it" — no. Sign convention.
- "USDCAD has positive interest on short CAD positions due to the BOC-CAD rate differential" —
  invented. It is a `+`/`−` mismatch in our own persistence layer.

**This is the most important lesson in this document:** when two systems disagree about a number,
check the units and the sign convention *before* reaching for an economic explanation.

---

## 2. F2 — cBot partial-close applies the cost sign twice (real money bug, currently latent)

`src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:571-573`

```csharp
var commission = pos.Commissions * fraction;   // negative (cTrader convention)
var swap       = pos.Swap * fraction;          // negative
var netProfit  = grossProfit - commission - swap;   // gross − (−c) − (−s) = gross + c + s
```

Costs are **added to profit** instead of subtracted. Every partial close over-reports by twice the
costs. The full-close path (line 530) is correct — it uses `pos.NetProfit` straight from the venue.

Not caught by R2 because every R2 run used `stripAddOns: true`, so `PartialTp` never fired. It will
fire the moment R3 turns the add-on packs on, and it will silently inflate every partial-close
result. Fix before R3, not after.

---

## 3. F3 — `config/symbols.json` is fabricated placeholder data, and it is the root of the money divergence

Read the file. Every FX pair has `commissionPerLotPerSide: 3.5` — the same round number, fourteen
times. The swap rates are round invented values (`-0.5`, `0.2`, `-0.4`, `0.15`…). Crypto and indices
have `commission: 0` and `swap: 0` — free to trade, free to hold overnight, forever.

The one that matters most:

```json
{ "symbol": "XAUUSD", "swapLongPerLotPerNight": -5, ... }
```

Under the tape's convention (positive = cost), **−5 means the tape pays you $5 per lot per night to
hold gold long.** Real long-gold swap at a retail broker is a substantial *cost*. The DB confirms
the tape credited $33.10 of swap on the XAUUSD run while cTrader charged $10.10.

Now connect that to the scoreboard: **`trend-breakout/XAUUSD/H4` is the #1 ranked cell (score 100.0).**
It is a trend strategy, on gold, in a 2025 uptrend, holding long across many nights. Its edge is being
subsidised by a carry credit that does not exist. The top of our scoreboard is partly an artifact of
a made-up number in a JSON file.

---

## 4. F4 — The uncommitted `commissionPerMillion` "fix" is wrong, and makes XAUUSD dramatically worse

`src/TradingEngine.Services/Helpers/TradeCostCalculator.cs:54-56` (uncommitted):

```csharp
var commission = commissionPerMillion.HasValue
    ? lots * symbol.ContractSize * commissionPerMillion.Value / 1_000_000m * 2m
    : lots * symbol.CommissionPerLotPerSide * 2m;
```

`ContractSize` is denominated in **base-currency units**, not USD notional. cTrader's `--commission=30`
means *$30 per million of **USD** volume, per side*. The formula treats 100 ounces of gold as if it
were 100 US dollars.

**Evidence that cTrader uses USD notional** (two independent symbols agree, derived from the DB):
- XAUUSD: $54.36 ÷ 15 trades = $3.62 round-turn → $1.81/side → $60,400 USD volume → at ~$3,300/oz
  that is **0.18 lots**. Cross-check: the tape's own commission ($16.31 ÷ 14 ÷ $7/lot) implies
  **0.166 lots**. Consistent.
- USDCAD: $128.80 ÷ 13 = $9.91 round-turn → $4.95/side → $165k USD volume → **1.65 lots**. Consistent
  with a risk-based sizer on a 100k account.

**Error of the shipped formula, per 1 lot round-turn:**

| Symbol | Shipped formula | True cTrader charge | Error |
|---|---|---|---|
| USDCAD / USDJPY / USDCHF (USD base) | $6.00 | $6.00 | exact — correct **by luck** |
| EURUSD | $6.00 | ~$6.48 | 7% low |
| **XAUUSD** | **$0.006** | **~$19.80** | **~3,300× low** |
| **BTCUSD** | **$0.00006** | **~$3.60** | **~60,000× low** |

It is exact only for the pairs whose base currency *is* USD, which is why it looked fine on USDCAD.
On XAUUSD — the top-ranked cell — it deletes commission entirely. It is **strictly worse than the
model it replaced** ($7/lot round-turn), which was at least the right order of magnitude.

The correct model: `notionalUsd = lots × contractSize × baseToUsdRate`, charged **per side** — half at
open, half at close (cTrader charges the entry side at entry; we charge the full round-turn at close,
which distorts intra-trade equity and therefore max-drawdown, a 15% score component, and FTMO
survival, a 25% component).

**Do not commit this diff as it stands.** The `OrderEntry` override plumbing in the same diff
(`BacktestOrchestrator.cs:896-916`) is sound and worth keeping.

---

## 5. F5 — The R1 "252-cell sweep" was 28 commingled runs; the scoreboard is not a census

The PLAN's truth gate was `ExperimentRuns ≥ 225`. Actual:

```sql
SELECT COUNT(*) FROM ExperimentRuns;   -- 4
SELECT COUNT(*) FROM WalkForwardWindowResults;  -- 0
```

**Four rows.** The scoreboard states "248 below-floor cells: all have valid reasons recorded." Those
rows do not exist. D3 required null-with-reason to be *persisted*; it wasn't.

Worse, two different "cells" point at the same backtest:

```sql
SELECT VariantLabel, BacktestRunId FROM ExperimentRuns;
-- bb-squeeze/USDCAD/H4      efb77acf
-- trend-breakout/USDCAD/H4  efb77acf   <-- same run
```

That run contains **seven strategies trading one account simultaneously**:

```sql
SELECT StrategyId, COUNT(*) FROM TradeResults WHERE RunId='efb77acf' GROUP BY StrategyId;
-- trend-breakout 40 | bb-squeeze 25 | rsi-divergence 11 | super-trend 8
-- mtf-trend 7 | macd-momentum 5 | ema-alignment 2
```

The scoreboard's own footnote says "~31 minutes (**28 batched runs** + scoring pass)" — 14 symbols ×
2 timeframes, with all nine strategies packed into each run. Two consequences, both fatal:

1. **The strategies share one risk budget.** Exposure caps, heat caps, and concurrent-position limits
   mean trend-breakout's 40 positions were actively blocking ema-alignment's entries. A per-strategy
   trade count from a commingled run is *not* that strategy's standalone trade count. So the headline
   finding — "only 4 of 252 cells cleared the 20-trade floor" — is very likely an artifact of the
   batching, not evidence that the strategies are sparse. The agent recorded it as "the 20-trade floor
   is restrictive… by design". That was the moment to stop and ask why 248 strategies weren't firing.

2. **40% of every score is the wrong number.** `SetupScoreService.cs` filters trades by strategy
   (line 45) — correct — but then reads `run.MaxDrawdownPct` (line 59, **run-level**) and computes
   FTMO survival from account-wide `EquitySnapshots` (line 61, **run-level**). Drawdown (weight 15) +
   FTMO survival (weight 25) = 40 points of every cell's score, taken from a seven-strategy commingled
   equity curve and therefore **identical for every strategy in the run**.

R1 must be re-run with **one cell per run**. This is also the direct answer to "I didn't see much
other than runs in the UI": the research tables never got fed. The PLAN opened by noting *"every
research table is empty — this plan is what finally feeds them."* After R1, `ExperimentRuns` has four
rows and `WalkForwardWindowResults` has zero. That fact is still true.

---

## 6. F6 — Position sizing itself diverges between venues (~29% on USDCAD)

Derived from the commission figures against each venue's known per-lot rate: tape implies ~2.13
lots/trade on USDCAD, cTrader ~1.65. Same config, same risk profile, so the sizer is seeing different
inputs — almost certainly a different entry price (F7 below) producing a different stop distance and
therefore a different risk-based lot size.

This has never been measured directly. The reconcile compares aggregates; it does not compare
per-trade lots, entry prices, or exit prices. It needs to.

---

## 7. F7 — Limit entries are the right structural fix, and the machinery already exists

The owner's instinct here is correct and it is the highest-leverage change in this document.

Both venues already support resting orders:
- **Tape:** `TapeReplayAdapter.cs:318-345` (`PendingLimit` / `PendingStop`), fills at **exactly**
  `limit.LimitPrice` (line 451), with an ask-adjusted touch test (line 445).
- **cBot:** `TradingEngineCBot.cs:393` (`PlaceLimitOrder`), with a cancel-on-expiry path (line 477-483).

Why this closes the gap: a market order fills at *whatever price the venue happens to be at*, so the
tape's 1-bar entry and cTrader's 2-bar entry (the "F2 entry latency" gap) produce different fill
prices, which cascade into different stops, different lot sizes, different exits, and eventually
different trade counts. **A limit order fills at the price we named, on both venues.** The entry price
becomes identical by construction, not by luck. Residual divergence then collapses to exits and costs
— both of which are separately fixable.

The uncommitted `BacktestOrchestrator` change that plumbs per-run `OrderEntry` overrides through
`_configResolver.Resolve` is what makes this reachable from config. Keep it.

The one thing that must be verified before trusting it: **both venues must use the same touch rule and
the same expiry**, or one will fill an order the other cancels — and a fill/no-fill disagreement is a
trade-count divergence, the worst kind. This needs a contract test, not an assumption.

---

## 8. F8 — Concurrency: the kernel is safe; the plumbing is missing

The engine is a pure reducer — `EngineReducer`, `PositionLifecycle`, and `PreTradeGate` are static
pure functions with no mutable static state. Each run owns its `BacktestRunState`, its adapter, and its
DI scope. **Concurrent tape backtests are architecturally safe.**

What's missing is everything around it: there is no queue and no concurrency limiter in
`BacktestOrchestrator` (just a `ConcurrentDictionary` of runs). The real constraints are SQLite write
contention (WAL allows many readers but one writer), memory per run (each loads its bars), and the hard
rule that **cTrader must stay strictly serial** — one CLI instance, one desktop.

So: a bounded worker pool for tape, a strictly-serial lane for cTrader, and a persisted queue.

---

## 9. F9 — Progress is a calendar estimate on the cTrader leg

`BacktestOrchestrator.cs:1028` — the cTrader compare-child is created with
`BarsTotal = EstimateBarCount(...)`, a **calendar estimate** that counts weekends and holidays as
tradeable bars. The tape path already pre-queries the real bar count (P2.1, ~line 1185). That mismatch
is the "progress bar messed up / stuck around 70%" symptom: the denominator is inflated by the ~28% of
calendar time the market is shut.

Fix is server-side and cheap: resolve the true bar count for **every** venue before the run starts,
from the same query the tape path already uses.

---

## 10. What this means for the plan

| Claim on the tracker | Reality |
|---|---|
| "R2 COMPLETE — parity PASS (13:13 on 2m)" | Trade *counts* matched. The money comparison behind the PASS was computed with inverted signs. |
| "Old F6 regression is DEAD" | This one holds up — trade counts really are within ±1. It is the only R2 conclusion that survives. |
| "R1 — 252 cells scored, 4 above floor" | 28 commingled runs, 4 persisted rows, 40% of each score taken from a shared equity curve. |
| "Agent recommends PROCEED to R3" | R3 on this foundation would be a scored search over fabricated carry credits and a contaminated ranking. |

The PLAN's own words: *"a scored search on a diverged tape is worthless."* The gate fired correctly.
It was then talked out of firing by lengthening the window until the percentage looked acceptable —
which is exactly the failure mode a pre-registered gate exists to prevent.

**Do not proceed to R3.** Land the P-phases in `PLAN.md §P` first.
</content>
</invoke>
