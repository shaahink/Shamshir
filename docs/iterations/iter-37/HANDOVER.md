# Iter-37 — Frontend Finish + Pressure/Reality Tests + Dead-Code Sign-off

**Branch:** `iter/37-frontend-finish`  
**Base:** `iter/36-kernel-cutover` (commit `d042a78`)  
**Status:** SIGNED OFF — all gates green. Build 0 err · Unit 228/0/5-skip · Simulation non-cTrader 97/0 · Integration 48/0 · SPA build green.  
**Companion plans:** `PLAN.md` (F1–F8 frontend), `TEST-PLAN.md` (G/F/J/E/B/C/D tests), `OPEN-ISSUES.md` → "iter-37 closure" + "iter-37 testing-found issues (T1–T12)".  

---

## What this iteration delivers

### 1. K-GAP code fixes (iter-36 cutover follow-ups)

| K-GAP | What | How | Test |
|-------|------|-----|------|
| **K-GAP-1** | Day/week/month roll emitted in the kernel loop so multi-day runs actually reset (DD re-bases to current equity; governor profit-lock clears). Fixes **C4 + H7** for multi-day runs. | `ResetClock.cs` (pure boundary detector) → `KernelBacktestLoop.ProcessBarAsync` → `EngineReducer.HandleDayRolled/WeekRolled/MonthRolled` | `ResetClockTests` (10), `ResetReducerTests` (4), `KernelResetMultiDayTests` (3) |
| **K-GAP-2** | Backtest equity persisted to `EquitySnapshots` table (was in-memory-only → empty on finish). | `EngineRunner.FlushBacktestEquityAsync` batch-flushes `BufferedEquitySink` on completion; `PersistentEquitySink` mode injected (not hard-coded `Live`) | `BacktestEquityFlushTests` (2), `KernelEquitySnapshotTests` (1) |
| **K-GAP-3** | Per-run bars persisted on the kernel path — live/non-catalog charts render (was only the oracle `TradingLoop` published `BarIngested`). | `EngineRunner.ReportBar` publishes `BarIngested` per bar → `BarPersistenceHandler` → `BufferedBarWriter` → `SqliteBarRepository` | `PerRunBarPersistenceTests` |
| **K-GAP-4** | Report/funnel readers repointed off the empty `PipelineEvents`/`BarEvaluations` onto the StepRecord journal. | `RunProjection` (timeline→`IJournalQueryRepository`, equity→persisted `EquitySnapshot`), `BacktestQueryService.GetStrategyBreakdownAsync` (→ journal `StrategyVerdicts`) | `StrategyBreakdownFromJournalTests` |
| **K-GAP-5** (partial) | Trade-detail exposes the run's timeframe (`t.timeframe`, no `|| 'H1'`). Per-trade entity column deferred (multi-TF only). | `TradesController` already resolved from the run's period | `ChartDataTests.TradeDetail_ExposesRunTimeframe` |
| **K-GAP-6** | Multi-symbol fill attribution — venue stamps `ExecutionEvent.Symbol`; pump prefers it over the old first-open-position/`EURUSD` guess. | `ExecutionEvent.Symbol` (optional init), `KernelBacktestLoop.PumpAsync` → `exec.Symbol ?? ResolveSymbol`; `FakeVenue` + `BacktestReplayAdapter` stamp it | `MultiSymbolAttributionTests` (2) |

### 2. Test spine (TEST-PLAN G/F/J/E/B/C/D) — 97 Simulation + 12 Unit tests

| Phase | Tests | What they prove |
|-------|-------|----------------|
| **G** | `GovernorDrawdownProtectionTests` (12) | Governor cooling-off/profit-lock, drawdown floors (trailing→peak, fixed→initial, daily-base, weekly/monthly), protection entry/exit (C4/C5) |
| **F** | `FtmoPressureTests` (F1–F3, + M6) | Daily loss limit halts→resumes next day, max-loss is terminal, profit target keys off equity (M6) |
| **J** | `JournalSourceOfTruthTests` + `JournalRejectTests` + `JournalReadPathTests` (9) | One StepRecord per event (gap-free Seq), OrderProposed↔OrderFilled join key, costs present, named violations, per-strategy verdicts, funnel totals, multi-day determinism, SQL paging, NDJSON round-trip |
| **E** | `BacktestEquityFlushTests` + `KernelEquitySnapshotTests` (3) | On-completion batch flush maps authoritative state with the run's `EngineMode` |
| **B** | `ChartDataTests` (2) | Bars dedup by timestamp, trade-detail returns the run's timeframe |
| **C** | `StrategyCharacterizationTests` (2) | `EmaAlignment` fires on trend, `MeanReversion` fires on oscillation (closes the "silently dead" gap) |
| **D** | `MultiSymbolAttributionTests` (2) + existing `DuplicateRunE2ETests` | Venue stamps Symbol, pump prefers it; D2 determinism + duplicate lineage already covered |

