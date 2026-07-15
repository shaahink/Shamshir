# Parity Truth 2 — why cTrader and the tape were never comparable

**Date:** 2026-07-11 (owner-delegated deep audit)
**Branch:** `iter/alpha-loop`
**Status:** SUPERSEDES the "P0–P3 DONE / parity closed" claim in `TRACKER.md`.
**Method:** DB query + cBot venue report (`shamshir-report.json`, `shamshir-events.json`) + cAlgo API
metadata (`cAlgo.API.xml`, cTrader.Automate 1.0.17). Every claim carries the arithmetic that produced it.

---

## 0. Verdict

P0–P3 fixed four real defects (F24, F30, F31, F32) — and none of them were the reason the two venues
disagree. **Three master defects were sitting underneath the whole time**, and every prior parity
session missed all three because every prior session tested on **XAUUSD**, the one symbol where the
biggest of them produces a plausible-looking number instead of an absurd one.

| # | Defect | Effect |
|---|---|---|
| **F33** | cBot passes **absolute prices** into cAlgo parameters that mean **pip distance** | Every cTrader SL/TP ever placed was at the wrong distance |
| **F34** | The cTrader account is **EUR-denominated**; the engine assumes **USD** everywhere | Every cTrader money figure is scaled by 0.8638 |
| **F36** | Runs finish `completed`/`exitCode 0` while reporting **all trades unreconstructable** | The owner's manual runs were 100% fabricated economics, presented as clean |

The owner's own manual runs are the proof. They are not a subtle divergence:

| Cell | tape gross | cTrader gross | tape comm | cTrader comm |
|---|---|---|---|---|
| BTCUSD H4 | 9,037 | **73,179** | −1,685 | **−83.56** |
| EURUSD H4 | −2,032 | **+74** | −401 | **−59.12** |

---

## 1. F33 (MASTER) — every cTrader stop-loss and take-profit is placed at the wrong distance

`TradingEngineCBot.cs:434/441/447` calls three cAlgo methods, passing the engine's **absolute SL/TP
prices**:

```csharp
result = PlaceLimitOrder(tradeType, symbol, volumeInUnits, limitPrice, clientOrderId,
    slPrice > 0 ? slPrice : null, tpPrice > 0 ? tpPrice : null);   // <-- prices
```

Those overloads are the **deprecated** ones (hence the `#pragma warning disable CS0618` wrapped around
each call — the compiler was warning us and we silenced it). From `cAlgo.API.xml`, cTrader.Automate
1.0.17:

```
ExecuteMarketOrder(tradeType, symbolName, volume, label, stopLossPips, takeProfitPips)
      -> stopLossPips:   "Stop loss in pips"
      -> takeProfitPips: "Take profit in pips"
```

The modern overloads take a `ProtectionType` (`Relative` = distance, `Absolute` = price) precisely to
remove this ambiguity. We use neither — we pass a **price** where the API wants a **pip distance**.

So the venue places protection at `intendedPrice × pipSize` away from entry, instead of
`|intendedPrice − entry|`.

### Proof 1 — EURUSD (`e9742585`), pipSize 0.0001

Engine's own journal (`OrderProposed`, order `00000001`):
`LimitPrice 1.159730 · StopLoss 1.162535 · TakeProfit 1.154125 · SlPips 28.05`

We asked for a **28-pip stop**. cTrader received `stopLossPips = 1.162535` → a **1.16-pip stop**.
The venue's own event log:

```
Create Position  | Short | entry 1.16156
Stop Loss Hit    | Short | close 1.16173 | pips -1.7
```

Stopped out **1.7 pips** from entry, on a 28-pip stop. Every EURUSD trade opened and closed in the
same minute. That is not a strategy result; that is a broken order.

### Proof 2 — BTCUSD (`7ccbc592`), pipSize 0.1

Journal: `LimitPrice 75973.16 · StopLoss 77083.609 · TakeProfit 73752.262 · SlPips 11104.49`

Predicted under the pips-misreading: TP distance = `73752.262 × 0.1` = 7,375.23 →
TP fires at `75973.16 − 7375.23` = **68,597.9**.
Venue actually closed that short at **68,581.8** (Take Profit Hit). ✓

The long, symmetrically: SL distance = `76601.01 × 0.1` = 7,660.1 → stop at `77234.21 − 7660.10` =
**69,574.1**. Venue closed it at **69,541.9**, labelled *"Stop Loss Hit"*. ✓

A long entered at 77,234 with a stop at 76,601 was allowed to run to **69,542** — a −29,500 loss on a
position that should have been cut at −633. The residuals (16 and 32 points) are spread/slippage.
**Confirmed on both symbols, both directions, SL and TP.**

### Why four sessions of parity work never saw it

Error magnitude is `intendedPrice × pipSize`, so it scales with the *price of the instrument*:

| Symbol | pipSize | Intended stop | Stop cTrader actually placed | Looks like |
|---|---|---|---|---|
| EURUSD | 0.0001 | ~28 pips | **~1.2 pips** | instant stop-out — obviously broken |
| BTCUSD | 0.1 | ~1,110 pts | **~7,708 pts** | position runs for days — obviously broken |
| **XAUUSD** | 0.1 | ~$30 | **~$330** | *a plausible gold stop* — **invisible** |

