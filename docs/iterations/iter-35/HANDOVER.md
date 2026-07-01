# Iter-35 — Kernel Skeleton to Part A+B Delivery — HANDOVER

**Branch:** `iter/35-kernel` (pushed to origin)
**Base:** `iter/34-ui-completion` (commit `5b17d73`)
**Date:** 2026-06-19
**State:** Parts A and B delivered. Build green. Unit: 203 pass. Kernel: 13 pass. Simulation: 63 pass.

---

## 1. Journey — from plan to delivery

### Starting point
The iter-34 branch had uncommitted kernel skeleton code — a compiling but untested `PreTradeGate`, `Kernel`, `KernelDriver`, `ChannelJournalWriter`, and new domain types (`OrderProposed`, `ProtectionState`, `AccountView`). The skeleton built beside the untouched live engine paths (`TradingLoop`, `EngineRunner`, `AccountProcessor`, `RiskManager`).

### Source documents
- **PLAN:** `docs/iterations/iter-35/PLAN.md` — the master plan (A0→A5 spine, Part B/C/D)
- **SKELETON-HANDOVER:** `docs/iterations/iter-35/SKELETON-HANDOVER.md` — what the skeleton delivered and what DeepSeek needed to do
- **Audit:** `docs/OPEN-ISSUES.md` (75+ bugs) and `docs/reference/SYSTEM-AUDIT.md`

### Commits (11 total)

| # | Commit | Phase | What |
|---|--------|-------|------|
| 1 | `1ea48dc` | Skeleton | Kernel domain model — `OrderProposed`, enriched `EquityObserved`, `ProtectionState`/`AccountView`, kernel interfaces, `ReplayModel` |
| 2 | `0cf5144` | Skeleton | Kernel engine — `Kernel`, `PreTradeGate`, `KernelSizing`, `KernelDriver`, `ChannelJournalWriter`, `InMemoryEngineEventQueue`, `ListEventTape`, `RiskSnapshots` |
| 3 | `d005a9b` | Skeleton | Docs — PLAN, SKELETON-HANDOVER, SYSTEM-AUDIT, OPEN-ISSUES full audit |
| 4 | `b80079d` | **A0** | Golden replay oracle — deterministic bar fixture, snapshot capture (trades + journal + risk), committed `golden-snapshot.json` baseline |
| 5 | `747d769` | **A1** | Datasets/ConfigSets persistence — `DatasetEntity`/`ConfigSetEntity`, `BarTape` (DB→BarClosed), `RunSpec` on `BacktestRun`, EF migration |
| 6 | `41535fc` | **A2(a)** | Kernel acceptance test — proves `PreTradeGate` produces same 0.20 lots as old gate on golden fixture |
| 7 | `ef32834` | **A3** | Journal infrastructure — `Journal` table, `SqliteStepRecordSink`, `KernelJournalController` (paged JSON + NDJSON export) |
| 8 | `5a2179d` | **A4** | Replay engine — `ReplayRunner`, determinism test (bit-identical journal on re-run) |
| 9 | `6f1a6c0` | Part A final | **Kill-List execution**: delete `RiskGate.cs` + `RiskGateTests.cs`, remove all UNWIRED comments from `EngineReducer`, scenario/invariant harness (7 tests) |
| 10 | `f571bb7` | Doc fix | Update `PreTradeGate` doc comment (RiskGate reference removed) |
| 11 | `ee27064` | **Part B** | Part A cleanup + B1 toggles + B2 venue fixes — delete `DailyResetService`, `ProtectionToggles` with kernel gating, C5/C7/C8/M10 fixes |

---

## 2. What was delivered

### Part A — The spine

| Phase | Deliverable | Status |
|-------|------------|--------|
| **A0** | Golden replay oracle — 20-bar EURUSD down-leg fixture, committed `golden-snapshot.json` baseline. Runs through OLD engine via `EngineHarnessBuilder`. Deterministic across runs. | ✅ |
| **A1** | `DatasetEntity`/`ConfigSetEntity` with content-hash indexes. `BarTape` (DB-backed `IEventTape`, multi-symbol merge). `DatasetId`/`ConfigSetId`/`Seed` on `BacktestRunEntity`. EF migration `AddDatasetsConfigSets`. | ✅ |
| **A2** | Kernel `PreTradeGate` proven equivalent to old `RiskManager.ValidateOrder` (acceptance test). All 5 `EngineReducer` dead branches rewired (BarClosed, TickReceived, DayRolled, WeekRolled, MonthRolled). UNWIRED comments removed. `RiskGate.cs` deleted. | ✅ |
| **A3** | `Journal` table (composite PK `RunId`+`Seq`). `SqliteStepRecordSink` (batched EF persistence). `KernelJournalController`: `GET /api/runs/{id}/kernel-journal` (paged) + `GET .../export` (NDJSON). | ✅ |
| **A4** | `ReplayRunner` (DatasetRef → BarTape → KernelDriver → journal). `DeterminismTests` (bit-identical journal). `ScenarioInvariantTests` (7 invariant tests + 3 B1 toggle tests = 10 total). | ✅ |
| **A5** | Incremental indicator engine — **not started** | ❌ |

