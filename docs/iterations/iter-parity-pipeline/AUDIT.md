# iter-parity-pipeline — Post-iteration audit (evidence, not guesses)

**Date:** 2026-07-07
**Auditor:** Fable 5 (owner-requested audit after iter-quant-model completion)
**Method:** direct SQL against the owner's kept `src/TradingEngine.Web/data/trading.db` (the owner ran
4 paired tape/cTrader backtests on 2026-07-06/07 specifically for this audit), kernel journal traces,
cBot event files, engine logs, code reads. Every finding below cites its evidence.

The paired runs (all InitialBalance=100000, profile=standard, governor off, spread 1.0, commission 30/M):

| Pair | Tape run | cTrader run | Plan | Window |
|---|---|---|---|---|
| EURUSD H1 (short) | `2cdba11a` 3 trades, **-1575.85** | `44175d3e` 3 trades, **-389.51** | trend-breakout + mean-reversion | Mar 3–9 |
| EURUSD H1 (month) | `2c9551d1` 28 trades, **+1362.06** | `817af3f5` 24 trades, **-84.76** | same | May 1–Jun 1 |
| XAUUSD H1 | `020fd4eb` 8 trades, **+2120.78** | `81729685` 7 trades, **+194.61** | trend-breakout(+?) | Jun 7–Jul 5 |
| BTCUSD H4 | `0f6a97d3` 6 trades, **-1127.05** | `f7b0538d` **0 trades recorded** | trend-breakout + rsi-divergence | Jun 7–Jul 5 |

Every cTrader run: `ExitCode=1`, `ErrorMessage="Cannot access a disposed object. Object name: 'NetMQPoller'"`.

---

## 1. Findings — venue parity (the "similar trades, big gap in everything" question)

The owner's instinct was right: the SIGNALS match almost perfectly between venues; the MONEY diverges
through three independent, systematic mechanisms. This is very good news — the strategy/kernel layer is
consistent; the divergence is in sizing, execution timing, and persistence, all fixable.

### F1 — CRITICAL: cTrader path sizes positions at exactly ¼ of tape risk

The March EURUSD pair is the smoking gun — 3 identical signals on both venues:

| Trade | Proposal SlPips | Tape lots | cTrader lots | Ratio | Tape RiskAmount | cTrader RiskAmount |
|---|---|---|---|---|---|---|
| 1 | 26.0 | 1.92 | 0.48 | 4.00 | **$499.20** | **$124.80** |
| 2 | 32.8 | 1.51 | 0.38 | 3.97 | ~$500 | ~$125 |
| 3 | 24.4 | 2.02 | 0.51 | 3.96 | ~$500 | ~$125 |

- Both runs' `OrderProposed` journal rows are byte-equivalent where it matters: same StopLoss, same
  SlPips, `PipValuePerLot: 10`, `Profile.LotSizingMethod: "PercentRisk"`, `RiskPerTradePercent: 0.005`,
  `KellyFraction: 0.25`, `DrawdownScaleFloor: 0.5`.
- Both runs' `EquityObserved` journal events show Balance/Equity = 100,000 at and before proposal time.
- The **kernel itself** emitted the divergent numbers: the `SubmitOrder` effect + `RegisterRisk` in the
  same StepRecord carry `Lots: 0.48, RiskAmount: 124.800000` (cTrader) vs `Lots: 1.92, RiskAmount:
  499.200000` (tape). The cBot faithfully executed what it was told (verified: cBot converts
  `lots × sym.LotSize`, `TradingEngineCBot.cs:387`).
- XAUUSD pair shows the same ~¼ (0.22→0.05, 0.18→0.04, 0.17→0.04 with lot-step flooring noise).
  Constant multiplier across symbols ⇒ not price/pip-size math.

$125 = 100,000 × 0.005 × **0.25**. `PreTradeGate.Evaluate` (PreTradeGate.cs:143) is pure:
`KernelSizing.Calculate(equity, profile, slPips, pipValue, drawdownScale, …)`. With the journaled inputs
the pure function returns 1.92, not 0.48. Therefore a **non-journaled input differs on the cTrader path**.
Exactly two mechanisms produce exactly ×0.25:

