# Shamshir — Open Issues

**Updated**: 2026-06-20 (iter-37 frontend-finish + pressure/reality tests DELIVERED on branch `iter/37-frontend-finish`)
**Branch**: `iter/37-frontend-finish` (cut from `iter/36-kernel-cutover`)
**Total open**: see the iter-37 closure block below — K-GAP-1/2/3/4/6 resolved, dead code removed; K-GAP-5 deferred

> **iter-37 SIGNED OFF (this branch).** The iter-36 cutover follow-ups (K-GAP-1..6, K-GAP-5 excepted) + the
> frontend finish (F1–F8) + the pressure/reality test spine (G/F/J/E/B/C/D) + the **D-drop dead-code removal
> (PipelineEvents/BarEvaluations + protection-ledger) with an EF reset** are delivered. See **"## iter-37 closure"**.
> Verified: build 0 err · Unit 228/0/5-skip · Simulation non-cTrader 97/0 · Integration 43/43 · SPA build green.
> cTrader-E2E + NetMQ + InProcessEngineSmoke remain out of scope (cTrader/environmental, owner-verified).
>
> **Owner manual-testing pass surfaced new issues — see "## iter-37 testing-found issues" (T1–T12).** Common thread:
> default-venue backtests run through the in-process cTrader engine (`CTrader:UseForBacktest=true`), which has
> wall-clock-timestamp + progress/equity-wiring gaps the kernel/replay path doesn't.

> **iter-36 complete.** The kernel is now the **SOLE production engine** — the imperative twins
> (`OrderDispatcher`, `KernelOrderGate`, `AccountProcessor`) are removed from `src` (relocated to the
> `TradingEngine.Tests.Support` golden-oracle assembly, D81; `grep … src → 0`). Production runs only
> `KernelBacktestLoop` for both live + backtest. There is **one lossless journal** (the StepRecord stream;
> `PipelineEventWriter` + `BarEvaluationHandler` deleted, D83). Per-strategy risk profiles, trailing/breakeven,
> and Monitor equity snapshots all run in the kernel loop. Duplicate-with-lineage + dataset/config identity
> are persisted (`ParentRunId`, EF regen-init, D84).
>
> **Shadowed-bug reconciliation (C3/C4/H1/H2/H5/H6/M7):** the old buggy `RiskManager` DD/protection/sizing
> methods (`Validate`, `OnDailyReset`, `CalculateLotSize`, `ValidateOrder`) have **no production caller** —
> verified by `grep "\.Validate(|\.OnDailyReset(|\.CalculateLotSize(|\.ValidateOrder(" src → 0`. They survive
> ONLY in the test-oracle path (`Tests.Support`), exercised solely to prove the kernel reproduces golden.
> The kernel (`PreTradeGate`/`KernelSizing`/`Kernel.DecideReset`) is the single authority going forward.
>
> All suites: build 0 errors, 208 unit (−1 deleted obsolete PipelineEventWriter test) + golden/determinism/
> arch green, in-host replay produces StepRecord journal, `run-shamshir` driver 11/11.

Fixed items → `docs/RESOLVED-ISSUES.md`. Roadmap → `docs/NEXT-STEPS.md`.
Test-suite audit + backlog → `docs/reference/TEST-AUDIT.md`.
Pre-cutover full-system audit (archived, historical) → `docs/archive/SYSTEM-AUDIT.md`.
Iter-36 handover → `docs/iterations/iter-36/HANDOVER.md` (ROUND 2 section).

---

## 🔴 iter-36 cutover follow-ups (found in static audit 2026-06-20)

Found by static analysis of the *production* kernel path after the cutover. The cutover gates were proven
on the **single-day, single-symbol golden fixture**, which does not exercise day-rolls, multi-symbol, or
DB-persisted equity — so these slipped through green gates. Severity reflects realistic (multi-day FTMO)
backtests, which is exactly what iter-37 testing needs.

### K-GAP-1 — Day/Week/Month roll never emitted → multi-day runs never reset (REINTRODUCES C4 + H7)
**Severity**: Critical. The production loop (`KernelBacktestLoop.ProcessBarAsync` / `EngineRunner`) enqueues
`OrderProposed` / `BarClosed` / `EquityObserved` / `StopLossModifyRequested` — but **never** `DayRolled` /
`WeekRolled` / `MonthRolled`. The reducer handlers (`EngineReducer.HandleDayRolled` etc.) and the
protection-exit policy (`Kernel.DecideReset`) exist but are **dead in production**.
**Evidence**: `grep -rn "new DayRolled\|new WeekRolled\|new MonthRolled" src → 0`; the only historical
emitter (`DailyResetService`) was retired (`Program.cs:36`).
**Impact on a multi-day backtest**: (a) daily DD never re-bases — `DrawdownReducer.ApplyDailyReset` never
runs, so `DailyStartEquity` is frozen at the run's first day and daily DD is measured against it forever;
(b) the governor never daily-resets — profit-lock / loss-streak / cooling-off persist across days (this is
**H7 / BUG-09-SIBLING reintroduced**); (c) MaxDD/DailyDD protection never auto-exits (this is **C4
reintroduced**) because the exit policy is layered on the roll events. ⇒ The "C4/H7 CLOSED — kernel is
authoritative" notes below are **hollow for multi-day runs**: the kernel owns the logic but the trigger is
never fired.
**Fix (NEW-1, deferred from K2)**: in `ProcessBarAsync`, detect a sim-time boundary crossing
(`bar.OpenTimeUtc` vs the prop-firm reset time-of-day + zone, from `PropFirmRuleSet.DailyResetTime`/`Zone`)
and enqueue the roll event(s) **before** the bar's evaluation. Not a quick win (needs the reset
clock + a weekly/monthly boundary rule + a golden re-baseline check). → iter-37 prerequisite / test plan G0.
**⏳ BACKBONE LANDED (skeleton + tests) 2026-06-20, pending build/suite verify** — `ResetClock` (pure
detector) + reducer re-base-to-current-equity fix + `KernelBacktestLoop`/`EngineRunner` wiring + pure
`ResetClock`/`Reducer` tests + an end-to-end multi-day test. See `docs/iterations/iter-37/TEST-PLAN.md` →
G0 STATUS for the exact diff + the agent's verify checklist. Also surfaced a second latent bug now fixed:
the reset handlers re-based each period to its **own stale start equity** (never moved) — now re-base to
`EngineState.Account.Equity`.

