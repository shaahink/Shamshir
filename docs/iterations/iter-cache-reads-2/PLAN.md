# iter-cache-reads-2 — Fix + speed up RunDataCache (frontend read path)

**Written:** 2026-07-01
**Branch base:** `develop` (post iter-cache-reads merge)
**Owner:** AI agent (OpenCode / DeepSeek)
**Scope guard:** NO kernel change. Touch only `TradingEngine.Infrastructure` (Caching/Persistence),
`TradingEngine.Host` DI wiring, `TradingEngine.Web` (RunQueryService / orchestrator / controllers),
and `web-ui`. Do **not** touch `TradingEngine.Engine` (the decision kernel/reducer), `EngineWorker`,
`KernelBacktestLoop`, or any strategy/risk logic. Golden replay must stay byte-identical.

---

## Why this iteration exists

iter-cache-reads added `RunDataCache` (write-through in-memory read layer) to stop the Angular UI
freezing during backtests. A static audit of the merged code found the cache **does not actually
serve fresh data during a running backtest**, **leaks memory across runs**, and leaves several read
paths still hitting SQLite. WAL is already enabled (`ServiceRegistration.cs:85` +
`SqlitePragmaInterceptor` busy_timeout=5000), so reader/writer blocking is already mostly solved by
WAL — the remaining UI slowness is (a) these cache bugs and (b) O(N)-per-poll read amplification, not
raw lock contention. Measure before assuming "more caching" is the fix (see P0).

---

## Findings (verified against merged source)

### Correctness bugs

**B1 — Live equity & trades snapshots FREEZE after the first read.** *(headline)*
`RunDataCache.RunEntry.EquitySnapshot` / `TradeSnapshot` getters rebuild only when
`_snapshot is null || _completedAt != DateTime.MaxValue`. While a run is in progress
`_completedAt == DateTime.MaxValue`, and — unlike `AppendJournal` (which sets `_journalSnapshot = null`) —
`AppendEquity`/`AppendTrade` do **not** invalidate their snapshot. So the first `GetEquity`/`GetTrades`
caches a *partial* list and every subsequent read returns that same stale list. The condition is also
inverted vs intent: once completed it re-sorts the whole bag on *every* read (wasteful), but while
running it never refreshes.
*File:* `src/TradingEngine.Infrastructure/Caching/RunDataCache.cs:83-105`, `:15-26`.
*Effect:* the live equity chart / trades table served cache-first (`RunQueryService.GetRunEquityAsync`,
`GetRunTradesAsync`) stop updating mid-run — the exact UX the cache was built to fix.

**B2 — `MarkCompleted` and `Evict` are never called in production** (only in unit tests —
`grep` shows the only callers are `RunDataCacheTests.cs`). Two consequences:
  1. Reinforces B1 — a run never transitions to "completed", so even the completed-run branch never runs.
  2. **Memory leak** — every run's `RunEntry` (including the *unbounded* equity/trades/bars bags) stays in
     `_runs` for the lifetime of the web process. Over many runs this is steady GC-pressure growth and is a
     plausible contributor to "slow in general."
*Expected caller:* `BacktestOrchestrator.RunAsync` `finally` block (`BacktestOrchestrator.cs:436-477`)
already tears down the run — it should `MarkCompleted` there and schedule eviction. It does neither.

**B3 — The bars cache is a dead write / pure memory leak.** `IRunDataCache.AppendBar` fills
`ConcurrentBag<Bar> Bars`, but the interface has **no `GetBars`** and nothing reads the bag.
`GetRunBarsAsync` reads the **journal** cache (`GetJournal`), not bars. So every bar of every backtest
(millions on M1 / multi-year) is copied into RAM and never read or evicted.
*Files:* `RunDataCache.cs:28-32,81`; `BufferedBarWriter.cs:75-79`; `IRunDataCache.cs` (no getter).

**B4 — Trade list order flips depending on cache hit vs miss.** Cache path sorts
`Trades.OrderBy(t => t.ClosedAtUtc)` **ASC** (`RunDataCache.cs:101`); DB fallback sorts
`.OrderByDescending(t => t.ClosedAtUtc)` **DESC** (`RunQueryService.cs:150`). The same run renders
newest-first or oldest-first purely based on whether it's still in cache. (Equity is consistent — both ASC.)

### Coverage gaps — reads that still hit SQLite during a run

