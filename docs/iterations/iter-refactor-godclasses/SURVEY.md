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

## Outcome (executed 2026-07-15, commits f2d37f1..HEAD)

| Phase | Commit | What moved |
|---|---|---|
| P1 | f2d37f1 | `BacktestRunState`/`RunWarning` → `Runs/`, progress projection + tally → `RunProgressProjector` |
| P2 | 75f474e | `RunConfigAssembler`, `RunRecordStore` (+`RunTradeStats`), `RunMarketContextLoader`, `EngineHostLifecycle`, `RunRequestParser` |
| P3 | a57a3c7 | **Venue seam**: `IVenueRunner` + `VenueRunnerRegistry` + `ReplayVenueRunner` + `CTraderVenueRunner` + `RunRegistry`; `ResolveUseCtrader` → `VenueRouting` |
| P4 | 3256254 | `ExitResolution` promoted to `IReplayVenue` — last concrete-adapter type-sniff gone |
| P5 | 192f48e | cBot split into partials (`.Commands`/`.Events`/`.Recorder`) — zero statement changes |

`BacktestOrchestrator` is now **821 lines**: run queue/lanes, lifecycle transitions, finalize
(barrier → stats → warnings → end record), compare-both composition, public command surface.
Every consumer (controllers, hub, query service) is source-compatible except the three test files
that referenced nested/static members (updated).

**Verified:** Unit 759 ✅ · Integration 134 ✅ · Architecture 6/8 (identical 2 pre-existing
failures) · app smoke via run-shamshir driver **11/11** on the final binaries — boots the new DI
graph, POSTs a replay run through orchestrator → registry → ReplayVenueRunner to a terminal state
with run-record persistence intact. Simulation suite: 2 failures at BASELINE (pre-refactor
binaries) — same count expected on final binaries (checked below).

**Observations logged, not fixed (pre-existing):**
- The compare-both cTrader child run is `Register`ed in the run registry but never removed —
  a slow leak of one `BacktestRunState` per compare-both run (was `_runs[id] = state` before).
- `RunQueryService` (684) remains the next read-side split candidate.
- Simulation suite reports "Test Run Aborted" after summary at baseline too (suite-teardown quirk).

**Pre-existing sim failure root-caused (and half-fixed) here:**
`Pipeline.NetMQBridgeTest.EngineReceivesBarAndTickOverNetMQ` — the standalone Host entry
(`Program.cs`) resolved the scoped `StrategyConfigSeeder`/`IStrategyConfigStore` from the ROOT
provider; under `dotnet run` (Development launch profile ⇒ scope validation ON — exactly how the
test spawns the engine) the process crashed before binding, so the test timed out. Untouched by
this branch (`git diff 3075584..HEAD -- src/TradingEngine.Host src/TradingEngine.Infrastructure`
is empty); fixed here by resolving both inside a scope. The engine now starts, binds the NetMQ
transport and completes the handshake — the test still fails on its real assertion (no
`BAR_EVAL|EURUSD` after the published bar), which is the legacy live-loop path and out of scope
for this refactor. Net: crash → clean handshake; the "environmental cTrader-Pipeline sim tests"
note from iter-27 is this bug.

## Verification contract for this branch
- After each phase: `dotnet build` + Unit + Integration + Architecture (no new failures vs baseline).
- Simulation suite at start and end.
- **Before merging to `iter/alpha-loop`: one live cTrader compare-both smoke** (owner or the agent
  holding creds) because `RunEngineNetMqAsync` code MOVED. The move is mechanical, but per
  `docs/reference/INVESTIGATION-METHOD.md` a green credential-free gate never supports a cTrader claim.

## Completion (executed 2026-07-16, branch `refactor/god-classes-finish`)

The two items this survey left open were finished on branch `refactor/god-classes-finish`
(worktree `.claude/worktrees/refactor-godclasses-finish`, branched from `main` @ c836886 — the
post-merge state of everything above).