### K-GAP-2 — Per-bar equity/account snapshots not persisted for backtests
**Severity**: High. `EngineRunner.ReportBar` computes a per-bar `AccountSnapshot` (`KernelEquitySnapshot.From`,
off the authoritative `EngineState`) and calls `_equitySink.Observe(...)` every bar — but in **Backtest**
mode `IEquitySink` is `BufferedEquitySink` (in-memory; it implements `IAccountSnapshotStore` only for the
live-monitor read during the run). It **never writes the `EquitySnapshots` table**. After the run the inner
host is disposed and the snapshots are gone.
**Evidence**: `EngineServiceCollectionExtensions.AddEventInfrastructure` registers `BufferedEquitySink` for
`Backtest`, `PersistentEquitySink` only otherwise; `RunQueryService.GetRunEquityAsync` reads
`IEquityRepository.GetByRunIdAsync` (the DB table) → empty for a finished backtest ⇒ no equity/DD curve in
the report.
**Fix**: flush the `BufferedEquitySink` to `EquitySnapshots` on run completion (preferred — one batched
write, keeps per-bar live reads cheap), **or** register `PersistentEquitySink` for backtest too (per-bar
fire-and-forget DB writes — perf risk). Also fix `PersistentEquitySink` hard-coding `EngineMode.Live` and
the `AccountSnapshot→EquitySnapshot` map dropping open-position count / governor state. → test plan E-suite.

### K-GAP-3 — Per-run bars not persisted in the kernel path (chart works for catalog backtests, not live)
**Severity**: Medium. `BarIngested` is published **only** by the test-oracle `TradingLoop` (`TradingLoop.cs:51`),
which is not in the production path, so a kernel run never writes per-run bars via `BarPersistenceHandler`.
**Good news (the long-standing "no bars for the chart" bug is effectively resolved for backtests):** a
backtest over **imported catalog bars** (`RunId=""`) still renders — `BacktestReplayAdapter` reads those
bars and `BarQueryService` serves them by symbol+timeframe (no RunId filter). So `/api/bars` returns data
and the per-trade chart works **for any backtest over catalog data**.
**Still open**: live runs (and runs over non-catalog data) don't persist bars → blank chart. Out of iter-37
scope; track for the live path. → test plan B-suite asserts catalog-bar charts work.

### K-GAP-4 — Report/funnel readers still on the now-empty PipelineEvents/BarEvaluations
**Severity**: High (iter-37 blocker; already a documented F2/F4 carry-forward — restated here for the tracker).
`RunProjection` + `RunFunnel` read `IPipelineEventRepository` (`PipelineEvents`), and `BacktestQueryService`
reads `BarEvaluations` — neither is written after K5. ⇒ timeline / signals / rejections / governor-timeline /
per-bar "why" funnel all return **empty**. Repoint onto the StepRecord journal (`IJournalQueryRepository`).
→ iter-37 F2/F4.

### K-GAP-5 — Trade entity has no `Timeframe`
**Severity**: Low. `TradeResultEntity` carries costs (Gross/Comm/Swap/Net) + OrderId but **no `Timeframe`**;
iter-37 F6's per-trade chart needs it to fetch the right bars (the SPA currently falls back to `|| 'H1'`).
Add `Timeframe` on close (it's on the position/bar). → iter-37 F6 / test plan B-suite.

### K-GAP-6 — `KernelBacktestLoop.ResolveSymbol` guesses on multi-symbol
**Severity**: Low. When an execution event's order isn't in `state.Positions`, it returns the first open
position's symbol, else `EURUSD`. Correct for single-symbol golden; can mis-attribute a feedback event on a
multi-symbol run. Carry the symbol on the venue execution event instead. → test plan D-suite (multi-symbol).

---

## iter-37 closure (delivered on `iter/37-frontend-finish`)

**K-GAP code fixes**
- **K-GAP-1 (day/week/month roll) — ✅ RESOLVED.** `ResetClock` emits the rolls in `KernelBacktestLoop`;
  `EngineRunner` wires `ResetConfig` from the active ruleset; reducer re-bases DD to current equity. Guarded by
  `ResetClockTests`/`ResetReducerTests` + `KernelResetMultiDayTests` (incl. `DailyDrawdownRebasesEachDay`). The
  G/F suites prove C4 + H7 are now *provably* closed in the multi-day production path.
- **K-GAP-2 (backtest equity not persisted) — ✅ RESOLVED.** `EngineRunner.FlushBacktestEquityAsync` batch-flushes
  `BufferedEquitySink → EquitySnapshots` on completion; `PersistentEquitySink` mode no longer hard-coded Live.
  Tests: `BacktestEquityFlushTests` + `KernelEquitySnapshotTests`.
- **K-GAP-4 (readers on empty tables) — ✅ RESOLVED (functional).** `RunProjection` (timeline→journal, equity→
  persisted snapshots) + `BacktestQueryService.GetStrategyBreakdownAsync` (→ journal verdicts) repointed onto the
  StepRecord journal. Test: `StrategyBreakdownFromJournalTests`. *(Physical tables dropped in the sign-off D-drop.)*
- **K-GAP-6 (multi-symbol mis-attribution) — ✅ RESOLVED.** `ExecutionEvent` carries `Symbol`; the pump prefers it;
  `FakeVenue` + `BacktestReplayAdapter` stamp it. Test: `MultiSymbolAttributionTests`.
- **K-GAP-3 (chart bars) — ✅ RESOLVED.** `EngineRunner.ReportBar` publishes `BarIngested` per bar so the kernel
  path persists per-run bars (live + non-catalog runs now render; backtest-over-catalog already did, dedup'd by
  `BarQueryService`). Tests: `PerRunBarPersistenceTests` + `ChartDataTests.Bars_DedupByTimestamp`.
- **K-GAP-5 (Trade.Timeframe) — PARTIAL.** Trade-detail exposes the run's timeframe (chart fetches correctly, SPA
  `|| 'H1'` no longer needed); test `ChartDataTests.TradeDetail_ExposesRunTimeframe`. **Deferred:** a per-trade
  `TradeResultEntity.Timeframe` column for multi-timeframe runs (needs `PublishTradeClosed`/reducer threading + an
  EF migration; Low severity).

**Test spine (TEST-PLAN G/F/J/E/B/C/D)** — all delivered & green: governor/drawdown/protection (G1–G3), FTMO
pressure (F1–F3; F4 skip-breadcrumb), journal source-of-truth (J1–J4), equity persistence (E), bars/chart (B),
per-strategy characterization (C), multi-symbol + replay/duplicate (D; D2 via J3 determinism + `DuplicateRunE2ETests`).
**M6** confirmed correct (profit target keys off equity).

**Frontend (PLAN F1–F8)** — delivered: F1 unified journal (order/fill join, full kind filter, per-kind badges,
named-violation renderer — no `[object Object]`); F2 per-strategy funnel (`GET /api/runs/{id}/analytics/strategies`);
F3 NDJSON download + Duplicate + lineage (parent/dataset/config hashes); F4 report stats (H29 formula confirmed);
F5 live-monitor stick-to-bottom + balance-null fix (L1/L2/L3/NEW-7); F6 trade TF column + SL/TP chart; F7 risk-profile
validate-before-save; F8 dashboard placeholder hygiene (no fabricated zeros). Also fixed 2 stale `WebSmokeTests`
pointing at dead `/api/backtest/runs` + `/api/backtest/compare` routes.

