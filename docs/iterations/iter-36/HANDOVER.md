# Iter-36 — Kernel Cutover HANDOVER (per-phase deltas)

Branch: `iter/36-kernel-cutover`. Tracks each phase's deltas, golden re-baselines (none so far), and
cTrader live-verification follow-ups. See `PLAN.md` for the full plan + inline per-phase STATUS blocks.

---

## K0 — Real gates (DELIVERED by owner, pre-handoff)
See the `> STATUS — K0 DELIVERED` block in `PLAN.md`. Purity body-scan hardened; determinism compares
the full StepRecord (effect payloads + risk); equivalence de-magic'd against `golden-snapshot.json`; the
date-dependent golden oracle fixed by anchoring the harness clock to fixture sim-time. No re-baseline.

## K1 — Evaluator stage (DELIVERED 2026-06-20)

**What landed**
- **`src/TradingEngine.Host/BarEvaluator.cs`** — per-`BarClosed` event producer porting
  `TradingLoop.ProcessBarAsync`'s evaluation: indicator recompute → regime → strategy bank → strategy
  eval → entry planner → signal gate. Emits one `OrderProposed` per firing strategy (with `SlPips` +
  cross-rate-aware `PipValuePerLot`) + a per-strategy `StrategyVerdict`. Output `BarEvaluation(Regime,
  Proposals, Verdicts)`; `Latest` cached for the driver `evaluatorView` seam (K3/K5). Deterministic
  order-id counter (no `Guid.NewGuid`).
- **External verdicts ported + replay-safe.** `KernelOrderGate.ComputeVerdicts`
  (news/weekend/compliance/governor) ported into the evaluator, evaluated at the **bar's sim-time**.
  Verdict frozen onto the event.

**Contract changes**
- `OrderProposed` (Domain) gained trailing `ExternalVerdicts External = default` (backward-compatible —
  existing positional constructions unaffected).
- `Kernel.DecideProposed` now passes `p.External` to `PreTradeGate.Evaluate` (was hard-coded `default`,
  which silently dropped ALL external protection in the kernel path — a real latent bug, now fixed).
- `ExternalVerdicts` moved `TradingEngine.Engine` (nested in `PreTradeGate`) →
  `TradingEngine.Domain/Kernel/ExternalVerdicts.cs` (a Domain event must be able to carry it).
  `PreTradeGate` + `KernelOrderGate` updated to the moved type; behaviour identical.

**Tests** — `tests/.../GoldenReplay/KernelEvaluatorEquivalenceTests.cs` (3, `[Speed=Fast]`):
evaluator→kernel reproduces golden's first order (0.20/Long, one `SubmitOrder`); kernel applies a
weekend verdict carried on a proposal (reject `WEEKEND_RESTRICTION`) vs default-verdict accept; evaluator
freezes the weekend verdict from a Saturday bar's sim-time.

**Golden re-baseline:** none. `golden-snapshot.json` preserved.

**Verified green:** K1 3/3 · K0 GoldenReplay+Determinism+KernelAcceptance 9/1-skip · Engine purity 4/4 ·
Architecture 4/4 · fast Simulation 21/1-skip · Unit 209/4-skip.

**Carried forward (K2/K3):** the K1 test hand-drives the event queue — the `BarEvaluator` + the
venue-feedback bridge (K2) get assembled into the real kernel backtest loop in K3, where the `[Skip]`
`KernelAcceptanceTests.KernelFullRun_MatchesGolden_TradesAndRisk` gets un-skipped. The driver
`evaluatorView` (folding `Verdicts`/`Regime` onto the BarClosed StepRecord) is wired in K5.

## K2 — Venue-feedback bridge (DELIVERED 2026-06-20)

**What landed**
- **`src/TradingEngine.Host/KernelFeedback.cs`** — pure venue→kernel translation: `ExecutionEvent` →
  `OrderFilled`/`OrderPartiallyFilled`/`OrderCancelled`/`OrderRejected`; `AccountUpdate` → `EquityObserved`.

