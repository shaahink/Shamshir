# Iter-35 Kernel Skeleton — Handover to DeepSeek

**Author:** Claude (architecture + core implementation; **builds clean**, not test-verified per owner instruction).
**Read first:** `docs/iterations/iter-35/PLAN.md`. This doc bridges that plan to the code now in the tree.

## 1. Status — what's done vs what's left

This is the **kernel funnel spine plus a real, compiling decision core** — well past a stub. As of this iteration:

- ✅ **`dotnet build` is green for the whole solution** (Domain + Engine + live src + tests; 0 errors). The new subsystem compiles *beside* the untouched live paths.
- ✅ The **pre-trade gate is fully implemented** (`PreTradeGate`), porting the corrected `RiskManager.ValidateOrder/ValidateBudgetEntry/CalculateLotSize` logic with C3/H1/H2/H3/M7/NEW-3 fixed and sizing (H5/H6) ported into `KernelSizing`.
- ✅ The **kernel routes and decides** real logic (`Kernel`): `OrderProposed`→gate→effects, `EquityObserved`→drawdown/account fold + breach watchdog (enter protection / force-close), `Day/Week/Month`→resets + protection-exit (C4) + governor reset (H7).
- ✅ The **funnel** (`KernelDriver`), the **lossless journal writer** (`ChannelJournalWriter`, fixes C9/H17/H19/M16), the **queue**, the **tape**, and the **account/equity state slice** are concrete.
- ❗ **Not test-verified.** Treat the golden/determinism tests (PLAN A0/A4) as the next gate. There may be logic nits the compiler can't catch (e.g. the breach-detection tuple ordering, the open-position projection seam).
- ❗ **Not wired into the live drivers.** Nothing in production calls the new subsystem yet — `TradingLoop`/`OrderDispatcher`/`RiskManager`/`AccountProcessor`/`EngineRunner` are untouched and still authoritative. **Your job is the cutover + deleting the twins (Kill-List), against the golden oracle.**

## 2. File inventory

**New (`TradingEngine.Domain`):**
| File | Role | State |
|------|------|-------|
| `RiskAndEquity/ProtectionState.cs` | Authoritative protection slice; `ClearsOn` owns protection-exit (C4). | Done (ResetPolicy matrix is a TODO). |
| `RiskAndEquity/AccountView.cs` | Time-varying account slice (Balance/Equity/FloatingPnL). | Done. |
| `Kernel/StepRecord.cs` | Unified journal record + `RiskSnapshot` + `StrategyVerdict`. | Done. |
| `Kernel/IJournalWriter.cs`, `Kernel/IStepRecordSink.cs` | Journal write contract + durable-persistence boundary. | Sink impl = your SQLite job. |
| `Kernel/IEventTape.cs`, `Kernel/IEngineEventQueue.cs` | Replayable event source + in-order queue. | Done (queue impl below). |
| `Kernel/ReplayModel.cs` | `DatasetRef`/`ConfigSet`/`RunSpec`/`DatasetGranularity`. | Types done; persistence = your job (A1). |
| `Kernel/IKernel.cs` | `IKernel.Decide` + `KernelConfig` (constraints, profile, sizing, symbol lookup, open-position projector, seed). | Done. |

**New (`TradingEngine.Engine`):**
| File | Role | State |
|------|------|-------|
| `Kernel/KernelDriver.cs` | THE FUNNEL: tape→queue→`IKernel.Decide`→journal `StepRecord`→`IEffectExecutor`→feedback. | Done. |
| `Kernel/Kernel.cs` | `IKernel`: routes + decides (gate / breach / resets). | Done. |
| `Kernel/PreTradeGate.cs` | The pure gate — all 8 checks + sizing + budget downsizing. | **Implemented.** |
| `Kernel/KernelSizing.cs` | Ported `PositionSizer`+`DrawdownScaler` (pure). | **Implemented** (AntiMartingale is explicit-but-PercentRisk; refine). |
| `Kernel/InMemoryEngineEventQueue.cs` | FIFO queue. | Done. |
| `Kernel/ListEventTape.cs` | Pure in-memory tape (tests + golden + shape for `BarTape`). | Done. |
| `Kernel/ChannelJournalWriter.cs` | Lossless journal writer (Wait channel, clear-after-success, retry, drain-on-dispose). | Done (needs the sink). |
| `Kernel/RiskSnapshots.cs` | `EngineState`→`RiskSnapshot` capture (driver default). | Done. |

**Edits (non-breaking):** `Events/EngineEvent.cs` (+`OrderProposed`, enriched `EquityObserved` to carry Balance/FloatingPnL), `RiskAndEquity/EngineState.cs` (+`Protection`,+`Account` slices, defaulted), `RiskAndEquity/ProtectionCause.cs` (+Weekly/Monthly), `Engine/EngineReducer.cs` (`HandleEquityObserved` now folds account+drawdown — wired, not dead), one test (`EngineReducerTests.cs:101`, updated `EquityObserved` ctor).

## 3. The open decision from last round — now RESOLVED