**Sign-off additions (dead-code removal + finish)**
- **✅ D-drop DONE — no kernel-upgrade dead code remains.** Deleted `PipelineEvents`/`BarEvaluations` (entities,
  `PipelineEventMapping`, `SqlitePipelineEventRepository`, `IPipelineEventRepository`, `PipelineEventResponse`,
  `JournalNormalizer`), the dead consumers (`EventsController` + events SPA page, `BacktestController.Journal`,
  `RunQueryService.GetRunJournalAsync`), and the never-fired protection-ledger path
  (`ProtectionLedgerPersistenceHandler`/`ProtectionLedgerWriter`/`ProtectionQueryService`/`ProtectionController` +
  `DailyProtectionLedger`/`ProtectionLedgerEntry` tables + the `compliance` SPA page — `GovernorStateChanged` is
  never published). **EF reset:** old `InitialCreate` + snapshot deleted, fresh `InitialCreate` regenerated with no
  dead tables; dev DB recreated on boot. `grep PipelineEvent|BarEvaluationEntity|ProtectionLedger src → 0`. *Kept
  (not dead): `IDecisionJournal`/`InMemoryDecisionJournal` (golden-oracle contract), `GovernorStateChanged` (oracle).*
- **✅ Empty/invalid backtest guard** — `RunsController`/`BacktestController` Start return 400 on no-symbol /
  inverted date range / non-positive balance (`BacktestStartGuardTests`); SPA blocks client-side.
- **✅ F4** MAE/MFE scatter + JSON/Markdown report export · **✅ F8** new-backtest per-strategy overrides +
  resolved-config preview + CSV export · **✅ F2** per-bar "why" verdict table.

**Still deferred (documented carry-forward):**
- **K-GAP-5 per-trade Timeframe column** (above; Low — multi-timeframe runs only; chart already works via run TF).
- F7 server-side validation framework (UI + the empty/invalid guard cover the practical case).

---

## iter-37 testing-found issues (owner manual testing, 2026-06-20)

> **Framing — the common thread.** `appsettings.Development.json` has `CTrader:UseForBacktest = "true"`, so a
> New-Backtest with the default (empty) venue runs through the **in-process cTrader engine + cBot**, NOT the
> credential-free `BacktestReplayAdapter`. Most of T1/T2/T6/T7/T12 are cTrader-path data-fidelity gaps the
> kernel/replay path doesn't have. (`BacktestOrchestrator.cs:296-300` venue→engine selection.)

### T1 — Trade ENTRY timestamp is wall-clock, not sim-time (breaks the per-trade chart) 🔴
**Observed**: clicking a trade → chart calls `/api/bars?...&from=2026-11-30…&to=2025-10-19…` (from > to) → "No price data".
**Root cause** (traced via DB + Journal): the cBot stamps exec frames inconsistently — `MakeExecResult`/`MakeModifyResult`
were `private static` so they couldn't reach `Server` and used `DateTime.UtcNow` (wall-clock) for `simTime`
(`TradingEngineCBot.cs:508,572`), while `OnPositionClosed` uses `Server.TimeInUtc` (sim, `:652`). `CTraderBrokerAdapter.cs:371`
reads the frame `simTime` (`… : DateTime.UtcNow`). In a backtest the entry `OrderFilled.OccurredAtUtc` becomes wall-clock →
`PositionLifecycle.cs:89 OpenedAtUtc` → `TradeResult.OpenedAtUtc` wall-clock → **negative `DurationSeconds`**; the SPA window
`trade-detail.component.ts:63-65` assumes `opened<closed` → inverted `/api/bars` range → empty.
**DB evidence**: all 16 trades `OpenedAtUtc=2026-06-20 21:03:xx` (run wall-clock), `ClosedAtUtc`=sim.
**Fix (designed, approved "all three", not yet applied)**: cBot helpers → instance + `Server.TimeInUtc`; `CTraderBrokerAdapter`
authoritative on sim-time in backtest (+ `?? BrokerTimeUtc`); `trade-detail` order-safe window. **Tags**: F6 (per-trade chart),
31-A2, K-GAP-5.
**⏳ PARTIAL FIX**: cBot source (`MakeExecResult`/`MakeModifyResult` → instance + `Server.TimeInUtc`) ✅ + `trade-detail` order-safe
window ✅ (chart renders for existing/future trades; SPA build green). **Still pending**: rebuild the cBot `.algo` in cTrader for
the source fix to take effect on live runs, and the `CTraderBrokerAdapter` backtest-authoritative override.

### T2 — Journal shows wall-clock `EquityObserved` interleaved + out-of-order 🟠
**Observed**: journal rows jump e.g. `05-20 21:00 EquityObserved` … `06-20 22:16 EquityObserved` … `05-20 20:00 DayRolled`.
**Root cause**: same wall-clock leak as T1 on the cTrader **account/equity** frames → some `EquityObserved` carry wall-clock
(`06-20 22:16`) while bar-derived ones are sim; the journal is `Seq`-ordered so displayed times jump. Also the journal is
swamped by `EquityObserved` (12,251 of them vs 1,559 BarClosed). **Tags**: T1 (same root), F1 (journal view) — consider
filtering/colouring `EquityObserved` noise.

### T3 — Per-strategy funnel "Top no-signal reasons" = `undefined (undefined)` 🟠
**Observed**: `trend-breakout … undefined (undefined), undefined (undefined), …`.
**Root cause**: `StrategyPerformance.TopRejections` is a C# **ValueTuple** `(string Reason, int Count)`
(`IBacktestQueryService.cs:31`). System.Text.Json does NOT preserve tuple element names → JSON is `{"item1":…,"item2":…}`,
but the SPA reads `r.reason`/`r.count` (`run-report.topReasons`) → undefined.
**Fix**: replace the tuple with a named record `NoSignalReason(string Reason, int Count)` so JSON has `reason`/`count`.
**✅ FIXED** — `NoSignalReason` record (`IBacktestQueryService.cs`) + `GetStrategyBreakdownAsync` mapping; `StrategyBreakdownFromJournalTests` green.
**Tags**: F2 (funnel).

### T4 — Per-bar "why" only shows warmup rows ("not enough bars, have 1..24 need 55") 🟠
**Root cause**: `run-report` fetches the journal client-side with `limit=200` (`ngOnInit getRunJournal(…,200)`) and computes
`perBarVerdicts` from it; the first 200 events are dominated by `EquityObserved` + the earliest `BarClosed`, so only the
warmup bars surface. Needs a **server-side per-bar/funnel endpoint** (paged over `BarClosed` verdicts) instead of a 200-row
client slice. **Tags**: F2, T2 (journal noise).

### T5 — Per-bar "why" appears in the Trades section / section labels confusing 🟡
**Observed**: the "Per-bar why (24)" table seemed to render where "Trades (5)" was expected.
**Action**: verify `run-report` section ordering/conditionals (funnel vs why vs trades) — likely a layout/`@if` issue or the
Trades table not rendering when the why-table does. **Tags**: F2/F4.

### T6 — Trades show no commission / swap 🟠
**Root cause**: cTrader close exec frames (or the FORCE-close path, T7) report 0/absent `commission`/`swap` → trades persist
cost-free. **Tags**: 31-A2 (cBot cost itemization), M1, T7. Refs: `TradingEngineCBot.MakeExecResult` cost fields →
`EffectExecutor` cost mapping.

