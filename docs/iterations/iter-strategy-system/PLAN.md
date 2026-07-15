# Iter Strategy-System — Backtest Builder, Metadata & Observability — PLAN

**Branch:** `iter/strategy-system` (currently = clean baseline `58b2745` + handover doc only)
**Foundation:** `iter/38-addons` @ `1dcd11e` (green, pushed) — **must be brought forward first (Phase 0)**
**Audience:** OpenCode/DeepSeek implementation agent
**Style:** failing-test-first where a behaviour changes; every phase ends on a machine-checkable gate.

---

## Why this iteration exists

The previous iteration delivered the **engine backbone** (strategy-agnostic config, `RunPlan` multi-pass
routing, reflection factories, the iter-36 progress-callback bug fix) — all green on `iter/38-addons`.
What it did **not** deliver is the **user-facing backtest-building + observability layer**, which was
under-specified. This plan locks those requirements and builds them on the existing backbone. **No part
of `iter/38-addons` is discarded.**

The owner's words, verbatim, are the spec:

> "no strategy selected for new backtest and spin new backtest with strategy timeframe and symbol
> selected (can select multiple) with risk and governor toggles and to choose add on for each row and go …
> risk and governor and money stuff should be same for whole backtest. but what strategies included for
> what timeframe and which symbol with what pack is one row each with the addon selection. and we store
> and display the selection … live progress and journal still incomplete not descriptive … no chart view
> in trade details and equities chart broken … backtest or run should store and show more metadata."

---

## Decisions locked (this session)

| # | Decision | Consequence |
|---|----------|-------------|
| **D1** | Keep the `iter/38-addons` backbone; write fresh plan. | Phase 0 merges it forward; everything else builds on it. |
| **D2** | Venue = **explicit, clearly-labeled choice** (not replay-only). | Two options only: *Stored-bar replay (deterministic)* vs *cTrader live forward-test*. Kill the duplicate "Default (replay)"/"Replay" entries. Persist + display the chosen venue. |
| **D3** | **A row = (strategy × timeframe × symbol × add-on pack).** Add-on is chosen **per row**. | `RunPlanEntry` carries `PackId`; per-pass config resolution (see P1-3). |
| **D4** | **Risk profile, governor, and money (balance/commission/spread) are run-level globals** — same for the whole backtest. | One risk-profile select + one governor toggle + money fields at run level; never per row. |
| **D5** | The row selection is **persisted on the run and displayed** on the report. | New `RunPlanJson` + run-level columns on `BacktestRunEntity` (P2). |
| **D6** | One phased iteration: P1 builder → P2 metadata → P3 observability → P4 charts. | Phases are independently shippable & gated. |

---

## Current-state findings (verified against `iter/38-addons`)

- **F1 — Builder is flat + auto-selects.** `new-backtest.component.ts` pre-selects all enabled strategies
  (`ngOnInit`), presents symbols/timeframes/strategies as independent lists, and offers a single global
  pack + an optional per-*strategy* pack. There is **no combination grid** and **no per-row pack**.
- **F2 — Packs are per-strategy, applied globally.** `BacktestOrchestrator.BuildLoadedConfigFromDbAsync`
  builds **one** `LoadedConfig` for the whole run and applies `UsePackId`/`PerStrategyPackIds` per
  strategy. The multi-pass loop in `RunEngineReplayAsync` reuses that single config across every
  `(symbol, timeframe)` pass — so the *same* strategy cannot carry *different* packs on different rows today.
- **F3 — `RunPlanEntry` has no pack.** `RunPlan.cs`: `record RunPlanEntry(string StrategyId, string Symbol,
  string Timeframe)`. `BuildRunPlan` does a pure cross-product of `strategyIds × symbols × periods`.
- **F4 — Venue labels are confusing.** The form lists `Default (replay)` / `Replay (stored bars)` /
  `cTrader (live stream)` — the first two are the same path. `ResolveUseCtrader` already maps
  `"ctrader" → true`, everything else → replay; only the UI labels and persistence need work.
