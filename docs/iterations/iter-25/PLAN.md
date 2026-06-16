# Iter-25 Plan — Make the Web UI Usable & Honest

Context: the Web app (`src/TradingEngine.Web`) accreted across iters 18–24 and now has
two parallel UI stacks, fabricated chart data, a broken SignalR terminal frame, dead/stub
APIs, and unit-mismatched numbers — so a user "can't run a backtest and assess the engine."
This iteration makes the UI **work end-to-end on real data** and removes the duplicate-page
confusion.

**Data model (corrected per owner, 2026-06-15):** bars are **not** pre-seeded. A backtest
launches `ctrader-cli` with the built `.algo`; cTrader's backtester replays history and
feeds bars over NetMQ to the in-process engine, which evaluates strategies and writes
trades/equity/journal to a fresh `data/trading.db`. A new DB per run is by design — no
`seed-bars.ps1` step is needed for the real (cTrader) path. The dev default is already this
path: `appsettings.Development.json` has `CTrader:UseForBacktest:true` with credentials set.
The one gap: **bars themselves are never persisted on ingest** — `BufferedBarWriter` (the
intended "store bars as we process them" vehicle) is dead code, referenced only inside its
own file. That's why downstream price charts have nothing real to draw. Strategies are
**code-first** (one class each) **+ JSON** params in `config/strategies/*.json` (9 of them);
the UI surfaces the registry, it does not author strategies.

Mostly the Web project + a small Host/Infrastructure wiring fix (bar persistence). No
engine/kernel/risk logic changes — that's iter-24's lane.

Working style: each phase ships independently and leaves the app building + runnable;
prefer small commits with the machine-checkable **Gate** met before moving on. Branch off
the current branch. Do **not** touch `aspire/AppHost` (unrelated `NU1903` build break) —
build/run the Web project directly: `dotnet build src/TradingEngine.Web`.

The single end-to-end acceptance test for the whole iteration (the "assess the engine"
flow): **New Backtest → Start → Monitor shows live progress/equity → Done → Report →
Trades list (filterable, with dates) → Trade Detail (real chart).**

---

## Root-cause map (symptom → diagnosis → location)

| # | User symptom | Root cause (verified) | Location |
|---|---|---|---|
| R0 | "can't run the backtest / assess the engine" | **Real path = cTrader NetMQ** (`UseForBacktest:true`), which feeds bars from cTrader's backtester — no seeding needed. But (a) bars are **never persisted on ingest** (`BufferedBarWriter` is dead code → no price data for charts), and (b) the *fallback* `RunEngineReplayAsync` path reads bars from the DB, which is empty on a fresh run → "No bars found" → 0 trades if that path is taken by mistake. | `BufferedBarWriter.cs` (unwired); `BacktestOrchestrator.RunEngineNetMqAsync:407` (real) vs `RunEngineReplayAsync:384` (fallback) |
| R1 | "strange multi-page flow" | **Two UI stacks.** Razor Pages (`/Pages/*.cshtml`) are the *only* nav-wired UI; the Blazor pages (`/Components/Pages/*.razor`: TradeExplorer, BacktestDashboard, RunDetail, StrategyManager, SymbolAnalysis, ExperimentBrowser, BacktestComparison) are **orphaned** — not in any nav, reachable only by direct URL via the `/blazor/_Host` fallback. Duplicate Trades/Backtest/Strategies views. | `Pages/_Layout.cshtml:13-31`; `Program.cs:84`; `Components/Pages/*` |
| R2 | "trades page is raw, can't filter, no createdOn" | Plain `<table>`, **no filter UI** (the `strategyId` filter exists in the model but nothing posts it), only `ClosedAtUtc` shown as "Date", no `OpenedAtUtc`, no account before/after. | `Pages/Trades/Index.cshtml`; `Index.cshtml.cs:9-22` |
| R3 | "no chart works" (trade detail) | **Chart data is fabricated** — 6 fake OHLC bars synthesized from hardcoded `delta = 0.0010m` around entry/exit. Never loads real bars. | `Pages/Trades/Detail.cshtml.cs:17-29` |
| R4 | "account before/after per each trade" missing | Trade Detail shows no running balance/equity context; there's no before/after equity on the trade record or page. | `Pages/Trades/Detail.cshtml` |
| R5 | SignalR issue (report CTA broken) | **`onDone` contract mismatch.** `RunProgressBroadcaster.PublishDone` sends the full `RunProgress` envelope; `Monitor.cshtml`'s `onDone: (status) => { if (status === 'completed') }` treats it as a string → always false → CTA renders "Run [object Object]". | `Services/RunProgressBroadcaster.cs:35-39`; `Pages/Runs/Monitor.cshtml:193-203` |
| R6 | Monitor "missing data placeholders" (0%, --) | `BuildProgress` hardcodes `BarsTotal: 0, Percent: 0, EtaSeconds: null` → progress bar, %, ETA never move. Total bars are knowable up-front. | `Services/BacktestOrchestrator.cs:99-109` |
| R7 | Monitor equity/curve empty during run | `state.Equity` is only populated **after** the run (`PopulateEquityStateAsync`), so equity KPI + sparkline stay `--`/empty for the entire run. | `BacktestOrchestrator.cs:232, 658-682` |
| R8 | Journal filters do nothing | Filter buttons use `data-filter="OrderSubmitted/OrderRejected/BreachDetected"` but emitted event names are `SIGNAL/ORDER/FILL/CLOSE/REJECT/BREACH`. Only `CLOSE` matches. | `Monitor.cshtml:48-52`; `BacktestOrchestrator.TallyEvent:112-135` |
| R9 | "json returning exception" / dead data | `TradesApiController` is a **stub** (`GetTrades`→`[]`, detail→"not yet implemented"). Other endpoints likely throw serializing Domain value-types. Needs per-endpoint repro. | `Api/TradesApiController.cs` |
| R10 | Numbers wrong on Runs/Compare | **Win-rate unit mismatch**: stored as fraction `wins/count` (0..1) but JS renders `(winRatePct).toFixed(1)+'%'` → "0.5%" instead of "50%". Compare "chart" plots a degenerate 2-point line, not an equity curve. | `BacktestOrchestrator.GetTradeStatsAsync:633`; `Runs/Index.cshtml:58, 81-92` |