### T7 — Live journal only shows `CLOSE … reason=FORCE` (no SIGNAL/ORDER/FILL; everything force-closed) 🟠
**Root cause (two parts)**: (a) the live-monitor journal/counters come from orchestrator progress events, but the kernel path
only emits `"BAR"` (`EngineRunner.ReportBar`) and `"CLOSE"` (`EffectExecutor`) — the old `SIGNAL`/`ORDER`/`EXEC`/`REJECTED`/
`BREACH` producers were deleted in the cutover (`BacktestOrchestrator.cs:139-141`), so Signals/Fills/Rejections stay 0 and only
CLOSE rows appear; (b) **every close reason=FORCE** → SL/TP exits aren't being detected on the cTrader path (or an end-of-run
flatten), so positions are force-closed. **Tags**: F5 (live monitor), OBS-02/03, T1 (cTrader path).

### T8 — Governor "disable" via UI saves but doesn't take effect 🟠
**Root cause = M18** (GovernorOptions registered as a stale `new GovernorOptions()` singleton — `ServiceRegistration.cs:136` —
never updated from the DB the UI writes to). The kernel gate also reads `ConstraintSet.Toggles.GovernorEnabled` from the
**ruleset**, a separate enable from the governor-options store → two sources of truth. **Tags**: **M18** (confirmed), F7.
**⚠ CONFIRMED dual-source — needs a design decision, NOT a quick patch:** (1) the engine host registers `GovernorOptions` from
**JSON** (`EngineServiceCollectionExtensions.cs:101 AddSingleton(loadedConfig.Governor)` via `LoadBase()`), ignoring the DB options
the orchestrator loads into `PreloadedConfig` — so the Governor page's `Enabled` never reaches the run's `GovernorMachine`;
(2) the kernel `PreTradeGate:54` gates on `ConstraintSet.GovernorEnabled` = `PropFirmRuleSet.Toggles.GovernorEnabled` (prop-firm
ruleset), which the Governor page doesn't touch; (3) the kernel `GovernorState` is evolved in the reducer (`EvaluateStatic`)
regardless of `Enabled`. **Decision needed**: which switch is authoritative — make the engine host use the DB `GovernorOptions`
**and** have `ConstraintSet.GovernorEnabled` honor it (`ruleset.Toggles && options.Enabled`)?
**✅ FIXED (Governor page authoritative)**: the per-run host already wires the DB `GovernorOptions` into the `GovernorMachine`
(`AddRiskFromOptions:318` via `PreloadedConfig`), so #1 was already correct; the gap was #2. `WireRiskRules`
(`EngineHostFactory.cs`) now ANDs `GovernorOptions.Enabled` into the kernel gate switch:
`constraints with { GovernorEnabled = constraints.GovernorEnabled && govOptions.Enabled }`. Disabling on the Governor page now
turns the governor off at `PreTradeGate` (which gates ALL governor blocking on `c.GovernorEnabled`); the ruleset toggle still
applies (AND). Compiles clean; full run-verify pending the dev Web app being stopped.

### T9 — Backtest throws a cancellation error on/near finish (but still saves) 🟡 ✅ FIXED
**Root cause**: a completion/teardown `OperationCanceledException` (user Cancel, the 30-min linked CTS timeout, or host/stream
teardown) was caught by the general `catch (Exception)` → run marked **failed** + `LogError`, despite trades being persisted.
**Fixed** (`BacktestOrchestrator.cs` — dedicated `catch (OperationCanceledException)`): finalize with trades-so-far,
status = `cancelled` (user) / `completed` (teardown), ExitCode 0, info log, end record written. (Compiles clean; full test run
pending the dev Web app being stopped — it currently locks the build output.) **Tags**: H22.

### T10 — "Duplicate" is pointless (re-runs via cTrader, ignores saved bars, no options to tweak) 🟠
**Root cause**: `RunsController.Duplicate` (`:119`) re-runs the config **through the engine** (cTrader when
`UseForBacktest=true`), NOT a deterministic replay of the saved `DatasetId` bars; the F3 SPA button posts no changes and there's
no edit UI for strategy/risk/overrides before launch. So a duplicate is just a fresh non-deterministic re-run.
**Fix**: duplicate should replay the saved dataset bars (K6 real-replay) + open `new-backtest` prefilled with editable
strategy/risk/overrides. **Tags**: **F3**, **K6** (real replay + duplicate).

### T11 — Live equity: no DD line + vertical/horizontal axes show wrong/no values 🟠
**Root cause (likely)**: the live `equity-chart` DD series isn't drawn and the time/price axes are mis-scaled — the wall-clock
`EquityObserved` points (T2) mixed with sim-time points corrupt the time axis; the DD series may not be fed. **Tags**: L1
(equity chart), T2. Refs: `shared/equity-chart.component.ts` + `run-monitor` `equityData`/DD feed.

### T12 — No DD timeline on the run report 🟠
**Root cause (likely)**: the report "DD Timeline" (daily-PnL bars) is empty — `getRunDailyPnl`/`RunQueryService.GetRunDailyPnLAsync`
groups trades by `ClosedAtUtc.Date`, and/or the equity/DD curve isn't persisted for the **cTrader in-process** path (K-GAP-2's
equity flush is on the kernel-backtest path; the cTrader path may not flush `EquitySnapshots` the same way). **Tags**: **K-GAP-2**,
T1 (cTrader path).

> **Net:** T1/T2/T6/T7/T11/T12 share the **cTrader-in-process backtest path** root (wall-clock frames + missing progress/equity
> wiring vs the kernel/replay path). T3/T4/T5 are SPA/serialization gaps in the new F2 surfaces. T8=M18, T10=F3/K6, T9=H22.
> Highest-value single lever: make the cTrader path stamp sim-time (T1) + reconsider whether default-venue backtests should use
> the credential-free replay path instead of cTrader.

---

## Critical (14) — Correctness-breaking, fix immediately

### C1 — cTrader limit orders always execute as market
**Severity**: Critical — ✅ **FIXED iter-35 finish** — cBot now reads `orderType`/`limitPrice`, routes "Limit" orders through `PlaceLimitOrder` instead of `ExecuteMarketOrder`.
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:298-345`

### C2 — cTrader has no `cancel_order` handler
**Severity**: Critical — ✅ **FIXED iter-35 finish** — `ExecuteCancelOrder` handler added, dispatches to `ClosePosition`.
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:236-266`

### C3 — Trailing max-DD floor uses `equity.Equity` instead of `equity.PeakEquity`
**Severity**: Critical — ✅ **FIXED iter-35** (kernel `PreTradeGate` → `DrawdownState.GetMaxDrawdownFloor`). Old `RiskManager.cs` still has the bug but kernel gate is authoritative going forward. **✅ CLOSED iter-36** — `RiskManager.Validate`/the buggy floor has **no production caller** (twins relocated to `Tests.Support`, D81); it executes only in the golden test oracle. The kernel `PreTradeGate` is the sole production gate.
**File**: `src/TradingEngine.Risk/RiskManager.cs:186-187`
```csharp
var drawdownBase = Drawdown.DrawdownType == "Trailing" ? equity.Equity : equity.Balance;
```
For trailing mode, should be `equity.PeakEquity`. As equity drops, the projected floor also drops, making the gate artificially permissive. Same bug in `RiskGate.cs:39`.

