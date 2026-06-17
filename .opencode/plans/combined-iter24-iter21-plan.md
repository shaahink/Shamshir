# Combined Plan: iter-24 Engine + iter-21 UI — 2026-06-15

**Branch**: `iter/24-build`. **Continues** `docs/iterations/iter-24/SESSION-HANDOVER.md` (commit `313b17e`).
**Baseline**: 175 Unit + 12 Simulation (FTMO) + 3 Integration green.

## Design decisions (settled)

| # | Decision |
|---|---------|
| D1 | **Venue-authoritative PnL.** `FakeVenue` computes close PnL directionally `(exit−entry)*lots*contractSize` cross-rate-aware via `PipCalculator`, stamps `ExecutionEvent.GrossProfit/NetProfit`. Harness consumes venue PnL from the execution stream — exercises `ExecutionEvent.NetProfit → EffectExecutor` path. Delete `ApproximateClosedPnL`. |
| D2 | **Keep `IPipelineJournal` + `IDecisionJournal` separate.** `IPipelineJournal` = ops trace (debug); `IDecisionJournal` = canonical structured decision ledger (UI Report). Route every decision point through `IDecisionJournal`. |
| D3 | **Procedural bar builder.** `Bars.Trend(start, pips, count)`, `.Range(center, widthPips, count)`, `.Gap(pips)`, `.Spike(pips)`. Chainable via `.Then(...)`. Returns `IReadOnlyList<Bar>`. |
| D4 | **`_RunNav` = per-run sub-nav + breadcrumb.** `_Layout` = full site IA (LIVE / RESEARCH / LIBRARY). |
| D5 | **Keep `_Host.cshtml`; decouple chart-loading.** Move LightweightCharts CDN into `_Layout` so Razor Pages get charts. Retire Blazor host when U2/U4/U5 replace its pages. |
| A3 refine | **AccountProcessor deps trivially constructable in harness.** Once wired, delete the harness's duplicate `CheckBreachAsync` — one breach path. |
| EffectExecutor | **Wire real `EffectExecutor`** in the harness with collecting fakes for `IEventBus`/`IDecisionJournal` + null for optional deps (`IEquitySink`, `ITradingGovernor`, `ISignalGate`). Exercises production code path for effects + trade results + journal writes. |

---

## Phase 1: EngineHarness — Venue-authoritative PnL + EffectExecutor

**Blocks**: Phase 2, Phase 3. **Depends on**: nothing.

**Goal**: Replace `ApproximateClosedPnL` (−50 pips naive) with venue-computed PnL flowing through the real `EffectExecutor`. Delete the harness's parallel PnL/breach computation.

- [ ] **1a** — Give `FakeVenue` a `PipCalculator` + `ISymbolInfoRegistry` reference. On `ClosePositionAsync`/`ClosePartialPositionAsync`, compute `(exit−entry)*lots*contractSize` per-symbol, cross-rate adjusted. Stamp `GrossProfit`/`NetProfit` on the close `ExecutionEvent`. Entry fills get null PnL.
- [ ] **1b** — Wire real `EffectExecutor` in `EngineHarnessBuilder.BuildAsync`:
  - `IBrokerAdapter` → `FakeVenue`
  - `IDecisionJournal` → `InMemoryDecisionJournal` (collects all `Record()` calls)
  - `IEventBus` → `CollectingEventBus` (collects `TradeClosed` etc.)
  - `IReadOnlyList<IStrategy>` → same strategies already in harness
  - `IRiskManager` → `Risk`
  - `IPositionManager` → new `PositionManager(symbolRegistry, indicatorService, logger)`
  - `ISymbolInfoRegistry`, cross-rate func → already in harness
  - `IEquitySink?`, `ITradingGovernor?`, `ISignalGate?`, `IProgress?` → null
  - `IEngineClock` → simple `ManualClock`
  - `EngineRunContext` → `new("test-run")`
- [ ] **1c** — Pass `IEffectExecutor` to `PositionTracker` (currently null in harness — see `EngineHarnessBuilder.cs:95`).
- [ ] **1d** — Delete `ApproximateClosedPnL()`. In `DriveBarsAsync`, track equity from `CollectingEventBus.TradeClosedEvents` (net PnL from closed trades). Equity per-bar = initial balance + cumulative net PnL.
- [ ] **1e** — Run FTMO suite (12 tests); fix drawdown-magnitude assertion drift.
- [ ] **1f** — Run exit-reason tests (2 tests); fix any drift.
- **Gate**: 175 Unit + 14 Simulation (12 FTMO + 2 exit-reason) green. A 100-pip down-leg on 0.01 lots EURUSD produces −$10 equity drift.