---

## Decisions (authoritative — do not re-litigate)

**D1 — Consolidate on Razor Pages; retire the orphan Blazor pages.** The Razor Pages stack
is the one wired into nav, server-rendered, and working; the Blazor `Components/Pages/*`
are dead duplicates. Porting everything to Blazor (or running both) is the larger, riskier
move and is what created the confusion. So: **Razor Pages is the shell.** For each orphan
Blazor page, either (a) port its one unique, valuable feature into the corresponding Razor
Page, or (b) delete it. Keep `Components/Shared/*` only if a Razor page actually mounts it;
otherwise delete. Net goal: **one** Trades view, **one** Backtest-run flow, **one** Runs
view, **one** Strategies view. (Reversible later if the user wants a full Blazor rebuild —
that would be its own iteration.)

**D1a — Strategies page is read-only for now.** Strategies are code-first classes + JSON
params in `config/strategies/*.json` (9 of them). The page lists the registry and shows each
strategy's JSON params read-only — it does **not** author or edit strategies. *(Future, not
this iteration: per-experiment parameter overrides stored in the DB so different tests can
tweak params without touching the config files — leave a seam, don't build it.)*

**D7 — Engine logic is mode-agnostic (live ≡ backtest).** The same code runs live and
backtest; backtest is just a UI-driven driver, not a separate tool. Bar persistence and
journal enrichment are **observability side-writes**: wired identically for both modes, run
in the background off the priority thread, and change what's recorded, never what's decided.
No `if (backtest)` in the logic. This determines the Q1 seam (EventBus from shared
`TradingLoop.ProcessBarAsync`) and the RunId approach (persistence boundary only).

**D2 — Bars come from cTrader and are persisted **per run**; no pre-seeding.** The DB is **not**
reset each run by default (it accumulates runs/trades; a fresh DB is only an occasional dev
choice). So bars must be **saved against each backtest separately** — add a `RunId` to bar
storage so two runs' bars don't collide. Wire `BufferedBarWriter` (currently dead code) into
the ingest path, persisting at the **strategy timeframe**. Include **indicator warmup** bars:
the engine needs lookback before the run's start so indicators are primed at bar 1, and the
chart needs that left-context — store/serve the warmup window, don't start exactly at
StartDate. Preflight checks **cTrader/cBot readiness** (credentials present, `.algo` built),
not bar coverage. The DB-replay path stays an explicit secondary option for stored bars.