### C4 — MaxDD protection mode never auto-exits
**Severity**: Critical — ✅ **FIXED iter-35** (kernel `ProtectionState.ClearsOn` + `Kernel.DecideReset`). Old `RiskManager.OnDailyReset` still has the bug but kernel path is authoritative going forward. **✅ CLOSED iter-36** — `RiskManager.OnDailyReset`/`AccountProcessor` have **no production caller** (twins relocated to `Tests.Support`, D81); protection-exit is owned solely by `Kernel.DecideReset` in production.
**File**: `src/TradingEngine.Risk/RiskManager.cs:299-307`
`OnDailyReset()` only clears `ProtectionCause.DailyDrawdown`. MaxDD-caused protection stays forever. `PropFirmRuleSet.ProtectionResetPolicy` is defined but **never read by any code**.

### C5 — SimulatedBrokerAdapter AccountUpdate param swap (Equity=0)
**Severity**: Critical — ✅ **FIXED iter-35** (all 3 sites: `(balance, balance, 0)` instead of `(balance, 0, balance)`)
**File**: `src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs:165-166,279-280,329-330`
```csharp
new AccountUpdate(_currentBalance, 0m, _currentBalance, now)
```
Passes `Equity = 0m, FloatingPnL = _currentBalance` instead of `Equity = balance + floatingPnl, FloatingPnL = actual floating PnL`. Breach watchdog sees zero equity, enters protection mode, force-closes all positions.

### C6 — SimulatedBrokerAdapter `ClosePartialPositionAsync` missing costs/balance update
**Severity**: Critical — ✅ **FIXED iter-35 (cont.)** — `ClosePartialPositionAsync` now computes costs on the closed lots via `TradeCostCalculator`, updates `_currentBalance`, stamps Gross/Comm/Swap/Net on the exec, and emits an `AccountUpdate`. (`BacktestReplayAdapter` already did this; the partial-close path has no live engine caller today, but both venues now agree.)
**File**: `src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs:171-208`

### C7 — SimulatedBrokerAdapter limit expiry decrements per tick, not per bar
**Severity**: Critical — ✅ **FIXED iter-35** (moved to `OnBarObserved` per-bar decrement)
**File**: `src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs:220-221`
`ExpiryBarCount--` runs in `OnTickReceived()` (every tick). With live tick feed (60+/sec), a 3-bar limit expires in 3 ticks. Accidentally works with the default 1-tick-per-bar feed.

### C8 — SessionBreakout uses all-time global high/low, not session window range
**Severity**: Critical — ✅ **FIXED iter-35** (range now filtered to `[RangeStartUtc, RangeEndUtc)` time-of-day window)
**File**: `src/TradingEngine.Strategies/SessionBreakout/SessionBreakoutStrategy.cs:55-56`
```csharp
_rangeHigh = h1Bars.Max(b => b.High);  // ALL bars in history, not just 05:00-07:00 bars
_rangeLow = h1Bars.Min(b => b.Low);
```
`h1Bars` is the entire bar collection. `Max(b.High)` returns the all-time high. `_rangeHigh`/`_rangeLow` are effectively global extrema — current price almost never exceeds them. Must filter to `[RangeStartUtc, RangeEndUtc)` bars.

### C9 — PipelineEventWriter silently drops journal events under backpressure
**Severity**: Critical — ✅ **FIXED iter-35 finish** — drop logging added (warns every 1000 drops), H20 buffer-clear-after-save fix applied.
**File**: `src/TradingEngine.Infrastructure/Events/PipelineEventWriter.cs:15-16,52,67`

### C10 — EquityPersistenceHandler stamps all snapshots with first item's RunId
**Severity**: Critical — ✅ **FIXED iter-35 finish** — flush loop now groups by RunId before persisting (same pattern as FlushAsync).
**File**: `src/TradingEngine.Infrastructure/Persistence/EquityPersistenceHandler.cs:46-47`

### C11 — Backtest replay path cancellation broken
**Severity**: Critical — ✅ **FIXED iter-35 finish** — replay path now uses `CancellationTokenSource.CreateLinkedTokenSource(userCt, timeoutCt)`.
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:276,306,491-492`

### C12 — Cancel endpoint cancels ALL backtests, ignoring runId
**Severity**: Critical — ✅ **FIXED iter-35 finish** — `Cancel(runId)` now cancels only the target run via per-run `CancellationTokenSource`.
**File**: `src/TradingEngine.Web/Api/RunsController.cs:88-93`

### C13 — Route collision: two controllers share `[Route("api/backtest")]`
**Severity**: Critical — ✅ **FIXED iter-35 finish** — `BacktestAnalyticsController` renamed to `api/backtest/analytics`.
**Files**: `BacktestController.cs:7`, `BacktestAnalyticsController.cs:6`

### C14 — `RiskProfile.MaxSlPips` defaults to 0, silently rejecting all trades
**Severity**: Critical — ✅ **FIXED iter-35** (kernel `PreTradeGate`: `MaxSlPips<=0` = "no limit")
**File**: `src/TradingEngine.Domain/RiskAndEquity/RiskProfile.cs:9`
`IsSlValid` checks `distance.Value > profile.MaxSlPips`. When `MaxSlPips = 0` (default), every positive SL distance is rejected.

---

## High (30) — Significant impact

### H1 — Fixed max-DD floor uses `equity.Balance` not `InitialAccountBalance`
**File**: `src/TradingEngine.Risk/RiskManager.cs:186`
If balance has grown from realized profit (e.g., $100k → $105k), floor becomes `$105k * 0.95 = $99,750` instead of correct `$100k * 0.95 = $95,000`.

### H2 — Weekly/monthly DD limits never checked in pre-trade gate
**File**: `src/TradingEngine.Risk/RiskManager.cs:103-109` — ✅ **FIXED iter-35** (kernel `PreTradeGate` enforces weekly/monthly; old `RiskManager.Validate` still missing them)

### H3 — `RiskGate.ProjectWorstCase` ignores `DailyDdBase`
**File**: `src/TradingEngine.Engine/RiskGate.cs:33` — ✅ **FIXED iter-35** (`RiskGate.cs` deleted; kernel `PreTradeGate` honors `DailyDdBase`)

### H4 — Trailing max-DD floor in `RiskGate` uses `currentEquity` not peak
**File**: `src/TradingEngine.Engine/RiskGate.cs:39` — ✅ **FIXED iter-35** (`RiskGate.cs` deleted)

### H5 — `AntiMartingale` sizing method not implemented
**File**: `src/TradingEngine.Risk/PositionSizer.cs:34-40` — ✅ **FIXED iter-35** (kernel `KernelSizing` has explicit `AntiMartingale` branch; old `PositionSizer` still broken)

### H6 — `FixedLots`/`FixedDollarRisk` bypass drawdown scaling
**File**: `src/TradingEngine.Risk/PositionSizer.cs:36,55-63` — ✅ **FIXED iter-35** (kernel `KernelSizing` applies drawdown scale to all methods; old `PositionSizer` still broken)

### H7 — Governor `OnDailyReset()` never called — profit-lock permanent
**Files**: `AccountProcessor.cs:72-73`, `DailyResetService.cs:18-30`, `TradingGovernorService.cs:200` — ✅ **FIXED iter-35** (kernel `HandleDayRolled` → `GovernorMachine.ApplyDailyReset`; `DailyResetService` deleted)

### H8 — BUG-09 STATUS: cooling-off fixed, but sibling remains
**Original BUG-09**: Governor cooling-off counter never decrements. **FIXED** — `TradingLoop.cs:83` now calls `governor?.OnBar(bar.OpenTimeUtc)`.
**Sibling bug (H7 above)**: Governor profit-lock never resets — `OnDailyReset()` never called.

### H9 — 500-bar cap not configurable, O(n) eviction
**File**: `src/TradingEngine.Host/TradingLoop.cs:55-62`
`list.RemoveAt(0)` on every bar after 500. Strategies needing >500 warm-up bars silently fail.

### H10 — Last-bar tail drain skipped on cancellation
**File**: `src/TradingEngine.Host/EngineRunner.cs:236-249` — ✅ **FIXED iter-35 finish** — tail drain now runs in outer `catch(OperationCanceledException)` block.

### H11 — Race on `RiskManager.CurrentState` in live path
**Files**: `EnginePacers.cs:15-21`, `RiskManager.cs:68-72,100`
Bar processing and account processing run concurrently via `Task.WhenAll`. `CurrentState` has no synchronization. Protection mode entry may not be visible to concurrent signal validation.

### H12 — CTraderBrokerAdapter synthetic close on disconnect has zero fill price
**File**: `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:448-457` — ✅ **FIXED iter-35 finish** — synthetic close now uses `_lastMid` (stored from last tick) instead of `Price(0m)`.

### H13 — NetMQ transport counter semantics wrong
**File**: `src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs:99,152,181`
`_barsReceived` counts all sub messages (ticks, acct, diag). `_commandsSent` counts all outgoing messages. `_executionsReceived` counts all router messages. Reconciliation telemetry permanently mismatched.

### H14 — BacktestReplayAdapter `FilledLots = 0` on full close
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:267-274` — ✅ **FIXED iter-35 (cont.)** — close exec now reports `trade.Lots`. `FilledLots == position lots` keeps it a full close in the lifecycle FSM (the partial branch requires `FilledLots < lots`); the order ledger / reconciliation now see the real volume. Golden + unit suites unchanged.

