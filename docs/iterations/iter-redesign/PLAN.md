# iter-redesign — Trustworthy Backtest Engine + Observability + Strategy/Add-on Split

**Author:** Claude (Opus) — investigation + plan, 2026-06-29
**Branch base:** `iter/strategy-system`
**Audience:** the OpenCode/DeepSeek implementation agent. Phased, failing-test-first, each phase gated by a machine-checkable check.
**Supersedes the diagnosis in:** `docs/iterations/iter-strategy-system/HANDOVER-P5-PROTECTION-TOGGLES.md` (its heat-cap/budget theory was a *symptom*, not the root cause — see §1).

---

## 0. Why this iteration exists (owner's words)

> "Even with all gates/protections disabled I get <15 signals in 3 months, but 11 in the *last month* of that same 3 months in a separate backtest. I want strategies to be plain entry/exit with opt-in add-ons (trail if I want). The journal isn't informative despite a kernel. Live backtest + equity/drawdown never show, no timeline moves. Can't see a chart for trade details. Trades page is raw, not linked to backtests. Backtests don't store all required info. Pre-bar/per-bar/journal exist yet I can't tell what the engine decided each bar and why. Guards/governor aren't confidently toggleable — I can't run a raw strategy and watch drawdown blow up as proof. Fix what we have if possible; otherwise consider a UI/desktop rewrite."

This plan is ordered by **leverage**: the single engine bug in §1 is the root cause of the headline symptom (fewer signals over a *longer* window), and several "UI is broken" symptoms are actually **downstream of bad/incomplete engine data**, not the framework. We fix the engine's correctness and data first, then observability, then UI. **Recommendation: keep Angular, do NOT rewrite the UI yet** (§9 / Phase 7 records the criteria to revisit).

---

## 1. ROOT-CAUSE DIAGNOSIS (evidence from the live DB, not theory)

All findings below were reproduced against the owner's working DB
`src/TradingEngine.Web/data/trading.db` using two real runs the owner launched on 2026-06-29:

| Run | Window | Bars | Trades | ExitCode |
|-----|--------|------|--------|----------|
| `38f2c7e9` | May 29 → Jun 29 (1 mo) | 500 | **11** | **-1** |
| `596bb202` | Mar 29 → Jun 29 (3 mo) | 1500 | **7** | **-1** |

### 1.1 🔴 THE root cause — leaked open book latches the risk gate (E1)

The 3-month run's `OrderProposed` decisions over time (from the `Journal` table):

```
Mar 31 → Apr 24:  9 proposals → ALL Accepted
Apr 28 → Jun 26: 26 proposals → ALL rejected (22× BudgetBlocked:lots=0.0100, then 3× MAX_EXPOSURE)
```

The risk snapshot (`Journal.RiskJson`) at the last accept (Apr 24) **and at every point after, through June**:

```json
{"balance":98075.35,"equity":98075.35,"floatingPnL":0,"openPositions":3, ...}
```

`openPositions` is **stuck at 3 forever**; equity is **frozen** for two months. The arithmetic
closes exactly: **10 accepted − 7 closed = 3 positions that never close.** Those 3 phantom
positions:

1. permanently inflate `SumWorstCase(openPositions)` (`totalOpenRisk`) → **every** later
   proposal fails `BudgetOk` / `MAX_EXPOSURE`, *even at the 0.01 minimum lot* (newRisk is
   irrelevant once `totalOpenRisk` alone exceeds the cap);
2. freeze realized equity (no further closes), so the budget never "recovers";
3. correspond to **85 `Illegal transition: Submitted × BarClosed`** records (§1.2).

**This is why a 3-month window yields *fewer* trades than a 1-month window.** The 1-month run
starts fresh on May 29 with an empty book, never hits the latch, and trades all of June (11
trades). The 3-month run hits the latch in late April → **0 trades in May & June** despite
processing every bar (its journal reaches sim-time Jun 29). It is **not** signal sparsity, **not**
heat caps, **not** budget tuning — it is a **leaked open book with no reconciliation/cleanup**.

