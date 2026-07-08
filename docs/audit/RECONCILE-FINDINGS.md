# Reconciliation — methodology + expected divergences

**Status:** harness built + unit-tested; LedgerReconcileService + GET /api/backtest/analytics/reconcile endpoint
built (iter-tape-trust T2). A real end-to-end reconcile is owner-run (needs a cTrader run).

## Pre-registered fidelity gaps — expect these in V4 (tape vs cTrader)

Before running the tape-vs-cTrader reconcile, register these KNOWN modelling gaps so they aren't mis-triaged
as bugs. `RawMoney` divergence from any of these is EXPECTED — the fix is in iter-tape-trust T3.

| # | Gap | Expected effect |
|---|---|---|
| **F1** | **No spread cost on entry/exit fills.** Both replay venues fill at mid; cTrader fills buys at ask, sells at bid, and detects exits on the opposite side. | Per-trade RawMoney divergence ≈ spread × pipValue × lots per round turn. EURUSD ~1 pip ≈ $10/lot. Systematic: tape is optimistic. |
| **F2** | **Intrabar floating equity not snapshotted.** `EmitAccountUpdate` fires per decision bar only, not per exit-TF bar. Intrabar equity troughs are invisible. | Tape MaxDD understates cTrader's floating DD — the "DB MaxDD=0 vs venue 4.6%" pain survives on the fast path. |
| **F3** | **Trailing/breakeven cadence.** Trailing stops update once per decision bar; cTrader trails per-tick. | Trailing exits systematically later/looser than cTrader. Sizing unknown — measure first. |
| **F4** | **Gap-through fills at exact stop price.** A bar opening beyond SL fills at the stop, not the (worse) open. | Optimistic on weekend/news gaps; FTMO daily DD punishes these tails. |
| **F6** | **Trade count divergence.** Tape consistently produces 34-83% more trades than cTrader for identical configs (both Market entry, same window, same DatasetId). Exit R-multiples are near-identical between venues. | Cause unknown. Hypotheses: HonestFills (tape delays entries → different cooldown windows → more re-entry opportunities), subtle indicator/value differences. Needs clean compare-both run post-B4 fix.

## What the harness does
`LedgerReconciler.Compare(engineLedger, venueLedger)` diffs two normalized `ReconcileLedger`s and classifies
each field difference:

- **RawMoney** (NetProfit / GrossProfit / Commission / Swap) — should agree **to the cent**. On the cTrader
  path the cBot forwards cTrader's OWN `pos.NetProfit/Commissions/Swap` (verified: `TradingEngineCBot.cs`
  reads them straight off the position, `CTraderBrokerAdapter` ingests them). **A RawMoney divergence means a
  real bug**, not a modelling choice.
- **Aggregation** (MaxDrawdownPct / WinRatePct / equity curve) — **divergence is EXPECTED here**. The engine
  re-derives these from sparser data than cTrader has. This is the predicted root of the owner's "DB ≠ cTrader"
  pain.
- **TradeSet** (TotalTrades / WinningTrades) — count mismatches, typically from late settlement.

## Sources
- **Venue (oracle):** `shamshir-report.json` — written by `ShamshirTradeLogger` in the cBot, harvested by
  `CtraderReportHarvester`. Parsed by `ShamshirReportParser`. Carries cTrader's own per-trade economics.
- **Engine:** the run's DB row (`BacktestRunEntity`: NetProfit/GrossPnL/CommissionTotal/SwapTotal/
  MaxDrawdownPct/TotalTrades/WinningTrades/WinRatePct) + `Trades`. Mapped to `ReconcileLedger` by
  `LedgerReconcileService.BuildEngineLedgerAsync`.
- **Web endpoint:** `GET /api/backtest/analytics/reconcile?left={runId}&right={runId}` returns a full
  `LedgerReconciler.Compare` output (per-field divergences + text summary).

## Predicted divergences (from the static trace — confirm with a real run)
1. **MaxDrawdownPct: engine ≪ venue.** cTrader's MaxDD includes intrabar *floating* equity; the engine sees
   only per-bar account snapshots / realized trades, so its DD is systematically smaller (the known
   "DB MaxDD=0 vs venue 4.6%"). *Fix options (P5): feed per-bar account equity into the engine's DD, or on the
   cTrader path display the venue's DD as truth.*