---

## Phase 2: EngineHarness — Wire AccountProcessor (one breach path)

**Blocks**: Phase 4, Phase 8. **Depends on**: Phase 1.

**Goal**: Delete the harness's duplicated `CheckBreachAsync()`; drive `AccountProcessor.HandleAsync` per-bar. Exercises production breach watchdog + daily/weekly/monthly resets. Closes deferred A2.

- [ ] **2a** — Construct `AccountProcessor` in `EngineHarnessBuilder`. All 10 deps trivially constructable:
  - `IRiskManager`, `PositionTracker` → already in harness
  - `SizingPolicyOptions` → already built in harness
  - `IEventBus` → `CollectingEventBus` (from Phase 1)
  - `IEngineClock` → `ManualClock` (bar-timestamp-based)
  - `EngineMode` → `Backtest`
  - `CrossRateStore` → already built in harness
  - `IEquitySink?` → null
  - `Action<EquitySnapshot>` → callback that patches `EquityBox.Value`
  - `ILogger` → NullLogger
- [ ] **2b** — In `DriveBarsAsync`: feed `AccountUpdate(balance, equity, ...)` through `_accountProcessor.HandleAsync(update)` per bar. Remove the call to `CheckBreachAsync()`.
- [ ] **2c** — Delete `CheckBreachAsync()` method entirely.
- [ ] **2d** — Re-validate all 12 FTMO tests + 2 exit-reason tests. Breach behavior identical (same logic, different code path).
- **Gate**: 175 Unit + 14 Simulation green. `grep CheckBreachAsync tests/` returns 0.

---

## Phase 3: Rich Decision Journal — IDecisionJournal canonical

**Blocks**: Phase 4. **Depends on**: Phase 1 (EffectExecutor wired, InMemoryDecisionJournal exists).

**Goal**: Every decision point recorded as a `DecisionRecord` written to `IDecisionJournal`. `RunProjection.GetRunAsync` returns full structured timeline.

- [ ] **3a** — Route signal/gate decisions through `IDecisionJournal`: emit `DecisionRecord` for every signal pass and gate block in `OrderDispatcher`.
- [ ] **3b** — Journal exit/close decisions via `RecordDecisionEvent` effect in `PositionTracker.OnExecutionCoreAsync`.
- [ ] **3c** — Journal drawdown/breach transitions in `AccountProcessor.HandleAsync`.
- [ ] **3d** — Journal fill events in `PositionTracker.OnExecutionCoreAsync`.
- [ ] **3e** — Populate `DecisionRecordView.StrategyId` in `RunProjection.GetRunAsync` (add field to `PipelineEvent` DB type if needed).
- [ ] **3f** — Bridge `IPipelineJournal` (TradingLoop ops trace) to same `InMemoryDecisionJournal` or separate in-memory list.
- [ ] **3g** — Deterministic harness test: 8-bar backtest → assert ordered decision sequence with correct values.
- **Gate**: `InMemoryDecisionJournal.Records` contains full decision trail. `RunProjection.GetRunAsync` returns `DecisionRecordView`s with correct `StrategyId`, `Event`, `GuardResult`, `Reason`.

---

## Phase 4: Wire Engine State into RunProgress Envelope

**Blocks**: Phase 6 (U2). **Depends on**: Phase 2 + Phase 3.

**Goal**: `RunProgress` fields currently hardcoded to zero → populated truthfully from the engine.

- [ ] **4a** — Hook `AccountProcessor.setEquity` callback into `BacktestOrchestrator`.
- [ ] **4b** — Populate `Equity`, `Balance`, `DailyDdPct`, `MaxDdPct`, `DistanceToDailyLimit`, `GovernorState`, `GovernorReason`, `OpenPositions` from latest snapshot.
- [ ] **4c** — Source `BarsTotal`/`Percent` from run config / bar repository.
- [ ] **4d** — Source `RecentJournal` from canonical `IDecisionJournal` (not old `TallyEvent` path).
- [ ] **4e** — Contract test: integration test asserts `RunProgress` envelope carries non-zero fields during a backtest.
- **Gate**: `RunProgress` signal carries non-zero equity, DD%, governor state. Integration contract test green.