**Mechanism (code):** `EngineReducer.HandleBarClosed` (`src/TradingEngine.Engine/EngineReducer.cs:239`)
iterates `state.Positions` and writes every position back via `newPositions[id] = nextPos`.
Positions are **never removed** from the dictionary, and there is **no timeout / reconciliation /
force-close** for a position that fails to reach a closing fill. Once ≥1 position is stuck, the
gate's `totalOpenRisk` (`PreTradeGate.cs:94` → `SumWorstCase`) and `state.Positions.Count`
(`PreTradeGate.cs:81`) are permanently wrong.

### 1.2 🔴 Entry-bar ordering race → illegal transitions + missed same-bar exits (E2)

In `KernelBacktestLoop.ProcessBarAsync` (`src/TradingEngine.Host/KernelBacktestLoop.cs:164-171`),
within one bar's pump the order is: dequeue `OrderProposed` → position `Intended→Submitted` +
`SubmitOrder` effect (venue queues a fill **to a channel**) → dequeue `BarClosed` (enqueued right
after the proposal at line 169) → `HandleBarClosed` applies `BarClosed` to the **still-`Submitted`**
position **before** the fill is drained from `_venue.ExecutionStream`.

- `(Submitted, BarClosed)` is not a legal arm in `PositionLifecycle.Apply`
  (`src/TradingEngine.Engine/PositionLifecycle.cs:8-30`) → the default `IllegalTransition` record
  (the **85**).
- SL/TP detection only runs for `Open`/`Reducing` (`EngineReducer.cs:248`), so a freshly entered
  position **cannot exit on its own entry bar** even if price gaps through its stop.

### 1.3 🟠 All exits mislabeled `FORCE` — strategy SL/TP reason is lost (E3)

Every trade in both runs has `ExitReason = FORCE`. The replay venue does **not** detect SL/TP; the
engine does (`EngineReducer.cs:250` → `DetectSlTpExit`) and emits `CloseOpenPosition(reason)`. But
when the venue's close fill comes back as a plain `OrderFilled` on an **Open** position, the FSM arm
`HandleOpenFilled` sets `exitReason = state.CloseReason ?? "FORCE"`
(`PositionLifecycle.cs:139`). `CloseReason` is only persisted onto the position in the **Closing**
path (`HandleCloseRequested`, line 158). The SL/TP path sets `CloseReason` on the *position record*
at `EngineReducer.cs:259` but the close fill resolves through `HandleOpenFilled`, and the value is
not reliably surviving onto the closing record → everything reads `FORCE`. Net effect: **the owner
can never see whether a raw strategy actually hit its stop or target** — the "watch drawdown blow
up" goal is unobservable.

### 1.4 🟠 Per-run "Raw" profile does not reach the engine / audit record (C1)

`596bb202` was launched with `RiskProfileId='raw'` (see `BacktestRuns.RiskProfileId`), yet its
stored `EffectiveConfigJson` — **the audit record the report UI shows** — contains
`"riskProfileId":"standard"` for the strategy. The override at
`BacktestOrchestrator.cs:562` (`c0 with { RiskProfileId = chosenProfile }`) is gated on
`profileIsKnown`, which is checked against `baseConfig.RiskProfiles` (JSON) while the *actual*
profiles used come from the DB (`cs:600`). There are **multiple divergent config-building /
serialization paths**, so the owner cannot trust that "Raw" disabled anything. With `standard`
(0.5%/trade, 3 positions, budget caps live) the run is *not* raw at all.

### 1.5 🟠 Runs never finalize — `ExitCode = -1`, `CompletedAtUtc = 0001-01-01` (O1)