2. **Swap: small gaps at rollover.** Swap is charged at daily rollover (triple Wednesday). Snapshot timing can
   differ. *Fix (P5): model rollover in `TradeCostCalculator`, calibrated from the oracle's per-trade swap.*
3. **TotalTrades: venue may undercount late-settled closes** (stats taken before final settlement).
4. **RawMoney: expected ~0.** If it isn't, that's the first bug to chase — start there.

## How to run a real reconcile (owner)

**P6.1 convenience (recommended):**
1. `POST /api/runs/compare-both` with body `{"configName": "eurusd-h1-1d.json"}`
2. After both runs complete, note the tape + cTrader run IDs from the log.
3. `GET /api/backtest/analytics/reconcile?left={tapeRunId}&right={ctraderRunId}`
4. Record findings in the V4 run table below. RawMoney divergence → bug hunt; Aggregation → P5 modelling.

**Manual (legacy):**
1. Run a short cTrader backtest (e.g. EURUSD H1, 1 day) with `--ReportPath` set so the cBot writes
   `shamshir-report.json` (already the default cTrader path).
2. `var venue = ShamshirReportParser.Parse(File.ReadAllText(shamshirReportPath));`
3. Build `engine` from the run's DB summary+trades (thin map).
4. `var report = LedgerReconciler.Compare(engine, venue); Console.WriteLine(report.ToText());`
5. Record which categories diverged in this file. RawMoney → bug hunt; Aggregation → P5 modelling.

## Weekly drift check (P6.3)

**Rule:** Run one compare-both reconcile per week while iterating on strategy/exit changes. If RawMoney
diverges from the prior week, STOP research and investigate the change. A growing divergence means the
tape venue is drifting from the oracle — every calibration result built on top of it inherits the drift.

**One-liner (API):**
```
POST /api/runs/compare-both  { "configName": "eurusd-h1-7d.json" }
→ wait for completion (~2–5 min for 7-day window with cTrader)
→ GET /api/backtest/analytics/reconcile?left={tapeRunId}&right={ctraderRunId}
→ check: RawMoney divergences = 0? MaxDD delta stable vs prior week?
→ if not → stop, create F-id, investigate before resuming research
```

**Health check endpoint:** `GET /api/system/reconcile-health` returns `daysSinceLastReconcile` and the
latest compare-both run IDs. The backtest dashboard shows a gray info chip: "Last reconcile: N days ago"
when N > 7.

## Pre-registered gap: F5 — kernel-path limit orders reach cTrader as Market (P6.4)

**Discovered:** P2.7 (stop orders) — the `CTraderBrokerAdapter.SubmitOrderAsync` derives `isLimit` from
`entryOpts?.Method == OrderEntryMethod.LimitOffset`, but on the kernel production path
(`Kernel.cs` → `EffectExecutor`), `entryOpts` is always null — meaning EVERY kernel-path order
(including genuine domain `Limit` orders from `OrderEntryMethod.LimitOffset`-configured strategies)
has always gone out to cTrader as `"Market"`. The default `OrderEntry.Method` for all 9 shipped
strategies is `LimitOffset`, so **every historical cTrader run has been placing Market orders under
the hood**.

**Expected effect in V4 reconcile:**
- Per-trade entry-price deltas: a Limit fill at the limit price vs a Market fill at the NEXT bar's open
  (± spread). Deltas ≈ spread/2 per entry on average, systematic in the tape's favor.
- Trade counts should be identical (same signals, just different fill mechanics).
- RawMoney divergence from this gap is additive with F1 (spread cost) — they compound, not cancel.

**Investigation methodology (P6.4):**
1. Run compare-both with a config that uses a LimitOffset strategy (all shipped defaults do).
2. In the reconcile output, inspect per-trade entry prices between tape and cTrader.
3. Classify each trade: "Market fill" (entry price ≠ limit price ± spread tolerance) or "Likely Limit"
   (entry price ≈ limit price).
4. If ≥80% of cTrader entries are Market fills, F5 is confirmed — create a P6.4 fix commit.
5. The fix: thread `request.Type` through the kernel path into `entryOpts` so the cTrader adapter can
   derive `isLimit` correctly, matching what the replay venues already do (both replay adapters already
   correctly handle `OrderType.Limit` from the engine).

**Status:** Pre-registered, not yet confirmed. Awaiting owner's V4 compare-both run.

## P0.4 — Measured entry-latency (F2), 2026-07-08 — measure-first per Q4

