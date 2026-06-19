# Iter-33 — Redesign Program: Master Plan

**Status:** PLAN WRITTEN (not executed) — 2026-06-18
**Author:** analysis pass over the whole repo (docs + code verified, not assumed)
**Base branch:** `iter/31-costs-journal` → cut a new `iter/33-redesign` integration branch
**Audience:** the implementing agent(s). This is a *program*, not a single iteration. It is split
into one serial foundation (Phase 0 + Phase 1) and four parallelizable tracks (A–D), each with its
own detail file in this folder.

> This master file owns: the locked decisions, the verified findings, the target architecture, the
> sequencing/worktree map, and the cross-cutting Definition of Done. Each track file owns its phased
> steps and machine-checkable gates. Read this file first, then your track file.

---

## 0. Locked decisions (owner-confirmed 2026-06-18)

| # | Decision | Choice | Consequence |
|---|----------|--------|-------------|
| D-A | Frontend migration scope | **Full Angular SPA, drop Razor** | ASP.NET becomes a pure JSON + SignalR API. No Razor Pages in the end state. |
| D-B | Backend restructure | **Big-bang vertical-slice reorg** | One structural pass into feature slices + CQRS-without-mediator + Result pattern. Tests are the safety net; behaviour must not change during the move. |
| D-C | Correctness gate | **Both; cTrader diff used as early discovery** | Layer 2 (cTrader CLI JSON/HTML parity diff vs our DB) runs **first, in Phase 0**, as the diagnostic that *surfaces* data/streaming/flow bugs to fix (owner directive). Layer 1 (credential-free internal reconciliation, recompute vs stored) is the durable **CI gate**; Layer 2 reverts to on-demand diagnosis once the system is clean. |
| D-D | Frontend start point | **After the API contract is frozen** | Angular track begins once Phase 1 publishes the OpenAPI + SignalR contract. |

Carried-over decisions still in force (from iter-31/32 handover): DB is the canonical config source
(JSON = seed/export); deep-merge override granularity; `decimal` money math + `Math.Floor` lots;
`TradeCostCalculator` is the single cost source; `EntryPlanner` is the single order-entry policy;
`IEngineClock` not `DateTime.UtcNow`; Serilog only; EF migrations only.

---

## 1. Why this program exists — verified findings

Each finding below was confirmed by reading the actual code on `iter/31-costs-journal`, not the docs
(the docs have already drifted — see F-0).

### F-0 — Reference docs drifted from code
`SYSTEM-REFERENCE.md` / `CODE-MAP.md` (dated 2026-06-18) describe `Backtests/Run.cshtml`,
`Backtests/Progress.cshtml`, `Monitor.cshtml`. Reality: a newer `Pages/Runs/{Index,Report,Monitor,Analyzer}`
stack co-exists with an older `Pages/Backtests/{Index,Detail,New}` stack. `_Layout.cshtml` links only
`/runs`, `/backtests/new`, `/trades`, `/strategies`, `/compliance`, `/events` → `Backtests/Index` and
`Backtests/Detail` are **dead**. *Implication: the redesign also retires drift by making the SPA + API
contract the single description of the surface.*

### F-1 — Web "code-behind is off" (architecture)
- `Web/Services/BacktestOrchestrator.cs` is an **~850-line god class**: run-state, venue routing,
  cTrader subprocess launch, equity polling, trade-stat recomputation, effective-config resolution,
  HTML report capture, SignalR projection — all in one type.
- `Web/Program.cs` is a flat ~30-line DI dump; the brittle
  `Path.Combine(AppContext.BaseDirectory, "..","..","..","..","..","data","trading.db")` solution-root
  walk is **duplicated** in `Program.cs` and `BacktestOrchestrator.cs` (3+ copies).
- Data crosses layers as **stringly-typed log strings**: the live sim-clock is parsed out of
  `evt.Message[4..pipeIdx]` ("Bar 2024-… | close=…"). Counters key off magic event-type strings.
- 13 API controllers + ~10 Web services with overlapping responsibilities and no command/query seam.

### F-2 — Stats are computed in ≥3 places that disagree (the "discrepancies" you feel)
- `BacktestOrchestrator.GetTradeStatsAsync` → MaxDD from a **realized trade-walk** → persisted on
  `BacktestRunEntity.MaxDrawdownPct`.