**Contract changes**
- `OrderRequest` gained `Guid? ClientOrderId = null`; `BacktestReplayAdapter.SubmitOrderAsync` + test
  `FakeVenue.SubmitOrderAsync` honor it; `EffectExecutor` submits under the kernel order id. ⇒ venue id ==
  kernel id (= PositionId); the feedback bridge needs no id translation.
- `CloseOpenPosition` gained `Price? ExitPrice = null`; `EngineReducer.HandleBarClosed` sets the position
  `CloseReason` + the stop/target price on the effect; `EffectExecutor` routes a priced close to
  `ClosePositionAtAsync`. ⇒ a kernel SL/TP closes at the stop price with reason "SL"/"TP" (not "FORCE"/bar
  close). `FakeVenue` gained a `ClosePositionAtAsync` override.

**Tests** — `KernelFeedbackTests` (3): flat-book `Equity==Balance` no false breach (C5); 6% drop → protection
exactly once; SL close fill closes via the reducer (`PublishTradeClosed` reason=SL, price=stop).

**Carried forward:** Day/Week/Month roll events (NEW-1) not yet emitted by the loop (golden is single-day);
threading venue commission/swap through `OrderFilled`→`PublishTradeClosed` (net==gross matches zero-cost
golden) — both pre-K4/K7 items.

## K3 — Kernel-driven backtest loop (DELIVERED 2026-06-20)

**What landed**
- **`src/TradingEngine.Host/KernelBacktestLoop.cs`** — `RunAsync` driving `BarTape → BarEvaluator → Kernel
  → IEffectExecutor → venue + feedback bridge`, `EngineState` as the single authority, one `StepRecord`
  per event. A "pump" drains the kernel queue + interleaves venue feedback to quiescence (determinism).

**Tests** — `KernelBacktestLoopGoldenTests` (2) + the **un-skipped**
`KernelAcceptanceTests.KernelFullRun_MatchesGolden_TradesAndRisk` (1): the full golden run through the new
loop reproduces golden's closed trade + final risk **exactly** and is **bit-identical across two runs**.
Shared wiring in `KernelLoopHarness` (FakeVenue + real `EffectExecutor` + realized equity).

**Equity model:** realized (`initial + closed net PnL`) to match the harness-generated golden; the loop also
supports mark-to-market (`realizedEquity: null` → venue `AccountStream`). **No golden re-baseline.**

**Old loop untouched** — `EngineRunner.RunBacktestLoopAsync` + imperative twins remain default. The flip +
twin deletions (with a golden re-baseline for mark-to-market DD) are **K4**.

**Verified green:** K2+K3 6/6 · kernel category 15/0 · Engine purity 4/4 · Architecture 4/4 · fast
Simulation 27/0 · Unit 209/4-skip.

## K4 — Full flip (IN PROGRESS, 2026-06-20)

Owner directive: **full flip — production runs only the kernel; the imperative loop must not continue.**
Validation model: deliver with fast tests, then verify via the existing cTrader e2e.

**Landed (foundation, committed, green):**
- **`KernelBacktestLoop.RunFromBrokerAsync`** — the mode-agnostic production entry point: drives the kernel
  loop off `IBrokerAdapter.BarStream`. Both `BacktestReplayAdapter` and the cTrader adapter publish bars
  there, so one kernel engine serves live + backtest (refactored the per-bar body into a shared
  `ProcessBarAsync`). Test `KernelLoop_DrivenFromBrokerStream_ReproducesGolden` proves it reproduces golden.

**THE FLIP landed (2nd commit):**
- **`EngineRunner` rewritten** — production `RunAsync` now builds `KernelConfig` (from the run's active
  profile + `RiskManager.Constraints`/ruleset) + `BarEvaluator` from `EngineWorkerDependencies`, then drives
  `KernelBacktestLoop.RunFromBrokerAsync` off the broker stream for **both** modes. Mark-to-market equity
  via the venue `AccountStream`; a per-bar `onBarProcessed` hook reports "BAR" progress (count + sim clock).
- **Imperative production engine removed:** `RunBacktestLoopAsync`, `SimulateBarExitsAsync`, the
  `ProcessTicks/Bars/Account/Execution` processors, `MarketEventSource`, `IEnginePacer`, `EnginePacers` —
  all deleted from `src`. `EngineRunner` no longer constructs `TradingLoop`/`AccountProcessor`.