**D6 — A run is multi-symbol × multi-strategy × multi-timeframe (engine requirement).** One
backtest can run several strategies against several symbols in different timeframes at once;
that's the engine's design. The current UI assumes a single symbol/timeframe and doesn't
surface strategy selection. The UI doesn't have to fully expose every combination this
iteration, but it must **not assume a single symbol/strategy** in lists/charts/aggregates,
and the New Backtest form should move toward multi-select (symbols, timeframes, strategies).
Where a view is still single-dimension, scope it explicitly (e.g. per symbol+timeframe) rather
than silently showing one and hiding the rest.

**D3 — Charts render real data or an explicit empty-state. Never fabricate.** Delete the
synthetic-bar generator. If real bars aren't available for a window, show "No price data
for this window" — do not draw fake candles.

**D4 — Money/percent units are normalized at the boundary.** Pick one convention: store
win-rate and drawdown as **fractions** (0..1) in the DB/DTO and multiply by 100 in exactly
one place (the view). Audit every `.toFixed(_)+'%'` site against what the API sends.

**D5 — One backtest start path.** New Backtest posts to `POST /api/backtest/start` → Monitor.
Retire the parallel `Run.cshtml`/`Progress.cshtml` Razor-page POST handler (or make it a thin
redirect to the API path) so there's a single, tested flow. Reconcile the default balance
(form says 10000, `RunModel` says 100000) — use the FTMO-realistic default (100000) in both.

**D7 — Engine logic is mode-agnostic; backtest is just a driver (overriding principle).** The
*same* code runs live and backtest — this is a live trading engine we happen to drive from the
UI in backtest mode, **not** a backtest tool. So nothing in the trading logic may branch on or
encode backtest behaviour. Bar persistence (U0) and journal enrichment (U5) are **observability
side-writes**: wired identically for live and backtest, running in the **background off the
priority thread**, and changing *what gets recorded* — never *what the engine decides*. This
restates the scope rule precisely:
- **Permitted:** mode-agnostic, record-only side-writes (persist bars, record decision context)
  that read already-in-scope data and don't alter evaluation/sizing/risk or control flow.
- **Prohibited:** any change to what the engine decides, and any `if (backtest)` branch in the
  logic. (`BufferedBarWriter` already fits — bounded channel + single background consumer +
  batched bulk insert, `DropOldest` under pressure, so `Enqueue` never blocks the hot path.)

---

## Resolved agent blockers (Q1–Q7 — authoritative)

**Q1 — Bar-persistence seam: a once-per-bar event on the *shared* path + background subscriber.**
Not Option A (`RunBacktestLoopAsync` is backtest-only — violates D7) and not Option B (the
orchestrator's NetMQ progress callback is backtest-only, won't persist bars live). Instead:
publish a once-per-bar `BarIngested(runId, bar)` event in the **shared** `TradingLoop.ProcessBarAsync`
(both live `EngineWorker.ProcessBarsAsync` and `EngineRunner.RunBacktestLoopAsync` funnel through
it), carrying `runContext.RunId`. A **background EventBus subscriber** (a persistence handler like
the existing `PipelineEventWriter`/`ProtectionLedgerPersistenceHandler`) consumes it and
`Enqueue`s into `BufferedBarWriter`. Generalizes Option C onto the event bus so live persists bars
too. If a clean once-per-bar event already exists, reuse it rather than adding `BarIngested`.

**Q2 — `RunId` lives at the persistence boundary, not on the domain `Bar` (Option B).** Do **not**
add `RunId` to the domain `Bar` record (Option A pollutes the engine across ~15 sites; Option C's
optional field still pollutes it). The `BarIngested` event carries `runId`; the persistence handler
stamps it onto `BarEntity` when mapping. Domain `Bar` is unchanged. Add `RunId` column + index to
`BarEntity` (+ migration) and a run-scoped `IBarRepository.GetAsync(runId, symbol, tf, from, to)`.

**Q3 — cTrader-path total bars: estimate from the date range (Option A).** Compute
`BarsTotal ≈ (End − Start) / timeframe_duration`; it's approximate (over-counts weekends/holidays),
so treat the progress bar as an estimate — cap `Percent` at 99% until the terminal frame, then snap
to 100%. No cTrader-side change (Option B is out of scope). Replace the hardcoded `BarsTotal:0`.

**Q4 — U5 journal enrichment is PERMITTED, not deferred.** Recording the indicator values +
account snapshot into `DecisionRecord.DetailJson` is an observability side-write under D7 — it
reads data already in hand (`strategyIndicators`, `currentEquity()`) and changes only what's
recorded, not what's decided. Constraint: serialize existing values at the existing journal write
site; add no new computation to the decision path. (Keep it cheap; the write itself can be async.)