**G1 — Run detail `/api/runs/{id}` is never cached.** `GetRunAsync` always calls
`_runRepo.GetByIdAsync` → DB (`RunQueryService.cs:77-120`). This is the live monitor's heartbeat poll.
The orchestrator already holds the authoritative in-memory `BacktestRunState` (`_runs`) plus
`GetCurrentProgress` — running runs can be answered from memory with zero DB I/O.

**G2 — DailyPnL & Analytics always hit DB, and without `AsNoTracking()`.**
`GetRunDailyPnLAsync` / `GetRunAnalyticsAsync` do `_db.Trades.Where(...).ToListAsync()` with change
tracking on (`RunQueryService.cs:202-239`) — extra allocations + tracker overhead, and full DB reads
if a report/analytics tab is open mid-run. Both can be computed from cached trades on a cache hit;
at minimum add `AsNoTracking()`.

### Efficiency / design

**E1 — O(N) work per poll.** Every cache read re-sorts + re-materializes the full dataset
(`OrderBy(...).ToList()` + DTO projection); `/bars` additionally re-groups records into a dictionary and
rebuilds narratives on every call (append nulls the journal snapshot each 500-batch). With frequent
polling and growing N this is real threadpool CPU and partly defeats the "fast" goal. Fix with
**cursor-based incremental reads** (`sinceSeq` / `sinceTs`) so the UI fetches only new points.