### H15 — BacktestReplayAdapter timestamp/price mismatch on fills
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:177-178,227-228` — ✅ **FIXED iter-35 finish** — fill timestamp now uses `bar.OpenTimeUtc + BarDuration(tf)` (bar close time).

### H16 — BacktestReplayAdapter floating PnL uses mid (close) not bid/ask
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:346-367` — ✅ **FIXED iter-35 finish** — `ComputeFloatingPnL` now uses directional bid/ask (`close - halfSpread` for longs, `close + halfSpread` for shorts).

### H17 — Bar-range SL/TP detection overstates fill probability vs tick-based
**Cross-cutting**: Backtest uses raw bar High/Low (no spread). Simulated venue uses tick bid/ask (with spread). Same strategy produces different results across venues.

### H18 — BarEvaluationHandler silently drops events
**File**: `src/TradingEngine.Infrastructure/Persistence/BarEvaluationHandler.cs:15,30` — ✅ **FIXED iter-35 finish** — drop logging added (warns every 1000 drops).

### H19 — BufferedBarWriter silently drops bars
**File**: `src/TradingEngine.Infrastructure/Caching/BufferedBarWriter.cs:12` — ✅ **FIXED iter-35 finish** — drop logging added.

### H20 — PipelineEventWriter flush failure loses entire batch
**File**: `src/TradingEngine.Infrastructure/Events/PipelineEventWriter.cs:42,82-95` — ✅ **FIXED iter-35 finish** — `buffer.Clear()` moved after successful save in both `PipelineEventWriter` and `BarEvaluationHandler`.

### H21 — No SQLite write serialization — 6 handlers compete for one file
**Files**: All persistence handlers — ✅ **FIXED iter-35 finish** — WAL mode + busy_timeout (5000ms) enabled via PRAGMA on startup.

### H22 — Unobserved exception leaves run stuck in "starting" status forever
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:281-283` — ✅ **FIXED iter-35 finish** — moved inside try/finally.

### H23 — Missing Venue/RiskProfileId propagation from legacy start endpoint
**File**: `src/TradingEngine.Web/Api/BacktestController.cs:44-78` — ✅ **FIXED iter-35 finish** — `StartRequest` gains `RiskProfileId` + `Venue` fields, wired into `cfg.CustomParams`.

### H24 — `StrategyOverrides` never propagated from UI to engine
**Files**: `RunsController.cs:46-86`, `Dtos/Runs/StartRunRequest.cs:3-23` — ✅ **FIXED iter-35 finish** — `StartRunRequest.StrategyOverrides` serialized to `cfg.CustomParams`.

### H25 — `BarCount++` race condition in progress callbacks
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:474-475` — ✅ **FIXED iter-35 finish** — `Interlocked.Increment(ref state.BarCount)`.

### H26 — Journal entries in live monitor use wall-clock time, not sim time
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:170-172` — ✅ **FIXED iter-35 finish** — `DecisionRecordView` uses parsed `state.SimTime`.

### H27 — Memory leak — `_runs` dictionary never purged
**Files**: `BacktestOrchestrator.cs:30,214`, `RunProgressBroadcaster.cs:19,42` — ✅ **FIXED iter-35 finish** — `_runs` + `_lastSentTicks` purged on completion.

### H28 — Angular: MAE vs MFE scatter chart broken (x-value discarded)
**File**: `web-ui/src/app/shared/scatter-chart.component.ts:50-54` — ✅ **FIXED iter-35 finish** — plots both MAE and MFE as two series.

### H29 — Angular: cost reconciliation formula wrong
**File**: `web-ui/src/app/features/runs/run-report/run-report.component.ts:136` — ✅ **FIXED iter-35 finish** — `Gross - Comm - Swap - Net`, no per-term `abs`.

### H30 — Angular: journal filter has invalid `'BAR'` kind, missing real kinds
**File**: `web-ui/src/app/features/runs/run-report/run-report.component.ts:118` — ✅ **FIXED iter-35 finish** — dropped `BAR`, added `GOVERNOR`, `ENTRY_EXPIRED`, `CANCELLED`.

---

## Medium (21) — Notable issues

### M1 — cTrader partial close reads commission/swap BEFORE close
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:400-401` — ✅ **FIXED iter-35 finish** — commission/swap read AFTER `ClosePosition()`, net calculated as `gross - comm - swap`.

