# Iter Strategy-System — LIVE PROGRESS / RESUMABLE HANDOVER

> Single source of truth for where the P0–P4 implementation stands. Updated after every checkpoint.
> Plan: `docs/iterations/iter-strategy-system/PLAN.md`. Branch: `iter/strategy-system`.
> **To resume:** read this file top-to-bottom, run the gate for the last in-progress phase, continue from
> the first unchecked box.

## Status board

| Phase | Title | State | Commit |
|-------|-------|-------|--------|
| P0 | Bring backbone forward (merge iter/38-addons) + G0 gate | ✅ done | merge `5c0e024`, fix `cc24c22` |
| P1 | Backtest builder (row grid + per-row pack + venue) | ✅ done | `c492d16` |
| P2 | Run metadata: store & display selection | ✅ done | `e4b91f4` |
| P3 | Live progress & journal: descriptive | 🟡 in progress | `<pending>` |
| P4 | Charts: equity + trade-detail price | 🟡 next | — |

Legend: ⬜ not started · 🟡 in progress · ✅ done & gated · ⚠️ done with caveat

## Gate commands (canonical)
```
dotnet build TradingEngine.slnx -c Debug
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Architecture
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation        # golden replay — must stay byte-identical
(cd web-ui && npm run build)
```
Expected baseline (from iter/38-addons G0): build 0 err · Unit 272/6skip · Arch 5 · Integration 68 · Simulation(golden) 61 · SPA 0 err.

---

## P0 — Bring backbone forward  ✅

- [x] Commit PLAN.md (`10e3daf`)
- [x] `git merge --no-ff iter/38-addons` → clean, no conflicts (`5c0e024`, 99 files)
- [x] G0 gate: **build 0 err · Unit 272/6skip · Arch 8 · Integration 68 · Golden 56/0 · SPA 0 err**
- [x] **Found + fixed a real latent bug during re-verification:** 3 of 9 strategies
  (`ema-alignment`, `mean-reversion`, `session-breakout`) had a `static Create` factory but **no
  `[StrategyId]` attribute**, so iter-38's reflection registry silently dropped them → selecting them
  threw "Active strategy ID '…' has no matching [StrategyId] class" at engine start. Added the 3
  attributes + a guard suite (`StrategyDiscoveryTests`, 3 tests) so it can't regress. Validated:
  `DiscoveryAuditTests` (exercises mean-reversion) now passes.

**Known environmental (NOT a regression):** ~17 `Simulation/E2E.*` + `NetMQBridgeTest` fail in this
sandbox because they launch the real `ctrader-cli` + NetMQ bridge (absent here). Pre-existing on
iter/38-addons (whose gate only ran the golden subset). Per project convention, trust Unit/Arch/
Golden/Integration for fast feedback; the cTrader E2E suite needs a real cTrader env (`ctrader-e2e` skill).

**Re-verify owner complaints (live app):** deferred to after P1 — the builder must exist to drive the
full flow. Static read confirms iter/38-addons already landed the short-run equity/bar persistence fix
(`FlushRunPersistenceAsync`), so P4 starts from "diagnose what's still empty", not "build charts".

---

## P1 — Backtest builder  🟡
Design: PLAN.md §Phase 1. **Done so far (uncommitted at this checkpoint, then committed):**
- `RunPlanEntry` gains `PackId` (Domain/RunPlan.cs).
- `StartRunRequest` gains `Rows: List<RunRowRequest>` + `GovernorEnabled` (Web/Dtos).
- `RunPlanBuilder` (Web/Services) — pure FromRows + IntoPasses (per-(symbol,tf) passes w/ strategy→pack map).
- `BacktestOrchestrator`: `ParseRunPlanEntries`; `BuildLoadedConfigFromDbAsync(cfg, perPassPacks)` —
  per-pass packs + **force-enable row strategies** (DB Enabled=false must not drop a row) + run-level
  governor toggle; `RunEngineReplayAsync` restructured into passes (per-pass config when rows present,
  shared config + byte-identical behaviour when not). Pass index/total tracked on run state (for P3).