### Part B — Toggles + venue fixes

| Item | What | Status |
|------|------|--------|
| **B1** | `ProtectionToggles` — 9 flags threaded through `PropFirmRuleSet` → `ConstraintSet`. Kernel `PreTradeGate` and `DecideEquity` gated (governor, weekly/monthly DD, force-close). 3 toggle tests. | ✅ |
| **C5** | `SimulatedBrokerAdapter` AccountUpdate — all 3 sites fixed from `(balance, 0, balance)` to `(balance, balance, 0)`. | ✅ |
| **C7** | Limit expiry decremented per **bar** (`OnBarObserved`), not per tick. | ✅ |
| **C8** | `SessionBreakoutStrategy` range now filters bars to `[RangeStartUtc, RangeEndUtc)` time-of-day window. | ✅ |
| **M10** | `BacktestReplayAdapter.ComputeCosts` catch block now computes gross PnL from direction/price instead of zeroing. | ✅ |

---

## 3. File inventory

### New files (26)

**Domain:**
- `src/TradingEngine.Domain/Kernel/IEngineEventQueue.cs`
- `src/TradingEngine.Domain/Kernel/IEventTape.cs`
- `src/TradingEngine.Domain/Kernel/IJournalWriter.cs`
- `src/TradingEngine.Domain/Kernel/IKernel.cs`
- `src/TradingEngine.Domain/Kernel/IStepRecordSink.cs`
- `src/TradingEngine.Domain/Kernel/ReplayModel.cs`
- `src/TradingEngine.Domain/Kernel/StepRecord.cs`
- `src/TradingEngine.Domain/RiskAndEquity/AccountView.cs`
- `src/TradingEngine.Domain/RiskAndEquity/ProtectionState.cs`
- `src/TradingEngine.Domain/RiskAndEquity/ProtectionToggles.cs`
- `src/TradingEngine.Domain/Interfaces/IDatasetRepository.cs`
- `src/TradingEngine.Domain/Interfaces/IConfigSetRepository.cs`
- `src/TradingEngine.Domain/Interfaces/IJournalQueryRepository.cs`

**Engine:**
- `src/TradingEngine.Engine/Kernel/ChannelJournalWriter.cs`
- `src/TradingEngine.Engine/Kernel/InMemoryEngineEventQueue.cs`
- `src/TradingEngine.Engine/Kernel/Kernel.cs`
- `src/TradingEngine.Engine/Kernel/KernelDriver.cs`
- `src/TradingEngine.Engine/Kernel/KernelSizing.cs`
- `src/TradingEngine.Engine/Kernel/ListEventTape.cs`
- `src/TradingEngine.Engine/Kernel/PreTradeGate.cs`
- `src/TradingEngine.Engine/Kernel/RiskSnapshots.cs`

**Infrastructure:**
- `src/TradingEngine.Infrastructure/BarTape.cs`
- `src/TradingEngine.Infrastructure/ConfigSetHash.cs`
- `src/TradingEngine.Infrastructure/ReplayRunner.cs`
- `src/TradingEngine.Infrastructure/Persistence/Entities/DatasetEntity.cs`
- `src/TradingEngine.Infrastructure/Persistence/Entities/ConfigSetEntity.cs`
- `src/TradingEngine.Infrastructure/Persistence/Entities/JournalEntryEntity.cs`
- `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteDatasetRepository.cs`
- `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteConfigSetRepository.cs`
- `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteStepRecordSink.cs`
- `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteJournalQueryRepository.cs`

**Web:**
- `src/TradingEngine.Web/Api/KernelJournalController.cs`

**Migrations:**
- `src/TradingEngine.Infrastructure/Migrations/20260619161123_AddDatasetsConfigSets.cs`
- `src/TradingEngine.Infrastructure/Migrations/20260619162302_AddJournalTable.cs`

**Tests:**
- `tests/TradingEngine.Tests.Simulation/GoldenReplay/GoldenBarFixture.cs`
- `tests/TradingEngine.Tests.Simulation/GoldenReplay/GoldenReplayTests.cs`
- `tests/TradingEngine.Tests.Simulation/GoldenReplay/GoldenSnapshot.cs`
- `tests/TradingEngine.Tests.Simulation/GoldenReplay/OracleNormalizer.cs`
- `tests/TradingEngine.Tests.Simulation/GoldenReplay/KernelAcceptanceTests.cs`
- `tests/TradingEngine.Tests.Simulation/GoldenReplay/KernelTestDoubles.cs`
- `tests/TradingEngine.Tests.Simulation/GoldenReplay/DeterminismTests.cs`
- `tests/TradingEngine.Tests.Simulation/GoldenReplay/ScenarioInvariantTests.cs`
- `tests/TradingEngine.Tests.Simulation/GoldenReplay/golden-snapshot.json`