### M2 — cTrader `_execsSent` excludes bar_result execs
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:617` — ✅ **FIXED iter-35 finish** — `_execsSent += execs.Count` added before bar_result send.

### M3 — cBot `Stop()` called from NetMQ poller thread
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:557` — ⚠ **OWNER LIVE-VERIFY** — requires cTrader platform to confirm thread crossing. E2E test `AfterRun_NoOrphanCtraderProcesses` verifies clean exit. Fix: wrap in `BeginInvokeOnMainThread` if needed.

### M4 — cTrader modify confirmations inflate `_execsReceived`
**File**: `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:254` — ✅ **CONFIRMED ALREADY CORRECT** — `TryHandleModifyConfirmation` returns true for modify → `HandleExecEvent` returns early before `_execsReceived++`.

### M5 — cTrader dedup signature excludes cost fields
**File**: `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:547` — ✅ **FIXED iter-35 finish** — signature now includes `GrossProfit|NetProfit|Commission|Swap`.

### M6 — `PropFirmRuleValidator.IsProfitTargetMet` uses balance, not equity
**File**: `src/TradingEngine.Risk/PropFirmRuleValidator.cs:27-31`
Checks `currentBalance >= target` instead of `currentEquity`. With open profitable positions, equity exceeds balance but the method says "not met."

### M7 — Worst-case projection excludes commission/swap costs
**File**: `src/TradingEngine.Risk/RiskManager.cs:162-168` — ✅ **FIXED iter-35** (kernel `PreTradeGate.CandidateWorstCase` includes round-trip commission; old `RiskManager` still missing it)

### M8 — `DrawdownVelocity` only updates at daily reset, stale all day
**File**: `src/TradingEngine.Engine/DrawdownReducer.cs:5-39`
`Apply()` (called every equity update) does NOT update velocity. Only `ApplyDailyReset()` computes it. `IsAccelerating` flag is always 1 day old.

### M9 — `IndicatorSnapshotService` CancellationToken never checked during recompute
**File**: `src/TradingEngine.Host/IndicatorSnapshotService.cs:30-99`
`RecomputeIndicatorsAsync` accepts `ct` but never checks it. Long recompute cannot be cancelled.

### M10 — `TradeCostCalculator.Compute` silently returns zero costs on exception
**File**: `src/TradingEngine.Services/Helpers/TradeCostCalculator.cs:304` (called from `BacktestReplayAdapter.cs:304`) — ✅ **FIXED iter-35** (catch block now computes gross PnL from direction/price instead of zeroing)
Catch block returns `new TradeCosts(0,0,0,0,0)`. No indication downstream that costs were not computed.

### M11 — `JournalNormalizer`: `"OrderCancelled"` always maps to `ENTRY_EXPIRED`, never `CANCELLED`
**File**: `src/TradingEngine.Infrastructure/Events/JournalNormalizer.cs:36` — ✅ **FIXED iter-35 finish** — checks reason for "cancelled" → `CANCELLED`, otherwise `ENTRY_EXPIRED`.

### M12 — Missing close reasons in `JournalNormalizer.CloseReasons` set
**File**: `src/TradingEngine.Infrastructure/Events/JournalNormalizer.cs:9-12` — ✅ **FIXED iter-35 finish** — added `TRAIL`, `BREAKEVEN`, `PARTIAL`.

### M13 — `EntryPlanner` no bounds check on SL/TP prices
**File**: `src/TradingEngine.Services/Helpers/EntryPlanner.cs:37-50`
No validation that resulting `newSl` is positive or `newTp` doesn't overflow. Extreme inputs produce negative/overflow prices.

### M14 — Fire-and-forget `PublishAsync` swallows handler exceptions
**Files**: `TradingLoop.cs:51,100,112,133`, `AccountProcessor.cs:121,124,129,133,138,156`
11 instances of `_ = eventBus.PublishAsync(..., CancellationToken.None)`. Exceptions in handlers silently lost to `TaskScheduler.UnobservedTaskException`.

### M15 — No dedup guard on `TradeResults.PositionId`
**File**: `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteTradeRepository.cs:9`
Duplicate `TradeClosed` events insert two rows with different IDs but same PositionId. No unique constraint or upsert.

### M16 — `EquityPersistenceHandler.DisposeAsync` race loses last items
**File**: `src/TradingEngine.Infrastructure/Persistence/EquityPersistenceHandler.cs:117-119` — ✅ **FIXED iter-35 finish** — `_channel.Writer.Complete()` called before cancel/drain.

### M17 — Journal API loads ALL events + filters in-memory (OOM risk)
**File**: `src/TradingEngine.Web/Api/BacktestController.cs:138-170` — ✅ **FIXED iter-35 finish** — `KernelJournalController` already provides SQL-paged endpoint; legacy endpoint noted for migration.

### M18 — `GovernorOptions` registered as stale singleton, never updated from DB
**File**: `src/TradingEngine.Web/Configuration/ServiceRegistration.cs:136`
`services.AddSingleton(new GovernorOptions())` — default-valued singleton. DB values never reach it. Two sources of truth: singleton (stale) vs DB-seeded `LoadedConfig.Governor`.

### M19 — `BuildLoadedConfigFromDbAsync` bare `catch {}` on governor store
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:434-438` — ✅ **FIXED iter-35 finish** — `catch (Exception ex)` with logged warning.

### M20 — Export CSV endpoint returns header only (no data)
**File**: `src/TradingEngine.Web/Api/ExportController.cs:11` — ✅ **FIXED iter-35 finish** — queries `IRunQueryService.GetRunTradesAsync`, emits full CSV.

### M21 — Angular `RunSummary` interface missing cost fields
**File**: `web-ui/src/app/models/api.types.ts` — ✅ **FIXED iter-35 finish** — `grossPnL`, `commissionTotal`, `swapTotal` added.

---

## Low (4) — Cosmetic / latent

### L1 — Angular equity chart double `setData` + no-op `forEach`
**File**: `web-ui/src/app/shared/equity-chart.component.ts:82-88` — ✅ **FIXED iter-35 finish** — no-op forEach removed, setData consolidated to one call, showBalance input triggers re-render via effect params.

### L2 — Angular journal replaces instead of appends in live monitor
**File**: `web-ui/src/app/features/runs/run-monitor/run-monitor.component.ts:122` — ✅ **FIXED iter-35 finish** — seq-based merge, deduplication, append-only with 500-item cap.

### L3 — Angular breach banner never clears after recovery
**File**: `web-ui/src/app/features/runs/run-monitor/run-monitor.component.ts:112` — ✅ **FIXED iter-35 finish** — cleared on run completion (no error) AND on DD recovery below 2% during live run.

### L4 — cBot 5-second blocking sleep during hello retry loop
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:125-133`
Main thread sleeps up to 5 seconds during handshake, blocking all ticks/bar events/UI updates.

---

## Pre-existing bugs (still open, verified in audit)

### BUG-09-SIBLING — Governor profit-lock never resets (→ H7)
The original BUG-09 (cooling-off counter) is **fixed** in `TradingLoop.cs:83`. But the sibling — `governor.OnDailyReset()` never called — is a separate bug. See H7 above.

### UNF-01 — `await Task.CompletedTask` cargo-cult
**Severity**: Low | **Files**: `BarEvaluationHandler.cs`, `BacktestReplayAdapter.cs`, `EngineWorker.cs`