### 3. Frontend F1–F8 (Angular, `web-ui/src/app/features/`)

| Phase | Feature | Key files | Try it |
|-------|---------|-----------|--------|
| **F1** | Unified journal — ORDER+FILL join by `orderId`, full kind filter (+TRAIL/BREAKEVEN/PARTIAL), per-kind badge colors, named-violation renderer (no raw JSON/`[object Object]`) | `run-report.component.ts` | Open a finished run → scroll to Journal |
| **F2** | Per-strategy funnel (`GET /api/runs/{id}/analytics/strategies` — signals/trades/win%/top no-signal reasons) + per-bar "why" verdict table | `RunsController.cs` (new endpoint) + `run-report.component.ts` | Open any run with data → see "Per-strategy funnel" + "Per-bar why" |
| **F3** | NDJSON download (`/api/runs/{id}/journal/export`), Duplicate (re-runs with same config via POST `/api/runs/{id}/duplicate`), run lineage (parent, dataset/config hashes) | `run-report.component.ts` header | Buttons in the report header |
| **F4** | Full report stats (Net/Gross/Comm/Swap, profit factor, win rate, MAE/MFE scatter chart, JSON/MD export buttons) | `run-report.component.ts` + `scatter-chart.component.ts` | Open a finished run → stats tiles + scatter |
| **F5** | Live monitor: stick-to-bottom journal (auto-scroll near bottom, "jump to latest" affordance), balance-null fix (falls back to equity), breach banner clears on recovery, `ngOnDestroy` cleanup | `run-monitor.component.ts` | Open a running/monitor page |
| **F6** | Trade-detail SL/TP chart (entry/exit/SL/TP markers, order-safe window), trade-list timeframe column | `trade-detail.component.ts`, `trade-list.component.ts`, `candle-chart.component.ts` | Click any trade |
| **F7** | Risk-profile validate-before-save (blocks invalid fractions, inverted DD, no name), per-strategy override textareas | `risk-profile-detail.component.ts`, `new-backtest.component.ts` | Enter invalid values → Save blocked + error list shown |
| **F8** | New-backtest: resolved-config preview panel, per-strategy JSON overrides, CSV export (`/api/export/trades.csv`), dashboard placeholder hygiene (removed fake tiles) | `new-backtest.component.ts`, `runs.service.ts`, `dashboard.component.ts` | `npm run start` + walk through |

### 4. D-drop — dead-code removal from the kernel upgrade (`5308b01`)

Deleted (no `grep` hits in `src`):

| Category | What was removed |
|----------|------------------|
| **Observability sinks** | `PipelineEventEntity`, `BarEvaluationEntity`, `PipelineEventMapping`, `SqlitePipelineEventRepository`, `IPipelineEventRepository`, `EventsController` + `/api/events` endpoint + events SPA page + route + nav link |
| **Journal interfaces** | `JournalNormalizer` (superseded by real event names on StepRecord) |
| **Protection ledger** (never fired — `GovernorStateChanged` is never published) | `ProtectionLedgerPersistenceHandler`, `ProtectionLedgerWriter`, `ProtectionQueryService`/`IProtectionQueryService`, `ProtectionController` + `/api/protection/*` endpoints + `DailyProtectionLedger`/`ProtectionLedgerEntry` entities+tables + `compliance` SPA page + route + nav link |
| **Dead consumers** | `RunQueryService.GetRunJournalAsync` (no caller), `BacktestController.Journal` endpoint (the `/api/backtest/{id}/journal` legacy endp. superseded by `/api/runs/{id}/journal`) |
| **EF reset** | Deleted the old `InitialCreate` + snapshot; regenerated a fresh `InitialCreate` with no dead tables; dev DBs deleted (recreated+seeded on boot) |
| **Kept (correctly)** | `IDecisionJournal`/`InMemoryDecisionJournal` (golden-oracle contract), `GovernorStateChanged` event (oracle tests), the live `BarEvaluation` kernel record |

