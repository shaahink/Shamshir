# Phase 0 — Backbone & Data Correctness

**Track:** SERIAL foundation. Nothing else starts until the Phase-0 exit gate is green.
**Structure:** stays in the *current* project layout (the reorg is Track B, after this).
**Principle:** **discovery first, then lock with gates.** Use the cTrader path (the ground-truth venue)
to *surface* data/streaming/flow bugs, fix them, then pin everything with a credential-free
reconciliation gate so the later big-bang reorg can't silently change a number.
**Executor:** DeepSeek/OpenCode v4 Pro agent. Slices are ordered by dependency — do them in order.

See `MASTER_PLAN.md` §1 (findings F-1…F-6) and §2.3 (data backbone) for context.

---

## P0.1 — cTrader-vs-DB comparison harness  ⟵ DISCOVERY, do this first

**Why first (owner directive):** the cTrader CLI report is the closest thing to ground truth. Diffing
it against what we persisted for the *same* backtest is the fastest way to expose where our streaming,
flow, or persistence diverges from reality. This is a *diagnostic*, run locally with credentials — it is
**not** part of the credential-free CI gate (that's P0.4).

**Do:**
1. Capture the cTrader CLI **JSON** report (not just `report.html`) to the per-run results dir; record
   `ReportJsonPath` + `ReportHtmlPath` on the run. (This is the old P0.7 — now a prerequisite here.)
2. Define a small DTO for the cTrader JSON (trades list, net/gross, commission/swap, max DD, trade
   count, per-trade entry/exit/price).
3. Build a `CompareToCtrader` harness: run a backtest through the cTrader path AND the same
   symbol/period/balance through our engine; diff cTrader report vs our DB (`Trades`, `EquitySnapshots`,
   `PipelineEvents`):
   - **Structural diffs = bugs**: trade-count mismatch beyond tolerance, missing/null cost fields,
     dropped executions, missing bars/events, wrong ordering, unmapped exit reasons.
   - **Numeric diffs within tolerance = expected**: fill price differing by ~spread, tiny PnL deltas
     from fill-model differences. Define explicit tolerance bands so signal isn't drowned by noise.
4. Emit a discrepancy report (the same shape Track C/A will later render).

**Outcome:** a triaged list of data/streaming/flow defects. Feed structural defects into P0.2.

**Gate:** harness runs locally (credentialed), produces a categorized diff; structural-diff count is the
Phase-0 work list.

---

## P0.2 — Triage & fix the surfaced data/streaming/flow defects

**Do:** fix each structural discrepancy from P0.1. Expected buckets (pre-seeded from findings):
- streaming/flow: dropped bars/executions, channel backpressure loss, event-ordering, RunId stamping
  gaps;
- persistence: null cost fields on close, missing journal entries, exit-reason mapping;
- data shape: trade fields not populated, equity snapshots missing/sparse.
Many of these are addressed structurally by P0.5 (hot-path isolation, lossless) and P0.7 (typed events);
link each fix to the slice that owns it rather than patching ad hoc.

**Gate:** re-run P0.1 — structural-diff count drops to zero (or to a documented, justified residue with
tolerance rationale).

---

## P0.3 — One canonical stats source of truth

**Problem (F-2):** NetPnL/WinRate/ProfitFactor/MaxDD are computed in `BacktestOrchestrator.GetTradeStatsAsync`
*and* `Runs/Report.cshtml.cs`; they can disagree (the report already self-flags `Reconciliation`).

**Do:**
1. `RunStatsProjection` (pure, no EF/IO) in `Services`/`Application`: input = trades + account snapshots +
   pipeline events + initial balance; output = a `RunStats` record (NetPnL, GrossPnL, CommissionTotal,
   SwapTotal, ReturnPct, WinRatePct, ProfitFactor, MaxDdPct [intra-bar if snapshots present else
   realized], MaxDailyDdPct, R-distribution, PerStrategyFunnel, BreachTimeline, TotalTrades,
   WinningTrades, AvgHoldSeconds).
2. Move `Runs/Report.cshtml.cs:BuildFunnel` into the projection (keep the double-count guard).
3. Compute once at run end, persist a `RunStats` row (table from P0.6). All readers read the row — no
   recompute. Delete the inline math in the orchestrator and the report page.

**Gate:** `RunStatsProjectionTests` pin every metric incl. the intra-bar-vs-realized MaxDD branch. Unit green.

---

## P0.4 — Internal reconciliation gate (CI safety net)  ⟵ credential-free

**Do:**
1. `ReconcileRun` independently recomputes from raw rows and asserts:
   `RunStats.NetPnL == Σ trade net`; `Initial + Σ trade net == last snapshot equity`;
   `funnel.Closes == trade count`; `MaxDdPct == independent peak-to-trough walk`;
   `Σ gross − Σ commission − Σ swap == Σ net`.
2. `RunReconciliationGateTests` drives a small known **replay** backtest end-to-end and asserts all
   invariants. Add to the fast/CI gate set.

**Gate:** `RunReconciliationGateTests` green. Must stay green through every later PR (esp. Track B).

---

## P0.5 — Engine hot-path isolation: non-blocking + lossless  ⟵ correctness, NOT perf

**Owner requirement:** "DB touching and anything other than processing the bar shouldn't slow the engine
— but we shouldn't lose data either." This is a *correctness* property of the channel/queue boundary,
handled here, **separately** from Track D's hot-path/indicator profiling. It also underpins P0.4: if any
critical stream drops, reconciliation fails.

**Do:**
1. **Audit the async boundary.** Confirm the bar-processing thread does **no synchronous DB/IO** — it
   only computes and *enqueues*. Today's handlers (`TradePersistenceHandler`, `PipelineEventWriter`,
   `EquityPersistenceHandler`, `BarEvaluationHandler`) are channel-backed background flushers; verify
   nothing on the bar path awaits a DB write or a network send.
