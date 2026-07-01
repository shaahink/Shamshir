# iter-ux-unify — Unified Charts & Tables, Trade-Chart-Everywhere, Live Timeline, Data-Truth Fixes

**Author:** Claude (Opus) — plan + ground-truth investigation, 2026-06-30
**Branch base:** `iter/redesign-ctrader` (charts + live monitor now render after the NG0203 / chart-render fixes this session)
**Audience:** the OpenCode/DeepSeek implementation agent. Phased, failing-test-first, gated.
**Reads first:** this whole doc, then `docs/iterations/iter-redesign-ctrader/HANDOVER.md` (what shipped before) and
`docs/iterations/iter-redesign/VERIFICATION.md` (the cTrader-truth evidence base).

> **Why this exists.** The chart + live monitor are alive again (two display bugs fixed this session: NG0203 in
> `RunMonitorComponent.ngOnInit` from `takeUntilDestroyed()` after `await`; and `BaseChartComponent` never calling
> `updateChart()` after `initChart()`). The owner now wants the UI to become a *fast analytical surface*: legends,
> trade charts inline on every list + a lazy gallery, a real start→end backtest timeline, sortable/searchable
> tables, full-width layout, smooth/auto-fit charts, the entry/exit formula per strategy at launch, a cTrader venue
> session history, and run profiling. Alongside that, the owner asked to **re-query the DB for inconsistencies and
> turn them into this plan** — done in §1. Several "UI looks wrong" symptoms are actually backend data bugs (SL/TP
> all `-`, "3 trades" vs 41 persisted, progress stuck ~70%), so fix the data backbone first.

---

## 0. Hard rules (do not violate)

1. **Failing test first.** Every bug phase starts with a test (or DB-oracle assertion) that fails on `main`/base and
   passes after. Every feature phase ships with at least a render/smoke test.