- `Runs/Report.cshtml.cs` → recomputes NetPnL/WinRate/ProfitFactor from the **Trades table**, and MaxDD
  from **either** persisted intra-bar `AccountSnapshots` **or** a trade-walk fallback — a *different*
  number than the run entity.
- The report page even renders a `Reconciliation` block that asserts `NetPnL == Σ trade net` /
  `== equity end` / `funnel closes == trade count`. **The system already knows its numbers can fail to
  reconcile.** Today that knowledge is ad-hoc per page, not a gate.

### F-3 — Schema is closer than feared; most gaps are *rendering*, a few are real
- Already present: `TradeResultEntity` has Gross/Commission/Swap/Net, Pips, R, MAE/MFE, Duration.
  `PipelineEventEntity` has `DetailJson/NormalizedKind/Reason/StrategyId`. `BarEvaluationEntity` has
  per-bar signal + reason. → A1–A5 (reporting) are mostly "render what exists."
- Real schema gaps (need migration):
  - `BacktestRunEntity` stores a single `Symbol`/`Period` only — **multi-symbol / run-plan is not
    faithfully recorded** at run level.
  - **No canonical persisted stats** record → every reader recomputes → F-2.
  - **No venue/run-parameter capture** (cTrader CLI args, version, ports, live venue status events) →
    blocks C1 (venue status) and B3 (cTrader param tracking).
  - cTrader path copies only `report.html` — **JSON report capture is not wired** → blocks the parity
    gate (D-C / your "capture JSON/HTML and compare to our DB" idea).

### F-4 — Performance suspects (confirmed in code)
- A hard `await Task.Delay(5_000)` settle in `BacktestOrchestrator.RunEngineReplayAsync` (every run
  pays 5s).
- Per-bar indicator recompute path (`IndicatorSnapshotService`) + per-bar DB flushes.
- Lossy live feed: 30-item in-memory `RecentJournal` ring (`BacktestOrchestrator`), 500-frame equity
  sparkline freeze (Monitor).

### F-5 — Footgun in the default config
`appsettings.Development.json` ships `CTrader:UseForBacktest = "true"` and live credentials, so a fresh
`dotnet run` routes backtests to the **credential-requiring cTrader path**, not the credential-free
replay venue. Also a **hardcoded** `new SymbolInfo(symbol, …, "EUR","USD", …)` in the NetMQ path,
regardless of the actual symbol (the D2 "hardcoded values" issue).

### F-6 — Engine kernel is sound but half-wired (do NOT blow it up)
The trading kernel (`Domain`, `Engine`, `Strategies`, `Risk`, `Services`, `Infrastructure`, `Host`) is
well-tested (207 unit + sim suites) and is the part that works. Known live-wiring gap: BUG-09 governor
cooling-off (`TradingLoop` calls `signalGate?.OnBar`, never `ITradingGovernor.OnBar`). The reorg is an
**application/API-layer** restructure; the kernel is refactored only where correctness fixes require it.

---

## 2. Target architecture

### 2.1 Backend — vertical slices + CQRS without a mediator

Keep the **trading kernel projects unchanged in responsibility** (Domain, Engine, Strategies, Risk,
Services, Infrastructure, Host). Restructure the **application + web** layers:

```
TradingEngine.Application                ← becomes the vertical-slice layer
  Common/
    Result.cs / Result<T>                ← Result pattern (no exceptions for control flow)
    ICommandHandler<TReq,TRes>           ← write seam
    IQueryHandler<TReq,TRes>             ← read seam
    Behaviors/                           ← cross-cutting via DECORATORS (no MediatR):
      LoggingBehavior, ValidationBehavior, ExceptionToResultBehavior, TimingBehavior
    Dispatch/                            ← thin generic dispatcher OR direct handler injection
  Features/
    Backtests/   StartBacktest/ CancelRun/ GetRunReport/ GetRunJournal/ StreamRunProgress/ ...
    Strategies/  ListConfigs/ GetConfig/ UpsertConfig/ ValidateConfig/ ...
    LiveRun/     GetVenueStatus/ StartLive/ StopLive/ ...
    Reporting/   GetRunStats/ GetFunnel/ GetTradeDetail/ GetEquityCurve/ ExportReport/ ...
    Verification/ ReconcileRun/ CompareToCtrader/ ...
  each slice folder = Command/Query record + Handler + Validator + response DTO(s), co-located.

TradingEngine.Api  (renamed/rebuilt from TradingEngine.Web)
  Endpoints/                             ← minimal-API endpoint groups, one per feature
    map HTTP → handler.Handle(...) → Result → ProblemDetails / 200
  Hubs/RunHub.cs                         ← SignalR push (typed envelopes, no stringly-typed parsing)
  Program.cs                             ← composition root: AddApplication(), AddInfrastructure(),
                                           AddEngine() — each an extension method, no path duplication
  (no Razor Pages, no wwwroot UI)
```

