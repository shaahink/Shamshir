# Iter-37 — Frontend Finish + Pressure/Reality Tests — HANDOVER

**Branch:** `iter/37-frontend-finish` (cut from `iter/36-kernel-cutover` after the cutover-finish + G0 backbone commit).
**Status:** DELIVERED. Build 0 err · Unit 237/0/5-skip · Simulation non-cTrader 97/0 · Integration 41/41 · SPA build green.
Out of scope (cTrader/environmental, owner-verified): cTrader-E2E, `NetMQBridgeTest`, `InProcessEngineSmoke`.

Companion plans: `PLAN.md` (frontend F1–F8) + `TEST-PLAN.md` (G/F/J/E/B/C/D). Issue tracker: `docs/OPEN-ISSUES.md`
→ "iter-37 closure".

---

## Per-checkpoint deltas (each its own commit)

- **Phase 0** — verified build+suites, committed the iter-36 cutover-finish + iter-37 G0 backbone on
  `iter/36`, cut `iter/37-frontend-finish`. Fixed iter-26 `F10` guard to the deliberate G0 semantics
  (MonthRolled re-bases monthly DD to current equity).
- **A1 / G0 (K-GAP-1)** — production roll wiring confirmed (`EngineRunner` → `ResetConfig.FromRuleSet`);
  added `KernelLoop_MultiDay_DailyDrawdownRebasesEachDay`.
- **Part G** — `GovernorDrawdownProtectionTests` (12): governor cooling-off/profit-lock (H7/H8), drawdown
  floors C3/H1/H3/H2, protection enter-once + C4 auto-exit.
- **Part F** — `FtmoPressureTests` (F1 daily-resumes / F2 max-loss-terminal) + M6 profit-target-by-equity.
- **Part J** — `JournalSourceOfTruthTests` + `JournalRejectTests` + `JournalReadPathTests`: one-record-per-event,
  order/fill join key, costs (Net==Gross−Comm−Swap), verdicts, funnel totals, multi-day determinism, SQL
  paging + NDJSON round-trip.
- **A2 / Part E (K-GAP-2)** — `EquitySnapshotFlush` + `EngineRunner.FlushBacktestEquityAsync` (batch flush on
  completion); `PersistentEquitySink` mode injected. `BacktestEquityFlushTests`.
- **A3 (K-GAP-4)** — `RunProjection` (timeline→journal, equity→persisted) + `BacktestQueryService` (verdicts)
  repointed onto the StepRecord journal. `StrategyBreakdownFromJournalTests`. `RunFunnel.BuildFunnel` left intact
  (iter-27 oracle).
- **A4 / Part B (K-GAP-3/5)** — `ChartDataTests`: dedup-by-timestamp + trade-detail run-timeframe.
- **A5 / Part D (K-GAP-6)** — `ExecutionEvent.Symbol` + pump preference + venue stamping. `MultiSymbolAttributionTests`.
  D2 via J3 determinism + `DuplicateRunE2ETests`.
- **Part C** — `StrategyCharacterizationTests` (EmaAlignment, MeanReversion) closing the silently-dead gap left by
  `StrategySignalContractTests`.
- **C-F1/F2/F3** — `api.types` extended (journal risk/verdicts, lineage, overrides); run-report unified journal
  (order/fill join, kind filter, badges, named-violation renderer) + per-strategy funnel
  (`GET /api/runs/{id}/analytics/strategies`) + Duplicate/NDJSON/lineage.
- **C-F5/F6/F8** — live-monitor stick-to-bottom + balance-null fix; trade-list TF column; dashboard placeholder
  hygiene.
- **C-F7** — risk-profile validate-before-save.
- **Phase Z** — full-suite gate green; fixed 2 stale `WebSmokeTests` (dead `/api/backtest/runs` + `/api/backtest/compare`
  → live `/api/runs` + `/api/backtest/analytics/compare`); docs reconciled.

## Sign-off pass (dead-code removal + finish — one go)

- **D-drop (✅ DONE)** — removed all kernel-upgrade dead code: `PipelineEvents`/`BarEvaluations` (entities,
  mapping, repo, interface, DTO, `JournalNormalizer`), dead consumers (`EventsController` + events SPA page,
  `BacktestController.Journal`, `RunQueryService.GetRunJournalAsync`), and the never-fired protection-ledger path
  (handlers, `ProtectionQueryService`, `ProtectionController`, ledger entities/tables, `compliance` SPA page —
  `GovernorStateChanged` is never published). **EF reset:** fresh `InitialCreate` regenerated (no dead tables), dev
  DB recreated on boot. Kept (not dead): `IDecisionJournal`/`InMemoryDecisionJournal` (oracle), `GovernorStateChanged`.
- **K-GAP-3 (✅ DONE)** — `EngineRunner.ReportBar` publishes `BarIngested` → per-run bars persist (live/non-catalog
  charts). `PerRunBarPersistenceTests`.
- **Empty/invalid backtest guard (✅)** — API 400 on no-symbol / inverted range / non-positive balance
  (`BacktestStartGuardTests`); SPA blocks client-side.
- **F8 (✅)** new-backtest per-strategy overrides + resolved-config preview + CSV export · **F4 (✅)** MAE/MFE scatter
  + JSON/Markdown report export · **F2 (✅)** per-bar "why" verdict table.
- **Gate:** build 0 err · Unit 228/0/5-skip · Simulation non-cTrader 97/0 · Integration 43/43 · SPA build green ·
  `grep PipelineEvent|BarEvaluationEntity|ProtectionLedger src → 0`.

## Carry-forward (documented in OPEN-ISSUES → "iter-37 closure")

- **K-GAP-5 per-trade `TradeResultEntity.Timeframe` column** (multi-timeframe runs only; chart already works via the
  run timeframe; needs `PublishTradeClosed`/reducer threading + an EF migration; Low severity).
- F7 server-side validation framework (the UI + empty/invalid guard cover the practical case).
- Owner live-verification: cTrader-E2E ledger reconciliation (the env has creds; the 4 cTrader tests + NetMQ are out of scope here).