2. **Classify every channel lossless vs droppable** and make it explicit + tested:
   - Lossless (Wait, bounded): executions/fills, trades, equity snapshots, journal/pipeline events,
     venue-status events — **losing these breaks reconciliation and the audit trail.**
   - Droppable (DropOldest): pure live-UI analytics (e.g. a throttled progress sparkline) — and ONLY
     those. (Today `BarEvaluationHandler` is DropOldest@50k — decide: is per-bar eval lossless-required
     for the A3 "why no signal" view? If yes, make it Wait/bounded or batch it.)
3. **Backpressure policy:** for lossless streams, the producer waits on a bounded queue (bounded =
   memory-safe) rather than dropping. The engine continues because consumers batch-write fast; if a
   consumer genuinely can't keep up, the engine slows but **never loses** lossless data. Document the
   chosen trade-off per stream.
4. **Decouple direction (lightweight, full version deferred to Track E):** the engine should *emit*
   events; the system (persistence/journaling/reporting) *consumes* them. Don't do the full
   engine/system split here — just ensure the emit→consume seam exists and is lossless.

**Gate:** a **lossless stress test** — drive a high bar-rate run, assert `Σ persisted trades/executions/
equity-snapshots/journal-events == Σ emitted` (zero loss on lossless streams), while asserting the bar
loop never blocks on a DB write (e.g. handler latency doesn't appear on the bar-processing thread).
Reconciliation gate (P0.4) green.

---

## P0.6 — Faithful run capture (schema)

**Problem (F-3):** `BacktestRunEntity` records single `Symbol`/`Period`; no canonical stats; no
venue/params; multi-symbol runs lose information.

**Do (EF migration — migrations only):**
1. `RunStats` table keyed by `RunId` (1:1) holding the P0.3 output (scalars as columns; funnel/R-buckets/
   breach-timeline as JSON columns).
2. Extend `BacktestRunEntity`: `RunPlanJson` (symbols×timeframes×strategies), `Venue`
   (`Replay|Simulated|CTrader`), `VenueParamsJson` (cTrader args/version/ports), confirm
   `ReportJsonPath`/`ReportHtmlPath` populated (from P0.1).
3. `BacktestRunSummary` + repository round-trip the new fields; persist the real `RunPlan`, not one
   symbol string.
4. Additive migration now (reviewable diffs); squash to single `InitialCreate` in Track B once schema is
   final.

**Gate:** `MigrationTests` green; repository round-trip test; a multi-symbol run's `RunPlanJson` lists
every symbol/TF. Integration green.

---

## P0.7 — Typed venue→engine event/state contract

**Problem (F-1):** sim-clock + counters are parsed from log *strings* (`evt.Message[4..pipeIdx]`, magic
`EventType` switch). Live venue status (connect/handshake/stop/error/finalized) is only in logs (blocks
C1).

**Do:**
1. Typed events for: bar processed (sim-time as `DateTime`, not a parsed substring), signal/order/fill/
   close/rejected/breach counters, venue-status transitions, engine-state changes — sourced from the
   engine, not reconstructed in Web.
2. Orchestrator/progress callback consume typed events; delete the substring parsing and the
   magic-string `TallyEvent` switch.
3. Persist venue-status + engine-state events (extend `EngineEvents`/`PipelineEvents` or a small
   `VenueStatusEvents` table) with `RunId` — the backbone C1 renders later. These are **lossless** (see
   P0.5).

**Gate:** test asserts sim-time + counters come from typed fields (no string parsing); venue-status
events persist and round-trip. Unit + Integration green.

---

## P0.8 — DB unification + test-DB cleanup

**Problem (OPEN-ISSUES D1):** multiple DB locations; the `..\..\..\..\..` walk is duplicated; tests
leave DBs in random folders.

**Do:**
1. One resolver (`IDbPathProvider` or a single bound option) used by `Program.cs`, the orchestrator, and
   `EngineHostFactory` — delete the duplicated `Path.Combine` walks.
2. Single canonical path from config (`Persistence:DbPath`), absolute-resolved once at startup.
3. Test harnesses create DBs under a per-test temp dir and delete on dispose; sweep stray creation sites.

**Gate:** grep shows one DB-path resolution site; a test run leaves no DB outside temp; Integration green.

---

## P0.9 — Footgun & hardcoded-value fixes

**Problem (F-5, OPEN-ISSUES D2):** dev default routes to cTrader; hardcoded `SymbolInfo("EUR","USD")`;
timeframe/symbol/balance defaults may bypass config; BUG-09 governor `OnBar` never called.

**Do:**
1. Default to the credential-free replay venue (`CTrader:UseForBacktest=false` for the default profile;
   keep a documented opt-in cTrader profile). Fail loudly if the cTrader path is chosen without creds.
2. Remove the hardcoded `new SymbolInfo(symbol, …, "EUR","USD", …)` in the NetMQ path — resolve from
   `ISymbolInfoRegistry`/`SymbolCatalog` like the replay path.
3. Audit the UI→engine config path for hardcoded timeframe/symbol/balance (`_ => Timeframe.H1`,
   `?? 100_000m`); honour the request or fail loudly.
4. Fold-in BUG-09: call `ITradingGovernor.OnBar` from `TradingLoop.ProcessBarAsync`; add a test.

**Gate:** a 15m run uses M15 (not silently H1); a USDJPY run uses JPY pip value; governor cooling-off
decrements over bars (test). Suites green.

---

## Phase 0 exit gate (all must be green)

- `dotnet test tests/TradingEngine.Tests.Unit`
- `dotnet test tests/TradingEngine.Tests.Architecture`
- `dotnet test tests/TradingEngine.Tests.Integration`
- `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true"`
- **`RunReconciliationGateTests` (P0.4) green**
- **lossless stress test (P0.5) green**
- A multi-symbol M15 replay run persists a faithful `RunPlanJson` + a reconciling `RunStats`.
- P0.1 cTrader-vs-DB diff (local, credentialed) shows zero structural discrepancies (or documented residue).

Only then cut Phase 1 (contract freeze) and open the parallel worktrees.