**Cross-cutting without mediator:** register each handler, then wrap with decorator registrations
(`Scrutor`-style `Decorate` or manual factory) so every command/query flows through
Logging → Validation → ExceptionToResult → Timing without a central mediator. Endpoints depend on the
concrete handler interface, not a `IMediator`.

**Result pattern:** handlers return `Result<T>` (`Ok` / `Fail(Error)`); the endpoint layer maps
`Error` → RFC7807 `ProblemDetails`. No throwing for expected failures (validation, not-found, etc.).

### 2.2 Frontend — Angular SPA

```
web-ui/  (new Angular workspace, sibling to src/)
  Angular (standalone components) + Tailwind + TypeScript
  Feature modules mirroring backend slices: backtests, runs (monitor+report), strategies, live, trades
  Core: typed API client GENERATED from the OpenAPI contract (Phase 1) — no hand-written DTOs
  State: a small store per feature (signals-based) + SignalR service for live push
  Charts: keep Lightweight-Charts (v4 API already in use) wrapped in a chart component
```

Dev: Angular dev server proxies `/api` + `/hubs` to the .NET API. Prod: `ng build` output served as
static files by the API (or any static host) — decoupled either way.

### 2.3 Data / schema (the backbone)

- **One stats source of truth:** a `RunStatsProjection` computed once (Phase 0) and persisted in a new
  `RunStats` table (or columns on `BacktestRunEntity`); all readers (API, report, exports) read it. No
  recompute in pages/orchestrator.
- **Faithful run capture:** persist the full `RunPlan` (symbols × timeframes × strategies), effective
  config, venue, and venue parameters with each run.
- **Venue → engine event/state contract:** a typed, persisted event stream (extend `EngineEvents` /
  `PipelineEvents`) covering venue status transitions (connect, handshake, stop-requested, error,
  finalized) and engine state changes — so live + backtest both have an inspectable timeline.
- **One DB location:** single configurable path; tests use isolated temp DBs that are cleaned up.

### 2.4 The API contract (the seam that enables parallelism)

Phase 1 freezes a versioned **OpenAPI** document for REST + a **documented SignalR envelope** for live
push. Both tracks (A frontend, B backend) build against it. Contract tests (extend
`RunProgressContractTests` / `WebSmokeTests`) pin it.

### 2.5 Engine vs System — hot-path isolation now, full decoupling later

There are two things, coupled today:

- **Trading engine** = the per-bar decision+execution core: indicators → `strategy.Evaluate` →
  `EntryPlanner` → risk gate → `OrderDispatcher` → venue submit → `PositionTracker`/reducer → close +
  cost. Deterministic, computation-only, ideally I/O-free.
- **Trading system** = everything around it: run lifecycle/orchestration, daily-reset scheduling, config
  loading/seeding, persistence handlers, journaling, equity snapshots, broadcasting, governor/rotation/
  experiments, reporting, host composition.

Today they are entangled (e.g. `EngineWorker`/`Host` drive bars *and* own persistence handlers + run
context). Two requirements follow, at different priorities:

1. **Hot-path isolation (do NOW — correctness, Phase 0 P0.5).** The bar-processing thread must do **no
   synchronous DB/IO** — it computes and *enqueues*; the system consumes off-thread via bounded channels.
   Critically, this must be **lossless** for audit/reconciliation streams (executions, trades, equity,
   journal, venue-status) — those use bounded `Wait` backpressure, never `DropOldest`; only pure live-UI
   analytics may drop. This is the owner's "DB shouldn't slow the engine, but don't lose data" rule, and
   the reconciliation gate (P0.4) depends on it. It is **separate from Track D** (perf/indicator hot
   points).
2. **Full engine/system decoupling (DEFERRED — Track E).** Clean module boundary: the engine emits
   domain events through a published port; the system subscribes as independent consumers; ideally
   separate assemblies with the engine free of host/persistence concerns. Lower priority — do after the
   core program lands. P0.5 only establishes the lossless emit→consume *seam*; Track E makes it a clean
   architecture.