`WriteStartRecordAsync` writes the row with `ExitCode = -1` and `CompletedAtUtc = DateTime.MinValue`
(`BacktestOrchestrator.cs:475`). Only `WriteEndRecordAsync` flips them — it runs in the `try` **and
every `catch`** (`cs:376/402/413`). Both runs still show `-1` ⇒ **`WriteEndRecordAsync` never ran**:
the process was killed / the run abandoned mid-flight even though the journal shows the full window
processed. Consequence: `BacktestRuns.TotalTrades`/`MaxDrawdownPct` are `0`/fabricated and only
"self-heal" by re-reading `TradeResults`. The report is built on a corpse, and the live monitor
shows nothing because the terminal frame never fired.

### 1.6 🟡 Equity/timeline data is wall-clock-stamped and out of order (O2)

In the 3-month journal, `EquityObserved` rows carry timestamps like `18:57` / `19:41` (the
wall-clock time the run executed) interleaved out of order with sim-time `BarClosed` rows whose data
ends at `15:00`. A wall-clock-stamped equity source is polluting the sim timeline → the equity chart
and any time scrubber cannot be built reliably from the journal as-is.

### 1.7 Summary of defects → symptoms

| # | Defect | Owner symptom it explains |
|---|--------|---------------------------|
| E1 | Leaked open book latches budget/exposure | **"fewer signals in 3 mo than 1 mo"**, "drawdown never blows up" |
| E2 | Entry-bar ordering race | 85 illegal transitions, missed same-bar exits, journal noise |
| E3 | Exit reason defaults to FORCE | "can't tell if SL/TP fired", raw drawdown unobservable |
| C1 | Divergent config paths; Raw ≠ raw | "can't confidently disable guards" |
| C2 | BudgetOk/heat/sizing not toggle-gated | guards still bite on "Raw" |
| O1 | Runs end ExitCode -1, no finalize | "live backtest never shows", broken stats |
| O2 | Wall-clock equity stamps | "equity/drawdown never show", "timeline doesn't move" |
| O3 | No per-bar decision narrative | "scattered fields, no insight per bar" |
| U1 | SignalR late-join, no snapshot-on-join | "live stopped working, can't self-verify" |
| U2 | No trade chart; trades not linked to runs | "no chart for trade detail; trades page raw" |
| B1 | Strategy logic + add-ons entangled | "want plain entry/exit + opt-in add-ons" |

---

## 2. Design principles for this iteration

1. **One source of truth per concept.** One `EffectiveConfig` that is resolved once, *persisted
   verbatim*, used by the engine, and shown by the UI. One bar-indexed decision record. One equity
   series. No "the engine computed X but the audit shows Y".
2. **The open book cannot leak.** Positions have a terminal lifecycle with reconciliation; a
   position that cannot close is force-resolved **and loudly journaled**, never silently retained.
3. **Raw means raw.** A "Raw" run provably applies *zero* limiters. Every limiter is individually
   toggle-gated and the resolved toggle set is visible.
4. **Strategy = signal; everything else = opt-in add-on.** A strategy returns entry/exit intent
   only. SL/TP method, breakeven, trailing, partial, ride, dynamic-SL/TP, re-entry, regime are
   composable add-ons attached per run. No add-on ⇒ the strategy's raw behavior, drawdown unmasked.
5. **Every rejection is explainable with numbers.** Not "BudgetBlocked" but "BudgetBlocked:
   openRisk=$X + new=$Y > cap=$Z (heat 3.0× of $W)".
6. **Self-verifiable.** Each engine/observability fix ships with a deterministic test on seeded
   bars so the owner (and the agent) never again has to eyeball the UI to know it works.

---

## 3. Reproduction harness (Phase 0 — do this first)

The agent's sandbox may have **0 bars**. Before any fix, build a deterministic repro so the headline
bug is a *failing test*, not a manual click.

- Seed bars: `scripts/seed-bars.ps1` (EURUSD H1) — or synthesize a deterministic OHLC series in a
  test fixture (preferred: no DB dependency).