---

## Phase 5: UI Foundation — U0 remaining

**Blocks**: Phase 6, 7, 10, 12. **Depends on**: nothing (parallel-safe).

U0 partial done: Razor format bugs fixed, U1 hub+envelope+contract tests done.

- [ ] **5a** — Move LightweightCharts CDN from `_Host.cshtml` to `_Layout.cshtml`. Delete Chart.js CDN + `chartjs-chart-financial` CDN + `/js/charts.js` from `_Layout`. Keep `_Host.cshtml`.
- [ ] **5b** — Create `wwwroot/js/charts/index.js` ES module: `equityChart`, `candleChart`, `setMarkers`, `histogram`, `scatter`. Reconcile colors with CSS tokens. Migrate dashboard to new `equityChart()`.
- [ ] **5c** — Rebuild nav: left sidebar (LIVE / RESEARCH / LIBRARY). Create `_RunNav` partial (run-scoped tabs + breadcrumb).
- [ ] **5d** — Add `:root` CSS design tokens per iter-21 Plan Appendix A. Unify chart colors with tokens.
- [ ] **5e** — Extract worst inline-style blocks into shared classes.
- [ ] **5f** — Verify no `:F5`/`:F2`/`:yyyy-MM-dd` format literal regressions.
- [ ] **5g** — Dashboard fetch with loading/empty/error states.
- **Gate**: Dashboard equity via LightweightCharts. No Chart.js CDN. No `:F5` literals. No console errors. Sidebar IA.

---

## Phase 6: Live Run Monitor — U2

**Depends on**: Phase 4 + Phase 5.

- [ ] **6a** — Sim-clock: large `tabular-nums` display of `simTimeUtc`.
- [ ] **6b** — Progress bar + ETA: `percent`, `barsProcessed/barsTotal`, ETA countdown, `barsPerSec`.
- [ ] **6c** — Live equity sparkline buffered at 500 points.
- [ ] **6d** — KPI tiles: equity, daily DD gauge, max DD, open positions, governor badge.
- [ ] **6e** — Live funnel: animated signals→orders→fills→closes (+ rejections, breaches).
- [ ] **6f** — Journal feed: color-coded streaming, auto-scroll, pause-on-hover, filter chips. `aria-live="polite"`.
- [ ] **6g** — Breach banner on breach increment or SoftStop/HardStop.
- [ ] **6h** — "View Report" CTA on `status=completed`.
- [ ] **6i** — Loading/empty/error states.
- **Gate**: Backtest → monitor shows advancing clock, streaming journal, updating equity, ETA, funnel. Breach triggers banner.

---

## Phase 7: Trade Detail + Backtest Report — U3 + U4 (parallel)

**Depends on**: Phase 5. Truthful data from Phase 4 for U4.

### U3 — Trade Detail
- [ ] Endpoint `GET /api/trades/{id}/chart`: candles + entry/exit/SL/TP/MAE/MFE levels.
- [ ] `candleChart` with markers, SL/TP lines, MAE/MFE band. Remove CSS-gradient placeholder.
- [ ] Fix every field: R-multiple, exit reason, holding time, commission/swap, gross vs net.
- [ ] Loading/empty/error states.

### U4 — Backtest Report
- [ ] Headline KPI tiles: net PnL, return %, max DD, profit factor, win rate, total trades.
- [ ] Run facts: duration, bars/sec, date range, symbols, strategies.
- [ ] Equity + drawdown chart (honest empty-state if sparse).
- [ ] Strategy funnel: per-strategy bars→signals→orders→trades, visual funnel, rejection drill-down.
- [ ] Compliance panel: breach timeline, governor transitions, protection-ledger.
- [ ] Trades table → U3 detail. CSV export.
- [ ] Tabs: Report / Analyzer / Trades.

---

## Phase 8: EngineHarness — Multi-symbol/Timeframe + Bar Generator (A2, A4, A5)

**Depends on**: Phase 2. **Blocks**: Phase 9.