### 5. Testing-found fixes (T1–T12)

Found during owner testing on the cTrader engine path (`CTrader:UseForBacktest=true`). Fixed/assessed:

| ID | Issue | Fix | Lines of code changed |
|----|-------|-----|----------------------|
| **T1 🟢** | Trade ENTRY timestamps wall-clock → inverted chart window → "No price data" | **cBot source**: `MakeExecResult`/`MakeModifyResult` → instance + `Server.TimeInUtc` (was `DateTime.UtcNow`). **Frontend**: `trade-detail` → order-safe window (`min/max ± pad` — no more inverted `/api/bars`). ⚠ Adapter-authoritative `CTraderBrokerAdapter` still pending; cBot `.algo` rebuild needed for live effect. | `TradingEngineCBot.cs:494,508,558,572` • `trade-detail.component.ts:63-66` |
| **T2** | Journal wall-clock `EquityObserved` interleaved + out-of-order | Diagnosed (same cBot wall-clock root as T1). Not fixed (needs cBot + account-stream sim-time fix). | — |
| **T3 🟢** | Funnel "Top no-signal reasons" → `undefined (undefined)` | **ValueTuple → named record**: `NoSignalReason(string Reason, int Count)` so JSON emits `reason`/`count` not `item1`/`item2`. | `IBacktestQueryService.cs:22-23` • `BacktestQueryService.cs` mapping • test assertions |
| **T4** | Per-bar "why" only warmup rows (200-entry journal limit) | Diagnosed (client-side 200-entry limit needs server-side paged endpoint). Not fixed. | — |
| **T5** | Section labels/layout confusion | Note for verification. | — |
| **T6** | Trades missing commission/swap | Diagnosed (cTrader FORCE-close cost reporting). Needs cBot cost fields fix + adapter path. | — |
| **T7** | Live journal only CLOSE/FORCE | Diagnosed (kernel path emits only BAR/CLOSE progress; SL/TP not detected on cTrader path → all force-closed). Not fixed. | — |
| **T8 🟢** | Governor disable in UI saved but didn't take effect | **AND the DB `GovernorOptions.Enabled` into the kernel gate switch** (`ConstraintSet.GovernorEnabled`). WireRiskRules now: `constraints with { GovernorEnabled = constraints.GovernorEnabled && govOptions.Enabled }`. | `EngineHostFactory.cs:51-56` |
| **T9 🟢** | Backtest cancellation error on finish (saved but "failed") | **Dedicated `catch(OperationCanceledException)`** that finalizes as `cancelled`/`completed` with the saved trades, info log (no error), ExitCode 0. | `BacktestOrchestrator.cs:333-351` |
| **T10** | Duplicate pointless (re-runs via cTrader, ignores saved dataset bars, no edit UI) | Not fixed (requires K6 deterministic replay + F3 duplicate edit UI). | — |
| **T11** | Live equity chart: no DD line + wrong axes | Diagnosed (DD not fed + wall-clock points corrupt time axis = T2 cascade). Not fixed. | — |
| **T12** | No DD timeline on report (cTrader path) | Diagnosed (cTrader path doesn't flush equity/daily-PnL like kernel path = K-GAP-2 gap on the cTrader engine). Not fixed. | — |

(Fixed gate: 7 fixes committed across T1/T3/T8/T9 + cBot.

### 6. Testing infrastructure improvements

| Change | Files |
|--------|-------|
| **HTTP contract tests for new/modified endpoints** — `RunEndpointsTests`: `/analytics/strategies`, `/journal`, `/journal/export`, `/equity`, `/export/trades.csv` (+ 400 case), `strategyOverrides` round-trip | `tests/.../Api/RunEndpointsTests.cs` |
| **Stale WebSmokeTests pruned** — removed stale `/events`, `/compliance`, `/api/events` checks (routes/controllers deleted in D-drop) | `tests/.../WebSmokeTests.cs` |
| **Repo-level tests → real SQLite `:memory:`** — 5 direct-DbContext tests converted from temp file (and the `TradeRepositoryTests` from the non-relational EF InMemory provider) to a shared `SqliteInMemory` helper (kept-open `SqliteConnection`, real SQLite engine = constraints/transactions/SQL fidelity). WebApplicationFactory tests stay on temp-file (highest fidelity for full-engine tests). | `Support/SqliteInMemory.cs` + 5 converted test files |
| **Playwright e2e spec extended** — 13→18 tests, covering new iter-37 surfaces (Duplicate, exports, funnel/why tables, dashboard, new-backtest preview, risk-profile validation) | `web-ui/tests/e2e/ui-smoke.spec.ts` |

---

## Design decisions logged

### D1 — K-GAP-1 re-base to current equity (EngineReducer)
Day/Week/MonthRolled now re-base drawdown to `state.Account.Equity` (the authoritative current equity, set by the last `EquityObserved`). The old code re-based to the stale previous start equity. `ResetClock` collapses weekend gaps to one crossing per kind (the re-base is idempotent). `ResetConfig.FromRuleSet` parses the prop-firm ruleset's `DailyResetTime` + timezone.

### D2 — On-completion batch equity flush (K-GAP-2)
Preferred over per-bar DB writes (which risk perf). `EngineRunner.FlushBacktestEquityAsync` writes the buffered snapshots in one batch at the end via `IEquityRepository.SaveBatchAsync`. `PersistentEquitySink` mode is now injected (not hard-coded `Live`). `EquitySnapshotFlush.ToEquity` is the shared mapper.

### D3 — Repoint readers onto the journal (K-GAP-4), not physically delete tables
We repointed `RunProjection` + `BacktestQueryService` onto the StepRecord journal via `IJournalQueryRepository` but kept the physical `PipelineEvents`/`BarEvaluations`/`DailyProtectionLedger` tables alive for the D-drop pass — to avoid breaking the `events`/`compliance` SPA pages mid-iteration. D-drop then removed them all in one go.

### D4 — Venue stamps symbol on ExecutionEvent (K-GAP-6)
`ExecutionEvent.Symbol` is optional (init-only, default null). The pump prefers `exec.Symbol` over `ResolveSymbol(state, orderId)`. `FakeVenue` + `BacktestReplayAdapter` stamp it; `CTraderBrokerAdapter` leaves null (falls back to `ResolveSymbol` — correct for the live cTrader path where one adapter handles one symbol at a time). No multi-symbol harness exists yet (the D1 test proves the translation, not a full multi-symbol run).

### D5 — Per-trade chart: order-safe window (T1)
The chart window no longer assumes `openedAtUtc < closedAtUtc`. Uses `Math.min/max` + absolute `Math.abs` pad, then builds `[lo-pad, hi+pad]`. This is defensive — wrong timestamps (wall-clock entry) no longer produce an inverted `/api/bars` range.

### D6 — Named record `NoSignalReason` over ValueTuple (T3)
C# `ValueTuple (string Reason, int Count) serializes as {"item1":...,"item2":...}` by System.Text.Json (tuple element names are erased). The SPA reads `reason`/`count` → undefined. Fixed by `public sealed record NoSignalReason(string Reason, int Count)`. JSON now matches the SPA interface.

### D7 — Governor page is authoritative (T8)
`ConstraintSet.GovernorEnabled` (the kernel `PreTradeGate` switch) is now the **AND** of the prop-firm ruleset toggle AND the DB `GovernorOptions.Enabled`. Disabling on the Governor page turns the governor off at the gate. Ruleset toggle still applies via AND. The `GovernorMachine` (external verdict) already reads the DB options via the per-run `AddRiskFromOptions` path.

### D8 — End-of-run cancellation → not "failed" (T9)
An `OperationCanceledException` from completion/teardown/user-cancel used to land in the general `catch(Exception)` → run marked **failed** + `LogError`. Now a dedicated `catch(OperationCanceledException)` finalizes as `cancelled` (user) or `completed` (teardown), with the trades-so-far persisted, ExitCode 0, info log only.

### D9 — EF reset for D-drop
Rather than additive migrations, we deleted the single `InitialCreate` + snapshot and regenerated a fresh `InitialCreate` from the trimmed model (no `PipelineEvents`/`BarEvaluations`/protection-ledger tables). Dev DBs deleted so the app recreates+seeds on boot. The regen-init approach matches the iter-36 convention.

### D10 — SQLite `:memory:` for repo tests (not EF InMemory)
Repo-level Integration tests use **real SQLite in-memory** via a kept-open `SqliteConnection` (shared across scopes — essential for `PerRunBarPersistenceTests`' `BufferedBarWriter` background-scope flush). EF InMemory provider is **avoided** — it's non-relational (no `EnsureCreated`/migrations, no constraint enforcement, LINQ runs client-side → false confidence). WebApplicationFactory tests stay on temp-file (fidelity + avoids shared-cache/concurrency caveats).

---

## Carry-forward (documented in OPEN-ISSUES → "iter-37 testing-found issues" + "Still deferred")

### Backend (code + tests needed)

| Item | Priority | What's needed | Applies to |
|------|----------|---------------|------------|
| **T1 adapter-authoritative** | Medium | `CTraderBrokerAdapter` backtest path: override frame `simTime` with `BrokerTimeUtc` when in backtest mode (so entry timestamps are correct even without a cBot rebuild). Needs an `EngineMode` flag. | cTrader backtest path |
| **T1 cBot rebuild** | Medium | Rebuild the cBot `.algo` in cTrader (the `Server.TimeInUtc` source fix is correct but needs recompilation into the running cBot). | cTrader cBot |
| **T2/T6/T7/T11/T12** (cTrader fidelity) | Medium | Make the cTrader/account path stamp sim-time (equity poller, account frames, progress events). Share the T1 cBot root fix. Many are cascades of the same wall-clock leak. | cTrader path |
| **T4 per-bar why endpoint** | Low | Add a server-side paged funnel endpoint that returns BarClosed verdicts (the client-side 200-row slice only shows warmup). | run-report |
| **T10 duplicate redesign** | Low | Duplicate should replay the saved `DatasetId` bars (deterministic), not re-fetch from cTrader. UI should open `new-backtest` prefilled with editable strategy/risk/overrides. | F3, K6 |
| **T5 layout check** | Low | Verify the why-table isn't rendered in the Trades slot | run-report |

### Frontend (SPA only)

| Item | Priority | What's needed |
|------|----------|---------------|
| **T4 workaround** | Low | Raise the client-side journal `limit=200` to `limit=500` or filter out `EquityObserved` so more `BarClosed` rows surface in the why-table. |

### Remaining items (out of iter-37's agreed scope)

- **cTrader-E2E** (CT-1/CT-2/M3/L4/H13 + the live ledger reconciliation mismatch 17-vs-16): needs the cTrader platform + credentials. Owner-verified. The 17-vs-16 mismatch suggests a missing trade in the DB journal from the live cTrader path — investigation needed.
- **K-GAP-5 per-trade Timeframe column** (multi-timeframe runs): Low; chart already works via the run-derived timeframe. Needs reducer threading + EF migration.
- **Non-UI bucket** (M8/M9/M13/H11/H17, UNF-*/MIN-*/OBS-*): not in scope, available for a future pass.

---

## How to start a fresh session on this branch

```powershell
git checkout iter/37-frontend-finish
dotnet build
# Delete the old dev DB so the fresh EF migration applies (the app recreates+seeds):
Remove-Item -Force src/TradingEngine.Web/data/trading.db, data/trading.db -ErrorAction SilentlyContinue
# Run the app:
# run-shamshir (the skill) or `dotnet run --project src/TradingEngine.Web`

# Verify suites:
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName!~.E2E.&FullyQualifiedName!~NetMQBridgeTest&FullyQualifiedName!~InProcessEngineSmoke"

# e2e (requires a running app at :5134 + SEEDED_RUN_ID):
# Set-SEEDED_RUN_ID "<run-id>"
# cd web-ui && npx playwright test
```

### Key files to land on

- **Architecture reference:** `docs/reference/CODE-MAP.md`, `docs/reference/SYSTEM-REFERENCE.md`
- **Open issues tracker:** `docs/OPEN-ISSUES.md` (iter-37 closure + testing-found issues T1–T12)
- **Iteration plans:** `docs/iterations/iter-37/PLAN.md` + `docs/iterations/iter-37/TEST-PLAN.md`
- **This handover:** `docs/iterations/iter-37/HANDOVER.md`
- **Resolved issues:** `docs/RESOLVED-ISSUES.md`
