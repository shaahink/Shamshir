# God-Class Survey & Refactor Plan — iter/refactor-godclasses

**Date:** 2026-07-15 · **Branch:** `refactor/god-classes` (worktree `C:\Code\shamshir-refactor`, branched from `iter/alpha-loop` @ 3075584)
**Constraint:** another agent holds live cTrader creds in the main worktree — NO live runs, NO RequiresCTrader tests, NO process kills from this worktree. All verification here is credential-free (Unit / Integration / Architecture / Simulation).

## Baseline (verified before any change)

| Suite | Result |
|---|---|
| Unit | 759 pass / 6 skip |
| Integration | 134 pass |
| Architecture | 6 pass / **2 pre-existing failures** (`Engine_has_no_ILogger_no_DateTimeNow`, `All_persistence_entities_implement_IAuditableEntity`) — fail at HEAD before this branch touched anything |
| Simulation | see PROGRESS note |

## Nominations (ranked by centrality × size × churn)

### 1. `BacktestOrchestrator` — 2,387 lines, THE god class — **TACKLED HERE**
`src/TradingEngine.Web/Services/BacktestOrchestrator.cs`. One class owning **ten** jobs:
run registry + state (`BacktestRunState`), queue/admission (tape semaphore + cTrader owner lane),
lifecycle state machine writes, progress projection + SignalR broadcast, run persistence
(start/end records + content addressing), teardown warnings, **DB config assembly** (packs/
overrides/toggles — 175 lines), **the replay/tape venue execution path** (270 lines), **the cTrader
CLI/NetMQ venue execution path** (365 lines), compare-both orchestration (a hand-maintained copy
of the finalize block), trade stats, cross-rate/venue-spec loading, port allocation, algo hashing.

Venue selection is a hard-coded `if/else` in `RunAsync`; adding a venue means editing the god class.
The compare-both leg re-implements finalization by hand (its own comment admits a past drift bug).

**Decomposition (mechanical moves, no behavior change):**
- `Services/Runs/BacktestRunState.cs` — the run-state record + `RunWarning`, out of the nest.
- `Services/Runs/RunProgressProjector.cs` — `BuildProgress` + `TallyEvent` (pure).
- `Services/Runs/RunConfigAssembler.cs` — `BuildLoadedConfigFromDbAsync`, `ResolveEffectiveConfigJsonAsync`, override/plan parsing.
- `Services/Runs/RunRecordStore.cs` — start/end record writes, content addressing, `RunTradeStats` + stats query, warnings JSON.
- `Services/Runs/RunMarketContextLoader.cs` — account currency, cross-rate series, venue symbol specs (used by BOTH venue paths; F34/F44 doctrine notes preserved).
- `Services/Runs/EngineHostLifecycle.cs` — equity polling, final-equity capture, persistence flush, async host dispose.
- `Services/Venues/IVenueRunner.cs` + `VenueRunnerRegistry` — **pluggable venue seam**: a venue is a DI-registered runner keyed by venue id; the orchestrator no longer knows venue internals.
- `Services/Venues/ReplayVenueRunner.cs` — replay + tape execution (one runner, two venue ids — honest about the current shared code path).
- `Services/Venues/CTraderVenueRunner.cs` — CLI launch, NetMQ host, report harvest, currency/protection warnings, port allocation, algo hash.
- Orchestrator keeps ONLY: registry, queue/lanes, lifecycle transitions, finalize (single copy used by both the main path and the compare-both child leg), public command surface.

### 2. `TradingEngineCBot` — 1,173 lines — **TACKLED HERE (file split only)**
`src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs`. Already `partial`; one file mixes five
concerns: engine session lifecycle (NetMQ handshake/bar loop), order command execution, venue
event handlers (resting fills / venue closes), recorder mode, ledger/reporting plumbing.
**cBot code can only be truly verified live** (project doctrine: green credential-free gates never
support a claim about cTrader), so the refactor here is a **pure partial-class file split — zero
statement changes** — verified by compilation + `.algo` build. Anything behavioral is out of scope.

### 3. Venue/engine boundary — **TACKLED HERE (one seam fix)**
`IBrokerAdapter` is already a clean port ("the engine never type-sniffs concrete adapters") with
default-no-op venue hooks. ONE violation exists, in the orchestrator: `adapter is TapeReplayAdapter
tape && tape.ExitResolution` → promote `ExitResolution` to `IReplayVenue` (default null) and kill
the type-sniff. cTrader-only observability callbacks (`OnSymbolSpec`, `OnStatusChange`) move with
their wiring into `CTraderVenueRunner`, so venue observability is owned by the venue's runner.

### 4. Nominated, NOT touched (rationale)
- **`TapeReplayAdapter` (806)** — the venue mimic. Byte-for-byte parity with cTrader is proven and
  guarded; any restructuring risks re-opening P0–P4 parity. Refactor only with a fresh live
  compare-both gate available. **Do not touch in a credential-free session.**
- **`CTraderBrokerAdapter` (719)** — same reason, transport half of the oracle path.
- **`BacktestReplayAdapter` (644)** — default UI venue; golden-replay guarded; low churn. Leave.
- **`RunQueryService` (684)** — read-side aggregation; cohesive if long; no state. Candidate for a
  later split (memory-vs-DB read paths), low urgency.
- **`ResearchCli/Program.cs` (828) + `HttpStepRunner` (765)** — CLI wiring, linear, low fan-in.
- **`RunsController` (582) / `SystemController` (506)** — thin-ish HTTP surface; fine.
- **`KernelBacktestLoop` (473) / `EngineRunner` (443) / `EngineReducer` (463)** — kernel core is
  deliberately centralized (event→state→effects reducer); size is inherent, not smell.
- **Migrations `*.Designer.cs`** — generated; ignore.

## Verification contract for this branch
- After each phase: `dotnet build` + Unit + Integration + Architecture (no new failures vs baseline).
- Simulation suite at start and end.
- **Before merging to `iter/alpha-loop`: one live cTrader compare-both smoke** (owner or the agent
  holding creds) because `RunEngineNetMqAsync` code MOVED. The move is mechanical, but per
  `docs/reference/INVESTIGATION-METHOD.md` a green credential-free gate never supports a cTrader claim.