- `RunsController.Start`: rows supersede the cross-product; derives symbols/periods/strategies from enabled
  rows; serializes rows to `CustomParams["RunRows"]`; always records `GovernorEnabled`.
- SPA `new-backtest.component.ts`: **full rewrite** — blank start, multi-select strategies/symbols/TFs,
  generated row grid (per-row pack + enable, enable/disable all), run-level risk/governor/regime/money,
  2-option venue (replay vs cTrader forward-test). `api.types.ts`: `RunRow` + `rows`/`governorEnabled`.

**Tests:** `RunPlanBuilderTests` (4, Integration) green — incl. same-strategy-different-pack-per-pass.
**Carry to P2:** assert `GovernorEnabled=false` end-to-end via the run-metadata round-trip (cheaper there).
**Verify after build:** live app run (skill `run-shamshir`) — drive a 2×2 row run, confirm it executes.

## P2 — Run metadata  🟡
Design: PLAN.md §Phase 2.
- `BacktestRunEntity` + `BacktestRunSummary` + `SqliteBacktestRunRepository` + `RunDetailResponse` +
  `RunQueryService` gain: `RunPlanJson`, `Venue`, `RiskProfileId`, `GovernorEnabled`, `RegimeEnabled`,
  `CommissionPerMillion`, `SpreadPips`.
- **EF migration** `20260629013309_AddRunMetadata` (the app uses `MigrateAsync`, not EnsureCreated — verified).
- `WriteStartRecordAsync` writes them from CustomParams; row plan + governor also fold into the ConfigSetId
  content address (K6 correctness now that packs are per-row).
- SPA `run-report`: "Run plan (N rows)" section — chips (venue/risk/governor/regime/comm/spread) + per-row
  table (strategy · symbol · TF · pack). `api.types` RunDetail extended.

**Tests:** `RunMetadataTests` (Integration) — row run round-trips: governor=false, per-row pack, 2 enabled
rows (disabled dropped) all surface on GET /runs/{id}. Green. (This also discharges the P1 governor
assertion.)

## P3 — Observability  🟡
Design: PLAN.md §Phase 3. Delivered:
- **Descriptive live journal (root cause fixed):** `EngineRunner.ReportEvent` emitted SIGNAL/ORDER/EXEC/
  REJECTED/BREACH with an **empty message** — the kernel event's strategy/symbol/side/price/size were
  thrown away. Now each emits a human line (e.g. "trend-breakout EURUSD Long signal @1.08320 (SL …, TP …)",
  "EURUSD filled 0.42 lots @1.08…", "EURUSD rejected: max-exposure"). The Monitor already renders the
  message, so the journal is now readable.
- **Per-pass progress:** `RunProgress` + envelope + Monitor show "Pass i/N · SYM/TF" for multi-row runs
  (state fields set in P1's replay loop).
- **Equity reconnect/refresh survival:** Monitor hydrates the equity curve from `GET /runs/{id}/equity` on
  init, so a refresh / late-join shows the curve-so-far instead of a blank chart.

**Tests:** `RunProgressContractTests` extended to pin currentPass/passIndex/passTotal.
**Deferred (noted):** merging the persisted StepRecord journal into the live ring — different seq spaces
(live tally seq vs StepRecord seq) make a naive merge collide; the Report page already shows the full
persisted journal, so live-ring backfill is lower value. Live SignalR auto-reconnect + rejoin keeps the
stream going; only the disconnect-gap is lost.

## P4 — Charts  (not started)
Design: PLAN.md §Phase 4. Re-verify after P0 first. Equity = data/flush; trade-detail = /api/bars
availability + timeframe casing.

---

## Decisions locked (see PLAN.md)
D1 keep backbone+fresh plan · D2 venue = explicit 2-option labeled · D3 row = strategy×tf×symbol×pack,
pack per row · D4 risk/governor/money run-level · D5 persist+display selection · D6 one phased iter.

## Open questions (non-blocking, defaults chosen)
1. Governor toggle = single on/off (default) vs per-run governor profile.
2. Show strategy-default add-ons inline in the row grid (default yes, tooltip).
3. cTrader stays single-row this iter (default yes).