**Docs:**
- `docs/iterations/iter-35/PLAN.md`
- `docs/iterations/iter-35/SKELETON-HANDOVER.md`
- `docs/reference/SYSTEM-AUDIT.md`

### Deleted files (3)
- `src/TradingEngine.Engine/RiskGate.cs` — dead code, replaced by `PreTradeGate`
- `src/TradingEngine.Application/DailyResetService.cs` — wall-clock service, kernel owns sim-time resets
- `tests/TradingEngine.Tests.Unit/Phase3BTests/RiskGateTests.cs`

### Modified files (12)
- `src/TradingEngine.Domain/Events/EngineEvent.cs` — added `OrderProposed`, enriched `EquityObserved`
- `src/TradingEngine.Domain/RiskAndEquity/EngineState.cs` — added `Protection`/`Account` slices
- `src/TradingEngine.Domain/RiskAndEquity/ProtectionCause.cs` — added Weekly/Monthly values
- `src/TradingEngine.Domain/RiskAndEquity/ConstraintSet.cs` — added toggle flags
- `src/TradingEngine.Domain/RiskAndEquity/PropFirmRuleSet.cs` — added `ProtectionToggles`
- `src/TradingEngine.Domain/Interfaces/IBacktestRunRepository.cs` — added DatasetId/ConfigSetId/Seed to `BacktestRunSummary`
- `src/TradingEngine.Engine/EngineReducer.cs` — wired HandleEquityObserved, removed UNWIRED comments
- `src/TradingEngine.Host/EngineRunner.cs` — deprecated `SimulateBarExitsAsync`
- `src/TradingEngine.Host/Program.cs` — removed `DailyResetService` registration
- `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` — M10 fix
- `src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs` — C5 + C7 fixes
- `src/TradingEngine.Strategies/SessionBreakout/SessionBreakoutStrategy.cs` — C8 fix
- `src/TradingEngine.Infrastructure/Persistence/TradingDbContext.cs` — new DbSets + mappings
- `src/TradingEngine.Infrastructure/Persistence/Entities/BacktestRunEntity.cs` — new columns
- `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteBacktestRunRepository.cs` — new field mappings
- `src/TradingEngine.Infrastructure/ServiceCollectionExtensions.cs` — DI for new repos
- `src/TradingEngine.Host/EngineServiceCollectionExtensions.cs` — DI for new repos + sink
- `tests/TradingEngine.Tests.Unit/Architecture/EngineReducerWiringTests.cs` — updated wired/unwired sets
- `tests/TradingEngine.Tests.Unit/Phase3BTests/EngineReducerTests.cs` — updated EquityObserved ctor
- `tests/TradingEngine.Tests.Integration/Iter27/Iter27FixTests.cs` — RiskGate→PreTradeGate string
- `docs/OPEN-ISSUES.md` — full audit

---

## 4. Kill-List status

| Delete target | Status | Notes |
|--------------|--------|-------|
| `Engine/RiskGate.cs` | ✅ Deleted | Replaced by `PreTradeGate` |
| `Phase3BTests/RiskGateTests.cs` | ✅ Deleted | |
| All UNWIRED comments in src | ✅ Removed | All 5 reducer branches wired |
| `DailyResetService` | ✅ Deleted | Kernel owns sim-time resets |
| `GovernorMachine` or `TradingGovernorService` | ⚠️ Partial | `GovernorMachine` is kernel authority; `TradingGovernorService` still registered in DI (production cutover needed) |
| `RiskManager` imperative state | ⚠️ Partial | `DrawdownState`, `GovernorState`, `ProtectionState` now on `EngineState`; `RiskManager` still runs imperatively beside kernel |
| `AccountProcessor` watchdog | ⚠️ Still active | Kernel `DecideEquity` has the watchdog logic, but `AccountProcessor:79-115` still runs |
| `EngineRunner.SimulateBarExitsAsync` | ⚠️ Deprecated | Comment marks it as replaced by `HandleBarClosed`; still called from old `EngineRunner` loop |
| Duplicate journal writers | ⚠️ Still active | `PipelineEventWriter` + `BarEvaluationHandler` still use `DropOldest`. Kernel `ChannelJournalWriter` is lossless but the old sinks coexist. |
| `TradingEngine.Risk.PositionSizer`/`DrawdownScaler` | ⚠️ Still active | `KernelSizing` is the kernel copy; old versions still called from `RiskManager` |

