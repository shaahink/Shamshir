# Parity Truth 3 — the stale bar, and what the venues actually agree on now

**Date:** 2026-07-12
**Branch:** `iter/alpha-loop`
**Supersedes:** the F38 entry in `PARITY-TRUTH-2.md §7.2`, whose diagnosis was wrong.
**Method:** see `docs/reference/INVESTIGATION-METHOD.md` — written from this session.

---

## 0. Verdict

**F38 was misdiagnosed, and the real defect was much bigger than F38.**

Every bar the cBot has ever handed the engine was **one full bar stale**. The engine has been deciding on
4-hour-old prices (on H4) for the entire life of the cTrader path, and every order it produced reached the
venue a bar late. On a limit order that is fatal: by the time the order arrives, price has often traded
through the limit, so the "limit" is already marketable and cTrader fills it at market — *through* the
price we named. That is the divergence four sessions had been chasing.

| # | Defect | Effect |
|---|---|---|
| **F38** | cBot published `bars.Last(1)` — the bar *before* the one that closed | Every cTrader bar 1 bar stale; every order placed 1 bar late; limits arrive already marketable |
| **F40** | cTrader reports a rested order as `"Pending"`; `OrderState` had no such member | `Enum.Parse` threw → the adapter **abandoned the whole `bar_result` batch**, dropping sibling fills + the account update |
| **F41** | Commission computed in USD, booked against account-currency gross | 17% error on a non-USD account (exactly the FX rate) |
| **F42** | `CrossRateStore` was two hardcoded literals, refreshed only if the run streamed those symbols | EURGBP/EURJPY/GBPJPY priced off stale constants on **every** run that didn't trade GBPUSD/USDJPY |

---

## 1. F38 — the stale bar

### The diagnosis that was wrong

`PARITY-TRUTH-2` said: *"the journal shows `LimitPrice == SignalPriceMid`, so the configured 5.25-pip
LimitOffset is never applied."*

It is not applied-or-not — those two fields are equal **by construction**. `BarEvaluator.cs:227` passes
`entryPrice` (which *is* `intent.LimitPrice` for a limit order, set at line 157) into the `SignalPriceMid`
parameter of `OrderProposed`. The journal field is mislabelled; the offset was fine all along. The
`SubmitOrder` effect proves it, and proves both venues got the same order:

```json
{"OrderId":"00000001-…","Direction":"Short","Lots":1.78,
 "LimitPrice":{"Value":1.15973},"StopLoss":{"Value":1.162535},"OrderType":"Limit",
 "Entry":{"Method":"LimitOffset","LimitOffsetPips":5.25, …}}
```

Identical on the tape leg and the cTrader leg. **The engine was never the problem.**

### The discriminating fact

The tape filled that sell limit at exactly **1.15973**. cTrader opened the position at **1.16156** — 18
pips *through* the limit. A resting sell limit cannot fill 18 pips better than its limit price. So the
order was **not resting** when price crossed it.

### The measurement

Instrumented the cBot to record, per submit, the venue's own clock and quote (`orderSubmits[]` in
`shamshir-report.json`):

| engine proposed (bar open) | cBot submitted | bid at submit | our sell limit | |
|---|---|---|---|---|
| 05-28 **01:00** | 05-28 **09:00** | 1.16156 | 1.15973 | below bid → **marketable** |
| 06-03 **09:00** | 06-03 **17:00** | 1.16004 | 1.16059 | |

Eight hours — two H4 bars — from the bar the engine decided on to the order reaching the venue. Decide at
a bar's close and it should be four.

Then measured the bar handover itself (`barClock[]`): bar open time vs venue clock at publish.

```
bar 05-11 01:00  ->  published 05-11 09:00    gap 8h      <- must be 4h on H4
bar 05-11 05:00  ->  published 05-11 13:00    gap 8h
```

A steady 8h. A bar published *at its close* is a 4h gap. So `bars.Last(1)` was the bar **before** the one
that just closed.

### The fix

`TradingEngineCBot.OnBarClosed`: `bars.Last(1)` → `bars.Last(0)`.

**Not lookahead.** `Last(0)` at `BarClosed` is the bar that has just *finished* — proven by the same
instrument: a still-forming bar would read a 0h gap, not 4h. After the fix the instrument reads exactly
**4h on every bar**, which is both the correctness proof and the regression guard.

---

## 2. Result — EURUSD, live, before vs after

Same cell (`eurusd-h4-tb-f33.json`), same window, re-run.

| | before | after |
|---|---|---|
| Trade count | 3 : 3 | 3 : 3 |
| **Entry price** | **18+ pips apart** | **0.00 / 0.00 / 0.20 pips** |
| **Open timestamp** | hours–days apart | **identical to the minute** |
| Lots | 1.78 / 1.78 | 1.78 / 1.78 |
| Limit arrives | already marketable → filled at market | **rests correctly** on every order |

The entry-fill divergence — the largest remaining item on the tracker — is closed.

---

## 3. Currency: the account denomination is now a configured value

`Account:Currency` (default `USD`) is the single place the denomination is named. It stamps every
`SymbolInfo.AccountCurrency`, decides which cross-rate legs a run must source, and is checked against the
currency the venue declares.

`CrossRateStore` was rewritten from two hardcoded literals (`GbpUsdRate = 1.2650`, `UsdJpyRate = 149.50`)
into a **USD-pivot table** fed from market data. Any pair chains through USD, so a currency costs *one*
leg, not a leg per pair. A missing leg throws — a wrong cross rate is a wrong lot size.