- **Failing test `OpenBookDoesNotLeak_LongWindowProducesAtLeastAsManyTradesAsItsSuffix`:**
  run the kernel backtest loop over a synthetic series where the *same* suffix sub-window produces
  N entries standalone; assert the full window produces **≥ N** entries over that suffix (today it
  produces 0 after the latch). This locks §1.1 permanently.
- **Failing test `NoIllegalTransitions_OnAnySeededRun`:** assert the journal contains **zero**
  `IllegalTransition` decision records (today: 85). Locks §1.2.
- **Failing test `ExitReasonReflectsSlOrTp`:** a position whose bar gaps through its stop closes
  with reason `SL` (today: `FORCE`). Locks §1.3.

**Gate P0:** the three tests exist and **fail** for the documented reasons; `dotnet test
tests/TradingEngine.Tests.Unit` is otherwise green.

---

## 4. Phase 1 — Engine truth (the core fix) 🔴 highest leverage

Goal: the same calendar period produces the same trades regardless of how much history precedes it.

### P1.1 — Stop the open-book leak (fixes §1.1)
- In `EngineReducer.HandleBarClosed` / wherever positions reach a terminal phase
  (`Closed`/`Rejected`/`Cancelled`), **remove them from `state.Positions`** (or move them to a
  separate `Closed` ledger that the risk gate never sums). `SumWorstCase` and `state.Positions.Count`
  must only ever see *live* (`Submitted`/`Open`/`Reducing`/`Closing`) positions.
- Add a **reconciliation pass**: at end of bar, any position in `Submitted` for more than its
  entry-fill window, or `Open` past a configurable max-hold/end-of-data, is force-resolved with an
  explicit reason (`STUCK_SUBMITTED` / `EOD_FLATTEN`) and journaled — never silently kept.
- **Invariant assertion (debug build / test):** after each bar, `state.Positions` contains no
  terminal-phase entries; `openPositions` in the risk snapshot equals the live count.

### P1.2 — Fix the entry-bar ordering race (fixes §1.2)
- Drain `_venue.ExecutionStream` (entry fills) into the queue **before** the `BarClosed` is applied,
  so a just-submitted position is `Open` when its bar is evaluated. Options (pick the one that keeps
  the golden tape byte-identical — verify against the golden suite):
  - (a) enqueue `BarClosed` *after* a fill-drain step, or
  - (b) in `HandleBarClosed`, skip non-`Open`/`Reducing` positions entirely (no lifecycle apply, no
    illegal record) and let them receive the bar only once `Open`.
- Either way: **zero** `IllegalTransition` records, and a position can exit on its entry bar.

### P1.3 — Propagate the real exit reason (fixes §1.3)
- Ensure the SL/TP/forced/EOD close reason set at `EngineReducer.cs:259` survives onto the
  `PublishTradeClosed` reason. Audit `HandleOpenFilled`/`HandleClosingFilled`/`HandleReducingFilled`
  in `PositionLifecycle.cs` so `state.CloseReason` is the source of truth and `FORCE` is only used
  when there genuinely was a force-close (not "I forgot to thread the reason").
- `TradeResult.ExitReason` ∈ {`SL`,`TP`,`TRAIL`,`BE`,`PARTIAL`,`FORCE`,`EOD_FLATTEN`,`STUCK_*`}.

### P1.4 — Engine invariants harness
- Add `EngineInvariants.Check(state)` asserting: no terminal positions retained; `totalOpenRisk` ==
  recomputed-from-live; equity == initial + Σ realized net (realized model). Run it in tests after
  every bar of the repro.

**Gate P1:** P0's three tests now **pass**; the golden suite is byte-identical (or the diff is
reviewed and the golden re-baselined with a written justification); `dotnet test
tests/TradingEngine.Tests.Unit` green; a 3-month seeded run produces ≥ the trade count of its
1-month suffix.

---

## 5. Phase 2 — Guards: one resolved config, fully toggleable, provably raw (C1/C2/E4)

Goal: "Raw" applies zero limiters; the owner can see exactly which guards are in force and why each
proposal was blocked.