Every compare-both config used for P0–P3 parity was XAUUSD. The one symbol that hides the bug is the
one we standardised on. That is the whole story of why this survived.

---

## 2. F34 (MASTER) — the cTrader account is in EUR; the engine models USD

For every one of the 7 BTCUSD trades, `reportedGross ÷ (quantity × priceMove)` is **exactly 0.86376**:

```
Short 4.50  76,977→68,582  move 7,395.43  gross 28,745.42  factor 0.86376
Long  4.44  77,234→69,542  move −7,692.27 gross −29,500.56 factor 0.86376
Short 4.10  75,829→68,479  move 7,350.23  gross 26,030.20  factor 0.86376
... all 7 identical ...
```

`1 / 0.86376 = 1.1577` — EURUSD. It reproduces on EURUSD trades too (3.56 lots × 1.7 pips = $60.52 USD
→ reported **−52.27**, i.e. EUR). The venue account is **EUR-denominated**; every price, pip value, and
risk calculation in our engine is USD.

The cBot **never sends `Account.Currency`** (`OnStart`'s hello carries only `balance` and `equity`) —
so nothing downstream could ever have noticed. Consequences:

- Every cTrader money figure is ~13.6% below the tape's by construction, before any real divergence.
- Worse: the tape models a **USD 100,000** account and cTrader a **EUR 100,000** account. Those are
  *different accounts* (~$115,800). Risk-based sizing therefore diverges on the very first trade — this
  is the never-explained "F6 position-size divergence" from `PARITY-TRUTH.md §6`.

**No code change makes a EUR account and a USD account agree.** This one needs the account to match the
model (see §5).

---

## 3. F35 / F36 / F37 — the integrity failures that let F33 and F34 hide

**F36 — lossy runs are reported as clean.** Both of the owner's manual runs carry warnings that were
silently tolerated:

- BTCUSD `7ccbc592`: `TRADES_LOST:7:0` — *"journal had 7 closes, **0 persisted**; backfilled 7"*
- EURUSD `e9742585`: `TRADES_PARTIALLY_UNRECONSTRUCTABLE:3` — *"3 close-fill(s) could not be paired
  with open-fill + proposal data; **economics not recovered** for those"*

Both runs still ended `completed`, `exitCode 0`, and rendered in the UI as normal results. **100% of
the trades in the owner's manual cTrader tests were reconstructions, and the EURUSD ones failed.** A
run that cannot account for its own trades must not be presentable as a result.

**F35 — `OrderEntryMethod` lies.** Both runs record `Market` on their cTrader trade rows; the journal
proves every order was `Limit` (`OrderType: "Limit"`, `Entry.Method: "LimitOffset"`). The
reconstruction path never sets the field, so it takes `TradeResult`'s default (`TradeResult.cs:27`
`OrderEntryMethod = "Market"`). Anyone debugging parity from the trades table is reading a fabricated
column.

**F37 — the protection-repair guard can't fire.** `TradingEngineCBot.cs:478`:

```csharp
if ((slPrice > 0 || tpPrice > 0) && pos.StopLoss != slPrice && pos.TakeProfit != tpPrice)
```

`&&` — it repairs only when *both* levels differ. And it sits on the market-order path only: for a
pending order `result.Position` is null, so it is never reached at all. P2's flip to limit entries
(D11) therefore removed the one code path that was partially masking F33 for market orders — which is
exactly why the divergence became glaring in the owner's runs immediately after P2/P3 shipped.

---

## 4. What this says about the method, not just the code

Every one of these was reachable from data we already had. What blocked it:

1. **One test symbol.** XAUUSD was the standard parity cell. It is the only liquid symbol in the book
   where F33 produces a plausible number. Parity must be measured on a **currency pair and a
   high-priced instrument** — the two extremes of `pipSize` — or it proves nothing.
2. **Warnings treated as background noise.** `TRADES_LOST:7:0` names the defect in plain language. It
   was on the run the owner was looking at, and it was carried as a non-blocking warning.
3. **Never comparing the venue's own report against our DB.** `shamshir-report.json` had the true
   entry/exit prices all along; the −29,500 long that ran 7,000 points past its stop is visible on the
   first read of it.
4. **Trusting a green gate battery.** Build + Unit + Integration + Sim-fast were green for all of it.
   None of them place an order at a real venue.

---

## 5. The fix, in order