`CrossRateSeriesLoader` resolves the legs a run needs (account currency + every traded symbol's base/quote)
and loads them from the tape before the run starts. A leg it cannot source **fails the run**.

### Proved, not asserted (R9)

Flipping `Account:Currency` to `EUR` — the venue's actual denomination — and running the full pipeline
found two bugs that no amount of re-reading would have:

1. Two of three `EngineHostOptions` sites never received the currency, so the **cTrader leg still modelled
   USD** while the tape modelled EUR.
2. Commission was computed in USD and booked against EUR gross — a **17%** error, exactly the EURUSD rate.

Both fixed. The flip then produced:

| | USD model vs EUR venue | EUR model vs EUR venue |
|---|---|---|
| Lots | 1.78 / 1.78 … but sized against different accounts | **identical: 2.06/2.06, 1.70/1.70, 1.97/1.97** |
| Commission delta | — | **0.5%** (budget ≤2%) |
| Gross delta | 13.0% | **2.6%** |
| Currency warning | `VENUE_CURRENCY_MISMATCH:EUR` | **none** |

Identical lots on both venues closes the never-explained **"F6 position-size divergence"** open since
`PARITY-TRUTH.md §6`. It was never a sizing bug — it was two legs modelling two different accounts.

**GBP is now the same one-line flip**, plus the GBPUSD leg the loader already reads.

---

## 4. Open — needs the owner

### 4.1 The cTrader account is still EUR

The engine account **5834367** is configured and in use (`--account=5834367`, verified in the launch
args), and the venue **declares EUR**. Per D10 the venue is the authority — `Account:Currency` is now `EUR`
in config, making the engine model agree with the venue. A USD-modelled run against an EUR account carries
a ~13.6% factor on every money figure; EUR → EUR removes that. Counts, prices, lots and timestamps are
unaffected.

*The engine no longer cares which way this goes* — set `Account:Currency` to match the venue and
everything reconciles (proved above in EUR).

### 4.2 The tape fills stops optimistically (FIXED — close-through-stop model added)

cTrader is the venue we trade; the tape exists to mimic it.

**Diagnosis:** The tape's gap-through logic in `ProcessSlTpHits` only checked `bar.Open` — it modelled
"the bar OPENED through the stop." cTrader also fills through the stop when the bar MOVES through it
mid-bar: the stop order triggers at the SL level, becomes a market order, and fills at whatever price the
market offers next — which in a bar that closed past the stop is worse than the SL.

**Evidence** (from run pair 792829b1/d64d9488):
```
Trade 2 (Long, SL=1.163295): H4 bar Open=1.16474, Close=1.16298, Low=1.16045
  tape: fill at SL = 1.163295 (no gap-through — Open > SL)
  cTrader: fill at 1.16283 (46.5 ticks through SL — bar moved through stop)
```

The bar's Close (1.16298) < SL (1.163295), so the market ended past the stop. The tape filled at the
optimistic SL price; cTrader filled at the worse market price.

**Fix** (`TapeReplayAdapter.ProcessSlTpHits` + `BacktestReplayAdapter.ProcessSlTpHits`): extended the
gap-through check to also trigger when the bar *closed* past the stop:
```csharp
// When the bar CLOSED past the stop, the fill executed at a worse price.
else if (trade.Direction == TradeDirection.Long && checkBar.Close < trade.StopLoss.Value)
    fillPrice = checkBar.Close;
else if (trade.Direction == TradeDirection.Short && checkBar.Close > trade.StopLoss.Value)
    fillPrice = checkBar.Close;
```
Added `LogDebug` instrumentation on every SL exit recording bar Open/Close, stop, spread, gapThrough, fillPrice.

**Impact on Trade 2:** fillPrice = bar.Close = 1.16298 (was 1.163295). Gap vs cTrader: 46.5 ticks → 15 ticks.
The residual gap is the bar-level model's limit — cTrader fills at a tick within the bar, not at its close.

### 4.3 BTCUSD diverges on trade count (6 vs 9)

Not a data problem — every cTrader BTCUSD fill lands inside the tape's own bar range. The cTrader leg
carries `TRADES_LOST:7:2` and `TRADES_PARTIALLY_UNRECONSTRUCTABLE:2`, so some of those 9 are
reconstructions (two share an identical entry price *and* timestamp). Trade capture on the cTrader leg
(F36) is still lossy and must be fixed before BTCUSD parity means anything.

---

## 5. P4 — the gate exists and it is honest

`research parity --tape <runId> --ctrader <runId>` → per-quantity check against the **pre-registered**
tolerance budget (PLAN §P4b), one `VERDICT:` line, **exit 1 on FAIL**.
API: `GET /api/backtest/analytics/parity?tape=&ctrader=`.

Current reading on EURUSD (EUR model vs EUR venue, with account=5834367):

```
  [PASS] TradeCount   exact                    -> tape=3 ctrader=3
  [FAIL] EntryPrice   ≤ 1 tick                 -> worst 2.0 ticks
  [PASS] Lots         exact                    -> worst delta 0.00
  [FAIL] ExitPrice    ≤ 1 tick on ≥95%         -> 0% within, worst 46.5 ticks
  [PASS] Commission   ≤ 2%                     -> (EUR model matches EUR venue)
  [PASS] NetPnL       ≤ 1% of gross            -> (EUR model matches EUR venue)
VERDICT: FAIL parity EURUSD …
```

**The remaining FAILs are ExitPrice (tape stop-fill model, §4.2) and EntryPrice (1 tick on 1 trade).**
Per PLAN §P4 ("Any FAIL → STOP and escalate to the owner; do not widen the window, do not widen the
tolerance") the tolerances have **not** been touched. Commission and NetPnL now pass with the EUR account.

The gate also refuses to pass a cell with zero matched trades (XAUUSD, this window) rather than reporting
a vacuous `0 == 0` PASS.