### P2.1 — Single EffectiveConfig resolution + verbatim persistence
- Collapse the divergent config paths (`BacktestOrchestrator.BuildLoadedConfigFromDb` override vs
  `EffectiveConfigResolver` vs the stored `EffectiveConfigJson`) into **one** resolver that produces
  the exact object the engine consumes. Persist *that* as `EffectiveConfigJson`. The run record must
  answer "what actually ran" with no inference.
- Fix the `profileIsKnown` check to consult the **same** profile set the engine uses (DB-first),
  so `raw` is recognized and actually overrides each strategy's `riskProfileId`.

### P2.2 — Every limiter is toggle-gated and per-run overridable
- Gate `BudgetOk` (`PreTradeGate.cs:169`) behind a toggle (e.g. `BudgetEnabled`, or fold under
  `DailyDdEnabled` since the budget is a daily-DD budget). Same for the heat cap
  (`MaxPortfolioHeatRiskMultiples`) and exposure.
- Make `SizingPolicyOptions` (`BudgetUseFraction`, `MaxPortfolioHeatRiskMultiples`) per-run
  overridable; the `raw` preset sets fraction=1.0 / heat=∞ (or disables the check).
- A run launched "Raw" must produce **zero** gate rejections attributable to DD/budget/heat/exposure
  on the §3 repro. Add a test: `RawProfile_NoLimiterRejections`.

### P2.3 — Explainable rejections ("guard trace")
- Every `GateResult.Reject` carries the *numbers*: e.g.
  `BudgetBlocked: openRisk=1840.50 + new=49.10 > cap=1815.00 (heatCap=2940 @3.0×)`.
- Surface these in the per-bar decision record (Phase 5) and the run-report rejection histogram
  (already started in P5 of the prior iter — extend it to show the resolved constraint values).

**Gate P2:** `RawProfile_NoLimiterRejections` passes; the stored `EffectiveConfigJson` for a Raw run
shows `raw`/all-toggles-off and matches what the engine used (assert in a test); rejection records
include numeric context.

---

## 6. Phase 3 — Strategy = plain entry/exit + opt-in add-ons (B1) 🎯 owner's explicit ask

Goal: a strategy is *just* signal logic; SL/TP/breakeven/trailing/partial/ride/dynamic/re-entry/
regime are composable add-ons attached per run. No add-on ⇒ raw entry/exit, drawdown unmasked.