- **F5 — Run metadata is thin.** `BacktestRunEntity` stores symbols/periods/dates/balance/stats/
  `EffectiveConfigJson`/`DatasetId`/`ConfigSetId`/`Seed`/`ParentRunId` — but **no Venue, RiskProfileId,
  Governor/Regime toggles, Commission, Spread, or the row/pack selection.**
- **F6 — Observability API is already rich; the frontend underuses it.** `RunsController` exposes
  seq-paged lossless `GET /runs/{id}/journal` (+ `kind` filter), `GET /runs/{id}/bar-decisions`,
  `GET /runs/{id}/equity`, and NDJSON `journal/export`. The persisted `JournalEntryEntity` carries
  `EventJson`/`EffectsJson`/`RiskJson`/`Regime`/`DecisionReason`/`VerdictsJson`.
- **F7 — Live monitor is SignalR-only and its journal is thin.** `run-monitor.component.ts` consumes only
  the SignalR `recentJournal` ring (≤30 entries) and renders just `time/kind/symbol/reason`. There is
  **no polling fallback** for reconnect/refresh (handover carry-forward #6). The live projection
  (`BacktestOrchestrator.TallyEvent` → `DecisionRecordView`) sets **`strategyId = null`** and a generic
  `detail` blob — so the journal can't say *which strategy* did *what* with *what numbers*.
- **F8 — Multi-pass is invisible.** A run over N `(symbol,timeframe)` passes shows one merged progress
  bar; the UI never says "now running EURUSD/H1 (pass 2 of 5)".
- **F9 — Charts are built; data is the question.** `equity-chart.component.ts` and the trade-detail
  `candle-chart` are correct. `run-report` feeds the equity chart from `GET /runs/{id}/equity`; the monitor
  builds it live from SignalR equity frames (needs >2 points). `trade-detail` fetches `/api/bars` for the
  trade window and shows "No price data" when none come back. **`iter/38-addons` already fixed short-run
  equity/bar persistence** (`FlushRunPersistenceAsync`) — so re-verify after Phase 0 before "fixing".

---

## Phase 0 — Bring the backbone forward (prerequisite, no new behaviour)

**Goal:** `iter/strategy-system` contains the `iter/38-addons` code, so the rest of this plan builds on it.

1. From a clean tree on `iter/strategy-system`:
   ```
   git merge --no-ff iter/38-addons
   ```
   Expect the only conflict to be `docs/iterations/iter-strategy-system/HANDOVER.md` (keep both: the
   handover already references this branch). If the merge is messy, prefer
   `git merge -s ours`-then-cherry-pick only if a real divergence exists — but the baseline is a strict
   ancestor of `1dcd11e`'s parent, so a fast-forward-style merge should be clean.
2. **Gate G0** (must match the `iter/38-addons` gate, no regressions):
   ```
   dotnet build                         0 errors
   (cd web-ui && npm run build)         0 errors
   dotnet test …Tests.Unit              272 pass / 6 skip
   dotnet test …Tests.Architecture        5 pass
   dotnet test …Tests.Integration        68 pass / 0 skip
   dotnet test …Tests.Simulation (golden) 61 pass / 0 skip
   ```
3. **Re-verify the owner's complaints on the merged tree** (some may already be resolved):
   run the app (skill `run-shamshir`), start a short EURUSD/H1 replay backtest, and record for each:
   live progress %, equity chart on monitor, equity chart on report, trade-detail price chart. Note which
   are already working post-merge — this re-scopes P3/P4 down.

> Do **not** start P1 until G0 is green.

---

## Phase 1 — The backtest builder (matrix + venue) — *the headline feature*

### Requirements (from D2/D3/D4)
- New-backtest opens with **nothing pre-selected** (no default strategy).
- User multi-selects **strategies**, **symbols**, **timeframes**.
- The form generates a **combination grid**: one row per `(strategy × symbol × timeframe)`.
- Each **row** has: an **enable/disable** toggle and an **add-on pack** dropdown (default = strategy's own
  add-ons). Rows can be removed so the run is not forced to be a full cross-product.
- **Run-level** (single value each, never per row): risk profile, **governor on/off**, regime on/off,
  initial balance, commission, spread, date range, **venue**.
- Venue selector has exactly two clearly-labeled options (D2).
- "Start" is disabled until ≥1 enabled row exists and dates/balance are valid.

### Backend changes

**P1-1 — Row-aware request DTO** (`StartRunRequest.cs`)
```csharp
public List<RunRowRequest>? Rows { get; init; }   // when present, supersedes the Symbols×Periods×StrategyIds cross-product
public bool GovernorEnabled { get; init; } = true; // run-level governor toggle (D4)

public sealed record RunRowRequest
{
    public required string StrategyId { get; init; }
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public string? PackId { get; init; }   // null/empty = strategy default add-ons
    public bool Enabled { get; init; } = true;
}
```
Keep `Symbols`/`Periods`/`StrategyIds` for back-compat; when `Rows` is null, behaviour is unchanged.

**P1-2 — Pack on the routing tuple** (`RunPlan.cs`)
```csharp
public sealed record RunPlanEntry(string StrategyId, string Symbol, string Timeframe, string? PackId = null);
```
The kernel routing (`StrategyBankService.IsInRunPlan`/`GetActive`) only reads `StrategyId/Symbol/Timeframe`
— **leave routing untouched**. `PackId` is consumed only at config-build time (P1-3).

**P1-3 — Per-pass config resolution** (`BacktestOrchestrator`) — *the real architectural change (fixes F2)*
- `BuildRunPlan`: when `Rows` is provided (carried through `BacktestConfig.CustomParams["RunRows"]` as
  JSON), build entries directly from the **enabled** rows (no cross-product). Otherwise keep the
  cross-product fallback.
- `RunEngineReplayAsync` already groups by unique `(symbol, timeframe)` and runs each as its own
  `EngineHost` pass. **Move `BuildLoadedConfigFromDbAsync` inside that loop**, parameterized by the
  rows for *that* pass: build a `strategyId → packId` map from the pass's `RunPlanEntry`s and apply each
  strategy's row-pack (via the existing `_configResolver.ApplyPack`). A strategy appears at most once per
  `(symbol,timeframe)`, so per-pass the pack is unambiguous. This is what lets the *same* strategy carry
  *different* packs on different rows.
- Run-level governor: thread `GovernorEnabled` into `BuildLoadedConfigFromDbAsync`. When false, disable the
  governor for the run. **Investigate first**: confirm whether `GovernorOptions` has an `Enabled`-style
  flag (mirror the existing `DisableRegime` mechanism that sets `RegimeFilterOptions.DetectionEnabled=false`).
  If no such flag exists, add a minimal run-level bypass in the same spirit and cover it with a unit test.

**P1-4 — Venue (D2)**
- `ResolveUseCtrader` already supports explicit `"ctrader"`; no engine change. Just ensure the persisted
  `Venue` string round-trips (P2) and the UI sends `"replay"` or `"ctrader"`.

### Frontend changes (`new-backtest.component.ts`)
- Remove the `ngOnInit` auto-select of enabled strategies; start with empty sets (F1).
- Add a **Generate** step: from selected strategies × symbols × timeframes, build a `rows` signal of
  `{ strategyId, symbol, timeframe, packId, enabled }`. Re-generating preserves existing per-row pack
  choices by key.
- Render the grid: each row shows strategy/symbol/tf, a pack `<select>` (options from `packsApi`), and an
  enable checkbox / remove button. Show a live count "N rows (M enabled)".
- Keep risk profile, **governor toggle (new)**, regime toggle, balance/commission/spread, date range, and
  the **2-option venue selector** at run level.
- `start()` posts `rows` + `governorEnabled` (drop the cross-product payload when rows are present).
- Update `api.types.ts` `StartRunRequest` to include `rows` + `governorEnabled`.

### Gate G1
- New unit test: `BuildRunPlan` from explicit rows yields exactly the enabled rows with their packs
  (no cross-product), and an excluded row never appears.
- New unit test: two rows for the *same strategy* with *different packs* on different `(symbol,tf)` resolve
  to the correct per-pass `PositionManagement` (assert add-on enrichment differs per pass).
- New unit test: `GovernorEnabled=false` disables the governor in the resolved config.
- SPA builds; manual: open new-backtest → nothing selected → pick 2 strategies × 2 symbols × 1 tf → grid
  shows 4 rows → set a pack on one row, disable another → Start → run completes.
- All G0 suites still green.

---

## Phase 2 — Run metadata: store & display the selection (D5)

### Backend
- **P2-1 — Persist** (`BacktestRunEntity` + `BacktestRunSummary` + `SqliteBacktestRunRepository` +
  `TradingDbContext` + **new EF migration**): add
  `RunPlanJson` (the enabled rows incl. pack), `Venue`, `RiskProfileId`, `GovernorEnabled` (bool),
  `RegimeEnabled` (bool), `CommissionPerMillion`, `SpreadPips`.
  Write them in `WriteStartRecordAsync` (so an interrupted run still shows its plan) and keep them through
  `WriteEndRecordAsync`. These already participate in `ConfigSetId` identity — keep that.
- **P2-2 — Expose** on the run DTO returned by `GET /runs/{id}` (`RunQueryService`/its response record):
  surface `runPlan` (rows) + the run-level fields.

### Frontend (`run-report.component.ts`)
- Add a **"Run plan"** section: a table of rows (strategy · symbol · timeframe · pack) plus run-level chips
  (venue, risk profile, governor on/off, regime on/off, balance / commission / spread / date range).
  This is the *persisted* selection (distinct from the new-backtest "resolved config preview").

### Gate G2
- New integration test: start a run with 3 enabled rows + governor off + venue=replay → reload via
  `GET /runs/{id}` → `runPlan` has 3 rows with correct packs and the run-level fields echo the request.
- EF migration applies cleanly on a fresh DB and is idempotent on the seeded DB.
- Manual: report page shows the run plan table + chips matching what was submitted.

---

## Phase 3 — Live progress & journal: make it descriptive (fixes F7/F8)

### P3-1 — Enrich the live journal projection (`BacktestOrchestrator.TallyEvent` / `DecisionRecordView`)
- Populate `strategyId`, `symbol`, and a concise human `reason` for SIGNAL/ORDER/EXEC/CLOSE/REJECTED/BREACH
  (e.g. `EXEC EURUSD long 0.42 lots @1.0832 (sl 1.0810 tp 1.0890)`, `CLOSE +1.8R +$320 (TP)`,
  `REJECTED max-exposure`). Source these from the `BacktestProgressEvent` payload the engine already emits;
  if a field isn't on the event, add it at the emit site (`TradingLoop`/`EffectExecutor`) — keep the
  StepRecord journal authoritative and lossless.

### P3-2 — Polling fallback for the monitor (handover carry-forward #6)
- On monitor init and after `completed$`, **fetch the persisted journal** via
  `GET /runs/{id}/journal?afterSeq=<lastSeen>` and merge by `seq` (dedupe — the merge logic already exists
  in the component). This makes the journal survive reconnects/refreshes and back-fills the full run after
  it ends (the SignalR ring only holds ≤30). Same for equity: hydrate `equityData` from
  `GET /runs/{id}/equity` on (re)connect so a refresh mid-run isn't blank.
- Add a small "reconnecting…/live/ended" status indicator driven by the hub connection state.

### P3-3 — Per-pass context (fixes F8)
- Add the current pass label to the progress envelope (`RunProgress`) — e.g. `currentPass = "EURUSD/H1"`,
  `passIndex/passTotal` — set in `RunEngineReplayAsync`'s loop. Render "Pass 2/5 · EURUSD/H1" on the monitor.

### P3-4 — Journal detail on demand
- Make each monitor/report journal row expandable to show the rich persisted fields (`EffectsJson`,
  `RiskJson`, `VerdictsJson`, `Regime`, `DecisionReason`) via the existing journal entry — no new endpoint
  needed; `JournalEntryResponse` already carries `Detail`, extend it to pass through the structured fields
  if the UI needs them.

### Gate G3
- New API test: `GET /runs/{id}/journal?afterSeq=N` returns only `seq > N`, ordered, with the enriched
  `strategyId`/`symbol`/`reason` populated for trade events.
- Manual: start a multi-pass run, refresh the monitor mid-run → progress, equity, and journal **re-hydrate**
  (not blank); journal lines name the strategy + numbers; pass indicator advances; after completion the full
  journal is present.
- G0 suites green.

---

## Phase 4 — Charts: equity + trade-detail price (fixes F9)

> First action: **re-test after Phase 0** — `iter/38-addons`'s `FlushRunPersistenceAsync` already fixed
> short-run equity/bar drop. Only build what's still broken.

### P4-1 — Equity chart
- Confirm `GET /runs/{id}/equity` returns ≥2 points with valid UTC timestamps for a normal replay run
  (data lives in `EquitySnapshots`, drained by `FlushRunPersistenceAsync`). If empty/sparse:
  - verify `EquityPersistenceHandler.FlushAsync` runs per pass (it does in the loop) and that snapshots
    are keyed by `runId` across **all** passes (multi-pass must append, not overwrite);
  - verify `toUtcTimestamp` mapping in `equity-chart.component.ts` isn't collapsing points (seconds vs ms).
- Report uses `GET /runs/{id}/equity`; monitor uses live frames — make the **report** the source of truth
  and ensure it renders for every completed run with trades.

### P4-2 — Trade-detail price chart
- Root-cause the "No price data for this window": `trade-detail` calls `/api/bars` with
  `(symbol, timeframe, from, to)`. Check (a) the trade's `timeframe` is persisted and cased to match the
  bar store, (b) stored bars exist for that symbol/tf/window (replay reads them, so they should), (c) the
  `/api/bars` query range/casing. Fix the mismatch; the chart component itself is fine.
- Add entry/exit/SL/TP markers verification (already wired) and a partial-fill marker if `PARTIAL` events
  exist for the trade.

### Gate G4
- New integration test: a completed replay run exposes a non-empty equity series via `GET /runs/{id}/equity`.
- New integration/API test: `/api/bars` returns bars for a closed trade's `(symbol, timeframe, window)`.
- Manual: report equity chart renders with drawdown; open a trade → candle chart shows bars + entry/exit/
  SL/TP markers.
- G0 suites green.

---

## Cross-cutting

- **Failing-test-first** for every behavioural change (P1-3 per-pass packs, P2 persistence round-trip,
  P3 enriched/paged journal, P4 chart data). UI-only rendering changes may be manually verified but must
  not regress the build.
- **Determinism preserved:** the golden replay suite (61) must stay byte-identical. Per-pass config
  resolution must produce the *same* result as today when every row uses the strategy default pack
  (assert this explicitly — it's the safety net for the P1-3 refactor).
- **No fabricated data:** if a value isn't available, render an empty-state — never a placeholder number
  (the codebase's standing rule).
- **Venue routing doc:** add a short `docs/reference/` note (or extend `BACKTEST-ARCHITECTURE.md`) stating
  plainly: *replay = deterministic stored-bar kernel (default); cTrader = live forward-test via NetMQ +
  ctrader-cli, explicit opt-in.* This is the durable answer to the "ctrader vs replay vs stored bar"
  confusion.

## Definition of Done
- G0–G4 all green; golden 61/61 byte-identical.
- New-backtest: blank start → multi-select → row grid with per-row packs + run-level risk/governor/money →
  start → run.
- Run report shows the persisted run plan + run-level metadata.
- Monitor re-hydrates progress/equity/journal on refresh, names strategies + numbers, shows pass progress.
- Equity chart (report) and trade-detail price chart render for a normal completed replay run.
- Handover written at `docs/iterations/iter-strategy-system/HANDOVER.md` (update, don't duplicate).

---

## Open questions for the owner (non-blocking; sensible defaults chosen)

1. **Governor toggle granularity** — D4 fixes it run-level. Confirm a single on/off is enough, or do you
   want to also pick a governor *profile* per run? (Default: single on/off.)
2. **Row pack default** — when a row's pack = "strategy default", should the grid show the strategy's
   *current* add-ons inline (read-only) so you can see what "default" means? (Default: yes, as a tooltip.)
3. **cTrader multi-pass** — replay handles N combinations in one run; the cTrader path still runs only the
   first `(symbol,timeframe)` (one CLI invocation). For now cTrader runs are single-row. OK to keep cTrader
   single-row this iteration and revisit batching later? (Default: yes.)