---

## 3. Sequencing & worktree map

```
              ┌─────────────────────────────────────────────────────────┐
  SERIAL ───► │ PHASE 0  Backbone & data correctness  (current structure)│
              │  - cTrader-vs-DB diff FIRST (discovery) → fix data/flow  │
              │  - hot-path isolation: non-blocking + LOSSLESS channels  │
              │  - canonical RunStatsProjection + RunStats table         │
              │  - LAYER-1 internal reconciliation GATE (credential-free)│
              │  - schema: run-plan/venue/params capture, JSON report     │
              │  - typed venue→engine event/state contract               │
              │  - DB-path unification + test-DB cleanup                  │
              │  - F-5 footgun fix (default replay venue, no hardcodes)  │
              └─────────────────────────────────────────────────────────┘
                                    │ gate: all suites + reconciliation green
                                    ▼
              ┌─────────────────────────────────────────────────────────┐
  SERIAL ───► │ PHASE 1  Freeze API contract (OpenAPI + SignalR envelope)│
              └─────────────────────────────────────────────────────────┘
                                    │ contract published + contract tests green
        ┌───────────────────────────┼───────────────────────────┬───────────────┐
        ▼                           ▼                           ▼               ▼
  ┌───────────┐             ┌───────────────┐           ┌───────────┐   ┌───────────┐
  │ TRACK B   │  worktree   │ TRACK A       │ worktree  │ TRACK D   │   │ TRACK C   │
  │ backend   │  wt-backend │ Angular SPA   │ wt-frontend│ perf     │   │ features  │
  │ big-bang  │             │ (full UI)     │           │ (D3 etc.) │   │ (lands on │
  │ reorg     │             │               │           │           │   │  B+A)     │
  └───────────┘             └───────────────┘           └───────────┘   └───────────┘
        │                           │                         │               │
        └──────────── integrate on iter/33-redesign; Track C features merge as B & A stabilize
```