- **`INewsFilter`/`SessionFilter` wired into `EngineWorkerDependencies.Risk`** (gap 2 closed) so the
  production evaluator computes external verdicts.
- **Test oracle preserved:** `TradingLoop`/`OrderDispatcher`/`KernelOrderGate`/`AccountProcessor` remain in
  `src` ONLY as the `EngineHarnessBuilder` regression oracle (golden-snapshot.json) + a handful of unit
  tests; they are no longer in the production engine path. (DI still registers them — harmless, unused by
  the engine — to avoid churn; can be trimmed later.)
- **Status:** full solution compiles; Unit 209/4-skip, fast Simulation 28/0, Architecture 4/4, kernel
  category 16/0 all green. The in-process IHost integration tests (which now run the kernel engine end-to-
  end) + the cTrader e2e are the production validation.
- **Deferred (documented):** gap 1 (per-strategy profile — uses the run's single active profile, which is
  how the imperative engine already resolved constraints); gap 3 (trailing/breakeven not yet in the kernel
  loop); gap 4 (equity-snapshot persistence for the Monitor — DD is trade-derived meanwhile); the
  StepRecord journal is a no-op (`NullJournalWriter`) until K5.

**Production-readiness gaps found during recon (must be closed for a *correct* flip — a blind flip is
subtly wrong, not just unfinished):**
1. **Per-strategy risk profile.** `KernelConfig.Profile` is a single run-constant profile; `KernelOrderGate`
   resolves per proposal (`intent.RiskProfileId`). Multi-profile runs would mis-size. Fix: carry the
   resolved `RiskProfile` (or its id) on `OrderProposed` and have `Kernel.DecideProposed` use it.
2. **News/session filters** are not in `EngineWorkerDependencies` — wire `INewsFilter` + `SessionFilter`
   into the deps/DI so the production `BarEvaluator` can compute external verdicts.
3. **Trailing/breakeven** lives in `TradingLoop.UpdateTrailingStopsAsync` (imperative, via `PositionTracker`)
   — must run in the kernel loop (emit `ModifyStopLoss` effects on `BarClosed`) or trailing strategies
   regress.
4. **Monitor equity** comes from `AccountProcessor → IEquitySink/AccountSnapshotStore` (polled by the
   orchestrator). The kernel loop must write equity/DD snapshots from `EngineState` or the Monitor goes
   blank.
5. **Test-oracle preservation.** 24 test files build on the imperative `EngineHarnessBuilder`
   (`TradingLoop`/`OrderDispatcher`/`KernelOrderGate`/`AccountProcessor`). Keep those classes as the
   regression oracle behind `golden-snapshot.json`; remove them from the **production** path only. ⇒ literal
   `grep→0`-in-`src` is intentionally NOT the gate; "no imperative engine in the production wiring" is.

**Remaining flip steps (next pass; each compile-safe):**
- A. Close gaps 1–2 (per-profile on the proposal; news/session in deps) as correct, tested kernel changes.
- B. Build the production kernel engine in `EngineRunner`: construct `KernelConfig` + `BarEvaluator` from
  `EngineWorkerDependencies`, drive `RunFromBrokerAsync`, write equity snapshots (gap 4) + progress/funnel
  events, journal to `SqliteStepRecordSink`.
- C. Point both pacers (`BarSteppedPacer`, `AsyncStreamPacer`) at the kernel engine; remove the imperative
  composition (`TradingLoop`/`AccountProcessor`/`MarketEventSource`) from `EngineRunner`; delete
  `RunBacktestLoopAsync` + `SimulateBarExitsAsync`; drop `OrderGate`/`AccountProcessor` from the production
  deps (classes stay for the test oracle).
- D. Wire trailing into the kernel loop (gap 3).
- E. Verify via cTrader e2e; reconcile.

## K5 — One journal (PARTIAL: production wiring landed; deletions deferred)

**Landed (committed, green):**
- The kernel engine now **journals losslessly to SQLite**: `EngineRunner` writes through the singleton
  `ChannelJournalWriter` (Wait-mode, retry, drain-on-dispose) → `ScopedStepRecordSink` (scope-per-flush) →
  `SqliteStepRecordSink` → `JournalEntries`. Registered in DI + threaded via
  `EngineWorkerDependencies.Persistence.StepJournal`. (Was `NullJournalWriter`.)