- [ ] **A2** — Parameterize `EngineHarnessBuilder` for `Dictionary<Symbol, IReadOnlyList<Bar>>`. Interleaved by timestamp. Per-symbol pip values.
- [ ] **A4** — Exercise `SeedOpenPositions` in-harness: seed venue position, restart loop, assert force-close.
- [ ] **A5** — Procedural bar builder: `Bars.Trend(...).ThenRange(...).ThenSpike(...).ThenGap(...).Build()`.
- **Gate**: 2-symbol run with correct per-symbol pip values. "Trend up then reverse" run trips a trailing stop.

---

## Phase 9: Edge-Case Scenario Tests

**Depends on**: Phase 8.

8+ deterministic tests on max-daily-loss breach, max-total-loss, profit target, exposure cap across N symbols, weekend hold, news window, per-strategy cap, daily reset re-enable. **Gate**: 8+ new green, <5s total.

---

## Phase 10: Analyzer + Strategies + Compliance + Wizard — U5 + U6

**Depends on**: Phase 5. Data from Phase 4.

- **U5**: Trade analyzer (R-multiple histogram, MAE/MFE scatter, PnL by symbol/strategy/hour/day). Strategies page. Compliance page (live gauges, protection-ledger).
- **U6**: New-backtest wizard (symbol/period/date/balance, strategy selection → redirect to U2 Monitor). Run history with compare (overlay equity curves). Cancel run control.

---

## Phase 11: Architecture Cleanup — IEnginePacer (Phase 0f)

**Depends on**: Phase 1 (harness). Define `IEnginePacer` with `AsyncStreamPacer` (live) + `BarSteppedPacer` (backtest). Remove `if (_engineMode == Backtest)` fork. **Gate**: Full suite green; EngineRunner has one run path.

---

## Phase 12: Polish — U7

**Depends on**: Phases 6–10 complete.

Loading/empty/error everywhere. Responsive. Keyboard nav + ARIA. Client buffer caps. SignalR reconnect UX.

---

## Phase 13: Confirm p12 End-to-End

**Depends on**: Phase 3.

Real cTrader run asserting SL/TP in `TradeRows[].ExitReason`. **Gate**: at least one exit reason is "SL" or "TP" (not all "FORCE").

---

## Backlog

- M1 asserting test (commission/swap → NetPnL ≠ GrossPnL)
- V1–V5 venue reconciliation (wire exists; needs real-cTrader reconnect testing)
- A4 unify `FloatingPnL` definition
- MonthRolled path / remove dead `ApplyMonthlyReset`
- Retire `TradingGovernorService`
- ctrader-cli exit-code interpretation hardening
- Remove dead `PositionTracker.DetermineExitReason`

---

## Dependency Graph

```
Phase 1 (venue PnL + EffectExecutor)
├─ Phase 2 (AccountProcessor — one breach path)
│  ├─ Phase 4 (wire RunProgress envelope)
│  │  └─ Phase 6 (U2 — live Run Monitor)
│  │       └─ Phase 12 (U7 — polish)
│  └─ Phase 8 (multi-symbol + bar gen)
│     └─ Phase 9 (edge-case scenario tests)
├─ Phase 3 (rich decision journal)
│  ├─ Phase 4 (wire RunProgress)
│  └─ Phase 13 (p12 e2e confirm)
└─ Phase 11 (IEnginePacer)

Phase 5 (U0 — UI foundation) ← parallel with 1–4
├─ Phase 6 (U2)
├─ Phase 7 (U3 + U4)
├─ Phase 10 (U5 + U6)
└─ Phase 12 (U7)

Backlog items ← no dependencies, pick up anytime
```

## Working rules (carried forward)

- Failing-test-first; build + fast suites green at every commit
- Fast suites: `dotnet test tests/TradingEngine.Tests.Unit` (175), `dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~Ftmo"` (~20)
- IDE0011 (braces) is an error in `Infrastructure`, `Services`, `Host`, `Tests.Simulation`
- Real cTrader e2e: `--filter "FullyQualifiedName~CtraderScenarioTests"` (skips without creds)
- cBot edits require `dotnet build src/TradingEngine.Adapters.CTrader/...` to repackage `src.algo`
- Commit message files: use `mktemp` OUTSIDE the repo
- Full-solution build fails on `aspire/AppHost` (NU1903) — build/test test projects directly