### UNF-02 — `double` for price comparison in MeanReversionStrategy
**Severity**: Low | **File**: `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs:55-56`

### UNF-03 — bare `catch { }` in ResolveHalfSpread
**Severity**: Low | **File**: `src/TradingEngine.Host/EngineWorker.cs:379`

### UNF-04 — `IEnumerable<IStrategy>` enumerated multiple times
**Severity**: Low | **File**: `src/TradingEngine.Host/EngineWorker.cs`

### UNF-05 — `CancellationToken` missing on async methods
**Severity**: Low | **Files**: `EngineWorker.cs`

### UNF-06 — `EngineRunContext` in Domain project (wrong layer)
**Severity**: Low | **File**: `TradingEngine.Domain/EngineRunContext.cs`

### MIN-01 — `WinRateLast20`/`AvgRLast20` never updated
**Severity**: Low | **File**: `MeanReversionStrategy.cs:88`

### MIN-02 — `SingleReader=true` missing on `BarEvaluationHandler` channel
**Severity**: Low | **File**: `BarEvaluationHandler.cs:14` — ✅ **FIXED iter-35 finish**

### MIN-03 — `WarmUpIndicatorsAsync` is a misleading no-op
**Severity**: Low | **File**: `EngineWorker.cs:366`

### MIN-04 — `BuildBarSnapshot` allocates new List per timeframe per bar
**Severity**: Low | **File**: `EngineWorker.cs:328`

### MIN-05 — `_processedExecutionIds` HashSet never pruned for rejected orders
**Severity**: Low | **File**: `PositionTracker.cs:19,231,310-313`
Rejected orders add OrderId but never remove it. Bounded LRU partially mitigates but not for rejections.

---

## cTrader E2E coverage (CT)

### CT-1 — cTrader E2E tests SILENTLY SKIP when the env isn't configured (critical coverage not running)
**Severity**: High — the cTrader E2E suite is the ONLY coverage that exercises the real cBot + cTrader CLI +
NetMQ + the full kernel engine + ledger reconciliation. When credentials are absent the tests do a bare
`return` and report as **PASS**, hiding that this live coverage never ran. **They must RUN, not skip.**
**Fix**: configure the cTrader env so they execute — real cTrader CLI on PATH, the compiled cBot
`src/TradingEngine.Adapters.CTrader/bin/{Debug,Release}/net6.0/src.algo`, and `CTrader:CtId`/`PwdFile`/
`Account` in `appsettings.Development.json` (or `CTrader__*` env vars). See the **`ctrader-e2e` skill**.
**Secondary**: switch the silent `return` to `[SkippableFact]` (xUnit v2 has no `Assert.Skip`) so a genuine
no-env skip is *visible*, and harden `HasCredentials` to also verify the algo/CLI are present so a *partial*
cred env skips instead of hard-failing mid-run.
**Files**: `tests/.../E2E/CtraderE2EHarnessSmokeTests.cs`, `CtraderScenarioE2ETests.cs`, `CtraderTestHelpers.cs`

### CT-2 — cTrader harness completion polled the deleted `BarEvaluations` table
**Severity**: High — ✅ **FIXED iter-36** — `CtraderE2EHarness.WaitForCompletionAsync`/`CollectResult`
polled `db.BarEvaluations` (no longer written after K5) → would hang to timeout even with credentials.
Repointed to the single StepRecord journal (`JournalEntries`).
**File**: `tests/.../Harness/CtraderE2EHarness.cs:270,317`

---

## Observability gaps

### OBS-01 — No bar flow visibility during backtest
### OBS-02 — No signal evaluation visibility (why was signal rejected at each bar?)
### OBS-03 — No order lifecycle visibility between SIGNAL and TRADE_SAVED

---

## Carry-forward from iter-31/32 (unchanged)

| Phase | What | Priority | Status |
|-------|------|----------|--------|
| 31-A2 | cBot emits commission/swap in close EXEC frame | Medium | **DONE in code** — HANDOVER.md is stale |
| 31-A3 | Report shows Commission/Swap/Gross/Net columns | Medium | Open |
| 31-C2 | Live limit path end-to-end — verify limit branch | Medium | **Blocked by C1** |
| 31-B2 | Monitor lossless journal | Low | Open |
| 31-C3 | Set mean-reversion.json → LimitOffset | Low | Open |
| 32-P4 | Strategy browse/edit UI | High | Open |
| 32-P5 | New-Backtest per-run override UI | High | Open |
| 32-P6 | Wire JsonExportService to endpoint, regenerate migration | Low | Open |
| 31-A4 | (Optional) Commission-aware risk budget | Optional | Open |

---

## Fix sequencing (updated iter-35 finish)

1. ✅ **Stop data loss** — C9, C10, H18, H19, H20, H21 (channel modes, SQLite WAL, buffer lifecycle)
2. ✅ **Risk correctness** — C3, C4, H1, H2, H7, C14, H5 (drawdown floors, protection exit, governor reset, sizing)
3. ✅ **Venue correctness** — C5, C6, C7, C8, H14, H15, H16 (AccountUpdate, partial close, limit expiry, session range)
4. ✅ **Web & frontend** — C11, C12, C13, H10, H22-H30, M11, M12, M17, M20, M21
5. ✅ **cTrader integration** — C1, C2, M1 (limit orders, cancel handler, partial close timing)
6. **Remaining** — M2-M5 (cTrader counters/deep), H11 (live race), H13 (NetMQ counters), H17 (bar vs tick), M8 (velocity), M13 (EntryPlanner), L1-L4, UNF, MIN, OBS

---

## iter-36 closure notes (kernel cutover)

- **C3/C4/H1/H2/H5/H6/M7** — the shadowed `RiskManager` DD/protection/sizing bugs are now **production-dead**: the imperative twins (`OrderDispatcher`/`KernelOrderGate`/`AccountProcessor`) are out of `src` (→ `Tests.Support`, D81), and `grep "\.Validate(|\.OnDailyReset(|\.CalculateLotSize(|\.ValidateOrder(" src → 0`. They execute only in the golden test oracle.
- **C9/H18/H20 (PipelineEventWriter/BarEvaluationHandler drop/loss)** — moot: both writers **deleted** (D83); the single journal is the lossless `Wait`-mode `ChannelJournalWriter` → StepRecord stream.
- **M14 (fire-and-forget in `AccountProcessor`)** — `AccountProcessor` is now test-oracle-only; the kernel path has no fire-and-forget account publishes.
- **M17 (journal OOM)** — `GET /api/runs/{id}/journal` now serves SQL-paged StepRecords (the legacy in-memory endpoint is gone).
- **Carry to iter-37 (F2/F4):** repoint the funnel/report readers (`RunFunnel`/`RunProjection`/`BacktestQueryService`) off the now-unwritten `PipelineEvents`/`BarEvaluations` onto the StepRecord journal, then drop those tables. `DatasetId` is currently a data-window-spec hash (not bar-content-hash) — revisit if true bar-content addressing is needed.
