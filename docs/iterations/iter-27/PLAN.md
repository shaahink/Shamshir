# Iter-27 Plan — Verify the UI Fixes, Finish the Equity Curve, Get to Manual-Test Ready

Context: a deep review of `TradingEngine.Web` (see the chat report) found the live Run Monitor felt
"stuck on connected" because the funnel counters, journal feed, and equity were all wired to the
wrong sources — frames *were* arriving, but every number showed `--`/0, so the page looked dead.
The owner clarified they **never asked for a live trading-style dashboard during a backtest** — the
Monitor just needs to honestly show *that work is happening* (progress, sim clock, a working
journal/funnel) and then hand off to the Report. The engine-side iter-26 fixes are already in.

I (the prior session) applied the fixes below. **This iteration is for an agent to VERIFY them,
COMPLETE the one remaining gap (the Report equity curve), FIX the test suites, and confirm the app
is ready for manual testing.** Build + run are expected here (the prior session did neither).

Working rules: build/test the **Web project and the engine** directly (`dotnet build src/TradingEngine.Web`,
`dotnet test tests/TradingEngine.Tests.Unit`, `...Simulation`, `...Integration`, `...Architecture`).
Do **not** touch `aspire/AppHost` (unrelated `NU1903`). Prefer small commits; keep each suite green
before moving on.

---

## Part A — Fixes already applied (VERIFY each compiles + behaves)

| # | Fix | Files | What to verify |
|---|-----|-------|----------------|
| F-RunId | Lifecycle decision records were persisted with `RunId=""` → Report funnel saw 0 fills/closes. `PipelineEventWriter.Record` now stamps `_runId` when the record's is empty. | `Infrastructure/Events/PipelineEventWriter.cs` | After a run, `PipelineEvents` rows for `OrderFilled`/`OrderSubmitted(Accepted)` have the real RunId; Report funnel Fills/Closes > 0. |
| F-Funnel | `TallyEvent` counted `"FILL"/"REJECT"/"BREACH"` — names the engine never emits. Now maps `"EXEC"→Fills`, `"CLOSE"→Closes`, `"REJECTED"→Rejections`, `"BREACH"→Breaches`; journal-enqueue set updated to match. | `Web/Services/BacktestOrchestrator.cs` | Live Monitor funnel counters increment during a run. |
| F-Close | `EffectExecutor` now emits a `"CLOSE"` progress event on **PublishTradeClosed** (every closed trade) and no longer on the close *request* (which double-counted force-closes). | `Host/EffectExecutor.cs` | Closes counter ≈ number of trades; SL/TP exits count too. |
| F-Breach | `AccountProcessor` now emits a `"BREACH"` progress event on entering protection mode (daily + max), wired via new optional `progress`/`runId` ctor params from `EngineRunner`. | `Host/AccountProcessor.cs`, `Host/EngineRunner.cs` | A breach scenario lights the Monitor breach banner + counter, and forces a frame through the throttle. |
| F-Filters | Monitor journal filter buttons used `data-filter="FILL"/"REJECT"`; now `"EXEC"/"REJECTED"` to match `j.event`. | `Web/Pages/Runs/Monitor.cshtml` | Fills/Rejections/Breaches filter tabs actually filter lines. |
| F-Done | Monitor `onDone` now paints the final equity + funnel from the terminal frame (was only updating the CTA). | `Web/Pages/Runs/Monitor.cshtml` | Final totals show on the Monitor without navigating away. |
| F-Equity | Live equity/DD/open-positions were read from `IBrokerAdapter.GetAccountStateAsync`, which returns the **initial balance forever** for replay. Now polled from the engine's in-memory `IAccountSnapshotStore` (`StartEquityPollingAsync(innerHost,…)`), and the final snapshot is captured onto the run state **before** host disposal (`CaptureFinalEquity`). | `Web/Services/BacktestOrchestrator.cs` | Monitor equity/DD/open-positions move during a replay run; final KPI correct. |
| F-Funnel2 | Report per-strategy funnel: removed `BAR_EVAL` inflation of "Signals"; counts the lifecycle `OrderSubmitted(Reason=="Accepted")` (not the dispatcher dupe); fills/closes from lifecycle reasons. | `Web/Pages/Runs/Report.cshtml.cs` | Funnel numbers are plausible (Signals ≈ orders+rejects, not thousands). |
| F-Perf | `PerformanceApiController` was a hardcoded all-zeros stub; now queries `ReportingDbContext.Trades`. | `Web/Api/PerformanceApiController.cs` | `/api/performance` returns real totals. |
| F-Acct | Trade Detail "balance before/after" started from 0; now seeded with the run's `InitialBalance`. | `Web/Pages/Trades/Detail.cshtml.cs` | Before/after are real account balances. |
| F-Clock | Sim clock parse truncated to the date; now keeps the full `yyyy-MM-dd HH:mm` up to `" | "`. | `Web/Services/BacktestOrchestrator.cs` | Monitor sim clock advances intra-day on M1/M5. |
| F-Send | `RunProgressBroadcaster` SignalR sends were unobserved fire-and-forget; now log on fault (new `ILogger` ctor dep). | `Web/Services/RunProgressBroadcaster.cs` | DI still resolves it (registered `AddSingleton`); failures are logged. |

**Compile-risk checklist (verify first):**
- `AccountProcessor` gained two **optional** trailing ctor params — `EngineHarnessBuilder.cs:119` positional call must still compile (it should).
- `RunProgressBroadcaster` gained a required `ILogger` param — only constructed via DI (`Program.cs:19`); no test constructs it (confirmed). 
- `PerformanceApiController` now needs `ReportingDbContext` (registered in `Program.cs:28`) — confirm DI resolves.
- `EngineRunner` passes `progress:`/`runId:` named args to `AccountProcessor` — confirm `_progress` is assigned before the `AccountProcessor` line.