2. **One unified component, not N copies.** When a phase says "unified table/chart," delete the ad-hoc variants and
   route every caller through the one component. No parallel second system (the project's recurring failure mode).
3. **No fabricated data.** Charts/tables read persisted truth (`TradeResults`, `EquitySnapshots`, `Bars`, `Journal`).
   If a value is missing, fix the producer — never synthesize a placeholder that looks real.
4. **Verify on the running app.** Use `.claude/skills/run-shamshir` (`node .claude/skills/run-shamshir/driver.mjs`)
   to confirm build + serve + run-lifecycle stays green. The owner does the browser smoke (no headless browser here).
5. **Gates are machine-checkable.** Each phase lists an acceptance check (a test name, a DB query result, or a
   driver assertion). "Looks done" is not a gate.
6. **cTrader stays in the loop.** Any data-finalization change (P1.2/P1.3) must be re-checked with
   `scripts/verify-ctrader-run.ps1 <runId>` against a real cTrader run (owner smoke) — replay parity is necessary
   but not sufficient (see iter-redesign-ctrader §13).
7. **Commit per phase** with the listed prefix; keep the golden replay tape byte-identical unless a phase explicitly
   re-baselines it with a written diff.

---

## 1. Ground truth — DB inconsistencies (queried 2026-06-30 against `src/TradingEngine.Web/data/trading.db`)

| # | Finding (evidence) | Root cause (verified) | Fix phase |
|---|---|---|---|
| D1 | **SL/TP render as `-`** for every trade on the report (e.g. run `83bc8971`) — yet the DB has them (`sl=1.16393 tp=1.1674`); **0** trades DB-wide have missing SL/TP. | `RunQueryService.GetRunTradesAsync` (`:104`) never maps `SlPrice`/`TpPrice`; **and** the DTO field is `SlPrice`/`TpPrice` (→ json `slPrice`/`tpPrice`) while the report's table keys are `stopLoss`/`takeProfit` (`run-report.component.ts` `tradeColumns`). Double mismatch ⇒ `undefined` ⇒ `formatValue` returns `-`. | P1.1 |
| D2 | **Run summary `TotalTrades` wildly undercounts** the persisted ledger on cTrader runs: `98c3ed76` says **3**, has **41** distinct trades; `754f4457` 20 vs 162; `d92d949e` 72 vs 232. (41 rows, 41 distinct PositionId/OrderId — **not** duplicates; genuinely 41 trades.) | Summary stats (`GetTradeStatsAsync` → `WriteEndRecordAsync`) are computed **before cTrader's trades finish persisting** (late venue settlement / tail-drain / reconciliation land trades after the stats query). Replay finalizes synchronously so it's correct there. Makes the report "Trades" tile + `recClosesOk` badge disagree with the trades table. | P1.2 |
| D3 | **cTrader `CompletedAtUtc` is a sim-time in the past** for **21 of 21** cTrader runs (e.g. started 12:36 today, "completed" 2026‑06‑30 07:00); replay **0/5**. | The wall-clock finalize (`WriteEndRecordAsync` uses `DateTime.UtcNow`) is being overwritten on the cTrader path by a sim-time terminal write (self-heal / ledger import path). P6 of iter-redesign-ctrader didn't hold for cTrader. | P1.3 |
| D4 | **All money/price columns stored as `TEXT`** (`Lots, EntryPrice, ExitPrice, StopLoss, TakeProfit, *PnLAmount`, and `EquitySnapshots.Equity/Balance`). `MIN/MAX` sort lexically (`"100000.0" < "99880.45"`). Breaks numeric column sort (P4) + risks precision/format drift. | EF stores `decimal` as `TEXT` under SQLite by default; no value converter / column type set. | P1.4 |
| D5 | Report "Type" column always shows **`Backtest`** (1042/1042). | `GetRunTradesAsync` maps `EntryType = t.Mode` (run mode), not the order entry method (Market/Limit). Mislabel. | P1.1 |
| D6 | **38** trades have `DurationSeconds <= 0` (same-bar entry+exit). Their Entry/Exit/SL/TP chart markers collapse on one x — invisible. | Single-bar SL hit; markers share the bar timestamp (known, see HANDOVER §2). | P3.4 |
| D7 | **No catalog bars** — all 48,144 `Bars` are RunId-stamped across 12 runs (`RunId IS NULL` count = 0). A fresh replay run only finds bars because `BarQueryService` dedups by timestamp across runs. | Bars are persisted per-run; there is no shared market-data catalog. Informational — affects "no bars" surprises, not this iteration's UI. | (note only) |
| D8 | Storage: `Journal` 213,090 rows, `EquitySnapshots` 72,280, `Bars` 48,144, `TradeResults` 1,042, `EngineEvents` **0**, DB 176 MB. | Journal + per-run bars dominate. `EngineEvents` table is dead (0 rows) — candidate for removal. | P9 (profiling) / note |

**Code root causes for the non-DB symptoms** (verified file:line):

- **Progress bar stuck ~70%, never 100%** → `BacktestOrchestrator.EstimateBarCount` (`:262`) = `calendarDuration / periodMinutes`. Forex is closed weekends/nights, so real bars ≈ 70% of calendar hours (H1/1-month: ~500 actual vs ~720 estimated → caps at ~69% then jumps to 100 only on the terminal frame via `BuildProgress` `:118`). `barsTotal` is the estimate until all passes finish.
- **Charts "zoomed out"** (MAE/MFE scatter, candle) → no `chart.timeScale().fitContent()` after `setData` in `candle-chart`/`scatter-chart`/`equity-chart` `updateChart()`.
- **Linear charts "not smooth / sudden movements"** → `LineSeries` uses the default `LineType.Simple` (straight segments); owner wants `LineType.Curved`.
- **No legend** → none of `equity/candle/scatter/histogram` render a series/marker legend; `candle-chart` draws SL/TP/Entry/Exit lines with no labels.
- **Clamped UI** → `app.component.ts` wraps nav (`:12`) and `<main>` (`:85`) in `mx-auto max-w-7xl` (1280px).
- **Tables not sortable/searchable** → `data-table.component.ts` renders static `<th>`; no sort state, no filter input; `formatValue` has no `'number'` case (falls to `String`).
- **Trade chart reuse** → the per-trade candle chart + Entry/Exit/SL/TP markers already exist in `trade-detail.component.ts` via `GET /api/trades/{id}/chart` (`getChart`). Reuse it; don't rebuild.
- **cTrader session history** → the venue already emits status events (`NETMQ_CONNECTED`, `CBOT`, …) via `CTraderBrokerAdapter.OnStatusChange` and logs `CTRADER|REAP|pid=…` on orphan kill (`BacktestOrchestrator` ~`:1058`). No persistence/UI for it yet.
- **Entry/exit formula per strategy** → `StrategyConfigEntry` carries `StopLoss` (`SlOptions.Method`), `TakeProfit` (`TpOptions.Method`), `OrderEntry` (`OrderEntryMethod`). The **entry** rule lives in each strategy class — needs a short human description per strategy.

---

## 2. Phases

> Sequencing rationale at the end (§4). P1 (data truth) unblocks everything that "looks wrong"; P2 unblocks the
> unified component reuse; P3/P4 are the shared components every later feature consumes; P5–P9 are features.

### P0 — Repro harness (failing-first)
- **Goal:** lock every bug below with a test/oracle that fails now.
- **Do:**
  - Backend: a test that `GET /api/runs/{id}/trades` returns non-null `slPrice`/`tpPrice` for a seeded trade (fails — D1).
  - Backend: a test that a finished run's `TotalTrades == COUNT(TradeResults for run)` (fails on the cTrader-timing fixture — D2).
  - Backend: a test that `EstimateBarCount`/the resolved `barsTotal` for a known forex window equals the **actual** bar count within tolerance (fails — progress bug).
  - Frontend: a `data-table` spec asserting a `'number'`-format cell renders the value (and that clicking a header sorts) — fails (no sort).
- **Gate:** all new tests present and red; `dotnet build` + `npm run build` green.
- **Commit:** `test(ux): P0 failing repros for SL/TP, trade-count, progress, table sort`

### P1 — Data-truth backbone (the "UI looks wrong" bugs are here)

**P1.1 — SL/TP + Type columns (fixes D1, D5).**
- Map `SlPrice = t.StopLoss`, `TpPrice = t.TakeProfit` in `GetRunTradesAsync` (`RunQueryService.cs:104`). Map a real
  order-entry type onto `EntryType` (from the trade's entry method, not `t.Mode`); if not persisted, persist it.
- Align the contract: rename the run-trades DTO fields to `StopLoss`/`TakeProfit` **or** change the report column
  keys + `TradeSummary` type to `slPrice`/`tpPrice`. **Pick one name and use it end-to-end** (recommend `stopLoss`/
  `takeProfit` to match `trade-detail`). Add a `'number'` case to `data-table.formatValue` (currently falls through).
- **Test:** P0 SL/TP test goes green; a frontend spec renders `stopLoss` for a row.
- **Acceptance:** open `/runs/83bc8971` → SL/TP columns show prices, Type shows Market/Limit.
- **Commit:** `fix(trades): P1.1 map+align SL/TP/EntryType so the report renders them`

**P1.2 — Finalize run stats from the persisted ledger after settlement (fixes D2).**
- Recompute `TotalTrades/NetProfit/Gross/Comm/Swap/WinRate/MaxDD` from `TradeResults` (+ `EquitySnapshots` for DD)
  **after** the cTrader ledger is fully drained/reconciled — i.e. at the terminal write that already runs in the
  `finally` of `RunAsync`, gated on settlement completion (not on bar-stream end). Replay already finalizes
  synchronously; keep it byte-identical.
- Add a self-heal: `SqliteBacktestRunRepository`/a finalize step rewrites the summary if `TotalTrades !=` distinct
  persisted trades. Confirm `recClosesOk`/`recNetOk` badges read OK on the report.
- **Test:** P0 trade-count test green on a fixture where trades persist *after* the first stats query.
- **Acceptance (cTrader smoke):** a fresh cTrader run's report "Trades" tile == trades-table length == `verify-ctrader-run.ps1` count.
- **Commit:** `fix(runs): P1.2 finalize summary stats from ledger after venue settlement`

**P1.3 — Wall-clock `CompletedAtUtc` on cTrader (fixes D3).**
- Find the path that overwrites `CompletedAtUtc` with a sim-time on cTrader (self-heal/ledger import) and stamp
  **wall-clock `DateTime.UtcNow`** at terminal write for all venues. `StartedAtUtc`/`CompletedAtUtc` must bracket
  real elapsed wall time.
- **Test:** finalize test asserts `CompletedAtUtc >= StartedAtUtc` for a cTrader-shaped fixture.
- **Acceptance:** DB query `SELECT COUNT(*) FROM BacktestRuns WHERE Venue='ctrader' AND CompletedAtUtc < StartedAtUtc` → trends to 0 for new runs.
- **Commit:** `fix(runs): P1.3 stamp wall-clock CompletedAtUtc on the cTrader path`

**P1.4 — Numeric money columns (fixes D4). [Q1 = DO NOW]**
- Add EF value-converters / column types so `decimal` money/price columns and `EquitySnapshots.Equity/Balance` store
  as **REAL/NUMERIC**, with a migration that rewrites existing TEXT values. This makes server-side numeric sort,
  `MIN/MAX`, and the chart series correct. It is a migration over a 176 MB DB, so: **back up the DB first**
  (`copy trading.db trading.db.bak`), wrap the rewrite in a transaction, and verify row counts pre/post.
- **Test:** a repo test round-trips a decimal and `ORDER BY Equity` returns numeric order.
- **Commit:** `fix(persistence): P1.4 store money/equity as numeric (+migration)`

### P2 — Progress + live timeline

**P2.1 — Real `barsTotal` (fixes progress-stuck-~70%).**
- Replace the calendar-estimate for the live `barsTotal` with the **actual** count: query the bar count for each
  pass's symbol/timeframe/range up front (or sum `BacktestReplayAdapter.BarCount` as passes complete and feed it
  back), so `percent` reaches 100% smoothly. Keep the terminal frame forcing 100%, but the bar should no longer
  stall at ~70%.
- **Test:** P0 progress test green; `BuildProgress` percent for a known window ends within 1% of 100 before the terminal frame.
- **Acceptance:** owner runs a backtest → progress climbs to ~100% (not stuck at ~70%).
- **Commit:** `fix(monitor): P2.1 drive progress from actual bar count, not calendar estimate`

**P2.2 — Backtest timeline view (start→end "timeline vibe").**
- On the live monitor (`run-monitor.component.ts`), add a horizontal timeline: a track from `BacktestFrom`→`BacktestTo`
  (sim-time) with a moving playhead at `simTime`, and event ticks (entries/exits/breaches/day-rolls) placed by
  sim-time, fed from the journal envelope already arriving over SignalR. Pass-aware (multi-pass shows pass segments).
- **Test:** monitor render spec with a mocked progress stream advances the playhead.
- **Acceptance:** during a run the timeline fills left→right and event ticks appear.
- **Commit:** `feat(monitor): P2.2 sim-time backtest timeline with event ticks`

### P3 — Unified chart component (legend + auto-fit + curves) — consumed by all later phases
- **Goal:** one chart layer all charts extend; add the three missing behaviours once.
- **Do:**
  - **Legend (P3.1):** a shared legend overlay in `BaseChartComponent` (series name + color swatch; for `candle-chart`,
    label the Entry/Exit/SL/TP marker lines). Optional crosshair value readout.
  - **Auto-fit (P3.2):** call `this.chart.timeScale().fitContent()` at the end of `updateChart()` (covers MAE/MFE
    scatter + candle "zoomed out"). Add a price-scale autoscale where a series sets a tiny range.
  - **Curves (P3.3):** add `lineType: LineType.Curved` to the line-based series (equity, scatter, drawdown). Expose a
    `smooth` input (default on) so it can be turned off.
  - **Same-bar markers (P3.4, fixes D6):** when Entry==Exit timestamp, nudge the exit marker to the next bar or render
    as a single combined marker with a tooltip, so single-bar trades are visible.
- **Test:** chart specs assert a legend node renders and `fitContent` is invoked; visual check by owner.
- **Acceptance:** equity/scatter/candle show a legend, fill the panel (not zoomed out), and lines look smooth.
- **Commit:** `feat(charts): P3 unified legend + auto-fit + curved lines (+ same-bar markers)`

### P4 — Unified, sortable, searchable table — consumed by every list
- **Goal:** `data-table.component.ts` becomes the single table with **click-to-sort** (asc/desc/none per column,
  numeric vs text aware using P1.4 numeric values) and a **full-text search box** (filters across visible columns).
- **Do:** sort state signal + comparator (respect `format`: numeric/datetime/text); a debounced filter input;
  keep `colorFn`, `trackKey`, `rowClick`. Replace the hand-rolled tables in `run-report` (run-plan, breakdown,
  per-bar, bar-inspector, journal) and `run-list` with this component (or a thin wrapper) where practical.
- **Test:** specs for sort toggle order and a search term narrowing rows.
- **Acceptance:** every trades/runs table sorts on header click and filters on type.
- **Commit:** `feat(table): P4 unified sortable + full-text searchable data-table`

### P5 — Trade charts everywhere + lazy gallery
- **Goal:** browse many trade charts fast without leaving the page.
- **Do:**
  - **P5.1 Inline:** in any trade list (report trades table, run-analyzer), make a row expand to show the
    `trade-detail` candle chart (Entry/Exit/SL/TP markers from `GET /api/trades/{id}/chart`) + the trade metadata
    tiles, inline. Extract the chart+meta block from `trade-detail.component.ts` into a reusable
    `TradeChartCardComponent`.
  - **P5.2 Gallery [Q2 = per-run first]:** a new lazy-loaded route `/runs/:id/gallery` rendering a grid of
    `TradeChartCard`s for that run's trades, each with metadata + links to its **backtest run** and **strategy**.
    Lazy-load with `IntersectionObserver` (only fetch a card's chart when it scrolls into view) so hundreds of trades
    stay smooth. A global `/trades/gallery` across all runs is a **deferred follow-up** (same component, different
    data source) — don't build it this iteration.
- **Test:** gallery spec lazy-loads a card on intersection; card spec renders markers.
- **Acceptance:** owner opens the gallery → scrolls a grid of trade charts, each linking to run + strategy.
- **Commit:** `feat(trades): P5 reusable TradeChartCard — inline expand + lazy gallery`

### P6 — Strategy entry/exit formula at backtest launch
- **Goal:** when configuring a backtest, see each strategy's entry rule + exit (SL/TP) formula.
- **Do [Q4 = static metadata map]:** add a short human-readable **entry description** per strategy as a **static
  metadata map keyed by strategy id** (no schema change — lives next to the strategy classes), and surface it with the
  resolved `StopLoss.Method`/`TakeProfit.Method`/`OrderEntry.Method` in the New-Backtest strategy picker (and the
  report run-plan). Expose via the strategies API the report/new-backtest already calls. (Promote to an editable
  config-row field only if the owner later asks.)
- **Test:** API returns entry/exit metadata for each strategy; picker spec shows it.
- **Acceptance:** New-Backtest shows e.g. "super-trend — Entry: close crosses SuperTrend flip · SL: 1.5×ATR · TP: 2R · Market".
- **Commit:** `feat(strategies): P6 surface entry rule + exit formula in New-Backtest`

### P7 — cTrader venue session history page
- **Goal:** a page listing cTrader (venue) instances this session: started / connected / stopped / reaped, with pid,
  ports, run id, exit code, timestamps.
- **Do [Q3 = persisted across restarts]:** persist venue-process lifecycle events (reuse
  `CTraderBrokerAdapter.OnStatusChange` statuses + the `CTRADER|REAP|pid=…` reap path in `BacktestOrchestrator`
  ~`:1058`) into a small **persisted table** (survives app restart, EF migration), expose `GET /api/ctrader/sessions`,
  and render a timeline/table page. Tie each row to its run.
- **Test:** the store records a start+stop pair for a fixture; API returns it.
- **Acceptance:** owner runs a cTrader backtest → the page shows the instance starting, connecting, and being reaped (matching Task Manager).
- **Commit:** `feat(ctrader): P7 venue session history (process lifecycle) page`

### P8 — Full-width layout
- **Goal:** use the page width; stop clamping at 1280px.
- **Do:** in `app.component.ts` replace `max-w-7xl` on `<main>` (`:85`) (and nav `:12` if desired) with a wide/fluid
  container (e.g. `max-w-[1800px]` or `w-full` with sensible `px`). Verify dense tables/charts use the new width and
  nothing overflows.
- **Test:** none (layout); owner visual check.
- **Acceptance:** content spans the viewport; no clamped column of whitespace.
- **Commit:** `feat(ui): P8 full-width app shell`

### P9 — Backtest run profiling, captured + displayed
- **Goal:** show how a run performed *as a computation*: wall elapsed, bars/sec, bar count, per-phase timing
  (warmup / replay / persistence-flush), peak memory if cheap, venue connect time (cTrader).
- **Do:** capture timings in `BacktestOrchestrator`/`EngineRunner` (already have `WallElapsedMs`, `BarsPerSec` in the
  progress frame — persist the terminal values + add phase stopwatches), store on the run, expose via the run detail
  API, and show a "Profiling" card on the report. Consider dropping the dead `EngineEvents` table (D8) while here.
- **Test:** finalize writes non-zero elapsed + bars/sec; API returns them.
- **Acceptance:** the report shows a Profiling card with real numbers; matches the live monitor's speed/elapsed.
- **Commit:** `feat(runs): P9 capture + display backtest run profiling`

---

## 3. Owner decisions — RESOLVED (baked in, 2026-06-30)

These are locked; the phases above already reflect them. Recorded here so the agent doesn't re-open them.

| Q | Decision | Chosen | Why / how |
|---|---|---|---|
| **Q1** | Numeric money/equity column migration (P1.4) — now or defer? | **DO NOW** | Load-bearing for correct numeric table sort (P4) + clean chart ranges; the rewrite only gets worse as the DB grows. Mitigate the 176 MB migration risk: back up `trading.db` first, transaction-wrap, verify row counts pre/post. |
| **Q2** | Trade-chart gallery scope (P5.2) — per-run, global, or both? | **PER-RUN FIRST** | Ship `/runs/:id/gallery`. A global `/trades/gallery` is a deferred follow-up (same `TradeChartCard`, different data source) — not this iteration. |
| **Q3** | cTrader session history (P7) — in-memory or persisted? | **PERSISTED** | Small EF-migrated table so the venue process history survives an app restart and ties each instance to its run. |
| **Q4** | Strategy entry-rule source (P6) — static map or config-row field? | **STATIC METADATA MAP** | Keyed by strategy id, lives next to the strategy classes; no schema change. Promote to an editable config field only if the owner later asks. |

---

## 4. Sequencing

```
P0 (repros) → P1 (data truth: SL/TP, trade-count, completed-at, [numeric cols]) → P2 (progress + timeline)
            → P3 (unified chart: legend/fit/curve)  ┐
            → P4 (unified table: sort/search)        ├─ shared components, do before the feature phases that consume them
P3,P4 → P5 (trade chart inline + lazy gallery) → P6 (strategy entry/exit) → P7 (cTrader history) → P8 (full width) → P9 (profiling)
```

- **P1 first** — the "blank/zero" symptoms (SL/TP `-`, "3 trades", stuck progress) erode trust and several later
  views render P1's data. P1.1/P1.2/P1.3 are small and high-value; P1.4 (numeric columns) lands here too
  (Q1 = now — back up the DB first).
- **P3 + P4 before P5–P9** — every later screen reuses the unified chart and table; building them once avoids a third
  table/chart variant.
- **P8 (full width)** can land any time but is cheap to do alongside P4 (tables benefit most from the width).
- Re-run `scripts/verify-ctrader-run.ps1` after P1.2/P1.3 on a real cTrader run (owner smoke). Keep replay golden green throughout.

## 5. Key file index

| Area | Files |
|---|---|
| Trades DTO / SL-TP / Type (D1,D5,P1.1) | `src/TradingEngine.Web/Services/RunQueryService.cs` (`GetRunTradesAsync :99`), `src/TradingEngine.Web/Dtos/Runs/TradeSummaryResponse.cs`, `web-ui/src/app/features/runs/run-report/run-report.component.ts` (`tradeColumns`), `web-ui/src/app/models/api.types.ts` (`TradeSummary`), `web-ui/src/app/shared/data-table.component.ts` (`formatValue`) |
| Run finalize / stats / completed-at (D2,D3,P1.2,P1.3) | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` (`RunAsync :341`, `GetTradeStatsAsync`, `WriteEndRecordAsync :540`), `SqliteBacktestRunRepository` |
| Numeric columns (D4,P1.4) | EF config in `src/TradingEngine.Infrastructure/Persistence/*` (TradeResult + EquitySnapshot mappings) + a new migration |
| Progress / timeline (P2) | `BacktestOrchestrator` (`EstimateBarCount :262`, `BuildProgress :102`, `RunEngineReplayAsync :726`), `web-ui/.../run-monitor/run-monitor.component.ts` |
| Unified chart (P3) | `web-ui/src/app/shared/base-chart.component.ts`, `equity-chart.component.ts`, `candle-chart.component.ts`, `scatter-chart.component.ts`, `histogram-chart.component.ts` |
| Unified table (P4) | `web-ui/src/app/shared/data-table.component.ts` + all current table call sites |
| Trade chart card + gallery (P5) | new `web-ui/src/app/shared/trade-chart-card.component.ts` (extract from `features/trades/trade-detail/trade-detail.component.ts`), new gallery route under `features/trades` or `features/runs`, `GET /api/trades/{id}/chart` |
| Strategy entry/exit (P6) | `src/TradingEngine.Web/Api/StrategiesController.cs`, `web-ui/.../new-backtest/*`, strategy classes (entry rule) |
| cTrader history (P7) | `BacktestOrchestrator.RunEngineNetMqAsync :892` + reap ~`:1058`, `CTraderBrokerAdapter.OnStatusChange`, new store/table + `/api/ctrader/sessions` + page |
| Full width (P8) | `web-ui/src/app/app.component.ts` (`:12`, `:85`) |
| Profiling (P9) | `BacktestOrchestrator`, `EngineRunner`, run detail API + report |

## 6. Definition of done

- Every P0 repro is green; `dotnet build` + `npm run build` = 0 errors; `node .claude/skills/run-shamshir/driver.mjs` 11/11.
- `/runs/83bc8971`: SL/TP + Type populated; "Trades" tile == trades-table length; reconciliation badges OK.
- New cTrader run: `CompletedAtUtc >= StartedAtUtc`; `verify-ctrader-run.ps1` passes; progress reached ~100% live.
- Charts: legend visible, auto-fit (not zoomed out), smooth lines. Tables: sort-on-click + full-text search everywhere.
- Trade charts viewable inline in lists + in a lazy gallery linking run+strategy. New-Backtest shows entry/exit per strategy.
- cTrader venue session history page reflects Task Manager. Layout uses full width. Report shows a Profiling card.
- Handover updated; owner does one cTrader browser smoke before "done."