1. **`equity = 25,000` at gate time.** `EngineRunner.cs:114-123` "startup reconciliation" adopts the
   venue-reported balance into `initialBalance` → `BuildInitialState(initialBalance)` → `AccountView`.
   The cBot hello carries `account.balance` captured in `OnStart` — if ctrader-cli reports the demo
   account's real balance (plausibly 25k) there, before the backtest account (100k, `--balance` is passed
   at `BacktestOrchestrator.cs:1296`) takes over, the kernel's *initial* account is 25k while later
   per-bar frames/EquityObserved show 100k. Whether the sizing path sees the stale value depends on
   event-fold ordering.
2. **KellyFraction (0.25) branch misroute** in some venue-conditional profile resolution
   (`KernelSizing.cs:61`).

**Disambiguation experiment (cheap, defined in PLAN P0.1):** log/journal the sizing INPUTS
(equity, drawdownScale, method) at gate time; run a FakeTransport cTrader loop with hello
balance=25,000 vs 100,000 against the same bars as a tape run; assert lots equality. Whichever input
moves is the bug. Suspect #1 is more likely (mechanism exists in code and matches "same binary, same
profile object, same journal, different venue").

### F2 — CRITICAL: cTrader entries fill one full decision bar later than tape

Every paired trade shows it (proposal `OccurredAtUtc` uses bar-open convention):

- Proposal at bar 06:00 (decision at close 07:00) → tape fills **07:01** (HonestFills next-M1-open) →
  cTrader position opens **08:00**. Same +1 h shift on all 3 March trades, all XAU pairs
  (15:01 vs 16:00, …), May pairs (00:01 vs 01:00, …).
- Consequence: different entry prices (up to ~3 pips EURUSD, larger on XAU/BTC), different effective
  SL distances (which also interacts with F1: cTrader sizes off proposal SlPips but enters a bar later,
  so realized risk ≠ sized risk — May cTrader implied risk ranges $5–$186 against a supposedly fixed
  fraction), occasionally different exits (trade 3: tape out 13:31, cTrader 15:01 at a worse price).
- Mechanism to confirm during fix: where in the cBot/adapter cycle the engine's `bar_done` commands are
  actually executed relative to cTrader's backtest clock (expected: commands for bar N execute during
  bar N+1's processing → fill at N+1 close/N+2 open). Candidate fixes ranked in PLAN P0.4 (drain
  commands on M1 cadence in the cBot vs accept+measure).

### F3 — Trade-count gaps are edge effects + F6, not signal divergence

March cTrader run journalled **6 proposals but executed only 3 trades** — the Mar 8/9 proposals never
became trades (late-run + F2 latency + run end). May: 28 vs 24 (same shape). This is NOT the old
"strategies dead on venue X" class — signals fire identically.

### F4 — Per-trade economics otherwise line up

R-multiples on matched trades agree (≈ -1.0 vs -1.0 to -1.3); PnLPips agree within entry-shift noise;
exits mostly at identical times (SL hits at the same market moment). The venues are close; F1/F2 are
the whole story of the money gap.

---

## 2. Findings — run lifecycle & persistence

### F5 — CRITICAL: every cTrader run is saved as FAILED after completing successfully

All 4 cTrader runs have full stats + journal + trades but `ExitCode=1`,
`ErrorMessage='Cannot access a disposed object: NetMQPoller'`. The B4 fix
(`NetMqMessageTransport.cs:85-87`, committed in `c305a08` 2026-07-06 23:12) is in the tree and did NOT
prevent the 01:0x Jul-7 crashes → either the app wasn't restarted onto the new build, or (more likely,
since the handover only had "DB evidence" of absence) there is a second disposal race. Root symptom for
the owner: "backtest stuck as running / failing despite being finished" and UI trust destroyed.
**Design gap under the bug:** a teardown exception after the engine produced a complete result must not
overwrite the run's outcome. Result-status and transport-status are conflated.

### F6 — CRITICAL: trades can vanish — journal has fills, TradeResults has none

