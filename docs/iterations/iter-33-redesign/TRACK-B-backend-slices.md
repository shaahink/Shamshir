# Track B — Backend Big-Bang Vertical-Slice Reorg

**Worktree:** `git worktree add ../shamshir-backend iter/33-track-b-backend`
**Starts after:** Phase 1 (contract frozen).
**Hard rule:** this track **preserves behaviour and the frozen contract**. It moves and relabels code;
it does not change JSON/SignalR shapes. If a shape must change → change `contract/openapi.v1.json` + a
contract test first, coordinate with Track A.
**Safety net:** Phase-0 reconciliation gate + Phase-1 contract tests must stay green at every step.

See `MASTER_PLAN.md` §2.1 for the target structure.

---

## Scope boundary (read first)

**In scope:** `TradingEngine.Web` (→ `TradingEngine.Api`) and `TradingEngine.Application`. The web
service sprawl (god-class `BacktestOrchestrator`, ~10 services, 13 controllers) collapses into vertical
slices.

**Out of scope (do NOT re-slice):** the trading kernel — `Domain`, `Engine`, `Strategies`, `Risk`,
`Services` (the calculators), `Infrastructure`, `Host`. It is well-tested and is the part that works
(MASTER_PLAN F-6). Touch it only where a slice needs a new read model and there's no existing query.

---

## B1 — Stand up the slice skeleton + cross-cutting

1. In `TradingEngine.Application` add `Common/`:
   - `Result` / `Result<T>` (Ok/Fail + typed `Error(Code, Message)`).
   - `ICommandHandler<TReq,TRes>`, `IQueryHandler<TReq,TRes>`.
   - `Behaviors/` decorators: `ValidationBehavior` (FluentValidation or a tiny validator), `LoggingBehavior`
     (Serilog, structured), `ExceptionToResultBehavior` (unexpected → `Result.Fail`), `TimingBehavior`.
   - `Dispatch/`: either a thin generic dispatcher or direct handler injection into endpoints. **No
     MediatR.** Wrap handler registrations with decorators (Scrutor `Decorate` or a manual factory).
2. `AddApplication(IServiceCollection)` extension: scans + registers handlers and wraps with the
   decorator chain. `AddInfrastructure`, `AddEngine` extensions own their own registrations.

**Gate:** a trivial sample query flows handler→decorators→Result; unit test asserts the decorator order
(validation→logging→exception→timing) and that a thrown exception becomes `Result.Fail`.

---

## B2 — Rebuild the API project as endpoints over handlers

1. Rename/replace `TradingEngine.Web` → `TradingEngine.Api`: remove Razor Pages, `wwwroot`, `_Layout`,
   all `.cshtml`. Keep `Hubs/RunHub`.
2. `Program.cs` = composition root only: `AddApplication()`, `AddInfrastructure()`, `AddEngine()`,
   SignalR, DbContexts, the single `IDbPathProvider` (from P0.5). No `Path.Combine` walks, no business
   logic.
3. Endpoints grouped per feature (minimal API `MapGroup("/api/v1/runs")` etc.) → call handler → map
   `Result` to `ProblemDetails`/200. One endpoint file per slice.

**Gate:** Phase-1 contract tests green against the rebuilt API; `WebSmokeTests` green; no `.cshtml`
remains.

---

## B3 — Migrate features into slices (decompose the god-class)

Break `BacktestOrchestrator` into slices under `Features/Backtests/` and `Features/LiveRun/`:

| Slice | Was (today) |
|-------|-------------|
| `StartBacktest` (command) | `Start` + `RunAsync` + venue routing |
| `RunReplayEngine` / `RunCtraderEngine` (services behind StartBacktest) | `RunEngineReplayAsync` / `RunEngineNetMqAsync` |
| `CancelRun` (command) | `Cancel` / `StopAllAsync` |
| `GetRunReport` (query) | `Runs/Report.cshtml.cs` → reads P0.1 `RunStats` (no recompute) |
| `GetRunJournal` (query) | journal API (paged, lossless) |
| `StreamRunProgress` | `RunProgressBroadcaster` + typed events from P0.4 |
| `GetVenueStatus` (query) | the venue-status events from P0.4 |

- The run-lifecycle/process management (subprocess launch, equity polling, host lifetime) moves to a
  focused `BacktestRunner`/`RunHostManager` infrastructure service the command depends on — not inline
  in the handler.
- Effective-config resolution stays via `EffectiveConfigResolver` (already a clean unit). Wire it into
  the `StartBacktest` slice.

**Gate after each slice:** reconciliation gate + contract tests + relevant unit/integration green. Move
one slice at a time; never leave the branch red across more than one slice.

---

## B4 — Strategies / Reporting / Verification slices

- `Features/Strategies/`: `ListConfigs`, `GetConfig`, `UpsertConfig` (reuse `ConfigLoader` cross-ref
  validation before upsert — OPEN-ISSUES E3), `ValidateConfig`, plus read slices for risk profiles /
  prop-firm / symbols. Replaces the empty `Strategies.cshtml.cs` + `StrategiesController`.
- `Features/Reporting/`: `GetRunStats`, `GetFunnel`, `GetEquityCurve`, `GetTradeDetail`, `ExportReport`.
- `Features/Verification/`: `ReconcileRun` (Layer-1, wraps P0.2), `CompareToCtrader` (Layer-2 stub the
  handler; Track C fills the diff logic).

**Gate:** contract tests for these endpoints; strategy upsert round-trips through validation; suites green.

---

## B5 — Cleanup & doc re-sync

1. Delete dead code surfaced by the move (orphaned `Backtests/Index`/`Detail` logic, duplicate stat
   math already removed in P0.1, unused services).
2. Squash to a single `InitialCreate` migration (carried decision) now that schema is stable.
3. Rewrite the reference docs to match reality (kill F-0 drift): `SYSTEM-REFERENCE.md`,
   `CODE-MAP.md`, `BACKTEST-ARCHITECTURE.md`. Mark OPEN-ISSUES F1/F2 + the relevant carry-forwards done.

**Track B exit gate:** full suite + reconciliation gate + contract tests green; API is endpoints-over-
handlers with Result + decorator cross-cutting; no Razor; no god-class; docs match code.