### P3.1 — Define the clean strategy contract
- `IStrategy.Evaluate(MarketContext) → TradeIntent?` returns **direction + entry** and *optionally*
  a baseline stop/target — nothing else. Remove embedded breakeven/trailing/partial/regime/re-entry
  decisioning from strategy bodies (today they live in `Config.PositionManagement` +
  `BarEvaluator`'s DynamicSlTp block at `BarEvaluator.cs:141-170`).
- A strategy with **no SL/TP and no add-ons** is legal: it enters and only exits on an explicit
  add-on or an opposite signal — this is the "raw, watch the drawdown" mode the owner wants.

### P3.2 — Add-on layer = single composition point
- Formalize the existing pack system (`AddOnPacks`, `EffectiveConfigResolver.ApplyPack`) as **the**
  way add-ons attach. One pipeline applies the ordered add-on chain to a position each bar:
  `[DynamicSlTp?] → [Breakeven?] → [Trailing?] → [PartialTp?] → [Ride?]`. Each add-on is independently
  on/off with its own params; "none selected" = identity.
- Move the `BarEvaluator` DynamicSlTp inlining into this pipeline so the evaluator only produces
  intents and the add-on layer owns all SL/TP mutation.

### P3.3 — UI: per-run strategy = pick signal + (optional) pack
- New-backtest builder: each row = strategy (signal) + optional add-on pack + risk profile. "Raw"
  pack = empty. Make the default visibly "no add-ons" so the owner can A/B raw vs add-on'd.

**Gate P3:** golden suite still byte-identical for default (all-add-ons) configs; a "no-pack" run of
a strategy produces entries with `SL=null`/`TP=null` and exits only via opposite-signal/EOD (test
`RawStrategy_NoAddOns_NoSyntheticStops`); the DynamicSlTp inlining is gone from `BarEvaluator`.

---

## 7. Phase 4 — Run lifecycle + equity/timeline data correctness (O1/O2)

### P4.1 — Robust finalization (fixes §1.5)
- Guarantee a terminal write: wrap the run so **completion is idempotent and always recorded** —
  e.g. checkpoint the summary on a heartbeat and on `finally`, and on app startup *reconcile* any
  `ExitCode=-1` run by re-deriving its summary from `TradeResults`/`Journal` (the self-heal becomes
  authoritative, not a patch). A run must never be left `-1` once its journal has a terminal bar.
- Persist `TotalTrades`/`MaxDrawdownPct`/win-rate from the authoritative engine state at end, not 0.

### P4.2 — Single, sim-time-correct equity series (fixes §1.6)
- Stamp **every** equity/account event with **sim-time** (the bar's `OpenTimeUtc`), never
  `DateTime.UtcNow`. Audit `EquitySnapshotFlush`, `KernelEquitySnapshot`, and the account-stream
  path for wall-clock leakage.
- Persist one ordered `EquitySnapshots` series per run (sim-time, equity, balance, dd) and drive the
  equity chart + the time scrubber from it. De-dupe overlapping multi-pass times (the iter-strategy
  P4 fix) but at the source.

**Gate P4:** a completed run has `ExitCode=0`, real `CompletedAtUtc`, and summary == re-derived
summary; the persisted equity series is strictly sim-time-ordered (test asserts monotonic
non-decreasing sim-time and no wall-clock values); restarting the app reconciles a `-1` run.

---

## 8. Phase 5 — Observability: one per-bar decision narrative (O3) 🎯 owner's "no insight"

Goal: for any bar, answer "what did the engine see, decide, and why" in one place. Replace the
scattered pre-bar/per-bar/journal fields with **one bar-indexed timeline**.

### P5.1 — Bar decision record
- Persist, per (run, symbol, bar): `{ simTime, regime, activeStrategies[], perStrategyVerdict[]
  (signal/none + reason + key indicators), proposals[], gateDecisions[] (accept/reject + numeric
  reason), openPositions snapshot, equity, dd }`. Most of this already exists transiently in
  `BarEvaluation` / `StepRecord` — the work is to **fold it into one bar-keyed row** and persist it
  (sampled or full; full for short runs, sampled for long with always-keep on any proposal/fill/
  reject).
- This consolidates `BarEvaluator.Latest`, the `StrategyVerdict[]`, the `StepRecord.DecisionReason`,
  and the gate reasons that today live in separate journal rows.

### P5.2 — Bar inspector API + UI
- API: `GET /api/runs/{id}/bars?from&to` → the bar decision records; `GET
  /api/runs/{id}/bars/{simTime}` → one bar's full narrative.
- UI: a scrubbable bar timeline (Phase 6) where clicking a bar shows the narrative: regime, each
  strategy's verdict + indicator values, every proposal and its gate decision with numbers, and the
  open book at that bar. This is the "why no trade here / why this trade" view.

**Gate P5:** for the §3 repro, the bar inspector for the first latched bar shows the exact
`BudgetBlocked` math from §2.3; a test asserts the bar record for a known proposal bar contains the
strategy verdict + the gate decision with numeric context.

---

## 9. Phase 6 — UI: fix-first (keep Angular), make live + charts trustworthy (U1/U2)

We keep the Angular SPA. The live-monitor pain is **late-join + no snapshot**, not the framework.

### P6.1 — Live monitor late-join + self-verify (fixes U1)
- **Snapshot on join:** when a client calls `JoinRun`, immediately send the current `RunProgress`
  (and recent journal) for that run from `BacktestProgressStore`, so a page load / reconnect mid-run
  is never blank. Today `RunHub.JoinRun` only adds to the group
  (`src/TradingEngine.Web/Hubs/RunHub.cs:15`) and the client subscribes *after* `hub.start()`
  (`run-hub.service.ts:64-68`) — any frame before join is lost.
- The client subscribes to a `JournalAppend` event the broadcaster never sends
  (`run-hub.service.ts:61` vs `RunProgressBroadcaster` only sends `RunProgress`/`RunCompleted`) —
  either send it or drive journal purely off `recentJournal`. Remove the dead path.
- **Self-verifiable harness (the recurring pain):** a Playwright/integration test that starts a
  seeded run, asserts the monitor's bar counter advances, equity series grows, and the terminal
  frame flips status to `completed`. This is what lets the agent verify SignalR without the owner
  eyeballing it.

### P6.2 — Trade-detail chart (fixes U2a)
- A candlestick view for one trade: persisted bars around the trade window + markers for entry,
  exit, SL, TP, and any trail/BE moves (from the per-bar records + trade). The data already exists
  (`Bars`, `TradeResults` with entry/exit/SL/TP, MAE/MFE); wire a `GET /api/trades/{id}/chart`.

### P6.3 — Trades ↔ runs linkage + complete backtest record (fixes U2b)
- The Trades page must filter/group by `RunId` and link back to the run report; the run report links
  out to each trade's chart. Audit that `TradeResults` carries everything the detail view needs
  (it does: R, MAE, MFE, pips, costs, strategy, exit reason — exit reason becomes meaningful after
  P1.3).

### P6.4 — Bar inspector UI (consumes Phase 5)
- The scrubbable timeline from P5.2. This is the centerpiece of "I can finally see what the engine
  is doing each bar."

**Gate P6:** the live-monitor Playwright test passes against a seeded run; the trade-detail chart
renders entry/exit/SL/TP for a real trade; Trades page is filtered by run and cross-links both ways.

---

## 10. Phase 7 — (Deferred decision) desktop / framework switch

**Do not start until Phases 1–6 land.** The evidence (§1) shows the "UI is broken" symptoms are
overwhelmingly **bad/incomplete engine data + late-join SignalR**, not Angular. Rewriting the UI now
would carry those bugs into a new shell and waste the work. Revisit a desktop/alternative-framework
move **only if**, after P6, the owner still finds: (a) live updates unreliable despite snapshot-on-
join, or (b) charting/interaction needs exceed what the SPA can do. If revisited, the decision input
is "interactivity/offline needs", not "the backtest is wrong" — that will already be fixed.

---

## 11. Suggested sequencing & dependencies

```
P0 (repro)  →  P1 (engine truth)  →  P2 (guards)        ┐
                      │              P3 (strategy split) ┤→ P5 (bar narrative) → P6 (UI)
                      └───────────→  P4 (lifecycle+equity)┘
P7 deferred (decision gate after P6)
```

- P1 unblocks everything (correct data).
- P2 + P3 + P4 are independent of each other after P1; do P2 first (it's the owner's "confidently
  disable guards" need and smallest).
- P5 depends on P1/P2 (it renders their corrected decisions).
- P6 depends on P4 (equity series) + P5 (bar records).

---

## 12. Owner decision block (answer before P3/P6; defaults chosen so the agent isn't blocked)

- **D1 — Raw exit policy:** when a strategy has no SL/TP and no add-ons, how should positions
  *ever* close? Default proposed: **opposite signal OR end-of-data flatten**, plus an optional
  per-run "max hold bars". (Needed so "raw" isn't "never exits".)
- **D2 — Add-on default:** should the New-Backtest default be **no add-ons (raw)** or the current
  auto-tuned pack? Default proposed: **no add-ons**, with a one-click "Apply recommended pack".
- **D3 — Bar-record retention:** full per-bar records for runs ≤ N bars, sampled above N (always
  keep bars with a proposal/fill/reject). Default N proposed: **5,000 bars**.
- **D4 — Golden re-baseline:** P1.2/P1.3 may change the golden tape (illegal-transition removal,
  exit-reason). OK to **re-baseline the golden with a written diff justification**? Default: yes.
- **D5 — Keep Angular (Phase 7 deferral):** confirm we fix-first and defer any desktop/framework
  move. Default: **yes** (per "fix what we have if possible").

---

## 13. Key file index (verified during investigation)

| File | Role / what changes |
|------|---------------------|
| `src/TradingEngine.Engine/EngineReducer.cs` | `HandleBarClosed` (open-book leak P1.1, ordering P1.2), `DetectSlTpExit`, `HandleForceCloseAll` |
| `src/TradingEngine.Engine/PositionLifecycle.cs` | FSM; `IllegalTransition` arm, `exitReason ?? "FORCE"` (P1.3), terminal removal (P1.1) |
| `src/TradingEngine.Host/KernelBacktestLoop.cs` | Per-bar pump ordering (P1.2), equity stamping (P4.2), `_onBarProcessed` hook for bar records (P5) |
| `src/TradingEngine.Host/BarEvaluator.cs` | Strategy eval; DynamicSlTp inlining to move out (P3.2); verdict source for bar records (P5) |
| `src/TradingEngine.Engine/Kernel/PreTradeGate.cs` | `BudgetOk`/heat/exposure toggle-gating + numeric reasons (P2) |
| `src/TradingEngine.Domain/RiskAndEquity/SizingPolicyOptions.cs`,`ConstraintSet.cs`,`ProtectionToggles.cs` | per-run sizing override + toggles (P2) |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | config resolution divergence (P2.1), finalization (`WriteStart/EndRecordAsync`, P4.1) |
| `src/TradingEngine.Services/EffectiveConfigResolver.cs` | single resolver + add-on pipeline (P2.1/P3.2) |
| `src/TradingEngine.Host/EquitySnapshotFlush.cs`,`KernelEquitySnapshot.cs` | sim-time equity series (P4.2) |
| `src/TradingEngine.Web/Hubs/RunHub.cs`,`Services/RunProgressBroadcaster.cs` | snapshot-on-join, dead `JournalAppend` (P6.1) |
| `web-ui/src/app/core/signalr/run-hub.service.ts`,`features/runs/run-monitor/*` | live monitor (P6.1) |
| `web-ui/src/app/features/trades/*` | trade chart + run linkage (P6.2/P6.3) |
| `scripts/seed-bars.ps1` | seed EURUSD H1 for the repro (P0) |

---

## 14. How to reproduce the diagnosis (for the agent)

```bash
cd src/TradingEngine.Web/data
# The latching pattern: accepts then a wall of BudgetBlocked at min lots
sqlite3 -header -column trading.db \
 "SELECT Seq, substr(SimTimeUtc,1,16) t, DecisionReason FROM Journal \
  WHERE RunId='596bb202' AND (DecisionReason LIKE 'Budget%' OR DecisionReason='Accepted' \
  OR DecisionReason LIKE 'MAX_%') ORDER BY Seq;"
# openPositions stuck at 3, equity frozen:
sqlite3 trading.db "SELECT RiskJson FROM Journal WHERE RunId='596bb202' AND Seq IN (4283,11181);"
# 85 illegal transitions:
sqlite3 trading.db "SELECT COUNT(*) FROM Journal WHERE RunId='596bb202' AND DecisionReason LIKE 'Illegal%';"
# all exits FORCE:
sqlite3 trading.db "SELECT ExitReason, COUNT(*) FROM TradeResults GROUP BY ExitReason;"
```

Keep these queries as the acceptance oracle: after P1, a fresh 3-month run has **no** wall of
`BudgetBlocked`, **no** `Illegal%`, **non-`FORCE`** exit reasons, and `openPositions` that returns to
0 between trades.
