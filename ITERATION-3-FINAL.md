# Shamshir — Iteration 3 Final Assessment

> Prepared: 2026-06-06
> Scope: Deep-dive code review of iteration 3 output. Covers data flow correctness,
>        state synchronization, concurrency, asset support, strategy composition,
>        edge cases, and iteration 4 planning.

---

## 1. What Iteration 3 Delivered

### Delivered ✅

- `IRiskProfileResolver` + `RiskProfileResolver` — resolves profile from loaded config by ID
- `IRiskManager.Validate()` — updated signature accepts `RiskProfile`; per-strategy position cap added
- `IRiskManager.RegisterPosition()` — updated signature includes `strategyId`
- `RiskManager._openPositionRisk` — now `Dictionary<Guid, (string StrategyId, decimal Risk)>`
- `SimulatedBrokerAdapter` — accepts `ISymbolInfoRegistry`, uses `symbolInfo.PipSize` for slippage
- `PositionManager` — stores `_tracked`, evaluates breakeven and trailing stop
- `SlTpCalculator` and `TrailingStopService` — delegate to helpers (no more `NotImplementedException`)
- `PersistenceService` — wraps repositories, fire-and-forget with error logging
- `OrderDispatcher` and `PositionTracker` — registered in DI (but not wired into EngineWorker — see below)
- `EngineWorker` — `_currentEquity` field + `Volatile.Read/Write`, cross-rate injected, `_riskProfileResolver` injected
- `Program.cs` — loads 16 symbols from `defaults.json`, mode read from config, slippage configurable
- `EquityUpdated` event — defined and emitted from `HandleAccountUpdate`
- `HandleAccountUpdate` — writes to `_currentEquity`, calls `_riskManager.OnEquityUpdate`, publishes event

### Critical Regressions / Incomplete

The refactoring created `OrderDispatcher` and `PositionTracker` but **neither appears in
`EngineWorker`'s constructor**. Both are registered in DI and never consumed. `EngineWorker`
still contains its own duplicated order-dispatch and position-tracking logic (317 lines —
Phase 3E goal of < 150 lines was not achieved).

---

## 2. Bugs Found — Deep Code Review

### CRITICAL

**C-1: EngineWorker lot sizing still uses hardcoded inline RiskProfile**
File: `src/TradingEngine.Host/EngineWorker.cs:139-141`
```csharp
var lots = _riskManager.CalculateLotSize(intent, equity,
    new RiskProfile("standard", "Standard", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo-standard"));
```
`profile` is correctly resolved on line 124 and passed to `Validate()`, but `CalculateLotSize`
still receives a hardcoded inline object. The per-strategy risk config is still ignored for sizing.
This is the iteration 2 C-4 bug: present through all three iterations unfixed.

**C-2: RecomputeIndicatorsAsync still casts to TrendBreakoutStrategy**
File: `src/TradingEngine.Host/EngineWorker.cs:289`
```csharp
var p = (strategy as TrendBreakoutStrategy)?.GetParameters();
if (p is null) continue;
```
The C-2 bug was not fixed in iteration 3. Any second strategy gets no indicator values.
`OrderDispatcher` was created to fix this via `RequiredIndicators` but is never used.

**C-3: WarmUpIndicatorsAsync still hardcodes `Symbol.Parse("EURUSD")`**
File: `src/TradingEngine.Host/EngineWorker.cs:308`
No-op warm-up still present. Not fixed.