**Status:** MEASURED (credential-free, from the kept audit DB) + instrumented into the reconcile endpoint.
The real paired-run confirmation on the post-P0 build is OWNER-PENDING (needs cTrader creds), but the
number below is the gate output and it did **not** need a new run — the audited runs already hold it.

**Instrumentation:** `GET /api/backtest/analytics/reconcile?left&right` now returns `leftLatency` /
`rightLatency`, each `{ matchedTrades, unmatchedFills, delaySeconds{median,mean,min,max},
delayBars{median,mean,min,max}, trades[] }`. Per-trade latency = `TradeResult.OpenedAtUtc` (fill) −
journalled `OrderProposed.OccurredAtUtc` (proposal, bar-open convention), joined on `OrderId`.
`delayBars` is in decision-timeframe units. Pure math: `EntryLatencyAnalyzer` (Infrastructure). No cBot
or execution change (Q4).

**Measured (audit DB `src/TradingEngine.Web/data/trading.db`, joined proposal→fill on OrderId):**

| Pair (H1) | Tape run | Tape delay | cTrader run | cTrader delay | Venue gap |
|---|---|---|---|---|---|
| EURUSD Mar (3/3 both legs) | `2cdba11a` | **3660 s = 1.017 H1 bars** | `44175d3e` | **7200 s = 2.000 H1 bars** | **3540 s ≈ 1 H1 bar** |
| EURUSD May | `2c9551d1` | ≈3660 s | `817af3f5` | ≈7200 s | ≈3540 s |
| XAUUSD | `020fd4eb` | ≈3660 s | `81729685` | ≈7200 s | ≈3540 s |

(sqlite `julianday` prints 3659/7199 for some rows — floating-point rounding of the same 3660/7200; the
exact `DateTime` tick math in the analyzer returns 3660/7200. May/XAU join 1:1 only where OrderIds align
— a few older-run trades have no matching proposal and surface as `unmatchedFills`, itself F3-consistent.)

**Interpretation (the F2 gate):**
- **Tape delay ≈ 1 M1 bar.** 3660 s = the H1 decision bar itself (3600 s, proposal bar-open → bar close)
  **+ 60 s = exactly 1 M1 bar** — the HonestFills next-M1-open after the decision bar close. This is the
  gate's "tape delay ≈ 1 M1 bar".
- **cTrader fills one full decision bar later than tape.** 7200 s = the decision bar (3600 s) **+ 3600 s
  = one full H1 bar**. The venue entry-latency gap is **3540 s ≈ one H1 decision bar** — exactly AUDIT F2
  ("cTrader entries fill one full decision bar later than tape").

**Decision (Q4):** the lag is a **constant one-decision-bar** offset (not variable, not >1 bar), so per
Q4 the follow-up (M1-cadence command drain in the cBot) is deferred — it is a real fidelity gap but a
predictable, correctable one, and correcting it is out of P0's measure-only scope. Reconcile now carries
the number on every run so any drift from "constant 1 bar" is immediately visible.

**Repro (credential-free):** `dotnet test tests/TradingEngine.Tests.Integration --filter
"FullyQualifiedName~EntryLatency"` seeds the exact March-pair timestamps (tape 06:00→07:01, cTrader
06:00→08:00, incl. the cTrader trailing-'Z' quirk) into real SQLite and asserts tape=3660 s/1.017 bars,
cTrader=7200 s/2.0 bars, gap=3540 s. `EntryLatencyAnalyzerTests` pins the pure math.

## V4 run findings template (P6.5)

After each compare-both reconcile session, fill in this table and record new F-ids below it:

```
### V4 run — {date}, {window}, EURUSD H1, trend-breakout
| Check | Expected | Actual | Verdict |
|---|---|---|---|
| Trade count match | = | ? | ? |
| Entries within 1 bar | yes | ? | ? |
| RawMoney within tolerance | $0.00 | ? | ? |
| Net delta explained by spread (F1) | yes | ? | ? |
| MaxDD within F1-F4 range | yes | ? | ? |
| F5: Limit→Market entry deltas | present | ? | ? |

Tape RunId: ?
cTrader RunId: ?
Reconcile URL: GET /api/backtest/analytics/reconcile?left={tapeRunId}&right={ctraderRunId}

New F-ids discovered:
- F6: ...
- F7: ...

Unexplained divergences: ...
```