**E2 — No memory bound per run.** Even with eviction-on-complete, one long run can hold huge unbounded
equity/trade collections. Add soft caps + equity downsampling (a chart can't render 100k points anyway).

**E3 — Unguarded snapshot field publication.** Non-atomic check-then-assign of
`_equitySnapshot`/`_tradeSnapshot`/`_journalSnapshot` across concurrent readers is benign today (worst
case a redundant rebuild) but should be tidied in the B1 rewrite (lock or `Volatile`/immutable swap).

### Flagged, not necessarily fixed here

- **R1 (honesty):** WAL already prevents reader-vs-writer blocking. Part of "frontend unresponsive" is
  almost certainly **client-side**: SignalR broadcast frequency + Angular re-render + O(N) polling.
  P0 measures this so we don't over-invest in server caching.
- **R2:** `BufferedBarWriter` drops bars (`DropOldest`, 10k channel) under a fast backtest → silent
  **DB bar loss**. Pre-existing, not cache-related, but relevant to "lots of writes." Note only.

---

## Owner decisions (answer before P3+)

- **D1 — Poll vs push for live deltas.** Option A: keep polling but make it incremental (cursor params,
  P3). Option B: extend the existing `RunProgressBroadcaster` SignalR envelope to carry equity/trade/
  journal deltas and drop live polling entirely (P6, larger frontend change). *Recommend A first
  (smaller, no contract churn), B as a fast-follow.*
- **D2 — Bars cache.** Remove the dead bar bag entirely (P2), or repurpose it into a bounded OHLC ring
  consumed by the still-uncached `BarQueryService` price chart? *Recommend remove now; only add an OHLC
  cache if P0 shows the price-chart read is a measured contention source.*
- **D3 — Eviction grace + caps.** Default: `MarkCompleted` on run end, evict completed runs after 60s,
  hard cap of N=8 resident runs (evict oldest completed), equity soft-cap 20k points/run with
  downsampling. Confirm the numbers.

---

## Phases (failing-test-first, each independently gated)

### P0 — Measure first (no product change)
- Add a lightweight timing log (or reuse `Engine:Diagnostics`) around each `/api/runs/{id}`,
  `/trades`, `/equity`, `/bars` handler: elapsed ms + `cacheHit` bool + row count.
- Drive one representative run (`/run-shamshir` skill) with the report + live monitor open; capture where
  wall-time actually goes (server handler time vs SignalR volume vs client render).
- **Gate:** a short `docs/iterations/iter-cache-reads-2/MEASURE.md` with before-numbers. This baselines
  every later phase and validates/necessity-checks D1/D2.

### P1 — Fix the snapshot lifecycle (B1 + B2)  ← highest value
- **Test first:** unit test — append 3 equity/trades, read, append 2 more, read again ⇒ second read
  returns 5 (currently returns 3). Add the symmetric journal test as a guard against regression.
- Invalidate `_equitySnapshot`/`_tradeSnapshot` on `AppendEquity`/`AppendTrade` (mirror the journal path),
  and rebuild-if-null; freeze (build-once) only after `MarkCompleted`.
- Call `MarkCompleted(runId)` in `BacktestOrchestrator.RunAsync` `finally` (all terminal paths:
  completed/failed/cancelled). Also call it in `CTraderListenService` on session end.
- **Gate:** new tests green; existing 19 cache tests + 290 unit green; a driven run shows the live equity
  chart advancing past the first poll (P0 harness).

### P2 — Kill the dead bar cache (B3)  [D2]
- Remove `AppendBar` + `Bars` from `IRunDataCache`/`RunDataCache`; drop the `_cache.AppendBar` calls in
  `BufferedBarWriter.FlushBatchAsync`. (Or, if D2=repurpose, replace with a bounded OHLC ring + `GetBars`.)
- **Gate:** builds; no reference to `AppendBar`/`GetBars` left unless intentionally added; unit green;
  memory of a long run no longer grows with bar count (spot-check via P0 process memory).

### P3 — Trade order parity + read hygiene (B4 + G2)
- Make the cache trades order match the DB (`ClosedAtUtc` DESC) — pick ONE canonical order and assert it
  in a test that runs both the cache-hit and cache-miss path for the same data.
- Add `AsNoTracking()` to `GetRunDailyPnLAsync` / `GetRunAnalyticsAsync`; on cache hit compute both from
  `GetTrades` instead of querying DB.
- **Gate:** parity test green; analytics/daily-pnl produce identical output cache-hit vs cache-miss.

### P4 — Serve run detail + runs list from memory for RUNNING runs (G1)
- In `GetRunAsync`, if the orchestrator has the run in `_runs` (still running), project the response from
  `BacktestRunState` (+ cached trades for stats) and skip `GetByIdAsync`. Completed/historical → DB as now.
- Wire `InvalidateRunsCache()` on run start/complete (already defined, never called —
  `RunQueryService.cs:75`) so the 2s list cache flips promptly instead of only self-healing.
- **Gate:** driven run — `/api/runs/{id}` issues **zero** SQLite reads while running (verify via P0 log);
  values match the post-completion DB row.

### P5 — Eviction + memory bounds (B2 tail + E2)  [D3]
- Background sweeper (hosted `IHostedService` in the Web host, NOT the kernel): evict completed runs after
  the grace window; enforce a hard cap of resident runs (evict oldest completed first); equity soft-cap +
  downsample on append.
- **Gate:** unit test — mark completed + advance a fake clock ⇒ entry evicted; cap test evicts oldest;
  process memory flat across 10 sequential runs (P0 harness).

### P6 — Incremental / cursor reads (E1)  [D1]
- Add `sinceSeq` (journal), `sinceTs`/`afterIndex` (equity), `afterId` (trades) query params; cache returns
  only the tail. Frontend (`web-ui`) appends deltas to its in-memory series instead of replacing.
- If D1=B: fold equity/trade/journal deltas into the SignalR `RunProgress` envelope and stop live polling.
- **Gate:** a live poll transfers O(delta) not O(N) (P0 log shows flat per-poll payload as N grows); chart
  still renders identically to a full reload.

---

## Definition of Done (per `project-code-standards`)
- `dotnet build` clean; `dotnet test tests/TradingEngine.Tests.Unit` green (incl. new tests).
- Golden replay byte-identical (`tests/TradingEngine.Tests.Simulation` GoldenReplay) — proves no kernel drift.
- One real driven run via the `run-shamshir` skill with report + live monitor open: equity/trades advance
  live, `/api/runs/{id}` does no DB read while running, process memory flat across repeated runs.
- `MEASURE.md` shows after-numbers beating the P0 baseline.

## File map (expected touch set)
| File | Phase |
|------|-------|
| `src/TradingEngine.Infrastructure/Caching/RunDataCache.cs` | P1,P2,P3,P5,P6 |
| `src/TradingEngine.Domain/Interfaces/IRunDataCache.cs` | P2,P6 |
| `src/TradingEngine.Web/Services/RunQueryService.cs` | P3,P4,P6 |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | P1,P4 |
| `src/TradingEngine.Web/Services/CTraderListenService.cs` | P1 |
| `src/TradingEngine.Infrastructure/Caching/BufferedBarWriter.cs` | P2 |
| `src/TradingEngine.Web/Configuration/ServiceRegistration.cs` (sweeper) | P5 |
| `web-ui/src/app/...` (delta append) | P6 |
| `tests/TradingEngine.Tests.Unit/Cache/RunDataCacheTests.cs` | P1,P2,P3,P5 |