| # | Fix | Where |
|---|---|---|
| F33 | Pending orders → `ProtectionType.Absolute` (exact prices). Market orders → pip distance derived from the live reference, then snapped to the exact prices via `ModifyPosition(..., ProtectionType.Absolute)`. Delete every `CS0618` suppression. | `TradingEngineCBot.cs` |
| F37 | Repair guard `&&` → `||`; snap after *every* fill, including resting-order fills (`OnPositionOpened`). | `TradingEngineCBot.cs` |
| F34a | cBot declares `Account.Currency` in the hello; engine persists it on the run. | cBot + `CTraderBrokerAdapter` |
| F34b | **Hard-fail** any run whose venue account currency ≠ the modelled currency. A 13.6% silent scaling factor must never be possible again. | `BacktestOrchestrator` |
| F35 | Reconstruction must carry the real `OrderEntryMethod` from the proposal, not the record default. | trade backfill path |
| F36 | `TRADES_LOST` / `TRADES_PARTIALLY_UNRECONSTRUCTABLE` must **fail** the run, not warn. | `BacktestOrchestrator` |
| — | **Venue-intent invariant:** after each fill, assert the venue's *actual* SL/TP equals what we asked for, within a tick. This single check turns F33 from a four-session blind spot into an immediate, loud failure. | cBot → engine |
| — | **Parity harness (P4):** per-trade matching + pre-registered tolerance budget + one `VERDICT:` line, run on **EURUSD *and* BTCUSD *and* XAUUSD**. | `research parity` |

F34 is the only one that is not purely a code fix: a EUR venue account cannot be reconciled against a
USD model. Either the cTrader account becomes USD-denominated, or the engine models the account in the
venue's declared currency. The code change here is to make the mismatch **impossible to ignore**.

---

## 6. Results — live compare-both, before vs after

Both cells are the owner's own manual tests, re-run unchanged. cTrader figures are in EUR (F34); the
USD column divides by the venue-declared 0.86376.

### EURUSD H4, trend-breakout, 2026-05-11 → 06-11

| | tape (before) | cTrader (before) | tape (after) | cTrader (after) |
|---|---|---|---|---|
| Trades | 3 | 3 | 3 | **3** |
| Gross | −2,032.75 | **+74.44** | −1,525.07 | −812.03 EUR (−940 USD) |
| Commission | −401.40 | −59.12 | **−34.44** | −29.50 EUR (**−34.15 USD**) |

- **Commission parity: 0.85%** (−34.44 vs −34.15 USD) — inside the ≤2% budget in PLAN §P4.
- **Stop-loss prices now identical to the tick on all 3 trades** (1.16254 = 1.16254, 1.16330 = 1.16330,
  1.16350 = 1.16350). Before: cTrader was stopping out at −1.7 pips on a 28-pip stop.
- **Exit timestamps now identical on all 3 trades** (05-28 12:26, 06-01 13:13, 06-04 10:49).
- Lots effectively identical (1.78/1.78, 1.45/1.46, 1.70/1.71). Before: 1.45 vs 2.93.
- `OrderEntryMethod` now correctly reads `Limit` on the cTrader rows (F35).

### BTCUSD H4, trend-breakout, same window

| | tape (before) | cTrader (before) | tape (after) | cTrader (after) |
|---|---|---|---|---|
| Trades | 8 | 7 | 6 | **6** |
| Gross | 9,037.72 | **73,179.17** | 1,487.00 | 1,584.02 EUR (1,834 USD) |
| Commission | −1,685.22 | −83.56 | **−10.29** | −8.50 EUR (**−9.84 USD**) |

- **Trade count exact (6:6).** Commission within 4.4%. The 8× gross gap and 20× commission gap are gone.

### Integrity signals now surface

The compare-both cTrader leg previously stored `WarningsJson = NULL` on every run — because
`RunCompareBothAsync` was a parallel copy of the finalize block that never called `MergeWarningsJson`
and never ran the trade-persistence barrier. **The one leg every parity claim is measured against was
the only leg with no integrity checks on it.** It now reports:

```
BAR_STREAM_TIMEOUT
VENUE_CURRENCY_MISMATCH:EUR   <- the venue declaring its own currency, from its own ledger
TRADES_PARTIALLY_UNRECONSTRUCTABLE:1
TRADES_LOST:5:1
```

## 7. What is still open

1. **F34 (currency) is detected, not resolved.** Every cTrader money figure is still EUR. Until the
   venue account is USD-denominated (or the engine models the venue's currency), cross-venue *money*
   comparison carries a ~13.6% scaling factor. Trade counts, prices, lots and timestamps are unaffected.
2. **Entry-fill price divergence (F38, new).** The tape fills a limit at exactly the limit price; cTrader
   fills elsewhere (e.g. limit 1.15973, cTrader filled 1.16156). The journal shows
   `LimitPrice == SignalPriceMid` — the configured 5.25-pip `LimitOffset` is *not being applied*, so the
   order is submitted at the market's own price and is immediately marketable on one venue but rests on
   the other. This is now the single largest remaining source of gross-PnL divergence.
3. **`TRADES_LOST:5:1` reports `still-missing -1`** — the barrier's arithmetic
   (`Expected − Persisted − Backfilled`) can go negative; cosmetic but it means the counters double-count.
4. **`CBOT|...` Print output does not survive the cTrader CLI.** The orchestrator's `CBOT|TIMING` scan has
   therefore been dead code. Anything the cBot needs to tell the engine must go over NetMQ or into the
   report file — the `PROTECTION_MISMATCH` invariant currently rides the stdout channel and so is
   *unverified in practice*; it should be moved to the report.