| Phase | Commit | What |
|---|---|---|
| P6 | 250f52d | **Single finalize copy.** `RunCompareBothAsync`'s cTrader child leg shared `FinalizeRunAsync` with `RunAsync` (the hand-maintained duplicate — the drift smell in nomination #1 — is gone). Also fixes the two logged leaks/gaps: the child `BacktestRunState` is now removed from the run registry (was `Register`ed forever, one leaked state + broadcaster throttle entry per compare-both run), and the child leg writes a terminal end record on cancel/fail (was left at ExitCode=-1 forever). Deliberate behavior alignments, both truthward: the child's end record now persists its terminal `Status` (was NULL), and a live viewer of the child leg receives the terminal SignalR frame (`PublishDone`) + `MarkCompleted` on the run-data cache, mirroring the parent finalize. |
| P7 | ff3a543 | **`RunQueryService` (684) split** into four cohesive query classes in `Runs/`: `RunListQuery` (list + healing overlays + 2s cache), `RunDetailQuery` (live-state vs DB detail), `RunDataQuery` (cache-first trades/equity/daily-PnL/analytics), `RunBarNarrativeQuery` (journal → bar narratives), plus `RunStatusOverlay` (shared stuck-threshold + persisted-terminal-status rule). All moves verbatim. `RunQueryService` is a 55-line composition facade with an unchanged constructor shape (tests compile untouched). The read side's dependency on the concrete `BacktestOrchestrator` — the last read↔write coupling smell — is replaced by the narrow `ILiveRunReader` port (orchestrator implements it; DI forwards). |

**Verified (credential-free):** build 0 err · Unit 766/6 skip (one unnamed failure in ONE background
run at 765/1, not reproduced across 4 subsequent runs — quiet logging swallowed the test name; treat
as cold-start flake unless seen again) · Integration 148 · Architecture 6/8 (identical 2 pre-existing
failures) · app smoke via run-shamshir driver **11/11**. Beyond the driver: a real **tape** run
(EURUSD H1 2025-09, window covered by marketdata.db) was driven through the refactored path to
`completed` — 11 trades, ExitCode 0, persisted `Status=completed` (the new unified-finalize status
write), and all four split read paths served it live (list overlay, trades 11, equity 528 pts,
analytics, daily-PnL, 559 bar narratives). The driver's own POSTed run fails with "No H1 market
data" in a fresh worktree — environmental, the documented expected class.

**Merge gate (same doctrine as the first branch):** one live cTrader **compare-both** smoke before
merging — `RunCompareBothAsync`'s finalize/cleanup changed (mechanically, but the compare instrument
itself moved, and the child-leg cleanup can only be OBSERVED live). No adapter/cBot/cost-model code
was touched.

**Still nominated, still deliberately untouched:** `TapeReplayAdapter` (806) and
`CTraderBrokerAdapter` (719) — parity-guarded, refactor only with a fresh live compare-both gate
budgeted; `ResearchCli/Program.cs` (828) / `HttpStepRunner` (765) — linear CLI wiring, low fan-in;
kernel classes — size inherent. The 2 pre-existing Architecture failures
(`Engine_has_no_ILogger_no_DateTimeNow`, `All_persistence_entities_implement_IAuditableEntity`)
remain open and are NOT part of this survey's scope.

**Logged, not fixed (pre-existing, observed during the smoke):** `EnqueueRun` writes a "queued"
start record and `RunAsync` writes a second start record — `SqliteBacktestRunRepository.SaveAsync`
INSERTs, so the second write hits `UNIQUE constraint failed: BacktestRuns.RunId` on every queued
run. It is caught + warned ("Failed to write start record") and the end-record `UpdateAsync` lands
fine, but the Status=running upgrade is silently lost and the log noise looks like a real failure.
Cheap fix: make `SaveAsync` upsert (or route the second write through `UpdateAsync`).