---

## Part B — COMPLETE: Report equity curve (the one gap left open)

**Problem (verified):** in backtest, `IAccountSnapshotStore` is a `BufferedEquitySink` registered as a
**singleton inside the inner engine host** (`Host/EngineServiceCollectionExtensions.cs:177-185`). The
Web host does **not** register it, and the inner host is disposed at end of run — so
`RunProjection.GetRunAsync` / `Report.cshtml.cs` read `null` and the **Report equity-curve chart is
always empty** (`EquityCurveJson = "[]"`). The Report's NetPnL/WinRate/MaxDD are computed from the
trades table and *do* work; only the curve viz is missing.

**Decision D-Equity:** persist the run's equity snapshots to the shared DB so the Report (a separate
request) can read them. Two acceptable approaches — pick the smaller diff:
1. **Persist on capture.** In `CaptureFinalEquity` (or a sibling), read all buffered `AccountSnapshot`s
   from the inner host and batch-save them to a run-scoped table the Web can query by `runId`. The
   existing `IEquityRepository` is date-ranged, not run-scoped — either add a `runId` column / 
   `GetByRunIdAsync`, or add a tiny `AccountSnapshotEntity` + repo. Then have `RunProjection`/`Report`
   read the curve from the DB instead of `IAccountSnapshotStore`.
2. **Register a persistent snapshot store in backtest.** Change `AddEventInfrastructure` so backtest
   also writes snapshots to the DB (alongside the in-memory buffer used for live polling), then have
   the Web read from the DB-backed `IAccountSnapshotStore`.

**Gate B:** run a replay backtest that produces trades → open the Report → the equity-curve chart
renders a non-empty, monotone-ish line that ends at `initialBalance + netPnL`.

---

## Part C — Finish/cleanup (small, do while verifying)

1. **Dead `PopulateEquityStateAsync`** (`BacktestOrchestrator.cs`): now superseded by `CaptureFinalEquity`
   (it early-returns because the Web scope has no snapshot store). Remove the method + its call, or
   repoint it at the DB store from Part B.
2. **Vestigial SSE** `/api/backtest/{runId}/stream` + the only writer (`doneJson`): nothing consumes it
   (the Monitor uses SignalR). Either delete the endpoint + `BacktestProgressStore` writes, or leave a
   one-line comment that it's intentionally unused. Don't leave it looking load-bearing.
3. **Counter thread-safety**: `state.Signals++` etc. in `TallyEvent` run on `Progress<T>` callbacks,
   which can fire on multiple thread-pool threads (no captured SyncContext). Make the six counters
   `Interlocked`/`int` increments or lock them like `RecentJournal` already is.
4. **`BarsTotal` estimate** ignores weekends/market gaps → progress % limps to the finish. Low priority;
   either accept it or clamp display copy ("~"). Don't over-engineer.
5. **Win-rate naming**: `WinRatePct` holds a *fraction* in the DB but a *percent* in `Report.cshtml.cs`.
   Currently each page is self-consistent (DB→`P1`/`×100`, Report→`F1%`). Don't "fix" one side without
   the other — just confirm no page shows a 100× error after Part A.

---

## Part D — Fix tests + green suites

1. **Build everything**; resolve any fallout from the iter-26 engine changes (CloseOpenPosition.OrderId
   rename, the new `ClosePositionAtAsync`, EffectExecutor's moved `"CLOSE"` progress) and the Part A
   changes.
2. **`RunProgressContractTests`** (Integration): I did **not** change the `RunProgress` shape, so the
   camelCase contract should still pass — confirm. If a test asserted the old funnel/counter values,
   update expectations to the corrected mapping.
3. **Add focused tests** for the data fixes (these are cheap and high-value):
   - `PipelineEventWriter.Record` stamps the writer's runId when the record's is empty (unit).
   - `TallyEvent` mapping: feed `EXEC/CLOSE/REJECTED/BREACH` events and assert the right counters move
     (may require making `TallyEvent` internal + `[InternalsVisibleTo]`, or testing via the public
     `Start`→progress path).
   - Report funnel: a timeline with one BAR_EVAL-heavy strategy yields Signals = orders+rejects, not
     the bar count.
4. **Run** Unit + Simulation + Architecture + Integration; keep the 28/28 FTMO sim tests green.

---

## Part E — Manual-test readiness checklist (the acceptance)

Run a real replay backtest from the UI and confirm, end to end:
1. **New Backtest → Start** → redirected to Monitor.
2. Monitor leaves "connecting/waiting" within a second or two and **visibly moves**: sim clock advances
   (with time, not just date), progress %/speed/elapsed climb, **equity & DD change**, funnel counters
   (Signals→Orders→Fills→Closes) increment, journal feed streams lines, filter tabs work.
3. A breach scenario lights the breach banner + counter.
4. On completion the Monitor shows final totals and a working **View Report** CTA.
5. **Report**: KPIs (NetPnL, WinRate, MaxDD, ProfitFactor), a non-empty **equity curve** (Part B),
   a plausible per-strategy funnel, and the trade table.
6. **Trades** list filters/sorts; **Trade Detail** shows a real candle chart and correct
   balance-before/after.
7. `/api/performance` returns real numbers.

## Definition of Done
Part A verified (builds + behaves), Part B gate met, Part C items closed or consciously deferred with a
note, all test suites green, and the Part E checklist passes against a real run. Update
`docs/OPEN-ISSUES.md` with the UI findings marked `✅ Fixed (Iteration 27)`.