**Parallelism rules (so worktrees don't collide):**
- Track B (backend reorg) **only moves/relabels code behind the frozen contract** — it must not change
  the JSON/SignalR shapes Track A consumes. If a shape must change, change the contract first (Phase 1
  doc + contract test), then both tracks react.
- Track A (frontend) lives entirely under `web-ui/` (new folder) → zero file overlap with Track B.
- Track D (perf) touches engine/host hot paths (`IndicatorSnapshotService`, replay loop, flush
  batching) — coordinate with B on `Host`/`Infrastructure` files; prefer landing D before B starts the
  reorg of those files, or sequence D's engine-side changes into B.
- Track C (features) builds on the *new* structure → starts as B and A reach "skeleton green," not
  before. Each feature is a vertical slice (back) + a feature module (front) → naturally parallel once
  the skeletons exist.

**Worktree commands (suggested):**
```
git worktree add ../shamshir-backend  iter/33-track-b-backend
git worktree add ../shamshir-frontend iter/33-track-a-frontend
git worktree add ../shamshir-perf     iter/33-track-d-perf
# integrate each track branch into iter/33-redesign via PR with green gates
```

### 3.1 Execution guide for the DeepSeek / OpenCode v4 Pro agent

Run order. "∥" = may run in parallel worktrees; everything else is sequential. Do not advance past a
gate that is red.

1. **Phase 0** (single worktree, `iter/33-redesign`). Slices P0.1→P0.9 in order. Within Phase 0:
   P0.5 (hot-path/lossless) and P0.6/P0.7 (schema/typed events) may proceed ∥ once P0.1–P0.4 are done,
   but land them serially if unsure — Phase 0 is small and correctness-critical. **Gate:** Phase-0 exit
   gate green.
2. **Phase 1** (same branch). Freeze the contract. **Gate:** OpenAPI committed + contract tests green +
   TS client compiles.
3. **Open worktrees and run in parallel:**
   - **Track B** (`wt-backend`) ∥ **Track A** (`wt-frontend`) ∥ **Track D** (`wt-perf`).
   - Track B must keep the frozen contract + reconciliation gate green at every slice. Track A only
     touches `web-ui/`. Track D must coordinate engine-file edits with B (land D's `Host`/`Infra` edits
     before B reorgs those files, or fold into B).
4. **Track C** features begin once B and A have green skeletons (B's slice pattern + endpoints exist; A's
   shell + generated client exist). Each C feature = one back slice ∥ one front module → multiple C
   features can run ∥ across the two worktrees.
5. **Track E** (decoupling) last, after the core program is stable. Optional/deferred.

Per-PR rules: see §7 Definition of Done. Stop-the-line on any red gate; fix before continuing.

---

## 4. Phase 0 — Backbone & data correctness  (SERIAL, current structure)

> Full detail + gates in **`PHASE-0-backbone.md`**. Slices are dependency-ordered; do them in order.

- **P0.1 cTrader-vs-DB comparison harness (DISCOVERY, first).** Capture cTrader CLI JSON+HTML; diff vs
  our DB for the same backtest; categorize structural diffs (= bugs) vs in-tolerance numeric diffs.
  Local/credentialed diagnostic — not a CI gate. Produces the data/streaming/flow work list.
- **P0.2 Triage & fix** the surfaced data/streaming/flow defects (most route into P0.5/P0.7).
- **P0.3 Canonical stats.** One pure `RunStatsProjection` → persisted `RunStats` row; delete the
  duplicate computations in `BacktestOrchestrator.GetTradeStatsAsync` and `Runs/Report.cshtml.cs`.
- **P0.4 Reconciliation gate (Layer 1, CI).** Credential-free test: independent recompute == persisted
  `RunStats` + the reconciliation invariants. *The safety net for Track B.*
- **P0.5 Engine hot-path isolation (non-blocking + LOSSLESS).** Bar thread does no synchronous DB/IO;
  classify every channel lossless(`Wait`)/droppable(`DropOldest`); lossless stress test proves zero loss
  on audit streams while the bar loop never blocks. Correctness, **separate from Track D** (§2.5).
- **P0.6 Schema faithfulness.** Migration: persist `RunPlan` (symbols×TF×strategy), `Venue`, venue params,
  report paths; add `RunStats`. Multi-symbol runs round-trip.
- **P0.7 Typed venue→engine event/state contract.** Replace stringly-typed log parsing (sim-clock,
  counters) with typed, persisted venue-status + engine-state events (lossless).
- **P0.8 DB unification + cleanup.** One DB-path resolver (no `..\..\..` walks); isolated temp test DBs
  with teardown. (OPEN-ISSUES D1.)
- **P0.9 Footgun fixes.** Default credential-free replay venue; remove hardcoded `SymbolInfo`; audit
  timeframe/symbol/balance defaults (OPEN-ISSUES D2); fold-in BUG-09 (governor `OnBar`).

**Phase 0 exit gate:** `Unit + Architecture + Integration + Simulation(credential-free)` green, **plus**
the reconciliation gate (P0.4) **and** the lossless stress test (P0.5) green, **plus** a multi-symbol
M15 replay run persists a faithful `RunPlanJson` + reconciling `RunStats`, **plus** the P0.1 cTrader diff
shows zero structural discrepancies (or a documented residue).

---

## 5. Phase 1 — Freeze the API contract  (SERIAL)

> Full detail in **`PHASE-1-api-contract.md`**.

- P1.1 Inventory the surface the SPA needs (runs list/detail/report/journal/stream, start/cancel,
  strategies list/detail/edit/validate, trades, live venue status, exports).
- P1.2 Define DTOs as the **stable contract** (versioned `/api/v1`). Generate OpenAPI (Swashbuckle/NSwag).
- P1.3 Define the **SignalR envelope** (typed `RunProgress`, venue-status, journal-append) — no
  stringly-typed payloads; camelCase already established.
- P1.4 Contract tests: every endpoint returns the declared shape; SignalR envelope matches the doc
  (extend `RunProgressContractTests`, `WebSmokeTests`).
- P1.5 Publish `contract/openapi.v1.json` into the repo as the artifact both tracks import.

**Phase 1 exit gate:** OpenAPI committed; contract tests green; client generation produces a compiling
TS client.

---

## 6. The parallel tracks

| Track | File | Worktree | Starts after | Summary |
|-------|------|----------|--------------|---------|
| **A** Frontend | `TRACK-A-frontend-angular.md` | `wt-frontend` | Phase 1 | New Angular+Tailwind SPA against the generated client; feature modules per slice; SignalR live; charts. Replaces all Razor. |
| **B** Backend | `TRACK-B-backend-slices.md` | `wt-backend` | Phase 1 | Big-bang reorg of Application+Web into vertical slices, CQRS-no-mediator, Result, decorator cross-cutting. Behaviour-preserving behind the frozen contract. |
| **C** Features | `TRACK-C-features.md` | per-feature | B+A skeletons | The NEXT-STEPS / OPEN-ISSUES backlog, each as a back slice + front module: reporting (A1–A7), config UX (E1–E3), venue status (C1), cTrader param tracking (B3), Layer-2 parity (B1/B2), rule-pressure tests (G1/G2). |
| **D** Perf | `TRACK-D-perf.md` | `wt-perf` | Phase 0 | Kill the 5s settle, batch flushes, profile bars/sec, fix lossy live feed. Coordinate engine-file edits with B. **Separate from P0.5** (which is correctness/lossless, not speed). |
| **E** Decoupling | `TRACK-E-engine-system-decoupling.md` | `wt-decouple` | **DEFERRED** (after core lands) | Full engine↔system separation (engine emits domain events through a port; system consumes; ideally separate assemblies). Lower priority — P0.5 already establishes the lossless seam. |

---

## 7. Cross-cutting Definition of Done (every PR on this program)

- Stop-the-line suites green: `dotnet test tests/TradingEngine.Tests.Unit` +
  `tests/TradingEngine.Tests.Architecture`; plus the Phase-0 reconciliation gate once it exists.
- No new stringly-typed cross-layer data; no `DateTime.UtcNow` (use `IEngineClock`); Serilog only;
  `decimal` money + `Math.Floor` lots; EF migrations only (no raw SQL).
- New backend code is a vertical slice with a `Result`-returning handler; cross-cutting via the shared
  decorators, never bespoke try/catch sprinkled in handlers.
- Contract changes update `contract/openapi.v1.json` + a contract test in the SAME PR.
- Docs: update the reference docs you invalidate (kill the drift — F-0). Mark resolved items in
  `docs/OPEN-ISSUES.md` / `docs/NEXT-STEPS.md`.

---

## 8. Risk register & open doubts for the owner

These are areas where I made a judgement call or want confirmation before the relevant track starts.
None block Phase 0; they affect later tracks.

1. **Big-bang reorg vs the test net.** You chose big-bang. The mitigation is that Phase 0's
   reconciliation gate + the frozen contract + contract tests make the move *mechanical and provable*.
   But a true big-bang still has a multi-day red window on the API project. **Doubt:** acceptable to
   keep `iter/33-track-b-backend` red against `main` while it's in flight (it integrates only when
   green), or do you want even the reorg chunked behind a feature flag? *Default I'll assume: keep it
   on its own branch, integrate only when green.*