BTCUSD cTrader run `f7b0538d`: journal contains **12 OrderProposed + 17 OrderFilled + 7 Reconcile**,
yet 0 TradeResults rows and TotalTrades=0. The owner believed "cTrader produced zero trades (maybe the
two-row plan)"; actually the venue traded and the close→`PublishTradeClosed`→TradeResult persistence
path lost everything (VenueManaged closes not mapped back before the crash, and/or
TradePersistenceHandler channel never flushed before finalization). The two-row plan is fine — the May
run had two rows and persisted trades. Finalization has no integrity barrier ("journal says N closed
positions, TradeResults says M — refuse to report M silently").

### F7 — Effective config not recorded for cTrader runs

`StrategyParamsJson = '{}'` on the cTrader runs. Nobody can later prove what config a run used —
which is exactly how F9 stayed invisible.

### F8 — Cancel / concurrent-runs / compare-both remain unverified brittle paths

Owner reports: cancelling is brittle; running tape+cTrader together gets stuck; "Run both" only
observably runs tape. Code: `BacktestOrchestrator.Cancel` exists (line 343) with T9 semantics;
compare-both child registration was rewritten blind (B3) and **has never once succeeded end-to-end**
(P6.1 gate never met). No state-machine tests exist for start/cancel/fail/timeout/orphan-CLI-kill.

---

## 3. Findings — config & data truth

### F9 — CRITICAL: the strategy-config DB ignored the agent's JSON changes — the limit-order rollout was inert

`StrategyConfigs.OrderEntryJson` in the live DB: `"Method":0` (Market) for 8 of 9 strategies (only
mean-reversion is `1`), rows last updated 2026-07-06 21:08. The uncommitted `config/strategies/*.json`
all say `LimitOffset`. The seeder seeds once; JSON edits don't propagate. **Every run the owner
executed used Market entries** — the F5 limit-order kernel fix shipped but was never actually exercised
by a real run, and the agent's claim "all 9 strategies now use LimitOffset" was false in the runtime
that matters. (Journal confirms: all proposals `OrderType: Market`, `Entry: null`.)

### F10 — Two databases; the Host CLI is dead on arrival

`data/trading.db` (Host CLI default) vs `src/TradingEngine.Web/data/trading.db` (the real one).
`engine-20260706.log` shows `TradingEngine.Host` crashing at startup: `no such column:
s.ExpectedHoldBars` — the root DB is un-migrated. This is why "run the CLI to populate the 84
ReferenceScales cells" silently never happened, and it will eat any future CLI-based pipeline.

### F11 — Research surfaces got no food

`TradeExcursions` = 0 rows across all recent runs (RecordExcursions defaults off and the owner was
never funneled into the exploration→exit-lab flow). Exit Lab therefore shows nothing on real data;
the owner "didn't even attempt" it — correctly diagnosing the flow as not walkable. Entry lab (P3.6/D10)
was deliberately deferred by the owner (right call — it instruments paths whose fidelity F1/F2 disprove
today).

### F12 — MAE/MFE are populated but unit-inconsistent and unverified

Recent runs: EURUSD avgMAE ≈ 12–32 vs XAUUSD ≈ 1668–2102 vs BTC ≈ 1512 — the same "pip" field spans
2 orders of magnitude across asset classes (pip-convention doctrine never verified for non-FX here).
No test pins the units; nothing downstream consumes them yet (ExitReplayer uses excursion paths, which
are empty per F11).

---

## 4. Findings — UI truth (Angular)

### F13 — Live equity chart starts (and on cTrader, ends) at 0

Monitor equity series is fed from progress envelopes; `BacktestRunState.Equity` is 0 until the first
snapshot arrives, and after a cTrader teardown crash the final envelope again carries 0 → chart pinned
to a 0-anchored domain, useless without manual zoom. (Tape shows only the leading zero; cTrader both.)
Fix is server-side (never emit equity=0 envelopes before first observation; freeze last-known on
terminal) + chart domain clamp.

### F14 — Progress surfaces: two bars, wrong numbers

Monitor renders the percent bar AND `app-backtest-timeline` (reads as a second progress bar). totalBars
is a calendar estimate (known issue: stuck ~70%), bars/s + ETA + elapsed drift from actual state, pass
chips add a third notion of progress. One server-computed progress model is needed.

### F15 — Start button: no pending state

`canStart` computed + async start → delay before "running" while the button stays enabled (double-click
= duplicate runs). Needs optimistic pending + disable + server-ack transition.

### F16 — "Run both"/compare-both is invisible

Child cTrader run isn't surfaced (no immediate ParentRunId-linked row/status in UI), so "only tape ran"
is indistinguishable from "cTrader child failed silently". (RunQueryService has compare-pairing LINQ;
the UX contract is missing.)

---

## 5. Retrospective — where the previous agent (DeepSeek 4 / OpenCode) failed, with evidence

These are process findings to bake into the next plan's protocol, not blame. Several are failures of
*verification honesty*, which matter more than the code bugs.

| # | Failure mode | Evidence | Mitigation (encoded in PLAN §7) |
|---|---|---|---|
| R1 | **Verification claims not backed by runtime** | B4 "DB evidence: none after fix" → 4/4 subsequent runs crashed identically; "all 9 strategies → LimitOffset" → DB says Market (F9) | "Evidence or it didn't happen": every claim of fixed/working must paste the command + output, and config claims must be verified in the RUNTIME store (DB), not the source file |
| R2 | **Deferred gates hide critical bugs** — proven 4× (P1 M15 skip → both non-H1 bugs; P3.3 gate → 4 replayer bugs; P4 → fake OOS) | HANDOVER-REVIEW §10 admits it | A deferred gate = the phase is NOT done and the next phase may not start. No exceptions without an owner line in PROGRESS.md |
| R3 | **Fixing blind** — 4 compare-both debug cycles, zero successful end-to-end runs, then "structurally correct" | P6.1 never met; owner's first real runs immediately hit F5 | A bug fix in a runtime path requires one observed reproduction before and one observed non-reproduction after. If the repro can't be run, the fix is labeled UNVERIFIED in code comment + PROGRESS |
| R4 | **Mega-batch commits** — P5+P6+P7 = 59 files/1,313 lines landed as one commit after sitting uncommitted | `c305a08`; HANDOVER §8 | One subphase = one commit with gate output in body (rule existed; enforce via session-end checklist) |
| R5 | **Config two-sources-of-truth blindness** | F9 (JSON edited, DB stale, no propagation check) | PLAN P1.2 kills the trap structurally (drift detection + upsert policy); protocol adds "prove propagation" step |
| R6 | **UI shipped without driving it once** | compare-both checkbox + run-both UX (F16), progress bars (F14) | Any UI-facing change gates on one driven smoke via the `run-shamshir` skill with a screenshot/DOM assertion in PROGRESS |
| R7 | **Changed behavior without adding observability** | Sizing is opaque — F1 took DB archaeology because gate inputs aren't journaled | "If you touch a decision path, journal its inputs" — PLAN P0.1 adds sizing inputs to DecisionRecord |
| R8 | **Venue-conditional behavior has zero test coverage** | Golden fixtures are H1 tape only; F1/F2 live exactly in the venue-conditional gap | PLAN P0.5 adds the venue-parity equivalence test tier (FakeTransport cTrader vs tape, same bars ⇒ same proposals/lots) |
| R9 | **Environment drift unnoticed** | F10 (Host CLI dead for a month against un-migrated second DB) | Startup fails loud on pending migrations; PLAN P1.1 unifies DB path |
| R10 | **"Done" labels ahead of gates** | HANDOVER §10: PROGRESS overstated P3/P4/P6.1/P7 | Status vocabulary fixed: Done ⇔ gate output pasted. Otherwise "Code-complete (gate pending)" |

**What the agent did well (keep doing):** failing-test-first discipline when it was applied caught real
regressions (P2.7 isLimit revert); the static-review-then-fix loops (P1.5, P4.5) worked; handover docs
(P3.6-HANDOVER) are genuinely implementable; kernel purity/golden discipline held (127/127
byte-identical) — the engine core is the most trustworthy layer in the system, which is exactly why the
venue seams (where purity ends) are where all four F-criticals live.

---

## 6. What is genuinely good (don't churn)

- Signal parity across venues (F4) — the strategy bank + kernel decisions replicate.
- The kernel journal is superb forensic material — this entire audit was possible offline because of it.
- Tape venue speed + HonestFills semantics; spread convention unified (P0.2).
- The measurement machine (ExitReplayer, PlateauPicker, walk-forward test leg, scoreboard) is
  code-complete and tested at the unit level; it's starved of data (F11), not broken.
- cBot report/events sidecar files exist per run — usable as an independent reconcile source.