- **`DecisionReason` threaded** onto every `StepRecord` (the `PreTradeGate` accept/reject reason, off the
  `RecordDecisionEvent` effect — was hard-coded null) + per-strategy **verdicts/regime folded** onto the
  `BarClosed` record (from K1's `BarEvaluator.Latest`).
- The lossless infrastructure + tests already existed and stay green: `JournalLosslessTests` (burst:
  500 into capacity-8 → 0 dropped; retry: failed batch retried, 0 lost); `SqliteJournalQueryRepository`
  reads `StepRecord`s SQL-paged by `seq`.

**Deferred (the K5 *deletion* gate — a large cascade, lower-risk to defer since the journal is now written):**
- Delete `PipelineEventWriter` + `BarEvaluationHandler` (+ their `DropOldest` channels) and repoint
  `EffectExecutor.RecordDecisionEvent` off `IDecisionJournal` — blocked by the **test oracle**
  (`EngineHarnessBuilder`/golden tests assert on `IDecisionJournal`) + `BacktestOrchestrator`'s flush +
  `WireEventHandlers(BarEvaluated)`. Do alongside trimming the oracle.
- Repoint `GET /api/runs/{id}/journal` to `SqliteJournalQueryRepository` (StepRecords) — coordinate with
  the iter-37 frontend that consumes the journal shape.
- ⇒ "exactly one journal writer / `DropOldest`→0" is NOT yet met; the StepRecord journal is now the
  primary, written-by-production journal, but `PipelineEvents` still co-exists for the oracle + current API.

## K6 — Real replay + duplicate (PARTIAL: fake deleted; duplicate endpoint deferred)

**Landed (committed):**
- **Deleted the fake replay path** — `ReplayRunner` + `ReplayEffectExecutor` (price-0/`EURUSD`/`MinValue`
  fabrication) + `ReplaySinkRead`. They were dead iter-35 skeleton code (no callers / DI / tests), so the
  K6 grep gate (`ReplayEffectExecutor|ReplaySinkRead` → 0 in `src`) is met cleanly.
- **Real replay is now the K4 kernel loop's determinism:** re-running a backtest `(dataset, config, seed)`
  reproduces the prior run **bit-identically** — proven by `KernelBacktestLoopGoldenTests`'
  `KernelLoop_IsDeterministic_AcrossRuns` (full StepRecord journal byte-identical across two runs) +
  `DeterminismTests`. The fake CSV/price-0 shortcut the plan warned about is gone; replay runs the same
  `EffectExecutor` + venue as production.

**Deferred (the duplicate *feature*):**
- `Run = (DatasetId=content-hash(bars), ConfigSetId=hash(effective config), Seed)` populated on the
  production run (the `IDatasetRepository`/`IConfigSetRepository` entities exist) + `ParentRunId`.
- `POST /api/runs/{id}/duplicate` (optional `StrategyIds`/`RiskProfileId`/`StrategyOverrides`) → a new run
  over the same `DatasetId`, new `ConfigSetId`, executed via the same kernel loop. The engine to reuse is
  in place (`BacktestOrchestrator.Start` + the K4 kernel loop); this is API + identity-hashing plumbing.

---

## VALIDATION — kernel engine proven end-to-end (2026-06-20)

The flipped kernel engine was validated against **real cTrader** (env creds) + the credential-free
in-host replay path. All green:

- **cTrader e2e (16 tests, `[Collection("CtraderSerial")]`):**
  - `CtraderE2EHarnessSmokeTests` 3/3 — incl. `TradeLedger_ClientOrderIdReconciliation_NoMissingTrades`
    (kernel order ids reconcile to the cTrader ledger — K2 ClientOrderId end-to-end).
  - `CtraderScenarioE2ETests` 3/3 — ledger integrity, weekend edge, no orphan processes.
  - `PipelineE2ETests` 8/8 — EURUSD/GBPUSD, H1/M15, 3-day→3-month, + in-process engine with cTrader CLI.
  - `DiffE2ETests` 2/2 — cTrader-vs-DB comparison + per-trade cost-integrity reconciliation.
- **In-host replay (credential-free):** `BacktestReplayTests.ReplayBacktest_FullPipeline_ProducesBarEvaluations`
  — the kernel engine over the REAL `BacktestReplayAdapter` produces BarEvaluations + valid closed trades
  (entry/exit/reason; all positions closed).
- **Fast suites:** Unit 209/4-skip · fast Simulation 28/0 · Architecture/purity 4/4 · kernel+journal 18/0.

**Fixes the e2e surfaced (committed):**
- `CTraderBrokerAdapter.SubmitOrderAsync` now honors `request.ClientOrderId` (was minting its own) — the
  K2 cTrader follow-up; required for fills to map back + ledger reconciliation.
- `EngineRunner` emits `BarEvaluated` events per verdict (the imperative `TradingLoop` used to) so the
  `BarEvaluations` table / UI / e2e completion-poll work.
- `ReplayTestHarness` updated for the kernel path (register `INewsFilter`/`SessionFilter`/`EntryPlanner`/
  `EffectExecutor`; configure the substitute `RiskManager` for the kernel reads — `ActiveRuleSet`/
  `Drawdown`/`CheckComplianceBlock`; idempotent host teardown). The substitute was wired for the deleted
  imperative path.

## K7 — Remaining-the-kernel-was-meant-to-fix (doc reconciliation OUTSTANDING)
The kernel is now authoritative + validated, so the shadowed imperative DD/protection/governor code no
longer executes in production (it survives only in the test oracle). Still to do: reconcile
`docs/OPEN-ISSUES.md` (C3/C4 etc. "RiskManager still has the bug but kernel authoritative" → "removed from
the production path"), `SYSTEM-AUDIT.md`/`CODE-MAP.md`/`SYSTEM-MODEL.md §3.2`, and confirm the named
backtest-venue items (H13/H14/H15/H16, C6) against the kernel path. Lightweight; not yet done.

## cTrader live-verification follow-ups (accumulating; resolved in K7)
- None new from K1 (backtest-path only). The sim-time verdict change also benefits live (weekend/news no
  longer wall-clock-dependent) but live e2e is out of sandbox scope — owner to confirm in K7.
- K2 `ClientOrderId`: only `BacktestReplayAdapter` (+ test `FakeVenue`) honor it. The cTrader adapter must
  use it as the venue Label/clientOrderId for the kernel path to join venue fills to kernel positions
  live — owner live-verification follow-up (no cTrader in sandbox).

---

## ROUND 2 — finish-the-cutover pass (2026-06-20, OpenCode/DeepSeek)

The K4 deferred gaps + the K5/K6 deletions/features were completed so **iter-36 is done with nothing
stalled** and the iter-37 journal/report frontend builds on a real foundation. All verified green:
**build 0 errors · Unit 209→208/4-skip · Simulation non-cTrader 82/2 · `run-shamshir` driver 11/11**.
(The 2 Simulation + 4 cTrader-E2E failures are **pre-existing/environmental**: the `InProcessEngineSmoke`
`EntryPlanner` DI gap + `NetMQBridgeTest` transport [both in PLAN K0 STATUS] and the cTrader-credential
tests. The −1 Unit is the deleted obsolete `Iter27FixTests` that tested the now-deleted writer.)

**K4 gap-1 (per-strategy profile):** `OrderProposed` carries the evaluator-resolved `RiskProfile`;
`Kernel.DecideProposed` sizes with `p.Profile ?? _config.Profile`. Test
`Kernel_SizesWithPerProposalProfile_NotRunConstantProfile`.

**K4 gap-3 (trailing/breakeven):** new `StopLossModifyRequested` event → pure `EngineReducer` handler
(updates the authoritative stop + emits `ModifyStopLoss` carrying the position TP). `KernelTrailingEvaluator`
(Host) reuses the **real** `PositionManager` (lazy register-on-open) and is driven end-of-bar by
`KernelBacktestLoop` (`evaluateTrailing` seam) — no intrabar look-ahead. `ModifyStopLoss` gained
`TakeProfit`; `EffectExecutor` passes it through. Tests: reducer apply + the arch wiring test updated.

**K4 gap-4 (Monitor equity):** `EngineRunner.ReportBar` writes a per-bar `AccountSnapshot` from the
authoritative `EngineState` via the pure `KernelEquitySnapshot.From` → `IEquitySink`/`IAccountSnapshotStore`.
Test `From_MapsAuthoritativeStateOntoSnapshot`. **Golden stays realized-equity** (FakeVenue) as the oracle;
production MtM-DD is validated by the in-host `BacktestReplayTests` + cTrader e2e — **no golden re-baseline**
(see DECISIONS).

**K4 twins (the literal grep gate):** `OrderDispatcher`/`KernelOrderGate`/`AccountProcessor` moved to a new
**`tests/TradingEngine.Tests.Support`** assembly (the golden-oracle home, referenced by Unit/Simulation/
Integration). `OrderContext` moved back to `IOrderGate.cs` (the contract). Production decoupled: `OrderGate`
removed from `StrategyServices` + DI. `TradingLoop`/`PositionTracker` stay in `src` (not gated; oracle shell +
still-used tracker). **`grep "AccountProcessor|KernelOrderGate|OrderDispatcher|RunBacktestLoopAsync(|SimulateBarExitsAsync(" src` → 0.**

**K5 (one journal):** `GET /api/runs/{id}/journal` repointed to the lossless **StepRecord** stream
(`IJournalQueryRepository`, SQL-paged by seq) + `GET /api/runs/{id}/journal/export` (NDJSON); parallel
`KernelJournalController` deleted. `EffectExecutor` repointed off `IDecisionJournal` (decision lands on the
StepRecord; journal now optional). **Deleted `PipelineEventWriter` + `BarEvaluationHandler`** (the two
`DropOldest` lossy writers) + all wiring (DI, subscriptions, orchestrator flush, `ExperimentRunner`,
`EngineHostFactory`, the `BarEvaluated` emit); legacy `IDecisionJournal`/`IPipelineJournal` consumers bind
to `NullDecisionJournal`/`NullPipelineJournal`. `BacktestReplayTests` repointed to assert `JournalEntries`
(proves the single journal writes in-host). **`DropOldest` → 0** in the journal/decision path (only
market-data/telemetry streams remain, documented). Fixed a latent bug: `IJournalQueryRepository` was not in
the **Web root DI** (surfaced by the driver when `RunsController` failed to activate).

**K6 (real replay + duplicate):** `ParentRunId` added to the run model; **EF regen-init** (single fresh
`InitialCreate`, dev DB recreated → app migrates + re-seeds on boot). Run identity populated at start:
`DatasetId`=hash(data-window spec), `ConfigSetId`=hash(effective config), `Seed`, `ParentRunId`.
**`POST /api/runs/{id}/duplicate`** (optional StrategyIds/RiskProfileId/Venue/StrategyOverrides) → new run,
same dataset, new config, parent-linked. Frontend: `runs.service` gained `duplicateRun` + `journalExportUrl`.

**Frontend reflection (iter-37 surfaces touched by the cutover):** `JournalEntry` type + `run-report`
journal repointed to the StepRecord `eventKind`/`decisionReason` (renders real data); SPA `npm run build`
green. **Deferred to iter-37 F1/F2/F4** (the rich rebuild, as planned): order+fill join by `orderId`,
named-violation rendering, the per-bar "why" funnel on StepRecords (the `BarEvaluations` table is now empty —
`BacktestQueryService`/`RunFunnel`/`RunProjection` readers still point at the now-unwritten
`PipelineEvents`/`BarEvaluations` and return empty until F2/F4 repoint them to the StepRecord journal), and
the duplicate-with-changes UI + lineage display.

**Carry-forward to iter-37:** repoint the funnel/report readers (`BacktestQueryService`,
`RunFunnel`/`RunProjection` analytics) off `PipelineEvents`/`BarEvaluations` onto the StepRecord journal
(F2/F4); build the F1 journal view (join + violations) + F3 duplicate UI on the now-ready endpoints. The
`PipelineEvents`/`BarEvaluations` DB tables can be dropped once those readers are migrated.