2. **Engine kernel scope.** I'm treating Domain/Engine/Strategies/Risk/Services/Host as *not* part of
   the vertical-slice reorg (only the app/API layers). **Doubt:** confirm you don't want the kernel
   itself re-sliced — re-slicing it risks the one part that works. *Default: kernel stays.*
3. **Angular specifics.** Standalone components + signals store + Tailwind, TS client generated from
   OpenAPI, Lightweight-Charts kept. **Doubt:** any preference for NgRx vs signals, or a component
   library (Angular Material / PrimeNG / headless + Tailwind)? *Default: signals + headless + Tailwind,
   no heavy component lib.*
4. **cTrader parity in CI.** RESOLVED by owner: Layer-2 cTrader-vs-DB diff runs **first in Phase 0 as
   discovery** (local/credentialed) to surface data/streaming/flow bugs, then reverts to on-demand
   diagnosis. Layer-1 internal reconciliation is the blocking CI gate. (No longer an open doubt.)
5. **Experiments / Compliance / Governor pages.** There are existing surfaces (`Compliance.cshtml`,
   `ExperimentsController`, `GovernorController`) beyond the core backtest flow. **Doubt:** are these
   in scope for the SPA rebuild now, or parked until the core (runs/report/strategies/live) is done?
   *Default: park them in Track C tail; rebuild core first.*
6. **`RunStats` table vs columns.** I propose a dedicated `RunStats` table over widening
   `BacktestRunEntity`. **Doubt:** fine to add a table? *Default: yes (keeps the run row lean and lets
   stats be recomputed/backfilled independently).*

> If any default above is wrong, say which item # and I'll update the affected track file before that
> track starts. Phase 0 can begin regardless.