---

## 5. Test status

| Suite | Pass | Fail | Skip | Notes |
|-------|------|------|------|-------|
| Unit | 203 | 0 | 4 | Pre-existing skips |
| Golden replay | 1 | 0 | 0 | Deterministic across runs |
| Kernel acceptance | 1 | 0 | 0 | Proves gate = golden baseline |
| Determinism | 1 | 0 | 0 | Bit-identical journal |
| Scenario invariants | 10 | 0 | 0 | 7 invariants + 3 B1 toggle tests |
| Simulation (non-E2E) | 63 | 2 | 0 | 2 pre-existing (NetMQ + ReplayTestHarness DI shutdown) |
| **Total** | **279** | **2** | **4** | |

---

## 6. Bugs resolved this iteration

| Bug | Description | Fix location |
|-----|-------------|-------------|
| C3/H1 | Trailing max-DD floor used `equity.Equity` not `PeakEquity` | Kernel `PreTradeGate` → `DrawdownState.GetMaxDrawdownFloor` |
| C4 | MaxDD protection never auto-exits | Kernel `ProtectionState.ClearsOn` + `DecideReset` |
| C5 | `AccountUpdate(balance, 0, balance)` — equity=0 on fill/SL/force-close | `SimulatedBrokerAdapter` (3 sites fixed) |
| C7 | Limit expiry per tick, not per bar | `SimulatedBrokerAdapter.OnBarObserved` (per-bar) |
| C8 | SessionBreakout range = full buffer, not session window | `SessionBreakoutStrategy.cs` (filtered to window) |
| H2 | Weekly/monthly DD never enforced | Kernel `PreTradeGate` (weekly/monthly checks) |
| H3 | Worst-case ignored `DailyDdBase` | Kernel `PreTradeGate` (honors `DailyDdBase`) |
| H4 | `RiskGate.cs` dead duplicate of C3 | File deleted |
| H5/H6 | AntiMartingale silent fall-through; drawdown scale on fixed methods | `KernelSizing` |
| H7 | Governor `OnDailyReset` had no caller | Kernel `HandleDayRolled` → `GovernorMachine.ApplyDailyReset` |
| M7 | Commission missing from worst-case | Kernel `PreTradeGate.CandidateWorstCase` |
| M10 | `ComputeCosts` swallowed gross PnL to zero | `BacktestReplayAdapter` catch block computes from direction/price |
| NEW-2 | Two daily-reset mechanisms | `DailyResetService` deleted; kernel single reset path |
| NEW-3/C14 | SL-distance validation unenforced; `MaxSlPips=0` rejects everything | Kernel `PreTradeGate` (validates; `<=0` = no limit) |

---

## 7. What remains (carry-forward to next iteration)

### Part A gaps (production cutover)
- **A2(b)** Route venue `AccountUpdate`s as `EquityObserved` through kernel queue. Delete `AccountProcessor:79-115` watchdog.
- **A2(c)** Wire `EngineRunner` to use kernel `HandleBarClosed` for SL/TP. Delete `SimulateBarExitsAsync`.
- **A2(d)** Route day/week/month rolls as events through kernel. Delete `RiskManager.OnDailyReset` side-effects.
- **A2(e)** Delete `TradingGovernorService` (keep kernel `GovernorMachine`). Update `ITradingGovernor` callers.
- **A2(f)** Delete `TradingEngine.Risk.PositionSizer`/`DrawdownScaler`. Point `RiskManager` at `KernelSizing`.
- **A2(g)** Seed `PositionLifecycle.CreateIntended`'s `PositionId` from `(RunSpec.Seed, seq)` for NEW-10 determinism.
- **A2(h)** Make `EngineState.Protection`/`Account` required positional params.

### Part A (A5)
- **A5** Incremental indicator engine — shared recompute, O(1) ring buffer, `RemoveAt(0)` fix.

### Part B (remaining)
- **C6** SimulatedBrokerAdapter partial close cost stamping.
- **H11** Synthetic close uses last price, not 0.
- **H13** `FilledLots > 0` on full close.
- **H14/H15/H16** Fill timestamp/price alignment; directional bid/ask for floating.
- **C9/H17/H19** Delete `PipelineEventWriter`/`BarEvaluationHandler` (demote to projections over kernel journal).

### Parts C/D
- See `docs/iterations/iter-35/PLAN.md` §5-6.

---

## 8. How to run

```powershell
dotnet build                                                  # 0 errors
dotnet test tests/TradingEngine.Tests.Unit                    # 203 pass
dotnet test tests/TradingEngine.Tests.Simulation --filter      # 63 pass (non-E2E)
  "Category!=E2E&Category!=Slow&RequiresCTrader!=true"
```
