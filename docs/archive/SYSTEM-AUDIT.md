# Shamshir Trading Engine — Full System Audit

**Audit Date:** 2026-06-19
**Branch:** `iter/31-costs-journal`
**Scope:** All layers — cTrader cBot, NetMQ transport, engine worker, trading loop, risk management, venue adapters, persistence, web API, Angular frontend

---

## Part 1 — System Model: Messaging Flows & Component Wiring

### 1.1 Topology Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                        cTrader Desktop (external process)            │
│  ┌──────────────────┐    ┌──────────────────┐                       │
│  │ TradingEngineCBot │    │ cTrader Server   │                       │
│  │ (C# 6, net48)     │◄──►│ (fills, prices)  │                       │
│  └────┬─────────────┘    └──────────────────┘                       │
│       │ NetMQ DEALER ──────► tcp://localhost:15555 (ROUTER)          │
│       │ NetMQ SUB    ◄────── tcp://localhost:15556 (PUB)             │
└───────┼─────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────────────┐
│                    NetMqMessageTransport (Infrastructure)              │
│  ROUTER socket (port 15555) ──► _routerChannel ──► CTraderBrokerAdapter│
│  PUB socket   (port 15556) ──► _subChannel     ──► CTraderBrokerAdapter│
│  pollNetMqPoller() — single-threaded poll loop                         │
└───────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────────────┐
│              CTraderBrokerAdapter : IBrokerAdapter                     │
│  ┌─────────────┐  ┌──────────────┐  ┌──────────────┐                  │
│  │ HandleBar    │  │ HandleExec   │  │ HandleStats  │                  │
│  │ (lock-step)  │  │ (async exec) │  │ (telemetry)  │                  │
│  └──────┬───────┘  └──────┬───────┘  └──────────────┘                  │
│         │                 │                                            │
│         ▼                 ▼                                            │
│  ┌──────────────┐  ┌──────────────────────────────────┐                │
│  │ BarStream    │  │ ExecutionStream                   │                │
│  │ (DropOldest, │  │ (Wait, 1000 cap)                  │                │
│  │  2000 cap)   │  │                                   │                │
│  └──────┬───────┘  └──────────────┬───────────────────┘                │
│         │                         │                                    │
│         ▼                         ▼                                    │
│  ┌──────────────┐  ┌──────────────────────────────────┐                │
│  │ TickStream   │  │ AccountStream                    │                │
│  │ (DropOldest, │  │ (unbounded channel? — via        │                │
│  │  10000 cap)  │  │  EmitAccountUpdate)              │                │
│  └──────────────┘  └──────────────┬───────────────────┘                │
└───────────────────────────────────┼────────────────────────────────────┘
                                    │
                                    ▼
┌───────────────────────────────────────────────────────────────────────┐
│                        EngineWorker / EngineRunner                     │
│                                                                       │
│  BACKTEST MODE: single-threaded bar loop                               │
│  EngineRunner.RunBacktestLoopAsync()                                   │
│    foreach bar in BarStream:                                           │
│      1. OnBarObserved(bar) → EmitAccountUpdate()                       │
│      2. UpdateCrossRates(bar)                                          │
│      3. Drain AccountStream → AccountProcessor.HandleAsync()           │
│      4. SimulateBarExitsAsync(bar) — close positions hitting SL/TP     │
│      5. ProcessBarAsync(bar) → TradingLoop                             │
│      6. Drain ExecutionStream → ConsumeExecutionsAsync                 │
│      7. Drain AccountStream again                                      │
│                                                                       │
│  LIVE MODE: concurrent task loop                                       │
│  EnginePacers.AsyncStreamPacer.PaceAsync()                             │
│    Task.WhenAll:                                                       │
│      - ProcessTicksAsync (fills, SL/TP)                                │
│      - ProcessBarsAsync (strategy eval)                                │
│      - ProcessAccountUpdatesAsync                                      │
│      - ProcessExecutionEventsAsync                                     │
│      - ConsumeExecutionsAsync                                          │
│      - ProcessAccountQueueAsync                                        │
└───────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────────────┐
│                           TradingLoop                                  │
│                                                                       │
│  ProcessBarAsync(bar):                                                 │
│    1. Publish BarIngested event                                        │
│    2. Add bar to _bars dict (capped at 500 per symbol/tf)              │
│    3. RecomputeIndicatorsAsync(symbol, tf)                             │
│    4. BuildSharedIndicatorSnapshot → ReusableIndicatorDict             │
│    5. Detect regime                                                    │
│    6. GetActive strategies (via StrategyBankService + RunPlan)         │
│    7. signalGate?.OnBar() + governor?.OnBar()                          │
│    8. For each active strategy:                                        │
│       a. Build MarketContext                                           │
│       b. strategy.Evaluate(context) → TradeIntent?                     │
│       c. EntryPlanner.Plan(intent, orderEntryOptions, signalPrice)     │
│       d. signalGate.Check(strategyId, direction) → blocked?            │
│       e. Record SIGNAL journal entry                                   │
│       f. OrderDispatcher.DispatchAsync(intent, equity, mid, broker)    │
│          ├─ RiskManager.Validate() → 8 checks                         │
│          ├─ PositionSizer.Calculate() → lot size                       │
│          ├─ broker.SubmitOrderAsync() → OrderRequest                   │
│          └─ Record ORDER journal entry                                 │
│    9. OnProgress callback (backtest UI progress)                       │
└───────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────────────┐
│                     RiskManager (singleton, shared)                     │
│                                                                       │
│  Validate(intent, equity, profile) → List<RiskViolation>:              │
│    1. PROTECTION_MODE_ACTIVE (InProtectionMode)                        │
│    2. DAILY_DD_LIMIT (DailyDrawdownUsed >= MaxDailyLoss)               │
│    3. MAX_DD_LIMIT (CurrentMaxDrawdown >= MaxTotalLoss)                │
│    4. MAX_POSITIONS (open count >= MaxConcurrentPositions)             │
│    5. STRATEGY_MAX_POSITIONS                                           │
│    6. MAX_EXPOSURE ((currentRisk + newRisk) / equity > max%)           │
│    7. NEWS_WINDOW (stub — always false)                                │
│    8. WEEKEND_RESTRICTION                                              │
│    ⚠ WEEKLY/MONTHLY — NOT CHECKED                                     │
│                                                                       │
│  ValidateOrder(intent, equity, profile) → order sizing:                │
│    - Worst-case projection (daily + max DD floors)                     │
│    - Budget downsizing (halve lots until within budget)                │
│    - Drawdown scaling (via DrawdownScaler)                             │
│                                                                       │
│  AccountProcessor.HandleAsync() — breach watchdog:                     │
│    - Checks DailyDrawdownUsed >= MaxDailyLoss * FlattenAtFraction      │
│    - On breach: EnterProtectionMode() → RequestForceCloseAllAsync()    │
│    - On daily reset: OnDailyReset() → clears DailyDD protection only   │
│    - ⚠ GOVERNOR OnDailyReset NEVER CALLED                             │
└───────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────────────┐
│                        PositionTracker                                 │
│                                                                       │
│  OnExecutionAsync(execEvent):                                          │
│    - OrderId NOT in _pendingOrders → ClosePositionAsync()              │
│      → DetermineExitReason (SL/TP/FORCE/DailyDD/MaxDD)                │
│      → EngineReducer.Apply(state, event) → PublishTradeClosed effect   │
│    - OrderId IN _pendingOrders → HandleOpenPositionAsync()             │
│      → Accumulate partial fills → Create Position                     │
│      → Register with RiskManager + PositionManager                    │
│    - OrderCancelled → ENTRY_EXPIRED journal                            │
│                                                                       │
│  EngineReducer.Apply(state, effect):                                   │
│    → PositionLifecycle FSM: Active → BreakevenSet → Trailing → Closed │
└───────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────────────┐
│                    EffectExecutor (writes to event bus)                │
│                                                                       │
│  HandlePublishTradeClosed:                                             │
│    1. PipCalculator.GrossPnL() → recompute (fallback)                 │
│    2. Use venue-authoritative costs if present on effect               │
│    3. Build TradeResult                                                │
│    4. Publish TradeClosed event → TradePersistenceHandler              │
│    5. Record CLOSE journal entry via PipelineEventWriter               │
│    6. RegisterCompletedTrade()                                         │
│    7. Notify strategies via OnTradeResult()                            │
│                                                                       │
│  HandleRegisterPosition / HandleDeregisterRisk:                        │
│    → RiskManager.RegisterPosition() / DeregisterPosition()             │
│                                                                       │
│  HandlePublishBreach:                                                  │
│    → RiskManager.EnterProtectionMode()                                 │
│    → Record BREACH journal entry                                       │
└───────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────────────┐
│                    Persistence Layer (background handlers)              │
│                                                                       │
│  PipelineEventWriter (3s flush, DropOldest 50k) ⚠                    │
│    → PipelineEvents table                                              │
│                                                                       │
│  TradePersistenceHandler (Wait, 1000 cap)                             │
│    → Trades table                                                      │
│                                                                       │
│  BarEvaluationHandler (3s flush, DropOldest 50k) ⚠                   │
│    → BarEvaluations table                                              │
│                                                                       │
│  EquityPersistenceHandler (5s flush, DropOldest 10k) ⚠               │
│    → EquitySnapshots table                                             │
│                                                                       │
│  ⚠ All compete for single SQLite file — no WAL, no retry               │
└───────────────────────────────────────────────────────────────────────┘
```

### 1.2 Lock-Step Protocol Flow (cTrader path only)

```
cBot (DEALER)                          Engine (ROUTER/PUB)
────────────                           ──────────────────
│                                      │
│ ── {type:"hello", symbols, periods, barsLoaded} ──►
│                                      │  Register cBot identity
│                                      │  Emit NETMQ_CONNECTED status
│                                      │
│ ◄── {type:"hello_ack", v:1} ────────
│                                      │
│ ── {type:"bar", seq:N, symbol, tf,   │
│     openTime, o,h,l,c, volume,       │
│     simTime, account} ──────────────►│
│                                      │  HandleBar(barJson):
│                                      │    Parse bar → write to BarStream
│                                      │    Parse executions from account
│                                      │    Parse ticks from cBot's tick feed
│                                      │
│                                      │  Engine processes bar (see TradingLoop)
│                                      │  Collects commands: submit_order, 
│                                      │    close_position, cancel_order
│                                      │
│ ◄── {type:"bar_done", seq:N,         │
│      commands:[...]} ───────────────│
│                                      │
│ Execute commands synchronously:      │
│   ExecuteSubmitOrder → market order  │
│   ExecuteClosePosition               │
│   ⚠ cancel_order: no handler!        │
│                                      │
│ ── {type:"bar_result", seq:N,        │
│     execs:[{clientOrderId, kind,     │
│     state, fillPrice, grossProfit,   │
│     netProfit, commission, swap,     │
│     lots}], account} ──────────────► │
│                                      │  HandleBarResult:
│                                      │    Parse each exec via ParseExecution
│                                      │    Dedup against _processedSignatures
│                                      │    Write to ExecutionStream
│                                      │    Apply AccountUpdate to AccountStream
│                                      │
│ Between bars (async):                │
│ ── {type:"exec", clientOrderId,      │
│     kind, state, fillPrice, lots,    │
│     grossProfit, netProfit,          │
│     commission, swap} ──────────────►│
│                                      │  HandleExecEvent:
│                                      │    Parse → ExecutionStream
│                                      │
│ ── {type:"stats", barsSent,          │
│     cmdsReceived, ordersExecuted,    │
│     execsSent} ────────────────────► │
│                                      │  HandleStats: reconciliation check
│                                      │
│ ◄── {type:"shutdown"} ──────────────│
│                                      │
│ cBot calls Stop()                    │
```

### 1.3 Backtest Replay Path (credential-free, default)

```
BacktestReplayAdapter.ConnectAsync()
│
├─ Load bars from SQLite Bars table (symbol, timeframe, from, to)
├─ FeedBarsAsync() — loop through bars:
│   ├─ Write bar to BarStream
│   ├─ Synthesize tick (close, close + 0.0001m)
│   ├─ Write tick to TickStream
│   ├─ Emit AccountUpdate
│   ├─ OnBarObserved(bar):
│   │   ├─ Check pending limit orders against bar range
│   │   ├─ Fill market entries at bar close
│   │   ├─ Cancel expired limit orders
│   │   └─ Emit AccountUpdate with fill results
│   └─ Drain ExecutionStream
│
└─ CloseAnyOpenPositions() — force-close remaining
```

### 1.4 Config Flow: UI → Engine

```
Angular UI (new-backtest.component.ts)
  │
  ├─ Form fields: symbols[], timeframes[], date range, balance,
  │   riskProfile (dropdown), venue (dropdown), strategy overrides
  │
  ▼
POST /api/runs  →  RunsController.Start()
  │
  ├─ cfg.CustomParams["RiskProfileId"] = req.RiskProfileId
  ├─ cfg.CustomParams["Venue"] = req.Venue
  │  ⚠ "StrategyOverrides" — NOT SENT (not in StartRunRequest)
  │
  ▼
BacktestOrchestrator.StartAsync(cfg)
  │
  └─ RunAsync(runId, cfg, ct)
       │
       ├─ ResolveEffectiveConfigJsonAsync(cfg)
       │    └─ EffectiveConfigResolver (strategy params only, for audit record)
       │
       ├─ Venue routing:
       │    cfg.CustomParams["Venue"] == "ctrader"  → RunEngineNetMqAsync()
       │    else (or null + appsettings)             → RunEngineReplayAsync()
       │
       └─ BOTH call BuildLoadedConfigFromDbAsync(cfg):

            ┌─ ConfigLoader.LoadBase() ────── JSON files (risk-profiles, prop-firms)
            │
            ├─ IStrategyConfigStore.GetAllAsync() ── DB (seeded from JSON)
            │    └─ RiskProfileId from UI → rewrite ALL strategy configs
            │
            ├─ IRiskProfileStore.GetAllAsync() ── DB (JSON fallback if empty)
            ├─ IPropFirmRuleSetStore.GetAllAsync() ── DB (JSON fallback if empty)
            ├─ IGovernorOptionsStore.GetAsync() ── DB (JSON fallback on error)
            │
            └─ NewsWindows, StrategyRotation, SizingPolicy, Regime
                 ── ALWAYS from JSON (no DB store exists)

            ↓  PreloadedConfig passed to EngineHostOptions

       EngineHostFactory.Create(options)
         └─ AddRiskFromOptions(services, options)
              └─ Registers: RiskManager(singleton), ConstraintSet, Governor, SizingPolicy

       EngineHostFactory.WireRiskRules(host)
         ├─ activeRiskProfileId = first strategy's RiskProfileId ?? "standard"
         ├─ activeProfile = lookup in LoadedConfig.RiskProfiles
         │    ⚠ if null: HARDCODED fallback RiskProfile (1%, 5% max DD, etc.)
         ├─ ruleSet = LoadedConfig.PropFirms[profile.PropFirmRuleSetId ?? "ftmo-standard"]
         ├─ rm.SetActiveRuleSet(ruleSet)
         ├─ rm.SetConstraints(ConstraintSet.Resolve(profile, ruleSet))
         └─ rm.SetSizePipeline(pipeline)
```

### 1.5 Message Types Catalog

| Protocol | Direction | Message Type | Carries | Consumer |
|----------|-----------|-------------|---------|----------|
| NetMQ ROUTER | cBot→Engine | `hello` | symbols, periods, barsLoaded | CTraderBrokerAdapter |
| NetMQ ROUTER | Engine→cBot | `hello_ack` | version | cBot |
| NetMQ ROUTER | cBot→Engine | `bar` | OHLCV, openTime, simTime, account | CTraderBrokerAdapter |
| NetMQ ROUTER | Engine→cBot | `bar_done` | commands[] (submit_order, close_position, cancel_order) | cBot |
| NetMQ ROUTER | cBot→Engine | `bar_result` | execs[] (fills/closes), account | CTraderBrokerAdapter |
| NetMQ ROUTER | cBot→Engine | `exec` | async SL/TP hit between bars | CTraderBrokerAdapter |
| NetMQ ROUTER | cBot→Engine | `stats` | telemetry counters | CTraderBrokerAdapter |
| NetMQ ROUTER | Engine→cBot | `shutdown` | — | cBot |
| NetMQ PUB | Engine→cBot | `diag` | trace lines | cBot (logs as CBOT\|...) |
| Channel | Producer→Consumer | `Bar` | OHLCV bar | TradingLoop (via _bars dict) |
| Channel | Producer→Consumer | `Tick` | bid, ask, timestamp | SimulatedBrokerAdapter |
| Channel | Producer→Consumer | `ExecutionEvent` | fill/close execution | PositionTracker |
| Channel | Producer→Consumer | `AccountUpdate` | balance, equity, floatingPnL | AccountProcessor |
| EventBus | Producer→Handler | `BarIngested` | bar metadata | Observers |
| EventBus | Producer→Handler | `BarEvaluated` | strategy, signal, reason | BarEvaluationHandler |
| EventBus | Producer→Handler | `TradeClosed` | TradeResult | TradePersistenceHandler |
| EventBus | Producer→Handler | `EquityUpdated` | equity, riskState | SseRiskHandler |
| EventBus | Producer→Handler | `DailyReset` / `WeekRolled` etc. | timestamp | RiskManager |
| Journal | Writer | `PipelineEvent` | Seq, RunId, Kind, Detail JSON | PipelineEvents table |
| SignalR | Server→Client | `RunProgress` | RunProgress envelope | Angular run-monitor |
| SignalR | Server→Client | `RunCompleted` | RunSummary | Angular run-detail |
| REST | GET | `/api/runs/{id}/journal` | paged journal entries | Angular run-report |
| REST | GET | `/api/risk-profiles` | profiles[] | Angular new-backtest |
| REST | POST | `/api/runs` | start config | BacktestOrchestrator |

---

## Part 2 — Bug Inventory (69 total)

### 2.1 Critical (14) — Correctness-breaking

| ID | Area | File | Lines | Description |
|----|------|------|-------|-------------|
| **C1** | cTrader | `TradingEngineCBot.cs` | 298-345 | **Limit orders always execute as market** — cBot ignores `orderType`, `limitPrice`, `expiryBars`, `maxSlippagePips`. All orders are `ExecuteMarketOrder`. |
| **C2** | cTrader | `TradingEngineCBot.cs` | 236-266 | **No `cancel_order` handler** — engine sends `cancel_order` command in `bar_done`, cBot has no branch for it. Cancellations silently dropped. |
| **C3** | Risk | `RiskManager.cs` | 186-187 | **Trailing max-DD floor uses `equity.Equity` instead of `equity.PeakEquity`**. `drawdownBase = equity.Equity` for trailing mode. As equity drops, the floor drops — trades that breach the real trailing limit pass the gate. |
| **C4** | Risk | `RiskManager.cs` | 299-307 | **MaxDD protection mode never exits**. Only `ProtectionCause.DailyDrawdown` is cleared at `OnDailyReset()`. MaxDD-caused protection is permanent. `ProtectionResetPolicy` field exists but is **never read**. |
| **C5** | Venue | `SimulatedBrokerAdapter.cs` | 165-166, 279-280, 329-330 | **AccountUpdate param swap** — passes `Equity = 0m, FloatingPnL = _currentBalance` on all writes. Breach watchdog sees zero equity, force-closes all positions immediately. |
| **C6** | Venue | `SimulatedBrokerAdapter.cs` | 171-189 | **`ClosePartialPositionAsync` missing costs/balance update**. No cost computation, no balance update, no `AccountUpdate` emitted. Balance drifts from reality. |
| **C7** | Venue | `SimulatedBrokerAdapter.cs` | 220-221 | **Limit expiry decrements per tick, not per bar**. `ExpiryBarCount--` in `OnTickReceived`. With live tick feed (60+/sec), limits expire in ~50ms. Accidentally works with 1 tick/bar default feed. |
| **C8** | Strategy | `SessionBreakoutStrategy.cs` | 55-56 | **Session range = all-time high/low**. Uses `h1Bars.Max(b => b.High)` over entire bar history instead of filtering to `[RangeStartUtc, RangeEndUtc)` bars. Breakouts nearly never trigger. |
| **C9** | Persistence | `PipelineEventWriter.cs` | 15-16, 52, 67 | **Journal events silently dropped under backpressure**. Channel uses `DropOldest` with 50k cap. `TryWrite` return value discarded — zero observability of drops. |
| **C10** | Persistence | `EquityPersistenceHandler.cs` | 46-47 | **All snapshots get first item's RunId**. When multiple runs' snapshots interleave in channel, every snapshot in batch gets `buffer[0].RunId`. |
| **C11** | Web | `BacktestOrchestrator.cs` | 276, 306, 491-492 | **Replay path cancellation broken**. `RunAsync()` receives user cancel token but `RunEngineReplayAsync()` discards it, creates isolated 30-min timeout. User cancel has zero effect on replay backtests. |
| **C12** | Web | `RunsController.cs` | 88-93 | **Cancel cancels ALL backtests**. `DELETE /api/runs/{runId}` calls `orchestrator.StopAllAsync()`, ignores `runId` parameter. Cancelling one run kills all concurrent runs. |
| **C13** | Web | `BacktestController.cs` + `BacktestAnalyticsController.cs` | line 7, line 6 | **Route collision**. Both controllers declare `[Route("api/backtest")]`. Ambiguous route table. |
| **C14** | Domain | `RiskProfile.cs` | 9 | **`MaxSlPips` defaults to `0`**. `SlTpHelpers.IsSlValid` rejects every trade (`distance > 0` always true). Any `RiskProfile` deserialized without explicit `MaxSlPips` silently blocks all trading. |

### 2.2 High (30) — Significant impact

| ID | Area | File | Lines | Description |
|----|------|------|-------|-------------|
| **H1** | Risk | `RiskManager.cs` | 186 | **Fixed max-DD floor uses `equity.Balance` not `InitialAccountBalance`**. If balance grows (realized profit), floor rises — trades breach real fixed limit pass the gate. |
| **H2** | Risk | `RiskManager.cs` | 103-109 | **Weekly/monthly DD limits never checked in pre-trade gate**. `ConstraintSet.MaxWeeklyLoss`/`MaxMonthlyLoss` exist but `Validate()` checks only daily and total. |
| **H3** | Risk | `RiskGate.cs` | 33 | **Worst-case projection ignores `DailyDdBase`**. Always uses `dailyStartEquity * (1 - pct)`, even when config says `DailyDdBase.InitialBalance`. |
| **H4** | Risk | `RiskGate.cs` | 39 | **Same bug as C3 — trailing floor uses `currentEquity` not peak**. Duplicate of the wrong drawdown base logic. |
| **H5** | Risk | `PositionSizer.cs` | 34-40 | **`AntiMartingale` sizing not implemented**. Switch case has no branch for `AntiMartingale` — falls through silently to `PercentRisk`. Strategy config with AntiMartingale gets PercentRisk with no warning. |
| **H6** | Risk | `PositionSizer.cs` | 36, 55-63 | **`FixedLots`/`FixedDollarRisk` bypass drawdown scaling**. `drawdownScaleFactor` never applied. Account at 80% DD risks same size as at 0%. |
| **H7** | Engine | `AccountProcessor.cs`, `DailyResetService.cs`, `TradingGovernorService.cs` | 72-73, 18-30, 200 | **Governor `OnDailyReset()` never called from any production path**. `_profitLockedToday` once `true`, stays `true` forever. After daily profit-lock triggers, governor permanently blocks ALL new trades. |
| **H8** | Engine | `TradingLoop.cs` | 55-62 | **500-bar cap not configurable**. `RemoveAt(0)` is O(n) on every eviction. Strategies needing >500 warm-up bars silently fail. |
| **H9** | Engine | `EngineRunner.cs` | 236-249 | **Last-bar tail drain skipped on cancellation**. `OperationCanceledException` re-thrown inside foreach skips `AccountStream.TryRead` drain. Final realized PnL never reaches `AccountProcessor`. |
| **H10** | Engine | `EnginePacers.cs`, `RiskManager.cs` | 15-21, 68-72, 100 | **Race on `RiskManager.CurrentState` in live path**. Bar processing and account processing run concurrently via `Task.WhenAll`. `CurrentState` has no synchronization — protection mode entry may not be visible to concurrent signal validation. |
| **H11** | cTrader | `CTraderBrokerAdapter.cs` | 448-457 | **Synthetic close on disconnect has zero fill price**. When cBot disconnects, engine injects `ExecutionEvent` with `Price(0m)`. PnL computed against zero — corrupts trade ledger with massive negative/positive PnL. |
| **H12** | cTrader | `NetMqMessageTransport.cs` | 99, 152, 181 | **Counter semantics wrong**. `_barsReceived` counts all sub messages (ticks, acct, diag). `_commandsSent` counts all outgoing messages. `_executionsReceived` counts all router messages. Reconciliation telemetry permanently mismatched. |
| **H13** | Venue | `BacktestReplayAdapter.cs` | 267-268 | **`FilledLots = 0` on full close**. All `CloseAtAsync` closes emit `ExecutionEvent` with `FilledLots = 0`. Should be trade's lot size. |
| **H14** | Venue | `BacktestReplayAdapter.cs` | 177-178, 227-228 | **Timestamp/price mismatch on fills**. Fill timestamp = `bar.OpenTimeUtc` but fill price = `bar.Close`. Affects `CountNightsHeld` swap calculation for boundary-crossing trades. |
| **H15** | Venue | `BacktestReplayAdapter.cs` | 346-367 | **Floating PnL uses mid (close) not bid/ask**. Unrealized PnL overstates by ~half spread per position. Breach watchdog reads inflated equity. |
| **H16** | Venue | Cross-cutting D2 | — | **Bar-range SL/TP detection overstates fill probability vs tick-based**. Backtest uses raw High/Low without spread. Simulated venue uses bid/ask with spread. Same strategy produces different results. |
| **H17** | Persistence | `BarEvaluationHandler.cs` | 15, 30 | **Bar evaluations silently dropped**. `DropOldest` + `TryWrite` return value ignored. |
| **H18** | Persistence | `BufferedBarWriter.cs` | 12 | **Bars silently dropped**. `DropOldest` on bar persistence channel. |
| **H19** | Persistence | `PipelineEventWriter.cs` | 42, 82-95 | **Flush failure loses entire batch**. `buffer.Clear()` at top of loop iteration — if `SaveChangesAsync` throws, buffered events gone before any retry. Same pattern in `BarEvaluationHandler`. |
| **H20** | Persistence | All handlers | — | **No SQLite write serialization**. 6 independent handlers write to same SQLite file. No WAL mode configured. No retry with backoff. Sporadic "database is locked" data losses. |
| **H21** | Web | `BacktestOrchestrator.cs` | 281-283 | **Unobserved exception leaves run stuck in "starting"**. `ResolveEffectiveConfigJsonAsync` + `WriteStartRecordAsync` outside try/catch. If either throws, run status never updated, finally block never runs, `_progressStore` never completed. |
| **H22** | Web | `BacktestController.cs` | 44-78 | **Missing Venue/RiskProfileId propagation**. Legacy `POST /api/backtest/start` doesn't send `Venue` or `RiskProfileId`. Runs started from this endpoint use DB defaults only. |
| **H23** | Web | `RunsController.cs` + `StartRunRequest.cs` | 46-86, 3-23 | **`StrategyOverrides` never propagated from UI to engine**. `StartRunRequest` has no `StrategyOverrides` field. `ParseOverrides()` always returns empty dict. Per-run parameter tweaks never reach the engine. |
| **H24** | Web | `BacktestOrchestrator.cs` | 474-475 | **`BarCount++` race condition**. Non-atomic increment from concurrent Progress<T> callbacks. Progress bar under-reports on high-frequency backtests. |
| **H25** | Web | `BacktestOrchestrator.cs` | 170-172 | **Journal wall-clock timestamps in monitor**. `DecisionRecordView` entries use `DateTime.UtcNow` instead of sim time. |
| **H26** | Web | `BacktestOrchestrator.cs` | 30, 214 | **Memory leak — `_runs` dict never purged**. `RunProgressBroadcaster._lastSentTicks` also leaks per-run entries. Long-running server exhausts memory. |
| **H27** | Frontend | `scatter-chart.component.ts` | 50-54 | **MAE vs MFE scatter chart broken**. Maps only y-value (`d.y` = MFE), x-value (`d.x` = MAE) discarded. Shows "MFE vs Index" instead. |
| **H28** | Frontend | `run-report.component.ts` | 136 | **Cost reconciliation formula wrong**. Uses `abs(Gross) - abs(Comm) - abs(Swap) - Net` instead of `Gross - Comm - Swap - Net`. Shows false "MISMATCH" badges even when costs are correct. |
| **H29** | Frontend | `run-report.component.ts` | 118 | **Journal filter has invalid `'BAR'` kind**. Backend has no `BAR` kind. Missing `GOVERNOR`, `ENTRY_EXPIRED`, `CANCELLED` filter buttons. |
| **H30** | Strategy | `TrendBreakoutStrategy.cs` | 98-100 | **Bypasses `SlTpResolver`**. Hardcodes `AtrBased` + `RRMultiple`, ignores `StopLoss.Method` and `TakeProfit.Method` from config. |

### 2.3 Medium (21) — Notable issues

| ID | Area | Description |
|----|------|-------------|
| **M1** | cTrader | Partial close in cBot reads commission/swap BEFORE close (line 400-401) — understates by ~50% vs full close which reads after |
| **M2** | cTrader | `_execsSent` counter excludes `bar_result` execs (line 617) — reconciliation permanently shows mismatch |
| **M3** | cTrader | `Stop()` called from NetMQ poller thread (line 557) — cAlgo `Robot.Stop()` should be on main robot thread |
| **M4** | cTrader | Modify confirmations inflate `_execsReceived` counter (line 254) |
| **M5** | cTrader | Dedup signature excludes cost fields (line 547) — corrected cost updates silently dropped |
| **M6** | Risk | `PropFirmRuleValidator.IsProfitTargetMet` uses balance, not equity (line 27-31) |
| **M7** | Risk | Worst-case projection excludes commission/swap costs from candidateLoss (line 162-168) |
| **M8** | Risk | `DrawdownVelocity` only updated at daily reset — stale all day |
| **M9** | Engine | `IndicatorSnapshotService` CancellationToken never checked during recompute |
| **M10** | Venue | `TradeCostCalculator.Compute` silently returns zero costs on exception (line 304) |
| **M11** | Venue | `JournalNormalizer`: `"OrderCancelled"` always maps to `ENTRY_EXPIRED`, never `CANCELLED` (line 36) |
| **M12** | Venue | Missing close reasons in normalizer: `"TRAIL"`, `"BREAKEVEN"`, `"PARTIAL"` (line 9-12) |
| **M13** | Venue | `EntryPlanner` no bounds check on SL/TP prices — negative/overflow with extreme inputs (line 37-50) |
| **M14** | Persistence | Fire-and-forget `PublishAsync` discards handler exceptions (11 instances) |
| **M15** | Persistence | No dedup guard on `TradeResults.PositionId` — duplicate `TradeClosed` inserts two rows |
| **M16** | Persistence | `EquityPersistenceHandler.DisposeAsync` race between drain and cancel loses last items |
| **M17** | Web | Journal API loads ALL events + filters in-memory (line 145) — OOM risk on large runs |
| **M18** | Web | `GovernorOptions` registered as stale singleton, never updated from DB (two sources of truth) |
| **M19** | Web | `BuildLoadedConfigFromDbAsync` bare `catch {}` swallows governor store errors (line 434-438) |
| **M20** | Web | Export CSV endpoint returns header only — no data (ExportController.cs line 11) |
| **M21** | Frontend | `RunSummary` interface missing `GrossPnL`, `CommissionTotal`, `SwapTotal` — cost data invisible on run list |

### 2.4 Low (4) — Minor/Cosmetic

| ID | Area | Description |
|----|------|-------------|
| L1 | Frontend | `EquityChartComponent` double `setData` call + no-op `forEach` loop (line 82-88) |
| L2 | Frontend | Journal replaces instead of appends in live monitor (line 122) — entries disappear on race |
| L3 | Frontend | Breach banner never clears after recovery (line 112) |
| L4 | cTrader | 5-second blocking sleep during cBot hello retry loop (line 125-133) |

---

## Part 3 — Data Flow Inconsistencies

### 3.1 Config Path: What Values Does RiskManager Actually Receive?

When a backtest starts via the Web UI:

| Property | Source | Overridable per-run? |
|----------|--------|---------------------|
| `RiskProfile` (risk%, max positions, exposure) | **DB** (JSON fallback) | ✅ Yes — via RiskProfile dropdown |
| `PropFirmRuleSet` (DD limits, profit target) | **DB** (JSON fallback) | ❌ No — always resolved from profile's `PropFirmRuleSetId` |
| `GovernorOptions` (cooling-off, profit-lock) | **DB** (JSON fallback) | ❌ No |
| `SizingPolicyOptions` (budget use fraction) | **JSON only** (sizing-policy.json) | ❌ No — no DB store exists |
| `RegimeOptions` | **JSON only** (regime.json) | ❌ No — no DB store exists |
| `StrategyRotation` | **JSON only** (rotation.json) | ❌ No — no DB store exists |
| `NewsWindows` | **JSON only** (blocked-windows.json) | ❌ No — no DB store exists |
| Strategy parameters (SL/TP, entry, etc.) | **DB** (seeded from JSON) | ❌ No — `StrategyOverrides` never sent from UI (Bug H23) |
| `EffectiveConfigResolver` | — | ❌ Used only for audit recording, NOT engine injection |

**Bottom line:** Only the RiskProfile ID can be changed per-run from the UI. Everything else uses seeded DB defaults (or JSON for config items with no DB store). Strategy parameter overrides are impossible from the current UI.

### 3.2 Hardcoded Fallback Risk Values

If no matching RiskProfile is found in the DB, `WireRiskRules()` creates a **hardcoded fallback**:

```
RiskPerTradePercent: 1.0%
MaxDailyDrawdownPercent: 5.0%
MaxTotalDrawdownPercent: 10.0%
MaxSlPips: 100
MaxExposurePercent: 10.0%
MaxConcurrentPositions: 5
LotSizingMethod: PercentRisk
KellyFraction: 0.25
AntiMartingaleMultiplier: 1.5
```

**File:** `EngineHostFactory.cs:50-53` and `EngineServiceCollectionExtensions.cs:495-498`

### 3.3 Config Items With No DB Store

These are **always loaded from JSON files** regardless of DB state:
- `SizingPolicy` — `config/sizing-policy.json`
- `Regime` — `config/regime.json`
- `StrategyRotation` — `config/rotation.json`
- `NewsWindows` — `config/news/blocked-windows.json`

If these JSON files are deleted, hardcoded defaults are used (`new SizingPolicyOptions()`, `new RegimeOptions()`, etc.).

### 3.4 Two Start Endpoints — Feature Gap

| Feature | `/api/runs` (RunsController) — New UI | `/api/backtest/start` (BacktestController) — Legacy |
|---------|--------------------------------------|-----------------------------------------------------|
| RiskProfileId | ✅ Sent | ❌ Not sent |
| Venue selection | ✅ Sent | ❌ Not sent |
| StrategyOverrides | ❌ Not sent (not in DTO) | ❌ Not sent |
| Multi-symbol | ✅ Supported | ❌ Single symbol only |
| Backtest detail page | ✅ Report page with charts | ⚠ Legacy page |

### 3.5 Cost Data Flow: 31-A2 Status

The HANDOVER.md lists cBot commission/swap emission (31-A2) as "to carry forward" but the **code is substantially complete**:

| Layer | Status | Detail |
|-------|--------|--------|
| cBot close exec | ✅ **Done** | Full close (line 370-373): emits commission/swap. Partial close (line 406-409): emits scaled values. Venue-initiated (line 611-614): emits full values. |
| Adapter parsing | ✅ **Done** | `ParseExecution` (line 375-376) maps `commission`/`swap` from JSON to `ExecutionEvent`. |
| Engine consumption | ✅ **Done** | `PositionTracker.cs:278-286` stamps cost fields on `PublishTradeClosed`. |
| Journal/trade result | ✅ **Done** | `EffectExecutor.cs:115-118` uses venue-authoritative values with recomputation fallback. |

**Remaining gap:** Partial close reads commission/swap BEFORE close (Bug M1) — may understate.

---

## Part 4 — Angular Frontend Audit

### 4.1 Architecture

- **Framework:** Angular 19+ (standalone components, signals)
- **Source:** `web-ui/src/` (39 TypeScript files)
- **Built output:** `src/TradingEngine.Web/wwwroot/` (38 files, minified)
- **Charts:** Lightweight Charts (TradingView library)
- **Real-time:** SignalR via `@microsoft/signalr` to `RunHub`
- **Routing:** `/`, `/runs`, `/runs/new`, `/runs/:id`, `/runs/:id/analyze`, `/trades`, `/events`, `/strategies`, `/settings`

### 4.2 API → Frontend Data Shape Mismatches

| API Response Field | Frontend Model Expects | Issue |
|--------------------|----------------------|-------|
| `RunListResponse.grossPnL` | `RunSummary` — field missing | Cost data invisible on run list |
| `RunListResponse.commissionTotal` | `RunSummary` — field missing | Same |
| `TradeSummaryResponse` (Dtos/Trades) | `TradeSummary` expects `commissionAmount`, `swapAmount` | Fields exist in Dtos/Runs version but NOT in Dtos/Trades version |
| `JournalEventKind.GOVERNOR` | Frontend has no filter button | Governor events invisible |
| `JournalEventKind.ENTRY_EXPIRED` | Frontend has no filter button | Limit expiry events invisible |
| Frontend `'BAR'` filter | Backend has no `BAR` kind | Filter returns zero results |

### 4.3 Broken Visualizations

1. **MAE vs MFE Scatter** (scatter-chart.component.ts:50-54) — x-value (MAE) discarded, plots MFE vs index
2. **Cost Reconciliation** (run-report.component.ts:136) — formula uses `abs()` on individual terms, producing false mismatches
3. **Equity Chart** (equity-chart.component.ts:82-88) — no-op `forEach` loop, double `setData` call

### 4.4 Stale/Zero Placeholder Data

| Component | Field | Status |
|-----------|-------|--------|
| Dashboard | `tradesToday` | Always 0 (never fetched) |
| Dashboard | `openPositions` | Always 0 (never fetched) |
| Dashboard | `maxDdPct` | Always 0 (never from equity) |
| Settings page | All values | Hardcoded strings |
| Strategy list | `winStreak`, `lossStreak`, `lastRegime` | Always 0 (hardcoded in C# controller) |
| Export CSV | Entire response | Header only, no data rows |

### 4.5 SignalR Issues

1. **`JournalAppend` handler is dead code** — backend `RunProgressBroadcaster` never sends this SignalR message type. Journal data arrives embedded in `RunProgress.RecentJournal`.
2. **`RunCompleted` and `RunProgress` are two separate methods** — frontend must subscribe to both. Angular stores handle both, but no type-check enforces it.
3. **Throttle (`_lastSentTicks`) leaks per-run entries** — never cleaned up if run terminates abnormally.

---

## Part 5 — Database & Persistence Issues

### 5.1 Channel Mode Problems

| Channel | Current Mode | Should Be | Data Loss Risk |
|---------|-------------|-----------|----------------|
| `PipelineEventWriter._channel` | `DropOldest` (50k) | `Wait` | **CRITICAL** — journal audit trail |
| `EquityPersistenceHandler._channel` | `DropOldest` (10k) | `Wait` | **CRITICAL** — equity curve |
| `BarEvaluationHandler._channel` | `DropOldest` (50k) | `Wait` | HIGH — bar analytics |
| `BufferedBarWriter._channel` | `DropOldest` (10k) | `Wait` | HIGH — bar persistence |
| `TradePersistenceHandler._channel` | `Wait` (1k) | ✅ Correct | — |
| `ExecutionStream` (broker) | `Wait` (1k) | ✅ Correct | — |
| `BarStream` (broker) | `DropOldest` (2k) | ✅ Acceptable (market data) | — |
| `TickStream` (broker) | `DropOldest` (10k) | ✅ Acceptable (fill data) | — |

### 5.2 SQLite Concurrency

6 independent background writers compete for the same SQLite file with no serialization:
- PipelineEventWriter (every 3s)
- BarEvaluationHandler (every 3s)
- EquityPersistenceHandler (every 5s)
- TradePersistenceHandler (continuous)
- BufferedBarWriter (continuous)
- ProtectionLedgerPersistenceHandler (synchronous on event)

No WAL mode configured. No retry with backoff. Results: sporadic "database is locked" errors, silently swallowed in catch blocks.

### 5.3 Batch Loss on DB Failure

Both `PipelineEventWriter.FlushLoopAsync` and `BarEvaluationHandler.FlushLoopAsync` call `buffer.Clear()` at the TOP of the loop iteration. If `SaveChangesAsync` throws, the next loop clears the buffer before any retry — items permanently lost.

### 5.4 Missing Indices

| Table | Missing Index |
|-------|--------------|
| `TradeResults` | No `RunId` index (full table scan on run queries) |
| `TradeResults` | No `PositionId` index |
| `EquitySnapshots` | No composite `(RunId, TimestampUtc)` index |
| `BarEvaluations` | No `OccurredAtUtc` index |

### 5.5 Schema Notes

- All numeric/decimal fields stored as `TEXT` in SQLite — prevents numeric comparison in raw SQL
- All datetime fields stored as `TEXT` — OK per design convention
- `EquitySnapshotEntity.Type` always `"Tick"` — dead column, never mapped explicitly
- `EngineEvents` table has entity + repository but NO consumer in engine or web layer — dead code

---

## Part 6 — Key Omissions & Gaps

### 6.1 Risk Rules Not Enforced

| Rule | Status |
|------|--------|
| Daily DD | ✅ Checked in gate + watchdog |
| Max (Total) DD | ✅ Checked in gate + watchdog |
| Weekly DD | ❌ Never checked in pre-trade gate |
| Monthly DD | ❌ Never checked in pre-trade gate |
| Profit Target | ❌ Only checked in `IsProfitTargetMet` (uses balance, not equity); no blocking |
| Protection Mode Exit (DailyDD) | ✅ Cleared at daily reset |
| Protection Mode Exit (MaxDD) | ❌ Never cleared — permanent |
| Governor Daily Reset | ❌ `OnDailyReset()` never called — profit-lock permanent |
| Force Close on Breach | ✅ Implemented |
| Commission-aware budget | ❌ Not implemented (worst-case projection excludes costs) |

### 6.2 Strategy Issues

| Strategy | Issue |
|----------|-------|
| SessionBreakout | Uses global max/min, not session range (Bug C8) |
| TrendBreakout | Hardcodes SL/TP method, ignores config (Bug H30) |
| TrendBreakout | `Stats` allocates new object on every read — WinRate/AvgR always 0 |
| MeanReversion | `RequiredBarCount` omits `RsiPeriod` from `Math.Max` |
| MeanReversion | Requests BollingerBands indicator but never reads it (wasted CPU) |
| EMA Alignment | Reason string says "EMA crossover" but checks alignment, not crossover |

### 6.3 Live Path vs Backtest Discrepancies

| Aspect | Backtest Replay | Simulated (tick) | cTrader Live |
|--------|----------------|------------------|--------------|
| SL/TP detection | Bar range (High/Low) | Tick bid/ask | cTrader server |
| Fill price | Bar close | Next tick bid/ask | cTrader server |
| Spread modeled | Hardcoded 0.0001 | Symbol TypicalSpread | Real market spread |
| Limit orders | ✅ Working | ⚠ Expiry per tick (broken) | ❌ Always market (broken) |
| Cost computation | ✅ Via TradeCostCalculator | ✅ Via TradeCostCalculator | ✅ cBot emits + adapter maps |
| Floating PnL | Mid price (overstated) | Bid/ask (correct) | Real prices |

---

## Part 7 — Fix Sequencing Recommendation

### Phase 1 — Stop Data Loss (today)
1. C9 + C10 + H17 + H18 — Change all persistence channels to `Wait` mode
2. H19 — Move `buffer.Clear()` to after successful `SaveChangesAsync`
3. H20 — Configure SQLite WAL mode + busy timeout + retry

### Phase 2 — Risk Correctness (this week)
4. C3 + H1 — Fix drawdown base: trailing uses PeakEquity, fixed uses InitialBalance
5. C4 + H7 — Wire governor `OnDailyReset()` through `RiskManager` + `DailyResetService`
6. C14 — Set `MaxSlPips` default to `decimal.MaxValue` or sentinel
7. H2 — Add weekly/monthly DD checks to pre-trade gate

### Phase 3 — Venue Correctness (this week)
8. C5 + C6 — Fix `SimulatedBrokerAdapter` AccountUpdate params + partial close costs
9. C7 — Move limit expiry decrement from tick to bar in SimulatedBrokerAdapter
10. C8 — Fix `SessionBreakoutStrategy` session range filtering
11. H11 — Fix synthetic close fill price (use last known price, not zero)
12. H30 — Wire `TrendBreakoutStrategy` through `SlTpResolver`

### Phase 4 — Web & Frontend (next week)
13. C11 + C12 + C13 — Fix cancellation, route collision, controller routing
14. H23 + H22 — Wire `StrategyOverrides` to DTO, propagate Venue/RiskProfileId
15. H27 + H28 + H29 — Fix scatter chart, cost reconciliation, journal filters
16. H21 — Move pre-try methods inside try/catch in RunAsync
17. H25 — Purge `_runs` dictionary on run completion

### Phase 5 — cTrader Integration (next sprint)
18. C1 — Implement `ExecuteSubmitLimitOrder` / `ExecutePlaceLimitOrder` in cBot
19. C2 — Add `cancel_order` handler in cBot command dispatch
20. M1 — Fix partial close commission read timing in cBot

---

## Part 8 — File Index for Agents

For any agent needing to work on a specific area:

### cTrader Adapter
| File | Purpose |
|------|---------|
| `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` | cBot — market data, order execution, NetMQ transport, lock-step protocol |
| `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` | Engine-side NetMQ adapter — message parsing, channel routing, dedup |
| `src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs` | Raw NetMQ socket management — PUB/SUB, ROUTER/DEALER, poller loop |
| `src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransportStatus.cs` | Transport health counters (inflated — Bug H12) |

### Engine Core
| File | Purpose |
|------|---------|
| `src/TradingEngine.Host/EngineWorker.cs` | BackgroundService entry point, DI wiring |
| `src/TradingEngine.Host/EngineRunner.cs` | Backtest bar loop, live task orchestration, reset state |
| `src/TradingEngine.Host/EnginePacers.cs` | Live path concurrent task pacing |
| `src/TradingEngine.Host/TradingLoop.cs` | Per-bar evaluate → plan → gate → dispatch pipeline |
| `src/TradingEngine.Host/EngineHostFactory.cs` | IHost DI composition, WireRiskRules() |
| `src/TradingEngine.Host/EngineServiceCollectionExtensions.cs` | DI registration extensions, AddRiskFromOptions() |
| `src/TradingEngine.Host/AccountProcessor.cs` | Breach watchdog, daily/weekly/monthly reset |
| `src/TradingEngine.Host/IndicatorSnapshotService.cs` | Per-symbol/timeframe indicator computation |
| `src/TradingEngine.Host/StrategyBankService.cs` | Active strategy filtering by regime + RunPlan |
| `src/TradingEngine.Host/EffectExecutor.cs` | Engine effect dispatcher — trade closed, risk register/deregister, breach |
| `src/TradingEngine.Host/ConfigLoader.cs` | JSON config loading from disk |
| `src/TradingEngine.Host/DailyResetService.cs` | 22:00 UTC daily reset scheduler |

### Risk & Sizing
| File | Purpose |
|------|---------|
| `src/TradingEngine.Risk/RiskManager.cs` | Central orchestrator — validate, size, protection mode, drawdown |
| `src/TradingEngine.Risk/PositionSizer.cs` | 5 lot sizing methods |
| `src/TradingEngine.Risk/DrawdownTracker.cs` | Peak equity, daily/max drawdown fractions |
| `src/TradingEngine.Risk/DrawdownScaler.cs` | Linear interpolation scale factor |
| `src/TradingEngine.Risk/PropFirmRuleValidator.cs` | Standalone rule validator (incomplete) |
| `src/TradingEngine.Services/OrderDispatcher.cs` | Signal → order dispatch (validate, size, submit) |
| `src/TradingEngine.Services/PipCalculator.cs` | Distance, PipValue, GrossPnL, FloatingPnL |
| `src/TradingEngine.Services/PositionTracker.cs` | Execution event processing, position lifecycle, reducer |
| `src/TradingEngine.Services/Helpers/TradeCostCalculator.cs` | Gross/Commission/Swap/Net computation (shared) |
| `src/TradingEngine.Services/Helpers/EntryPlanner.cs` | Order type + limit price + SL/TP re-derivation |
| `src/TradingEngine.Services/Helpers/JournalNormalizer.cs` | Event vocabulary → JournalEventKind mapping |
| `src/TradingEngine.Services/Helpers/EffectiveConfigResolver.cs` | Deep-merge stored default ← overrides ← run plan |

### Venue Adapters
| File | Purpose |
|------|---------|
| `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` | Bar replay from SQLite — instant fills, costs, limit orders |
| `src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs` | Tick-driven synthetic broker — costs, fills, SL/TP |

### Persistence
| File | Purpose |
|------|---------|
| `src/TradingEngine.Host/PipelineEventWriter.cs` | Journal event background flusher (3s) |
| `src/TradingEngine.Host/TradePersistenceHandler.cs` | Trade result background flusher |
| `src/TradingEngine.Host/BarEvaluationHandler.cs` | Bar evaluation background flusher (3s) |
| `src/TradingEngine.Host/EquityPersistenceHandler.cs` | Equity snapshot background flusher (5s) |
| `src/TradingEngine.Infrastructure/Persistence/TradingDbContext.cs` | EF Core context + OnModelCreating |
| `src/TradingEngine.Infrastructure/Configuration/StrategyConfigSeeder.cs` | JSON → DB strategy config seed |

### Web API & Orchestration
| File | Purpose |
|------|---------|
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Backtest lifecycle — start, run, cancel, config, progress |
| `src/TradingEngine.Web/Services/BacktestProgressStore.cs` | Per-run progress channel |
| `src/TradingEngine.Web/Services/RunProgressBroadcaster.cs` | SignalR throttle + publish |
| `src/TradingEngine.Web/Api/RunsController.cs` | New backtest API (start, cancel, list, detail, trades, journal) |
| `src/TradingEngine.Web/Api/BacktestController.cs` | Legacy backtest API |
| `src/TradingEngine.Web/Configuration/ServiceRegistration.cs` | DI registrations |
| `src/TradingEngine.Web/Configuration/MiddlewarePipeline.cs` | Startup seeders + SPA fallback |
| `src/TradingEngine.Web/Hubs/RunHub.cs` | SignalR hub for run progress |

### Domain
| File | Purpose |
|------|---------|
| `src/TradingEngine.Domain/Trading/TradeIntent.cs` | Signal → order bridge |
| `src/TradingEngine.Domain/Trading/TradeResult.cs` | Completed trade record |
| `src/TradingEngine.Domain/Trading/Position.cs` | Open position state |
| `src/TradingEngine.Domain/RiskAndEquity/RiskProfile.cs` | Per-strategy risk config |
| `src/TradingEngine.Domain/RiskAndEquity/PropFirmRuleSet.cs` | FTMO rule set |
| `src/TradingEngine.Domain/RiskAndEquity/ConstraintSet.cs` | Resolved constraints from profile + ruleSet |
| `src/TradingEngine.Domain/SymbolInfo/SymbolInfo.cs` | Symbol metadata (pip size, cost fields) |
| `src/TradingEngine.Domain/Engine/EngineReducer.cs` | Pure functional reducer (half-wired) |
| `src/TradingEngine.Domain/Engine/PositionLifecycle.cs` | Position FSM |

### Angular Frontend
| File | Purpose |
|------|---------|
| `web-ui/src/app/app.routes.ts` | Route definitions |
| `web-ui/src/app/models/api.types.ts` | All TypeScript interfaces matching C# DTOs |
| `web-ui/src/app/core/signalr/run-hub.service.ts` | SignalR connection + RxJS subjects |
| `web-ui/src/app/features/runs/runs.service.ts` | HTTP client for runs API |
| `web-ui/src/app/features/runs/new-backtest/new-backtest.component.ts` | Backtest start form |
| `web-ui/src/app/features/runs/run-monitor/run-monitor.component.ts` | Live progress display |
| `web-ui/src/app/features/runs/run-report/run-report.component.ts` | Completed run detail + journal |
| `web-ui/src/app/features/runs/run-analyzer/run-analyzer.component.ts` | MAE/MFE scatter + histograms |
| `web-ui/src/app/features/dashboard/dashboard.component.ts` | Dashboard with stale placeholders |
| `web-ui/src/app/features/trades/trade-list/trade-list.component.ts` | All trades list (missing cost columns) |
| `web-ui/src/app/shared/equity-chart.component.ts` | Equity curve chart (Lightweight Charts) |
| `web-ui/src/app/shared/scatter-chart.component.ts` | Scatter chart (broken — MAE ignored) |