**C-4: SimulatedBrokerAdapter never emits `AccountUpdate` — all equity checks use zeros**
`_currentBalance` field exists (with `#pragma warning disable IDE0044` suppressing "make
readonly") but is never mutated in `OnTickReceived` when a position closes. `DataFeedService`
no longer emits `AccountUpdate` (D31 fix applied). Result: **no `AccountUpdate` is ever
emitted in backtest mode.** `_currentEquity` in `EngineWorker` remains `DateTime.MinValue / all-zeros`
for the entire run. Every risk check and lot-size call uses zero equity.

**C-5: `PersistenceService` registered as `AddScoped` but consumed by singleton `EngineWorker`**
File: `src/TradingEngine.Host/Program.cs:113`
```csharp
builder.Services.AddScoped<PersistenceService>();
```
`EngineWorker` is a singleton (via `AddHostedService`). Injecting a scoped service into a
singleton throws `InvalidOperationException` at startup — **the engine cannot start.**

**C-6: Pipe length-prefix reader does not handle partial 4-byte reads**
File: `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs:70-72`
```csharp
var bytesRead = await _pipeServer.ReadAsync(buffer, 0, 4, ct);
if (bytesRead < 4) break;
```
Named pipes can return fewer than 4 bytes per `ReadAsync`. 2 bytes in → `bytesRead < 4` →
breaks the read loop — silently terminating the connection. All subsequent messages lost.
The message-body loop (lines 75-80) correctly handles partial reads but the header does not.

### SERIOUS

**S-1: `DrawdownTracker.CurrentDailyDrawdown` uses wrong base for FTMO**
File: `src/TradingEngine.Risk/DrawdownTracker.cs:34`
```csharp
var dailyDd = DailyStartEquity > 0
    ? (DailyStartEquity - equity) / DailyStartEquity
    : 0m;
```
FTMO defines the daily floor as `InitialAccountBalance × (1 − 0.05)`, meaning the
percentage basis is `InitialAccountBalance`, not `DailyStartEquity`. If the account is at
$104,000 in profit and loses $5,000:
- Current formula: 5000/104000 = 4.8% → does NOT trigger the 5% limit
- FTMO correct:    5000/100000 = 5.0% → DOES trigger

Accounts in profit can exceed the FTMO daily limit without triggering protection mode.

**S-2: `RiskManager.Validate` drawdown checks compare fractions to percentages**
File: `src/TradingEngine.Risk/RiskManager.cs:41-44`
```csharp
if (equity.CurrentDailyDrawdown >= (decimal)_activeRuleSet.MaxDailyLossPercent)
```
`CurrentDailyDrawdown` is a fraction (e.g., `0.045` for 4.5%). `MaxDailyLossPercent` is
a percentage (e.g., `5.0`). The check reads `0.045 >= 5.0` → always false.
**The DD risk gates never trigger.** Both daily and max DD checks have this bug.

**S-3: `PositionManager.Evaluate` passes wrong values to `AtrTrail`**
File: `src/TradingEngine.Services/PositionManager.cs:40-44`
```csharp
var atrTrail = TrailingHelpers.AtrTrail(
    position,
    currentTick.Bid > position.EntryPrice.Value ? currentTick.Bid : position.EntryPrice.Value,
    currentTick.Ask < position.EntryPrice.Value ? currentTick.Ask : position.EntryPrice.Value,
    config.TrailingStop.AtrMultiple,   // ← "currentAtr" param — should be ATR in price, not the multiplier
    config.TrailingStop.AtrMultiple,   // ← "multiplier" param — same value, both wrong
    symbolInfo);
```
`AtrTrail(position, highestBidSinceEntry, lowestAskSinceEntry, currentAtr, multiplier, symbol)`:
(1) `currentTick.Bid` is not the highest bid since entry — needs a running high-water mark;
(2) `config.TrailingStop.AtrMultiple` (e.g., `2.0`) is used as `currentAtr` — should be
the actual ATR price value (e.g., `0.0021`). The trailing stop produces garbage.

**S-4: `_latestRiskAmount` race — wrong amount assigned when two strategies fire same tick**
File: `src/TradingEngine.Host/EngineWorker.cs:25,142,243`
Single `decimal` field overwritten per-intent in the strategies loop. If EURUSD intent
writes `50m` then USDJPY intent writes `30m`, the EURUSD position is registered with
`30m` risk when its `ExecutionEvent` arrives. Exposure tracking is miscounted.

**S-5: Breakeven fires repeatedly after triggered**
`Position` is an immutable record; `CurrentStopLoss` is never updated by `PositionManager`
after the initial open. `Breakeven` recalculates every tick and re-emits `MoveStopLoss`
each time the condition holds. The broker receives repeated `ModifyOrderAsync` calls for
the same SL value. Needs a one-shot `_beApplied HashSet<Guid>`.

**S-6: Indicator values shared across symbols in flat dictionary**
File: `src/TradingEngine.Host/EngineWorker.cs:19`
```csharp
private readonly ConcurrentDictionary<string, double> _indicatorValues = new();
```
Key `"ATR_14"` on EURUSD and `"ATR_14"` on USDJPY collide. USDJPY ATR (~0.14) overrides
EURUSD ATR (~0.0021). Both strategies evaluate signals against wrong indicator values.

**S-7: `OrderDispatcher` generates an orderId that never matches any broker response**
File: `src/TradingEngine.Services/OrderDispatcher.cs:38-41`
```csharp
var orderId = Guid.NewGuid();
await broker.SubmitOrderAsync(orderReq, ct);
return new OrderContext(orderId, ...);
```
`SimulatedBrokerAdapter.SubmitOrderAsync` generates its OWN `Guid.NewGuid()` as the
pending order key. The `orderId` from `OrderDispatcher` never matches any execution event.
If `PositionTracker.TrackOrder(orderId, ...)` is called, the order is never matched to its
fill — positions are never opened.

### MODERATE

**M-1: `_bars` list grows unbounded — O(n) memory and allocation growth**
Every bar appended forever. `BuildBarSnapshot` does `list.ToList()` (full copy) on every
tick. 1-year H1 backtest: 8,760 bars × 4 ticks × ~40 bytes = 1.4MB per symbol per year
of list copies, causing progressive GC pressure.

**M-2: `_indicatorValues.ToDictionary()` on every tick — hot allocation**
Line 117: new `Dictionary<string, double>` per strategy per tick. Unnecessary — can pass
`_indicatorValues` directly or use `IReadOnlyDictionary` wrapper.

**M-3: Engine mode hardcoded in startup log**
File: `src/TradingEngine.Host/EngineWorker.cs:73`
`EngineMode.Backtest` is always logged; actual runtime mode is ignored.

**M-4: Aspire `ENGINE_MODE` env var uses wrong naming convention**
File: `aspire/TradingEngine.AppHost/AppHost.cs:4`
`.WithEnvironment("ENGINE_MODE", "Backtest")` → ASP.NET Core maps `ENGINE_MODE` as a
single flat key, not `Engine:Mode`. Must use `Engine__Mode` (double underscore).

**M-5: Aspire `WithReference(engine)` has no effect**
Engine exposes no HTTP endpoints. `WithReference` injects unused service-discovery env vars.
Intended integration (shared DB path) is not implemented in Aspire at all.

**M-6: `HistoricalDataProvider` uses hardcoded half-spread 0.00005m**
File: `src/TradingEngine.Infrastructure/Adapters/HistoricalDataProvider.cs:50`
USDJPY should be `0.005`, XAUUSD should be `0.15`. All symbols simulate EURUSD spread.

**M-7: Tick synthesis always derives from H1 bars**
A strategy requesting M15 bars gets M15 OHLCV data but ticks synthesized from H1 — timing
is misaligned.

**M-8: Pipe parse errors use `Debug.WriteLine` — invisible in production**
File: `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs:149`
All pipe message parse failures are completely silent in production.

**M-9: `SimulatedBrokerAdapter` default no-arg constructor creates its own `SymbolInfoRegistry`**
Test harness uses the no-arg constructor, which registers only EURUSD. USDJPY tests get
wrong slippage and will throw `KeyNotFoundException` on symbol lookup.

**M-10: `PositionTracker.ClosePosition` hardcodes `"standard"` risk profile and `EngineMode.Backtest`**
File: `src/TradingEngine.Services/PositionTracker.cs:79-80`
All trade records in the DB show risk profile `"standard"` and mode `Backtest` regardless
of actual runtime values.

---

## 3. Data Flow Coherence

### 3.1 Current Backtest Flow

```
CSV
  → HistoricalDataProvider.StreamBarsAsync/StreamTicksAsync
  → DataFeedService
      → BarWriter      → [BarChannel]   → EngineWorker.ProcessBarsAsync
      → TickWriter     → [TickChannel]  → EngineWorker.ProcessTicksAsync
      → OnTickReceived → SimulatedBrokerAdapter
                            [fills pending orders]
                            [checks SL/TP on open positions]
                            → ExecutionWriter → [ExecutionChannel] → EngineWorker.ProcessExecutionEventsAsync
                            MISSING → AccountWriter (D31 fix was not applied)
```

**Gap:** No `AccountUpdate` ever emitted → `_currentEquity` is zeros for entire run.

### 3.2 Live-Mode State Sync Problems

**Missing: startup reconciliation.** On engine start in live mode, the engine must request
the current account snapshot (balance, equity, open positions, pending orders) from cTrader
and reconcile its internal state before accepting new signals. Without this, an engine
restart mid-session sees no open positions and may trade beyond its intended exposure.

**Missing: disconnect handling.** If the pipe disconnects, the engine should:
1. Enter protection mode (block new signals)
2. Attempt reconnect (up to 3 times, exponential backoff: 2s, 4s, 8s)
3. Re-sync state after reconnect
4. Resume trading only after successful reconciliation

**Missing: periodic re-sync.** Every 30 minutes (configurable), request a full account
snapshot to detect any drift (cTrader-side manual modifications, missed events).

**Source of truth:** Broker is always right. Engine internal state is a cache. When they
diverge, discard engine state and reload from broker.

### 3.3 Market Gap Handling

When price gaps past the SL:
- cTrader fills at the gap price, not the SL price
- `ExecutionEvent.FillPrice` contains the actual gap fill price
- `PositionTracker.ClosePosition` uses actual fill price for PnL — **correct** ✅
- Risk implication: loss may be N× the planned 1R

**Window of risk after gap:** The engine may evaluate strategy signals BETWEEN the gap fill
and the next `AccountUpdate`. If the gap loss pushes equity below the DD limit, the engine
could open another trade before knowing it's in breach.

**Fix:** After every position-close `ExecutionEvent`, immediately compute conservative
equity = `Balance + sum(remaining open positions' floating PnL at worst case)` before
evaluating new signals. Don't wait for the next `AccountUpdate`.

---

## 4. Multi-Strategy Correctness Review

### 4.1 Indicator Namespace Fix (D37)

Change indicator key from `"ATR_14"` to `"EURUSD:ATR_14"`:
```csharp
// In RecomputeIndicatorsAsync:
foreach (var req in strategy.RequiredIndicators)
{
    var key = $"{symbol}:{req.Key}";
    _indicatorValues[key] = req.Type switch
    {
        IndicatorType.Atr => _indicators.Atr(bars, req.Period),
        IndicatorType.Ema => _indicators.Ema(bars, req.Period),
        IndicatorType.Rsi => _indicators.Rsi(bars, req.Period),
        _ => throw new NotSupportedException($"Indicator {req.Type}")
    };
}

// In MarketContext construction:
var indicators = _indicatorValues
    .Where(kv => kv.Key.StartsWith($"{tick.Symbol}:"))
    .ToDictionary(kv => kv.Key[(tick.Symbol.ToString().Length + 1)..], kv => kv.Value);
```

### 4.2 Risk Amount Race Fix (S-4)

Replace the single `_latestRiskAmount` field with a per-order dictionary:
```csharp
private readonly Dictionary<Guid, decimal> _pendingOrderRisk = new();
// After order submitted: _pendingOrderRisk[orderId] = riskAmount;
// In HandleExecutionEvent: var risk = _pendingOrderRisk.GetValueOrDefault(evt.OrderId, 0m);
//                          _pendingOrderRisk.Remove(evt.OrderId);
```

---

## 5. Strategy Composition Architecture

### 5.1 Problem

`TrendBreakoutStrategy` contains: signal logic, SL/TP calculation, session awareness, and
state tracking. Adding a second strategy requires duplicating this structure. Adding
"London session only" means editing each strategy separately.

### 5.2 Proposed Composition Model

Keep `IStrategy` unchanged (backward compatible). Add four new interfaces for composed
strategies:

```csharp
// Produces trade direction + reason; knows nothing about sizing or SL/TP
public interface ISignalProvider
{
    string SignalId { get; }
    IReadOnlyList<IndicatorRequest> RequiredIndicators { get; }
    int RequiredBarCount { get; }
    (TradeDirection Direction, string Reason)? Evaluate(MarketContext context);
}

// Returns false to block a signal
public interface IEntryFilter
{
    bool Allows(MarketContext context);
}

// Computes concrete SL and TP prices from entry
public interface IExitBehavior
{
    Price ComputeStopLoss(Price entry, TradeDirection dir, MarketContext ctx, SymbolInfo sym);
    Price? ComputeTakeProfit(Price entry, Price sl, TradeDirection dir, MarketContext ctx, SymbolInfo sym);
}

// Applied per-tick to open positions; returns modifications
public interface IPositionBehavior
{
    string BehaviorId { get; }
    IReadOnlyList<PositionModification> Evaluate(
        Position pos, Tick tick, IReadOnlyDictionary<string, double> indicators, SymbolInfo sym);
}
```

`IStrategy` gains one new property:
```csharp
IReadOnlyList<IPositionBehavior> PositionBehaviors { get; }
```

`PositionManager.Evaluate` reads behaviors from the strategy's registered config instead
of the hardcoded `TrailingConfig` switch.

### 5.3 Built-in Entry Filters

```csharp
// Only trade during specific UTC hours
public sealed class SessionFilter(TimeOnly openUtc, TimeOnly closeUtc) : IEntryFilter
{
    public bool Allows(MarketContext ctx)
    {
        var t = TimeOnly.FromDateTime(ctx.EngineTimeUtc);
        return t >= openUtc && t < closeUtc;
    }
}

// Block if spread exceeds N pips
public sealed class MaxSpreadFilter(decimal maxPips, ISymbolInfoRegistry reg) : IEntryFilter
{
    public bool Allows(MarketContext ctx)
    {
        var sym = reg.Get(ctx.Symbol);
        var spreadPips = (ctx.LatestTick.Ask - ctx.LatestTick.Bid) / sym.PipSize;
        return spreadPips <= maxPips;
    }
}

// Block if ATR outside range (avoid choppy / explosive markets)
public sealed class AtrRangeFilter(string atrKey, double minAtr, double maxAtr) : IEntryFilter
{
    public bool Allows(MarketContext ctx)
    {
        var atr = ctx.IndicatorValues.GetValueOrDefault(atrKey);
        return atr >= minAtr && atr <= maxAtr;
    }
}
```

### 5.4 Built-in Position Behaviors

```csharp
// Fires exactly once when profit >= triggerR × initial risk
public sealed class BreakevenBehavior(double triggerR, Pips buffer) : IPositionBehavior
{
    private readonly HashSet<Guid> _applied = [];
    public string BehaviorId => "breakeven";

    public IReadOnlyList<PositionModification> Evaluate(
        Position pos, Tick tick, IReadOnlyDictionary<string, double> indicators, SymbolInfo sym)
    {
        if (_applied.Contains(pos.Id)) return [];
        var newSl = TrailingHelpers.Breakeven(pos, tick.Bid, tick.Ask, triggerR, buffer, sym);
        if (newSl is null) return [];
        _applied.Add(pos.Id);
        return [new MoveStopLoss(pos.Id, newSl.Value)];
    }
}

// ATR trailing with correct high-water / low-water tracking
public sealed class AtrTrailingBehavior(string atrKey, double multiplier) : IPositionBehavior
{
    private readonly Dictionary<Guid, decimal> _highWater = [];
    private readonly Dictionary<Guid, decimal> _lowWater = [];
    public string BehaviorId => "atr-trail";

    public IReadOnlyList<PositionModification> Evaluate(
        Position pos, Tick tick, IReadOnlyDictionary<string, double> indicators, SymbolInfo sym)
    {
        if (pos.Direction == TradeDirection.Long)
            _highWater[pos.Id] = Math.Max(_highWater.GetValueOrDefault(pos.Id, pos.EntryPrice.Value), tick.Bid);
        else
            _lowWater[pos.Id] = Math.Min(_lowWater.GetValueOrDefault(pos.Id, pos.EntryPrice.Value), tick.Ask);

        var atr = indicators.GetValueOrDefault(atrKey);
        var hw = _highWater.GetValueOrDefault(pos.Id, pos.EntryPrice.Value);
        var lw = _lowWater.GetValueOrDefault(pos.Id, pos.EntryPrice.Value);
        var newSl = TrailingHelpers.AtrTrail(pos, hw, lw, atr, multiplier, sym);
        return newSl.HasValue ? [new MoveStopLoss(pos.Id, newSl.Value)] : [];
    }
}

// Close position when time reaches session end
public sealed class SessionExitBehavior(TimeOnly closeUtc) : IPositionBehavior
{
    public string BehaviorId => "session-exit";

    public IReadOnlyList<PositionModification> Evaluate(
        Position pos, Tick tick, IReadOnlyDictionary<string, double> indicators, SymbolInfo sym)
    {
        var now = TimeOnly.FromDateTime(tick.TimestampUtc);
        return now >= closeUtc ? [new ClosePosition(pos.Id, "SessionEnd")] : [];
    }
}

// Close fraction at N-R profit; fires once
public sealed class PartialCloseBehavior(double triggerR, decimal closeFraction) : IPositionBehavior
{
    private readonly HashSet<Guid> _applied = [];
    public string BehaviorId => $"partial-{triggerR}R";

    public IReadOnlyList<PositionModification> Evaluate(
        Position pos, Tick tick, IReadOnlyDictionary<string, double> indicators, SymbolInfo sym)
    {
        if (_applied.Contains(pos.Id)) return [];
        var slDist = Math.Abs(pos.EntryPrice.Value - pos.CurrentStopLoss.Value);
        var inProfit = pos.Direction == TradeDirection.Long
            ? tick.Bid - pos.EntryPrice.Value
            : pos.EntryPrice.Value - tick.Ask;
        if (inProfit < slDist * (decimal)triggerR) return [];
        _applied.Add(pos.Id);
        var lots = Math.Floor(pos.Lots * closeFraction / sym.LotStep) * sym.LotStep;
        return lots > 0 ? [new PartialClose(pos.Id, lots)] : [];
    }
}
```

### 5.5 Three New Strategies

**EmaAlignment** — dual EMA crossover + ATR filter:
- Signal: fast EMA (e.g., 20) crosses above slow EMA (e.g., 50) → Long
- Entry filters: `SessionFilter(07:00, 20:00)`, `MaxSpreadFilter(2.0)`, `AtrRangeFilter`
- Exit: AtrSL + 2R TP
- Behaviors: `BreakevenBehavior(1.0R)` + `AtrTrailingBehavior`

**MeanReversion** — RSI + Bollinger Band confluence:
- Signal: RSI < 30 + price touches lower BB → Long; RSI > 70 + upper BB → Short
- Entry filters: `SessionFilter(07:00, 16:00)` (London only; mean reversion less reliable overnight)
- Exit: ATR SL + 1R TP (mean reversion exits at TP, does not trail)
- Behaviors: `BreakevenBehavior(0.5R)` only

**SessionBreakout** — London open range breakout:
- Signal: establishes range 05:00–07:00 UTC; breakout above/below range at open
- Entry filters: `SessionFilter(07:00, 09:00)` (open window only), `MaxSpreadFilter(1.5)`
- Exit: Swing SL (range boundary) + 2R TP
- Behaviors: `SessionExitBehavior(12:00 UTC)` (flatten by noon regardless of P&L)

---

## 6. Dynamic Lot Sizing — Fluent Design

### 6.1 Extend LotSizingMethod

```csharp
public enum LotSizingMethod
{
    PercentRisk,      // current behavior: risk X% of equity (default)
    FixedLots,        // always the same number of lots
    FixedDollarRisk,  // always risk $N per trade
    KellyFraction,    // fractional Kelly criterion (requires win rate + avg R)
    AntiMartingale,   // increase after wins, decrease after losses
}
```

Add to `RiskProfile`:
```csharp
public record RiskProfile(
    // ... existing 12 fields ...
    LotSizingMethod LotSizingMethod = LotSizingMethod.PercentRisk,
    decimal FixedLots = 0.1m,
    decimal FixedDollarRisk = 0m,
    double KellyFraction = 0.25,
    double AntiMartingaleMultiplier = 1.5,
    int AntiMartingaleMaxSteps = 3
);
```

`PositionSizer.Calculate` dispatches on method:
```csharp
public static decimal Calculate(
    EquitySnapshot equity, RiskProfile profile,
    Pips slDistance, decimal pipValue,
    decimal drawdownScale, SymbolInfo symbol,
    StrategyStats stats)
    => profile.LotSizingMethod switch
    {
        LotSizingMethod.FixedLots       => Floor(profile.FixedLots, symbol),
        LotSizingMethod.PercentRisk     => CalculatePercentRisk(equity, profile, slDistance, pipValue, drawdownScale, symbol),
        LotSizingMethod.FixedDollarRisk => CalculateFixedDollar(profile.FixedDollarRisk, slDistance, pipValue, symbol),
        LotSizingMethod.KellyFraction   => CalculateKelly(equity, profile, stats, slDistance, pipValue, symbol),
        LotSizingMethod.AntiMartingale  => CalculateAntiMartingale(equity, profile, stats, slDistance, pipValue, symbol),
        _ => symbol.MinLots
    };
```

`StrategyStats` (add to `IStrategy`):
```csharp
public record StrategyStats(
    int ConsecutiveWins,
    int ConsecutiveLosses,
    double WinRateLast20,   // rolling 20-trade win rate
    double AvgRLast20       // rolling 20-trade average R multiple
);
```

---

## 7. Position State Machine

### 7.1 States

```
Pending (order submitted, not filled)
  → Active (filled; no modifications yet)
      → BreakevenSet (SL moved to breakeven)
          → Trailing (trailing stop active)
      → Trailing (trailing started before breakeven)
      → PartialClosed (partial close applied; remaining lots still open)
  → Rejected (fill failed)
  → Closed (SL/TP hit, time exit, force close)
```

### 7.2 Track State in PositionManager

```csharp
public enum PositionLifecycleState
{
    Active, BreakevenSet, Trailing, PartialClosed, Closed
}

// In _tracked:
private readonly Dictionary<Guid, (Position Pos, PositionManagementConfig Config, PositionLifecycleState State)> _tracked = new();
```

Log every state transition:
```csharp
logger.LogInformation("Position state changed. Id={Id} From={From} To={To}", pos.Id, prevState, newState);
```

This makes it trivial to debug "why is this position still open?" or "did breakeven fire?".

---

## 8. Edge Cases — Live Trading

### 8.1 Order Rejection

`ExecutionEvent.NewState == OrderState.Rejected` — currently the engine returns early and
leaves the order in `_pendingOrdersMap` forever (memory leak + position tracking corruption
for orders submitted after this one that happen to share an index).

**Fix:** On rejection:
```csharp
if (evt.NewState == OrderState.Rejected)
{
    _pendingOrdersMap.Remove(evt.OrderId);
    _logger.LogWarning("Order rejected. Id={Id} Reason={Reason}", evt.OrderId, evt.RejectionReason);
    return;
}
```

### 8.2 Partial Fill

cTrader may partially fill a large order. A second fill event arrives for the remaining
lots. Current code: on second fill, `_pendingOrdersMap.TryGetValue` succeeds again →
creates a second `Position` for the same order → duplicate position in `_openPositionsMap`.

**Fix:** Track `FilledLots` cumulative per order:
```csharp
private readonly Dictionary<Guid, (OrderRequest Order, decimal FilledLots)> _pendingOrdersMap = new();
// On fill: FilledLots += evt.FilledLots; only remove from map when FilledLots >= requested
```

### 8.3 Duplicate ExecutionEvent on Reconnect

cTrader may resend events. A second `Filled` for the same `OrderId` → tries to open a
position that already exists or close one already closed.

**Fix:** `HashSet<Guid> _processedExecutionIds` — skip if already seen.

### 8.4 Force Close on DD Breach

`PropFirmRuleSet.ForceCloseOnBreach` field exists but is never read. When protection mode
is entered with `ProtectionCause.MaxDrawdown` AND `ForceCloseOnBreach == true`, all open
positions must be closed immediately:

```csharp
// In RiskManager.EnterProtectionMode:
if (cause == ProtectionCause.MaxDrawdown && _activeRuleSet?.ForceCloseOnBreach == true)
    ForceCloseAll?.Invoke();  // delegate registered by EngineWorker
```

Or preferably via the event bus: publish `ForceCloseRequested` event; `EngineWorker`
handles by calling `_broker.ClosePositionAsync` for all open positions.

### 8.5 Memory Leak — `_bars` Unbounded Growth

After 1-year H1 backtest: ~8,760 bars per symbol. `BuildBarSnapshot` copies the full list
on every tick (4×/bar = 35,040 full-list copies). Each copy is N × ~40 bytes.

**Fix:** Cap at `MaxBarHistory` (default 500):
```csharp
lock (list)
{
    list.Add(bar);
    if (list.Count > MaxBarHistory)
        list.RemoveAt(0);  // or use a circular buffer / LinkedList
}
```

### 8.6 Concurrency Lint on `PositionManager._tracked`

`PositionManager._tracked` is a plain `Dictionary`. `RegisterPosition` and
`DeregisterPosition` are called from `PositionTracker` (tick thread). `Evaluate` is also
called from the tick thread. Since both are single-threaded (drain-first), no race exists.
But if `PositionManager` is ever called from a background thread (e.g., force-close logic),
it will corrupt. Add a `lock (_tracked)` guard or switch to `ConcurrentDictionary`.

### 8.7 `TypedEventBus.PublishAsync` — Handler Timeout

`HandleAccountUpdate` calls `_ = _eventBus.PublishAsync(...)` fire-and-forget. If an SSE
handler blocks (client disconnected, write stalls), the background task accumulates
indefinitely. Add 100ms timeout per handler:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
try { await handler.HandleAsync(evt, cts.Token); }
catch (OperationCanceledException) { /* handler timed out — skip */ }
```

---

## 9. cTrader Asset Support Review

| Category | Symbol | Pip Value Calc | Notes |
|---|---|---|---|
| Forex USD-quote | EURUSD, GBPUSD | Case 1: `PipSize × ContractSize` | $10/pip/lot ✅ |
| Forex USD-base | USDJPY, USDCAD | Case 2: `PipSize × ContractSize / price` | ~$6.70/lot for JPY ✅ |
| Cross pair | GBPJPY, EURJPY | Case 3: `PipSize × ContractSize × crossRate` | needs real rate feed ✅ |
| Metal | XAUUSD | Case 1: `0.01 × 100 = $1/pip/lot` | correct for 100oz contract ✅ |
| Crypto | BTCUSD | Case 1: `1.0 × 1 = $1/pip/lot` | 1 lot = 1 BTC, 1 pip = $1 ✅ |
| Index | US30 | Case 1: `1.0 × 1 = $1/pip/lot` | correct ✅ |
| Index | NAS100 | Case 1: `0.25 × 1 = $0.25/pip/lot` | correct ✅ |

**All asset classes have correct pip value formulas once `ISymbolInfoRegistry` has correct
`ContractSize` values.** The `defaults.json` values are correct. ✅

**Spread simulation is wrong** for all non-EURUSD assets (M-6 above). Fix: use
`symbolInfo.TypicalSpread / 2` as the half-spread in `HistoricalDataProvider`.

**Crypto-specific edge case:** cTrader crypto contracts often have `lotStep = 0.001` and
`minLots = 0.001`. `PositionSizer.Calculate` uses `Math.Floor` with `lotStep`. For BTC:
`Math.Floor(0.00123 / 0.001) × 0.001 = 0.001`. This is correct. ✅

**Index-specific edge case:** Indices often have weekend gaps (market closed Friday 22:00
to Sunday 22:00). `SessionFilter.IsWeekend` should also block INDEX symbols with a
different time window than forex. FTMO treats indices differently — indices may have tighter
daily DD limits (5% instead of 5% but applied to index position size).

---

## 10. Aspire Host — Correct Wiring

### Current Issues
1. `ENGINE_MODE` → should be `Engine__Mode` (double underscore for config hierarchy)
2. `WithReference(engine)` does nothing useful — engine exposes no HTTP endpoints
3. Shared DB path not communicated to web
4. Web starts immediately; engine may not have finished the backtest yet

### Correct AppHost.cs

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var dbPath = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "data", "trading.db"));

var engine = builder.AddProject<Projects.TradingEngine_Host>("engine")
    .WithEnvironment("Engine__Mode", "Backtest")
    .WithEnvironment("Persistence__DbPath", dbPath);

var web = builder.AddProject<Projects.TradingEngine_Web>("web")
    .WithEnvironment("Persistence__DbPath", dbPath)
    .WithEndpoint(port: 5200, scheme: "http", name: "web-http")
    .WaitForCompletion(engine);  // web only starts after backtest finishes

builder.Build().Run();
```

`Host/Program.cs` and `Web/Program.cs` both read:
```csharp
var dbPath = builder.Configuration["Persistence:DbPath"]
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "trading.db"));
```

---

## 11. Decisions (D36–D50)

| ID | Decision | Vote |
|---|---|---|
| D36 | Bar history cap | **A** — `MaxBarsPerTimeframe = 500`; evict oldest when exceeded; configurable per strategy |
| D37 | Indicator key namespace | **A** — prefix with symbol: `"EURUSD:ATR_14"`; strip prefix when building `MarketContext.IndicatorValues` |
| D38 | Strategy composition model | **A** — `ISignalProvider` + `IEntryFilter` + `IExitBehavior` + `IPositionBehavior`; `IStrategy` unchanged; new strategies use `ComposedStrategy` wrapper |
| D39 | PositionManagementConfig source | **A** — strategies declare `IReadOnlyList<IPositionBehavior> PositionBehaviors { get; }`; `PositionManager` reads behaviors from this instead of hardcoded switch |
| D40 | Lot sizing methods | **A** — add `LotSizingMethod` enum + supporting fields to `RiskProfile`; `PositionSizer` dispatches on method |
| D41 | Position state machine | **A** — `PositionLifecycleState` enum tracked in `PositionManager._tracked`; log every transition |
| D42 | Pipe reconnection | **A** — 3 retries, exponential backoff (2s, 4s, 8s); enter protection mode if all fail; re-sync state on reconnect |
| D43 | Broker state sync on startup | **A** — `IBrokerAdapter.GetAccountStateAsync()` called after `ConnectAsync` in live/paper mode; reconcile before accepting signals |
| D44 | Tick synthesis spread | **A** — `HistoricalDataProvider` uses `symbolInfo.TypicalSpread / 2` as half-spread |
| D45 | Order rejection handling | **A** — `OrderState.Rejected` removes from pending map, logs `RejectionReason`, deregisters risk |
| D46 | Partial fill handling | **A** — track cumulative `FilledLots` per `OrderId`; only remove from pending when `FilledLots >= RequestedLots` |
| D47 | Duplicate execution guard | **A** — `HashSet<Guid> _processedExecutionIds`; skip already-processed events |
| D48 | Force close on DD breach | **A** — when `ForceCloseOnBreach == true` and max-DD protection entered: publish `ForceCloseAllRequested` event; `EngineWorker` calls `ClosePositionAsync` for all open positions |
| D49 | Aspire shared DB path | **A** — `Engine__Mode` env var (double underscore); `Persistence__DbPath` shared; `WaitForCompletion(engine)` on web |
| D50 | Three new strategies | **A** — `EmaAlignmentStrategy`, `MeanReversionStrategy`, `SessionBreakoutStrategy`; all use `ComposedStrategy`; each with appropriate filters and behaviors |

---

## 12. Iteration 4 Phase Plan

### Phase 4A — Critical Bug Fixes

**Nothing else starts until 4A completes.**

| Task | File | Bug Fixed |
|---|---|---|
| 4A-1 | `EngineWorker.cs:139` | Use resolved `profile` in `CalculateLotSize` (C-1) |
| 4A-2 | `EngineWorker.cs:289` | Replace strategy cast with `RequiredIndicators` loop (C-2) |
| 4A-3 | `EngineWorker.cs:308` | Warm-up derives symbols from strategy configs, not hardcoded (C-3) |
| 4A-4 | `SimulatedBrokerAdapter.cs` | Track `_currentBalance`, update on fill/close, emit real `AccountUpdate` (C-4) |
| 4A-5 | `Program.cs:113` | `AddSingleton<PersistenceService>` (C-5) |
| 4A-6 | `NamedPipeBrokerAdapter.cs:70` | Fix partial 4-byte header read with loop (C-6) |
| 4A-7 | `DrawdownTracker.cs:34` | `CurrentDailyDrawdown` base = `InitialAccountBalance` (S-1) |
| 4A-8 | `RiskManager.cs:41-44` | `MaxDailyLossPercent / 100.0m` for unit-correct comparison (S-2) |
| 4A-9 | `EngineWorker.cs:19` | Indicator key = `"{symbol}:{key}"` (S-6, D37) |
| 4A-10 | `HandleExecutionEvent` | Rejected orders: remove from pending, log reason (D45) |
| 4A-11 | `PositionManager.cs` | Fix breakeven one-shot via `_beApplied HashSet<Guid>` (S-5) |
| 4A-12 | `PositionManager.cs:40-44` | Fix `AtrTrail` inputs: high-water tracking + real ATR value (S-3) |
| 4A-13 | `NamedPipeBrokerAdapter.cs:149` | Replace `Debug.WriteLine` with `ILogger` (M-8) |

**New tests:** DD unit test (S-1+S-2 validation), breakeven one-shot, USDJPY indicator isolation

---

### Phase 4B — OrderDispatcher/PositionTracker Wiring

| Task | File | Change |
|---|---|---|
| 4B-1 | `IBrokerAdapter.cs` | `SubmitOrderAsync` returns `Task<Guid>` (the broker-assigned order ID) |
| 4B-2 | `SimulatedBrokerAdapter.cs` | `SubmitOrderAsync` returns the generated Guid |
| 4B-3 | `NamedPipeBrokerAdapter.cs` | `SubmitOrderAsync` sends command; uses correlation ID in response to return order Guid |
| 4B-4 | `OrderDispatcher.cs` | Capture returned orderId from `SubmitOrderAsync` (fix S-7) |
| 4B-5 | `EngineWorker.cs` | Wire `OrderDispatcher.DispatchAsync` in tick loop |
| 4B-6 | `EngineWorker.cs` | Wire `PositionTracker.OnExecution` in `HandleExecutionEvent` |
| 4B-7 | `EngineWorker.cs` | Remove `_pendingOrdersMap`, `_openPositionsMap`, `_latestRiskAmount`, `_latestAccountUpdate` |
| 4B-8 | `PositionTracker.cs` | Inject `EngineMode` and actual `RiskProfileId` from `OrderContext` (fix M-10, M-11) |
| 4B-9 | `_bars` | Cap at `MaxBarHistory = 500`, evict oldest (D36, M-1) |
| 4B-10 | Partial fill | Cumulative `FilledLots` tracking in `PositionTracker._pendingOrders` (D46) |
| 4B-11 | Duplicate guard | `_processedExecutionIds HashSet` in `PositionTracker` (D47) |

---

### Phase 4C — Strategy Composition + Three New Strategies

| Task | File | Change |
|---|---|---|
| 4C-1 | `TradingEngine.Domain/Strategy/` | `ISignalProvider`, `IEntryFilter`, `IExitBehavior`, `IPositionBehavior`, `PositionLifecycleState` |
| 4C-2 | `TradingEngine.Domain/Interfaces/IStrategy.cs` | Add `IReadOnlyList<IPositionBehavior> PositionBehaviors { get; }` |
| 4C-3 | `TradingEngine.Services/Strategy/` | `ComposedStrategy`, entry filters, exit behaviors, position behaviors (§5.3–5.4) |
| 4C-4 | `TradingEngine.Services/PositionManager.cs` | Iterate `strategy.PositionBehaviors` instead of `TrailingConfig` switch (D39) |
| 4C-5 | `TradingEngine.Infrastructure/Indicators/SkenderIndicatorService.cs` | Add `Rsi()` and `BollingerBands()` implementations |
| 4C-6 | `TradingEngine.Strategies/EmaAlignment/` | `EmaAlignmentStrategy` + config + JSON (D50) |
| 4C-7 | `TradingEngine.Strategies/MeanReversion/` | `MeanReversionStrategy` + config + JSON (D50) |
| 4C-8 | `TradingEngine.Strategies/SessionBreakout/` | `SessionBreakoutStrategy` + config + JSON (D50) |

---

### Phase 4D — Lot Sizing + Risk Completeness

| Task | File | Change |
|---|---|---|
| 4D-1 | `RiskProfile.cs` | Add `LotSizingMethod` and associated fields (D40) |
| 4D-2 | `PositionSizer.cs` | Method dispatcher (D40) |
| 4D-3 | `IStrategy.cs` | Add `StrategyStats Stats { get; }` |
| 4D-4 | All strategies | Implement `Stats` from win/loss history |
| 4D-5 | `RiskManager.cs` + `EngineWorker.cs` | Force close on max DD breach (D48) |
| 4D-6 | `HistoricalDataProvider.cs` | Use `symbolInfo.TypicalSpread / 2` (D44, M-6) |

---

### Phase 4E — State Sync + Reconnection

| Task | File | Change |
|---|---|---|
| 4E-1 | `IBrokerAdapter.cs` | Add `GetAccountStateAsync` (D43) |
| 4E-2 | `SimulatedBrokerAdapter.cs` | Implement returning current balance + open positions |
| 4E-3 | `NamedPipeBrokerAdapter.cs` | Request/response for account state; reconnect loop (D42, D43) |
| 4E-4 | `EngineWorker.ExecuteAsync` | Startup reconciliation in live mode (D43) |
| 4E-5 | `PositionLifecycleState` tracking | Implement in `PositionManager` (D41) |

---

### Phase 4F — Aspire + Testing

| Task | File | Change |
|---|---|---|
| 4F-1 | `AppHost.cs` | Fix env var naming, shared DB path, `WaitForCompletion(engine)` (D49) |
| 4F-2 | `EngineTestHarness.cs` | Real Skender indicators, `PositionSizer.Calculate`, running equity |
| 4F-3 | New multi-strategy tests | Two strategies simultaneously; exposure cap; per-strategy limits |
| 4F-4 | New composition tests | `ComposedStrategy` with two filters; verify filter blocks signal |
| 4F-5 | New edge-case tests | Rejection handling; duplicate execution guard; gap scenario |

---

## 13. Definition of Done — Iteration 4

**Financial correctness:**
- [ ] DD unit comparison uses fraction vs fraction (5% = 0.05m, not 5.0m)
- [ ] Daily DD base uses `InitialAccountBalance` (FTMO rule)
- [ ] Zero equity never used for risk decisions (real AccountUpdate always arrives)
- [ ] Per-strategy risk profiles correctly applied to both `Validate` AND `CalculateLotSize`

**Data flow:**
- [ ] `SimulatedBrokerAdapter` emits real `AccountUpdate` after every fill/close
- [ ] No `AccountUpdate` from `DataFeedService`
- [ ] `_bars` capped at 500 per symbol per timeframe

**State machine:**
- [ ] Breakeven fires exactly once per position
- [ ] ATR trailing uses correct high-water/low-water
- [ ] All position state transitions logged with structured fields

**Multi-strategy:**
- [ ] Indicator keys namespaced by symbol (`"EURUSD:ATR_14"`)
- [ ] Per-order risk amount tracked separately (no single `_latestRiskAmount` field)
- [ ] Three strategies run simultaneously without indicator collision

**Strategy composition:**
- [ ] `ComposedStrategy` with pluggable filters and behaviors
- [ ] `EmaAlignmentStrategy`, `MeanReversionStrategy`, `SessionBreakoutStrategy` all operational
- [ ] Session filter blocks signals outside declared window

**Live-mode robustness:**
- [ ] Order rejection removes from pending map + logs reason
- [ ] Partial fills tracked cumulatively
- [ ] Duplicate execution events ignored
- [ ] Pipe partial-header read handled (no silent disconnection)
- [ ] All pipe errors logged via `ILogger` (no `Debug.WriteLine`)
- [ ] Force-close on max DD breach when `PropFirmRuleSet.ForceCloseOnBreach = true`

**Aspire:**
- [ ] `dotnet run --project aspire/TradingEngine.AppHost` starts engine then web
- [ ] Web reads real data from shared DB path
- [ ] Engine mode reads correctly from Aspire env var

**Tests:**
- [ ] `dotnet test` → 0 failures
- [ ] Bull scenario: net positive PnL
- [ ] Bear scenario: net negative PnL
- [ ] Max DD scenario: protection mode triggered (with correct fraction comparison)
- [ ] Multi-strategy: exposure cap enforced across strategies