**Q5 — Add `StrategyIds` (string[]) to `BacktestConfig` + `StartRequest`; empty = all active.**
Mirrors `appsettings ActiveStrategyIds` semantics. Thread the selected IDs through to the host's
strategy activation (the same setting the host already reads). This is config plumbing, not engine
logic — permitted. The form populates the multi-select from the read-only strategy registry.

**Q6 — Keep `New.cshtml`; retire `Run.cshtml` + `Progress.cshtml` (Option A).** `New.cshtml` is the
modern path (JS → `/api/backtest/start` → SignalR **Monitor**), which is the good live-monitor UX.
`Run.cshtml` posts to the older SSE **Progress** page. Port `Run.cshtml`'s multi-select symbols/
timeframes into `New.cshtml`, add strategy multi-select (Q5), then delete both `Run.*` and
`Progress.*`.

**Q7 — Use the existing `tests/TradingEngine.Tests.Integration`.** Don't create a new project. Drive
the **DB-replay path** with a small in-test bar fixture via `WebApplicationFactory<Program>`; avoid
cTrader. Heed the harness gotchas (don't boot the slow `ReplayTestHarness`/IHost ~60s floor) — keep
it a focused smoke test: start → poll to completion → assert trades + bars-with-RunId + Report.

---

## Phases

> Each phase: make the change, `dotnet build src/TradingEngine.Web` clean, manually verify
> the Gate by running the app (`dotnet run --project src/TradingEngine.Web`) against a seeded
> DB. Where a unit/contract test is cheap, add it.

### U0 — Persist bars on ingest + cTrader preflight (unblocks everything)
The real path feeds bars from cTrader; the only data gap is that they aren't stored, so
charts have nothing to draw. Fix the storage, not a seed file.
- **Wire bar persistence, scoped per run (seam per Q1/Q2).** Publish a once-per-bar
  `BarIngested(runId, bar)` from the **shared** `TradingLoop.ProcessBarAsync` (both live and
  backtest funnel through it — do **not** tap the backtest-only `RunBacktestLoopAsync`; note
  the kernel's `BarClosed` is dead code and is *not* the seam). A background EventBus
  persistence subscriber consumes it and `Enqueue`s into `BufferedBarWriter` (register it;
  it's currently dead code), which bulk-inserts in the background — `Enqueue` must never block
  the hot path. Add a `RunId` column + index to `BarEntity` (+ EF migration) and a run-scoped
  `IBarRepository.GetAsync(runId, …)`; `RunId` stays at the persistence boundary, **not** on
  the domain `Bar` record. Persist at the **strategy timeframe**. Flush on run completion.
- **Indicator warmup.** Ensure the stored/served bar window includes the warmup lookback
  before the run's `StartDate` (enough bars for the longest indicator period in the active
  strategies), so indicators are valid from the first evaluated bar and charts have
  left-context. If the cTrader feed already includes warmup, just make sure those bars are
  persisted too; if not, request the lookback explicitly.
- **cTrader/cBot preflight.** Before launching a run, verify credentials present
  (`RunModel.OnGet` already checks `CtId/PwdFile/Account`) **and** the `.algo` exists
  (`ResolveAlgoPath` throws if not built). Surface a clear, actionable message on the New
  Backtest page when either is missing — not a 500.
- **Make the path explicit in the UI.** Show which engine the run will use (cTrader NetMQ vs
  DB-replay) based on `CTrader:UseForBacktest`, so it's never ambiguous why a run found/needed
  data. The DB-replay path stays available but clearly secondary.

**Gate:** a backtest started from the UI on the default (cTrader) path runs to completion and
leaves the `bars` table populated **with that run's `RunId`** for the run's symbol/timeframe
(including warmup bars before StartDate); two runs don't share bar rows; with credentials or
`.algo` missing, the New Backtest page shows a specific preflight error instead of
starting/500-ing.

### U1 — Kill the dual-stack confusion (D1)
- Inventory which `Components/Pages/*.razor` features are not present in the Razor Pages.
- Port the few worth keeping into the corresponding Razor Page; delete the rest and any
  now-unused `Components/Shared/*` + `_Host`/Blazor wiring you no longer need. If Blazor is
  fully removed, drop `AddServerSideBlazor`/`MapBlazorHub`/`MapFallbackToPage` and replace the
  fallback with a Razor Pages 404/redirect.
- Every nav target in `_Layout.cshtml` resolves to exactly one page; no orphan routes remain.

**Gate:** `grep -r "Components/Pages"` shows only files still referenced by nav; navigating
every sidebar link renders a real page; there is exactly one Trades, one Runs, one Backtest,
one Strategies route.

### U2 — Trades list: filters + dates + linking (R2)
- Replace the raw table with a filter bar: **run, symbol, strategy**, direction, date range
  (open/close), and win/loss. Wire each filter through `OnGet` query params (extend the
  existing `strategyId` pattern) — server-side filtering + paging, page size selector. A run
  spans several symbols/strategies (D6), so symbol and strategy are real, frequently-used
  filters here, not decoration.
- Columns: add **Opened** (`OpenedAtUtc`) alongside **Closed**, show both as `yyyy-MM-dd HH:mm`,
  plus **Strategy** and (when unfiltered) **Run**; make headers sortable (server-side). Keep
  PnL/R-multiple coloring.
- Show total/filtered count and proper pagination (current code renders one `<a>` per page —
  cap with prev/next + ellipsis).

**Gate:** Trades page lets you filter by symbol+date+win/loss and the row count + results
change accordingly; both open and close timestamps are visible; sorting by PnL works.

### U3 — Trade Detail: real chart + account before/after (R3, R4)
- Delete the synthetic-bar block (`Detail.cshtml.cs:17-29`). Load real bars from
  `IBarRepository` for the trade's symbol/timeframe over `[OpenedAtUtc − N, ClosedAtUtc + N]`
  (N ≈ 20 bars padding). Serialize `{time,open,high,low,close}` for `candleChart`.
  **Depends on U0** — these bars only exist because the run now persists them on ingest; a
  trade from a window with no stored bars falls through to the empty-state (next bullet).
- Render entry/exit/SL/TP as price lines and entry/exit markers at the real bar times
  (the `charts/index.js candleChart` API already supports `addPriceLine`/`addMarkers`).
- If no bars exist for the window, render the explicit empty-state (D3), not fake candles.
- Add **Account before/after**: show balance & equity immediately before and after the
  trade. Source from `IAccountSnapshotStore`/`IEquityRepository` if a snapshot exists at the
  trade boundary; otherwise compute a running balance from the ordered trade sequence for the
  run (`Σ NetPnL` up to and including this trade) and label it as derived.

**Gate:** opening a real trade shows a candlestick chart of actual market bars around the
trade with entry/exit/SL/TP lines, plus balance-before / balance-after values that reconcile
with `NetPnLAmount`.

### U4 — Live Monitor: progress, equity, done (R5, R6, R7)
- **R6 progress:** before replay, count bars in range (`IBarRepository`) → set `BarsTotal`;
  compute `Percent = BarsProcessed/BarsTotal` and a simple `EtaSeconds` from `BarsPerSec` in
  `BuildProgress`. Progress bar + % + ETA must advance.
- **R7 equity:** stream equity during the run. Push an `AccountSnapshot` (equity/balance/
  daily-DD/open-positions) into each progress frame as bars close, instead of only
  populating after completion. The Monitor sparkline + Equity KPI update live.
- **R5 done:** fix the `onDone` handler to read `progress.status` (it receives the full
  `RunProgress` envelope). The Report CTA must show "View Report" on success and the real
  failure reason on failure — never "[object Object]".

**Gate:** during a run the progress bar climbs 0→100%, the equity sparkline draws live, and
on completion the CTA reads "View Report" and links to `/runs/{id}/report`.

### U5 — Journal: flat decisive-factors view + working filters (R8 + new)
The journal must show **why** a decision was made — a flat, columnar view of the decisive
factors (indicator values, account info) per decision, not a raw JSON blob or a one-line
string. The data already flows through the engine; it's just not captured structurally or
rendered flat.
- **Capture decisive factors structurally.** At each decision (`SIGNAL`/`BAR_EVAL`/`ORDER`/
  `REJECT`/`BREACH`), record into `DecisionRecord.DetailJson` a structured payload: the
  strategy's indicator values (already available as `BarEvaluated.strategyIndicators` /
  `strategyIndicators` in `TradingLoop.cs:127,140`) **and** the account snapshot at that
  instant (equity/balance/daily-DD/open-positions — `currentEquity()` is in hand at
  `TradingLoop.cs:145`). Keep `Symbol`/`StrategyId`/`Reason`/`GuardResult` populated.
- **Render flat.** Build a journal table: one row per decision with columns sim-time, symbol,
  strategy, event, direction, reason, the key indicator values, and account info (equity,
  daily-DD). Indicator columns can be dynamic per strategy. This is the primary "assess the
  engine" surface — make it filterable (by event, symbol, strategy) and exportable.
- **Fix the live-feed filters (R8).** Align the Monitor filter buttons' `data-filter` values
  with the emitted event names (`SIGNAL/ORDER/FILL/CLOSE/REJECT/BREACH`) — pick one canonical
  set and make both sides agree; verify `applyJournalFilter` shows/hides correctly.

**Gate:** the journal renders a flat table where each signal/order/reject row shows the
indicator values and account equity/DD that drove it; filtering by event/symbol/strategy
narrows the rows; the live Monitor filter tabs each show only matching lines.

### U6 — Fix the data APIs (R9, R10)
- Reproduce every `/api/**` endpoint the live pages call (start with `TradesApiController`,
  `BacktestAnalyticsController` runs/compare, `EquityController`, `BarsController`,
  `EventsController`). For each one that throws, fix it — usually by projecting Domain
  value-types (`Symbol`, `Money`, etc.) to plain DTOs instead of serializing them raw.
- Implement `TradesApiController.GetTrades`/`GetTradeDetail` for real (back them with
  `ITradeRepository`/`ReportingDbContext`) **or** delete the controller if the Razor pages
  read the DB directly and nothing else calls it. No stub endpoints left in the build.
- **R10 units:** make win-rate one convention end-to-end (D4). Fix
  `Runs/Index.cshtml` so win-rate renders correctly (currently shows "0.5%" for 50%).
- **R10 compare:** either plot real per-run equity curves in the compare modal or replace the
  fake 2-point line with an honest bar chart of final PnL/DD. No degenerate charts.

**Gate:** every endpoint hit by a live page returns 200 + valid JSON (no 500s in the console);
Runs list shows win-rate as a sensible percentage; compare renders a real comparison.

### U7 — One backtest start path + multi-dimension form (D5, D6)
- **Keep `New.cshtml`; delete `Run.*` + `Progress.*` (Q6).** `New.cshtml` already POSTs
  `/api/backtest/start` → SignalR Monitor (the good path). Port `Run.cshtml`'s multi-select
  symbols/timeframes into it, then remove `Run.cshtml(.cs)` and `Progress.cshtml(.cs)`.
  Reconcile the default balance to 100000 in the form and `StartRequest`.
- **Multi-select + strategy selection (D6, Q5).** `StartRequest`/`BacktestConfig` already carry
  `Symbols`/`Periods` arrays — surface them as multi-select. Add a **`StrategyIds` (string[])**
  field to `BacktestConfig` + `StartRequest` (empty = all active, mirroring `ActiveStrategyIds`)
  and a strategy multi-select populated from the read-only registry; thread the IDs to the
  host's strategy activation. The form must stop forcing a single symbol/timeframe; Monitor/
  Report/Trades must render correctly when a run spans several symbols/strategies.

**Gate:** New Backtest can start a run with ≥2 symbols and ≥2 strategies in one go; there is
exactly one way to start a backtest; the run lands on Monitor and reaches a Report.

### U8 — End-to-end smoke + polish
- Walk the full acceptance flow (top of this doc) on the cTrader path and fix remaining rough
  edges (empty-states, loading skeletons that never resolve, broken links in `_RunNav`),
  including multi-symbol/multi-strategy runs not collapsing to one dimension.
- Add a lightweight integration test (xUnit + `WebApplicationFactory<Program>`). Since the
  real path needs cTrader, the test can drive the **DB-replay path** against a small in-test
  bar set (or a recorded fixture): POST `/api/backtest/start`, poll to completion, assert the
  run produced trades, persisted bars **with the run's `RunId`**, and the Report endpoint
  returns them — so this flow can't silently rot again.

**Gate:** the end-to-end acceptance flow works start-to-finish; a run persists its own bars;
the new integration test is green.

---

## Out of scope
- Engine/kernel/risk **logic** changes (that's iter-24's lane). The one allowed
  Host/Infrastructure touch is the U0 bar-persistence side-write — it must not alter
  evaluation, sizing, or risk behaviour, only store bars that already flow through.
- A full Blazor rebuild (explicitly deferred by D1).
- New strategies or analytics features beyond making existing pages show real data.