The account/equity slice is added (`AccountView` on `EngineState`, folded by the enriched `EquityObserved` in the reducer), and `KernelConfig` carries the run-constant lookups (`ResolveSymbol`, `ProjectOpenPositions`, `SizingPolicyOptions`). The gate is therefore a pure function of `(state, proposal, config)`. **One residual seam:** `KernelConfig.ProjectOpenPositions` recomputes each open position's worst-case `(slPips*pipValue*lots)`; the clean follow-up is to store `RiskAmount` on `PositionState` at entry (the gate already computes it → `RegisterRisk`) and sum it from state — removing the recompute + cross-rate dependency. Flagged in `IKernel.cs`.

## 4. Integration order (strangler — golden-green after each step; never double-system)

1. **A0** — build the golden replay oracle over the *current* engine; commit the baseline snapshot. (Do this before any cutover.)
2. **A1** — `Datasets`/`ConfigSets` tables + a DB-backed `BarTape : IEventTape` (read `Bars` → `BarClosed`) + `RunSpec` on the run repo. `ListEventTape` is the in-memory reference.
3. **A3 journal sink** — implement `IStepRecordSink` (SQLite `Journal` table, WAL+retry) + NDJSON export endpoint. Wire `ChannelJournalWriter` in. Low-risk, independently valuable.
4. **A2 cutover — one event family at a time, golden-green after each:**
   a. **Evaluator → `OrderProposed`:** make the strategy/indicator stage emit `OrderProposed` (with `SlPips`+`PipValuePerLot`) into the queue instead of calling `OrderDispatcher`. Provide `KernelConfig.ResolveSymbol` + `ProjectOpenPositions`. Verify the gate's accept/reject + sizing match the golden baseline. Then delete `OrderDispatcher`'s gate, `RiskGate.cs`, `RiskGateTests.cs`.
   b. **Account feed → `EquityObserved`:** route venue `AccountUpdate`s in as `EquityObserved`. `Kernel.DecideEquity` now owns the breach watchdog — delete `AccountProcessor:79-115`.
   c. **`BarClosed` exits:** un-comment/finish `EngineReducer.HandleBarClosed`'s `DetectSlTpExit`→`CloseOpenPosition` as the single SL/TP authority; delete `EngineRunner.SimulateBarExitsAsync`.
   d. **Resets:** route day/week/month rolls as events; `Kernel.DecideReset` clears protection (C4) and the reducer resets governor (H7). Delete `RiskManager.OnDailyReset` side-effects; retire the hosted `DailyResetService` (NEW-2); add the prop-firm reset time/zone (NEW-1) to `KernelConfig` + the boundary keying.
   e. **Governor (NEW-9):** keep kernel `GovernorMachine`; retire `TradingGovernorService`.
   f. **Sizing:** delete `TradingEngine.Risk.PositionSizer`/`DrawdownScaler`; `KernelSizing` is the port. Refine `AntiMartingale` (H5) off the trade streak.
   g. **Determinism (NEW-10):** seed `PositionLifecycle.CreateIntended`'s id (the one `Guid.NewGuid` in the decision path) from `(RunSpec.Seed, seq)`; add the Arch test asserting no `DateTime.UtcNow`/`Guid.NewGuid` in `Kernel.cs`/`PreTradeGate.cs`/`EngineReducer.cs`/`KernelSizing.cs`.
   h. Make `EngineState.Protection`/`Account` required once `RiskManager`'s imperative state is gone.
5. **A4** — `ReplayRunner` from a `RunSpec`; "re-run with a different ConfigSet over the same DatasetRef"; the **bit-identical determinism test**; the scenario/invariant harness.
6. **A5** — incremental shared indicator engine feeding the evaluator.
7. **Part A gate**, then Part B/C/D (toggles, venue money, web, charts, live page, perf, frontend).

## 5. Funnel flow (reference)
```
BarTape.ReadAsync → BarClosed → queue → Kernel.Decide → (state', effects)
                                            │                  │
  evaluator (A5) emits OrderProposed ───────┘            IEffectExecutor
  (with SlPips/PipValuePerLot) into the queue            ├ SubmitOrder → broker → OrderFilled ─┐
                                                          ├ CloseOpenPosition → broker          │ feedback
  every processed event → StepRecord → ChannelJournalWriter → IStepRecordSink   AccountUpdate→EquityObserved ◄┘
```

## 6. Known nits / watch-points for you
- **`Kernel.DecideEquity` breach** mirrors `AccountProcessor`'s daily→max→weekly→monthly order using `Sizing.FlattenAtFraction`. Verify the thresholds/`*flatten` semantics match the golden baseline; gate weekly/monthly behind B1 toggles.
- **`Kernel.DecideProposed`** finds the new position by `OrderId` after delegating to the reducer; confirm `PositionLifecycle.CreateIntended` sets `OrderId` (it does) and seed its PositionId (NEW-10).
- **Polymorphic journal serialization:** `KernelDriver` serializes `EngineEffect[]` via STJ; the abstract base serializes only declared members. Add a polymorphic config (or per-type DTOs) when you make the NDJSON export stable — it's part of the determinism contract.
- **`ChannelJournalWriter.Append`** is sync-over-async on a full channel (rare; single-threaded producer). Fine for the driver; revisit if a multi-writer scenario appears.
- **`EngineState.Protection`/`Account`** use the `= null!`+coalesce default so positional ctors still compile; expect to touch positional `new EngineState(...)` test sites when you make them required.
