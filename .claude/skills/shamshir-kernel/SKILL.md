# Skill: shamshir-kernel

# Shamshir Kernel Architecture

The kernel (`TradingEngine.Engine`) is Shamshir's **pure, deterministic decision core**.
It owns: positions, drawdown, protection, governor, order gate, sizing, SL/TP detection,
breach watchdog, and resets. No I/O, no `DateTime.UtcNow`, no `Guid.NewGuid()`.

## Architecture at a glance

```
Production path (TradingLoop):
  Signal → KernelOrderGate.DispatchAsync
    → PreTradeGate.Evaluate (pure)
    → KernelSizing.Calculate
    → broker.SubmitOrderAsync

  Bar → EngineRunner.SimulateBarExitsAsync
    → EngineReducer.DetectSlTpExit (pure static)

  AccountUpdate → AccountProcessor.HandleAsync
    → Kernel.EvaluateDrawdownBreach (pure static, toggle-gated)

Kernel replay path (KernelDriver):
  IEventTape → Kernel.Decide(state, event) → EngineDecision(state', effects)
    → EffectExecutor → journal / broker / risk
```

## Determinism rules (non-negotiable)

1. **No `Guid.NewGuid()`** in `TradingEngine.Engine` — `PositionLifecycle.CreateIntended` uses `orderId` as `positionId`
2. **No `DateTime.UtcNow` / `DateTime.Now`** in Engine — sim-time comes from events
3. **No I/O** inside the reducer — all side effects go through `EngineEffect` → `EffectExecutor`
4. **Body-scan purity test** at `EnginePurityTests.Engine_has_no_GuidNewGuid_or_DateTimeUtcNow_in_source` scans all `.cs` files under `src/TradingEngine.Engine/`
5. **Determinism test** at `DeterminismTests` opens/fills/closes positions, runs twice, asserts byte-identical journal

## Key kernel files

| File | Purpose |
|------|---------|
| `EngineReducer.cs` | Pure state machine: `Apply(state, event) → EngineDecision(state', effects)` |
| `PositionLifecycle.cs` | Per-position FSM: Intended → Submitted → Open → Reducing → Closed |
| `PreTradeGate.cs` | Single pre-trade gate: protection, governor, SL validation, exposure, sizing, worst-case DD |
| `KernelSizing.cs` | Position-sizing math: `Calculate()` + `ComputeScaleFactor()` | 
| `DrawdownReducer.cs` | Drawdown tracking: daily/weekly/monthly/max, resets, velocity |
| `GovernorMachine.cs` | Trading governor: loss bands, cooling-off, streak, profit-lock (also implements `ITradingGovernor` for DI) |
| `Kernel.cs` | Top-level router: `Decide(state, event)` dispatches to gate + reducer. Holds static helpers `EvaluateDrawdownBreach` and `DetectSlTpExit` |
| `KernelDriver.cs` | Replay driver: consumes `IEventTape`, writes `StepRecord` journal |
| `ChannelJournalWriter.cs` | Lossless journal: `Wait` mode channel, retry-on-failure, drains on dispose |

## Cutover pattern: shadow-then-replace

When replacing an imperative path with kernel authority:
1. Expose the kernel logic as a **public static** method
2. Call it **alongside** the old path in production
3. Verify equivalence (golden test stays green)
4. Delete the old imperative twin
5. Run `grep` gate: ensure old code is gone from `src/`

Examples already done:
- `EngineReducer.DetectSlTpExit(TradeDirection, Price, Price?, Bar)` — called from `SimulateBarExitsAsync`
- `Kernel.EvaluateDrawdownBreach(DrawdownState, ConstraintSet, decimal)` — called from `AccountProcessor`

## Adding a new kernel decision

To add a new event handler:
1. Add a branch in `Kernel.Decide(state, event)` 
2. Implement the handler in `EngineReducer` (pure state transition) or `Kernel` (config-dependent policy)
3. If the handler needs impure data (wall-clock, external service), compute it outside the kernel and pass it in as a parameter or via `ExternalVerdicts`
4. Add a test in `ScenarioInvariantTests` or `KernelAcceptanceTests`
5. Verify `golden-snapshot.json` unchanged or re-baseline with documented reason

## State model

```
EngineState {
  Positions: Dictionary<Guid, PositionState>  // keyed by PositionId (= OrderId)
  Governor: GovernorState
  Drawdown: DrawdownState
  OpenPositionCount: int
  Protection: ProtectionState
  Account: AccountView
}
```

## Key invariants

- `PositionId == OrderId` (deterministic, since AF1)
- `ProtectionState.ClearsOn(boundary)` is the single place that decides protection exit
- Drawdown uses `GetMaxDrawdownFloor(MaxTotalLoss)` → Trailing: PeakEquity, Fixed: InitialBalance
- Weekly/Monthly DD checked only when `WeeklyDdEnabled`/`MonthlyDdEnabled` toggles are on
- `MaxSlPips <= 0` means "no SL distance limit"

Base directory for this skill: file:///C:/Code/Shamshir/.claude/skills/shamshir-kernel
