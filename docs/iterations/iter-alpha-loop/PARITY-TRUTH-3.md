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

The new demo account **5857867** is configured and in use (`--account=5857867`, verified in the launch
args), and the venue **still declares EUR**. So either the account is not USD-denominated, or cTrader's
backtest deposit currency is set in the desktop settings rather than by `--account`.

Until that is settled, a USD-modelled run against it carries a ~13.6% factor on every money figure, and
the guard says so on every run. Counts, prices, lots and timestamps are unaffected.

*The engine no longer cares which way this goes* — set `Account:Currency` to match the venue and
everything reconciles (proved above in EUR). But the model and the venue must agree.

### 4.2 The tape fills stops optimistically

cTrader is the venue we trade; the tape exists to mimic it. On exits they do not yet agree:

```
exit gap  8.5 ticks  Short opened 05-28 05:34  (tape 1.162635 vs venue 1.16255, SL)
exit gap 46.5 ticks  Long  opened 05-29 17:24  (tape 1.163295 vs venue 1.16283, SL)
exit gap  9.5 ticks  Short opened 06-03 13:31  (tape 1.163595 vs venue 1.1635,  SL)
```

The tape fills a stop at **exactly** the stop price. cTrader fills *through* it — which is what a real
venue does. **The tape is the one that is wrong**, and it is wrong in the optimistic direction, so every
tape-measured expectancy is currently flattering. This is the next thing to fix, and it is a change to
`TapeReplayAdapter`'s exit model, not to cTrader.

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

Current reading on EURUSD (USD model vs EUR venue):

```
  [PASS] TradeCount   exact                    -> tape=3 ctrader=3
  [FAIL] EntryPrice   ≤ 1 tick                 -> worst 2.0 ticks
  [PASS] Lots         exact                    -> worst delta 0.00
  [FAIL] ExitPrice    ≤ 1 tick on ≥95%         -> 0% within, worst 46.5 ticks
  [FAIL] Commission   ≤ 2%                     -> …
  [FAIL] NetPnL       ≤ 1% of gross            -> 10.97%
VERDICT: FAIL parity EURUSD …
```

**This FAIL is the correct output.** Per PLAN §P4 ("Any FAIL → STOP and escalate to the owner; do not
widen the window, do not widen the tolerance") the tolerances have **not** been touched. The three FAILs
are exactly §4.1 (money, from the EUR account) and §4.2 (exits, from the tape's stop model); `EntryPrice`
misses by a single tick on one of three trades.

The gate also refuses to pass a cell with zero matched trades (XAUUSD, this window) rather than reporting
a vacuous `0 == 0` PASS.
