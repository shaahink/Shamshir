# Shamshir — Phase 4A: Money Management Circuit — Agent Brief

> Prepared: 2026-06-06
> Self-contained. Start here with no prior conversation context.
> Task: Fix the money management data circuit and write tests that prove it works as a unit.

---

## 1. What This Repo Is

A prop-firm trading engine (.NET 10 / C# 13) targeting FTMO challenge rules.
Root: `C:\code\Shamshir\`

Key projects:
- `src/TradingEngine.Risk/` — DrawdownTracker, RiskManager, PositionSizer, DrawdownScaler
- `src/TradingEngine.Host/` — EngineWorker (BackgroundService), Program.cs
- `src/TradingEngine.Infrastructure/Adapters/` — SimulatedBrokerAdapter (backtest), NamedPipeBrokerAdapter (live)
- `tests/TradingEngine.Tests.Unit/` — xUnit unit tests (run these)
- `tests/TradingEngine.Tests.Simulation/` — end-to-end backtest scenarios

Build: `dotnet build TradingEngine.sln`
Test: `dotnet test TradingEngine.sln`

---

## 2. Mission

The money management subsystem is broken at multiple points. A winning or losing trade
does not correctly flow through to drawdown state, which means:
- Risk gates never trigger (the engine never stops trading after a limit breach)
- Lot sizing ignores per-strategy risk config
- DrawdownTracker uses the wrong base for FTMO daily DD

Fix all broken links. Then write tests that verify each fix in isolation AND the whole
circuit end-to-end as a unit.

**Nothing else (strategy composition, new strategies, lot sizing variants) is in scope here.**

---

## 3. The Money Management Circuit — Complete Data Flow

Understanding this flow is mandatory before touching any code.

```
Broker fills / closes position
        │
        ▼
SimulatedBrokerAdapter.OnTickReceived()          [BACKTEST PATH]
 ├─ PendingOrder → SimPosition (on fill)
 ├─ SL / TP hit → SimPosition removed
 ├─ PnL computed: (exitPrice - entryPrice) × lots × contractSize [in quote ccy]
 ├─ _currentBalance += pnl  ◄── BUG C-4: never happens, field never mutated
 └─ AccountUpdate written to _accountChannel  ◄── BUG C-4: never happens
        │
        ▼ (via Channel<AccountUpdate>)
EngineWorker.ProcessAccountUpdatesAsync()
 └─ Interlocked.Exchange(ref _latestAccountUpdate, update)
        │
        ▼ (drained at top of next tick)
EngineWorker.HandleAccountUpdate(update)
 ├─ Reads _riskManager.CurrentState  ◄── BUG: reads STALE DD before updating tracker
 ├─ Builds EquitySnapshot with stale DailyDrawdownUsed / MaxDrawdownUsed
 ├─ Volatile.Write(ref _currentEquity, equity)
 └─ _riskManager.OnEquityUpdate(equity)  ◄── tracker updated AFTER snapshot written
        │
        ▼
DrawdownTracker.OnEquityUpdate(equity.Equity)
 ├─ Updates PeakEquity if new high
 ├─ CurrentDailyDrawdown = (DailyStartEquity - equity) / DailyStartEquity  ◄── BUG S-1
 │   FTMO requires: (InitialAccountBalance - equity) / InitialAccountBalance
 └─ CurrentMaxDrawdown = (base - equity) / base  (base = Peak or Initial per DrawdownType)
        │
        ▼
RiskManager.CurrentState updated
 ├─ DailyDrawdownUsed = snapshot.CurrentDailyDrawdown   ◄── reads from stale snapshot
 └─ MaxDrawdownUsed = snapshot.CurrentMaxDrawdown        ◄── same problem
        │
        ▼ (on next tick, per strategy)
EngineWorker.ProcessTicksAsync()
 ├─ equity = Volatile.Read(ref _currentEquity)   ◄── one update old
 ├─ profile = _riskProfileResolver.Resolve(intent.RiskProfileId)
 └─ violations = _riskManager.Validate(intent, equity, profile)
        │
        ▼
RiskManager.Validate()
 ├─ equity.CurrentDailyDrawdown >= (decimal)_activeRuleSet.MaxDailyLossPercent  ◄── CORRECT (both fractions)
 └─ equity.CurrentMaxDrawdown >= (decimal)_activeRuleSet.MaxTotalLossPercent    ◄── CORRECT (both fractions)
        │
        ▼ (if allowed)
RiskManager.CalculateLotSize(intent, equity, profile)
 └─ PositionSizer.Calculate(equity.Equity, profile.RiskPerTradePercent, ...)
        │
        ▼
EngineWorker calls CalculateLotSize with  ◄── BUG C-1: hardcoded new RiskProfile(...)
 new RiskProfile("standard", "Standard", 0.01, 0.04, ...)   should be: profile
```

---

## 4. Every Broken Link — Exact Location and Fix

### BUG C-4 (CRITICAL): SimulatedBrokerAdapter never emits AccountUpdate

**File:** `src/TradingEngine.Infrastructure/Adapters/SimulatedBrokerAdapter.cs`

`_currentBalance` exists (line 29) but is never changed. The `_accountChannel` is never
written to in `OnTickReceived`. Because of this:
- `_currentEquity` in EngineWorker stays at `DateTime.MinValue / 0 / 0 / 0` forever
- Every `Validate()` call uses zero equity — exposure and DD checks compare against 0
- Every `CalculateLotSize()` call divides by 0 equity → returns `minLots`
- No risk gate ever fires in backtest

**Current code (OnTickReceived — close section):**
```csharp
if (slHit || tpHit)
{
    _openPositions.Remove(id);
    _executionChannel.Writer.TryWrite(new ExecutionEvent(
        id, OrderState.Filled,
        slHit ? pos.StopLoss : pos.TakeProfit!,
        pos.Lots, null, tick.TimestampUtc));
    // ← _currentBalance NOT updated, AccountUpdate NOT emitted
}
```

**Fix — add after remove from _openPositions:**
```csharp
if (slHit || tpHit)
{
    _openPositions.Remove(id);

    var exitPrice = slHit ? pos.StopLoss.Value : pos.TakeProfit!.Value.Value;
    var symbolInfo = _symbolRegistry.Get(pos.Symbol);

    // PnL in quote currency
    var rawPnl = pos.Direction == TradeDirection.Long
        ? (exitPrice - pos.EntryPrice.Value) * pos.Lots * symbolInfo.ContractSize
        : (pos.EntryPrice.Value - exitPrice) * pos.Lots * symbolInfo.ContractSize;

    // Convert to account currency (USD) using cross rate
    var pnlUsd = symbolInfo.QuoteCurrency == "USD"
        ? rawPnl
        : rawPnl * _crossRateProvider(symbolInfo.QuoteCurrency, "USD");

    _currentBalance += pnlUsd;

    _executionChannel.Writer.TryWrite(new ExecutionEvent(
        id, OrderState.Filled,
        slHit ? pos.StopLoss : pos.TakeProfit!,
        pos.Lots, null, tick.TimestampUtc));

    _accountChannel.Writer.TryWrite(new AccountUpdate(
        _currentBalance, 0m, _currentBalance, tick.TimestampUtc));
}
```

**Also fix — after fill (pending → open), emit AccountUpdate with unchanged balance:**
```csharp
// After: _openPositions[id] = pos; _pendingOrders.Remove(id);
_accountChannel.Writer.TryWrite(new AccountUpdate(
    _currentBalance, 0m, _currentBalance, tick.TimestampUtc));
```

This ensures EngineWorker gets an equity signal immediately on fill (so lot sizing has real equity
on the very first position) and on every close.

---

### BUG C-1 (CRITICAL): EngineWorker uses hardcoded inline RiskProfile for lot sizing

**File:** `src/TradingEngine.Host/EngineWorker.cs:139-141`

```csharp
// Current — WRONG: profile resolved on line 124 is unused here
var lots = _riskManager.CalculateLotSize(intent, equity,
    new RiskProfile("standard", "Standard", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo-standard"));
```

**Fix:**
```csharp
var lots = _riskManager.CalculateLotSize(intent, equity, profile);
```

`profile` is already resolved at line 124: `var profile = _riskProfileResolver.Resolve(intent.RiskProfileId);`

---

### BUG S-1 (SERIOUS): DrawdownTracker daily DD uses wrong base for FTMO

**File:** `src/TradingEngine.Risk/DrawdownTracker.cs:34-37`

```csharp
// Current — WRONG for FTMO
var dailyDd = DailyStartEquity > 0
    ? (DailyStartEquity - equity) / DailyStartEquity
    : 0m;
```

**Why it's wrong:**
FTMO daily loss limit: equity cannot fall more than 5% of the INITIAL account balance in any
24-hour period. The floor is always `InitialAccountBalance × 0.95 = $95,000` on a $100k account —
fixed, does not change as profits accumulate.

Current code uses `DailyStartEquity` as base. If the account grew to $104,000 over prior days,
the daily floor becomes `$104,000 × 0.95 = $98,800` — 65% more restrictive than FTMO's actual
$95,000 floor. Trading is halted $3,800 early.

**Fix:**
```csharp
var dailyDd = InitialAccountBalance > 0
    ? (InitialAccountBalance - equity) / InitialAccountBalance
    : 0m;
CurrentDailyDrawdown = Math.Max(0, dailyDd);
```

`DailyStartEquity` is still used to detect intra-day recovery for informational purposes, but
it must not be the basis for the FTMO limit comparison.

---

### BUG: EquitySnapshot written with stale DD (one AccountUpdate lag)

**File:** `src/TradingEngine.Host/EngineWorker.cs:256-266`

```csharp
// Current — WRONG order
private void HandleAccountUpdate(AccountUpdate update)
{
    var riskState = _riskManager.CurrentState;  // reads DD from PREVIOUS update
    var equity = new EquitySnapshot(
        ..., riskState.DailyDrawdownUsed, riskState.MaxDrawdownUsed, ...);
    Volatile.Write(ref _currentEquity, equity);   // writes stale DD
    _riskManager.OnEquityUpdate(equity);          // updates DD AFTER snapshot written
    ...
}
```

Result: `_currentEquity` always holds DD values from the previous AccountUpdate, not the current
one. Validate() compares against one-update-old DD. The DD gate fires one update late — in a fast
market during backtest this means the gate fires one bar late.

**Fix — update tracker first, then build snapshot from fresh values:**
```csharp
private void HandleAccountUpdate(AccountUpdate update)
{
    // 1. Update tracker with new equity — this gives us fresh DD values
    _riskManager.UpdateEquityLevels(update.Equity);

    // 2. Build snapshot from FRESH state
    var riskState = _riskManager.CurrentState;
    var equity = new EquitySnapshot(
        update.TimestampUtc, update.Balance, update.FloatingPnL, update.Equity,
        update.Equity, update.Equity,
        riskState.DailyDrawdownUsed, riskState.MaxDrawdownUsed,
        EngineMode.Backtest);

    Volatile.Write(ref _currentEquity, equity);
    _ = _persistence.SaveEquitySnapshotAsync(equity, CancellationToken.None);
    _ = _eventBus.PublishAsync(new EquityUpdated(equity, riskState, _clock.UtcNow), CancellationToken.None);
}
```

**New method on `IRiskManager` and `RiskManager`:**
```csharp
// IRiskManager
void UpdateEquityLevels(decimal rawEquity);

// RiskManager
public void UpdateEquityLevels(decimal rawEquity)
{
    drawdownTracker.OnEquityUpdate(rawEquity);
    CurrentState = CurrentState with
    {
        DailyDrawdownUsed = drawdownTracker.CurrentDailyDrawdown,
        MaxDrawdownUsed = drawdownTracker.CurrentMaxDrawdown
    };
}
```

Remove the old `OnEquityUpdate(EquitySnapshot)` method from the interface — it is now replaced
by this pattern. `EngineWorker` builds the equity snapshot; `RiskManager` owns the DD values.

---

### BUG C-5 (CRITICAL): PersistenceService registered as AddScoped, injected by singleton

**File:** `src/TradingEngine.Host/Program.cs:113`

```csharp
builder.Services.AddScoped<PersistenceService>();  // WRONG
```

`EngineWorker` is a singleton (via `AddHostedService`). Injecting a scoped service into a
singleton throws `InvalidOperationException: Cannot consume scoped service from singleton`.
The engine cannot start.

**Fix:**
```csharp
builder.Services.AddSingleton<PersistenceService>();
```

`PersistenceService` uses fire-and-forget pattern with `IServiceScopeFactory` internally — it
must create its own scopes per save operation. Update `PersistenceService` to accept
`IServiceScopeFactory` and create a scope per async call:

```csharp
public sealed class PersistenceService(IServiceScopeFactory scopeFactory, ILogger<PersistenceService> logger)
{
    public async Task SaveTradeAsync(TradeResult trade, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            await repo.SaveAsync(trade, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist trade {TradeId}", trade.Id);
        }
    }

    public async Task SaveEquitySnapshotAsync(EquitySnapshot snapshot, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IEquityRepository>();
            await repo.SaveAsync(snapshot, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist equity snapshot at {Time}", snapshot.TimestampUtc);
        }
    }
}
```

`ITradeRepository` and `IEquityRepository` stay as `AddScoped` — created per save operation.

---

### BUG S-2 (NOTE — not actually a unit mismatch, but a bootstrap problem)

The ITERATION-3-FINAL.md assessed S-2 as "fraction vs percentage". After code review, both
`CurrentDailyDrawdown` (from DrawdownTracker) and `MaxDailyLossPercent` (from PropFirmRuleSet)
are stored as fractions (e.g. 0.05 = 5%). The comparison is correct.

The REAL S-2 is: **`_activeRuleSet` is null at startup until `SetActiveRuleSet` is called.**
`SetActiveRuleSet` is called nowhere in `Program.cs` or `EngineWorker`. The `if (_activeRuleSet != null)`
guard means DD checks are silently skipped forever — risk gates never fire even with correct DD.

**Fix in Program.cs** — load the active ruleset and call SetActiveRuleSet:
```csharp
var riskManager = app.Services.GetRequiredService<RiskManager>();
var activeRuleSetId = loadedConfig.StrategyConfigs
    .Select(c => c.PropFirmRuleSetId).FirstOrDefault() ?? "ftmo-standard";
var ruleSet = loadedConfig.PropFirmRuleSets.FirstOrDefault(r => r.Id == activeRuleSetId);
if (ruleSet is not null)
    riskManager.SetActiveRuleSet(ruleSet);
else
    Log.Warning("No PropFirmRuleSet found for id={Id} — risk gates disabled", activeRuleSetId);
```

Or: inject `LoadedConfig` into `EngineWorker` and call this in `ExecuteAsync` before `ConnectAsync`.

---

## 5. DrawdownTracker — Corrected Implementation

Replace `src/TradingEngine.Risk/DrawdownTracker.cs` with this:

```csharp
namespace TradingEngine.Risk;

public sealed class DrawdownTracker
{
    public decimal InitialAccountBalance { get; private set; }
    public decimal PeakEquity { get; private set; }
    public decimal DailyStartEquity { get; private set; }
    public decimal CurrentDailyDrawdown { get; private set; }
    public decimal CurrentMaxDrawdown { get; private set; }
    public string DrawdownType { get; private set; } = "Fixed";

    private bool _initialized;

    public void Initialize(decimal initialBalance, string drawdownType = "Fixed")
    {
        if (_initialized) return;
        InitialAccountBalance = initialBalance;
        PeakEquity = initialBalance;
        DailyStartEquity = initialBalance;
        DrawdownType = drawdownType;
        _initialized = true;
    }

    public void OnEquityUpdate(decimal equity)
    {
        if (!_initialized) return;

        if (equity > PeakEquity)
            PeakEquity = equity;

        // FTMO daily DD: always measured from INITIAL balance, not daily start
        // Floor = InitialAccountBalance × (1 - maxDailyPercent) — fixed, never shifts
        CurrentDailyDrawdown = InitialAccountBalance > 0
            ? Math.Max(0m, (InitialAccountBalance - equity) / InitialAccountBalance)
            : 0m;

        // Max DD: trailing uses peak, fixed uses initial
        var equityBase = DrawdownType == "Trailing" ? PeakEquity : InitialAccountBalance;
        CurrentMaxDrawdown = equityBase > 0
            ? Math.Max(0m, (equityBase - equity) / equityBase)
            : 0m;
    }

    public void OnDailyReset(decimal currentEquity)
    {
        DailyStartEquity = currentEquity;
        // NOTE: we do NOT reset CurrentDailyDrawdown here.
        // FTMO's daily DD is measured from InitialAccountBalance and is cumulative
        // intraday — the reset means a new day starts but the FLOOR is still $95k.
        // Resetting CurrentDailyDrawdown would allow breaching the floor on a new day
        // if equity is already below InitialAccountBalance.
        //
        // What we DO reset: the daily start reference for informational tracking only.
        // The actual gate comparison (CurrentDailyDrawdown vs MaxDailyLossPercent) uses
        // InitialAccountBalance as base, so there's nothing to reset here for the gate.
    }

    public decimal GetMaxDrawdownFloor(decimal maxTotalLossPercent) =>
        DrawdownType == "Trailing"
            ? PeakEquity * (1m - maxTotalLossPercent)
            : InitialAccountBalance * (1m - maxTotalLossPercent);

    public decimal GetDailyLossLimit(decimal maxDailyLossPercent) =>
        InitialAccountBalance * (1m - maxDailyLossPercent);
}
```

**Key change:** `OnDailyReset` no longer zeros `CurrentDailyDrawdown`. Under FTMO rules, if you
lost 3% on day 1 and reset to a new day, the daily DD from initial is still 3%. FTMO does not
give you a fresh 5% budget each day from wherever you are — the floor is fixed at $95,000.

If the prop firm config is NOT FTMO-style (some give a fresh daily DD budget), that requires a
new `DailyDdBase` enum on `PropFirmRuleSet`: `InitialBalance` (FTMO) vs `DailyStart` (relative).
Leave this as a follow-up (D51 in DECISIONS.md).

---

## 6. Complete Test Specification

### 6.1 DrawdownTracker Unit Tests

File: `tests/TradingEngine.Tests.Unit/RiskTests/DrawdownTrackerTests.cs`
Replace the existing file entirely.

```csharp
namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class DrawdownTrackerTests
{
    // ─── Initialization ──────────────────────────────────────────────────

    [Fact]
    public void Initialize_SetsInitialBalance_Immutable()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(90_000);
        t.InitialAccountBalance.Should().Be(100_000m);
    }

    [Fact]
    public void Initialize_Idempotent_SecondCallIgnored()
    {
        var t = Make(100_000);
        t.Initialize(200_000);
        t.InitialAccountBalance.Should().Be(100_000m);
    }

    // ─── Daily DD uses InitialAccountBalance as base (FTMO rule) ─────────

    [Fact]
    public void DailyDD_UsesInitialBalance_NotDailyStart()
    {
        var t = Make(100_000);
        t.OnDailyReset(104_000);        // simulate a profitable previous day
        t.OnEquityUpdate(100_000);      // lost $4k today — but still at initial

        // FTMO: 0% daily DD (still at floor)
        // Wrong (DailyStart): ($104k - $100k) / $104k = 3.85%
        t.CurrentDailyDrawdown.Should().Be(0m);
    }

    [Fact]
    public void DailyDD_AtExactFTMOLimit()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(95_000);       // exactly 5% below initial
        t.CurrentDailyDrawdown.Should().BeApproximately(0.05m, 0.0001m);
    }

    [Fact]
    public void DailyDD_JustBelowFTMOLimit_NotBreached()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(95_001);       // $1 above floor
        t.CurrentDailyDrawdown.Should().BeLessThan(0.05m);
    }

    [Fact]
    public void DailyDD_JustAboveFTMOLimit_Breached()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(94_999);       // $1 below floor
        t.CurrentDailyDrawdown.Should().BeGreaterThan(0.05m);
    }

    [Fact]
    public void DailyDD_WinDoesNotGoNegative()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(110_000);      // big win
        t.CurrentDailyDrawdown.Should().Be(0m);
    }

    [Fact]
    public void DailyDD_AfterProfitableDayReset_PreviousLossStillCounts()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(97_000);       // 3% daily DD accumulated
        t.OnDailyReset(97_000);         // new day starts at 97k (still below initial)
        // Daily DD from initial should still be 3% — floor hasn't moved
        t.CurrentDailyDrawdown.Should().BeApproximately(0.03m, 0.0001m);
    }

    // ─── Max DD ──────────────────────────────────────────────────────────

    [Fact]
    public void MaxDD_Fixed_UsesInitialBalance()
    {
        var t = Make(100_000, "Fixed");
        t.OnEquityUpdate(110_000);      // peak at 110k
        t.OnEquityUpdate(92_000);       // draw from initial
        // (100k - 92k) / 100k = 8%
        t.CurrentMaxDrawdown.Should().BeApproximately(0.08m, 0.0001m);
    }

    [Fact]
    public void MaxDD_Trailing_UsesPeakEquity()
    {
        var t = Make(100_000, "Trailing");
        t.OnEquityUpdate(110_000);      // peak moves to 110k
        t.OnEquityUpdate(105_000);
        // (110k - 105k) / 110k ≈ 4.55%
        t.CurrentMaxDrawdown.Should().BeApproximately(0.0455m, 0.001m);
    }

    [Fact]
    public void MaxDD_Trailing_PeakOnlyMovesUp()
    {
        var t = Make(100_000, "Trailing");
        t.OnEquityUpdate(110_000);
        t.OnEquityUpdate(105_000);
        t.PeakEquity.Should().Be(110_000m);

        t.OnEquityUpdate(112_000);
        t.PeakEquity.Should().Be(112_000m);
    }

    [Fact]
    public void MaxDD_DoesNotClearOnDailyReset()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(90_000);
        var maxDdBefore = t.CurrentMaxDrawdown;
        t.OnDailyReset(90_000);
        t.CurrentMaxDrawdown.Should().Be(maxDdBefore);
    }

    // ─── Floors ──────────────────────────────────────────────────────────

    [Fact]
    public void GetDailyLossLimit_ReturnsCorrectFloor()
    {
        var t = Make(100_000);
        t.GetDailyLossLimit(0.05m).Should().Be(95_000m);
    }

    [Fact]
    public void GetMaxDrawdownFloor_Fixed_UsesInitial()
    {
        var t = Make(100_000, "Fixed");
        t.OnEquityUpdate(110_000);       // peak moved
        t.GetMaxDrawdownFloor(0.10m).Should().Be(90_000m);  // still from initial
    }

    [Fact]
    public void GetMaxDrawdownFloor_Trailing_UsesPeak()
    {
        var t = Make(100_000, "Trailing");
        t.OnEquityUpdate(110_000);
        t.GetMaxDrawdownFloor(0.10m).Should().Be(99_000m);  // 10% from peak
    }

    private static DrawdownTracker Make(decimal initial, string type = "Fixed")
    {
        var t = new DrawdownTracker();
        t.Initialize(initial, type);
        return t;
    }
}
```

### 6.2 RiskManager — Gate Tests

File: `tests/TradingEngine.Tests.Unit/RiskTests/RiskManagerTests.cs`
Extend or replace:

```csharp
namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class RiskManagerTests
{
    private static readonly PropFirmRuleSet FtmoRules = new(
        "ftmo-standard", "FTMO Standard", "Fixed", 0.05, 0.10, 0.10, 4,
        "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
        false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false);

    private static readonly RiskProfile Profile = new(
        "standard", "Standard", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 5, false, "ftmo-standard");

    private static RiskManager MakeRm(decimal initialBalance = 100_000)
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(initialBalance);
        var registry = new SymbolInfoRegistry();
        registry.Register(EurUsd());
        var rm = new RiskManager(tracker, registry, (_, _) => 1m,
            new NewsFilter(), new SessionFilter(), new StubClock(DateTime.UtcNow));
        rm.SetActiveRuleSet(FtmoRules);
        return rm;
    }

    private static SymbolInfo EurUsd() => new(
        Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static EquitySnapshot Snapshot(decimal equity, decimal dailyDd = 0m, decimal maxDd = 0m) =>
        new(DateTime.UtcNow, equity, 0, equity, equity, equity, dailyDd, maxDd, EngineMode.Backtest);

    private static TradeIntent LongEurUsd() => new(
        Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
        new Price(1.08210m), new Price(1.08500m),
        "strat-1", "standard", "Test signal", DateTime.UtcNow);

    // ─── Daily DD gate ────────────────────────────────────────────────────

    [Fact]
    public void Validate_DailyDD_JustBeforeLimit_Allows()
    {
        var rm = MakeRm();
        // 4.99% daily DD — just below 5% limit
        var snap = Snapshot(100_000, dailyDd: 0.0499m);
        var v = rm.Validate(LongEurUsd(), snap, Profile);
        v.Should().NotContain(x => x.Code == "DAILY_DD_LIMIT");
    }

    [Fact]
    public void Validate_DailyDD_AtLimit_Blocks()
    {
        var rm = MakeRm();
        var snap = Snapshot(100_000, dailyDd: 0.05m);
        var v = rm.Validate(LongEurUsd(), snap, Profile);
        v.Should().Contain(x => x.Code == "DAILY_DD_LIMIT");
    }

    [Fact]
    public void Validate_DailyDD_ExceedsLimit_Blocks()
    {
        var rm = MakeRm();
        var snap = Snapshot(100_000, dailyDd: 0.072m);
        var v = rm.Validate(LongEurUsd(), snap, Profile);
        v.Should().Contain(x => x.Code == "DAILY_DD_LIMIT");
    }

    // ─── Max DD gate ──────────────────────────────────────────────────────

    [Fact]
    public void Validate_MaxDD_JustBeforeLimit_Allows()
    {
        var rm = MakeRm();
        var snap = Snapshot(100_000, maxDd: 0.0999m);
        var v = rm.Validate(LongEurUsd(), snap, Profile);
        v.Should().NotContain(x => x.Code == "MAX_DD_LIMIT");
    }

    [Fact]
    public void Validate_MaxDD_AtLimit_Blocks()
    {
        var rm = MakeRm();
        var snap = Snapshot(100_000, maxDd: 0.10m);
        var v = rm.Validate(LongEurUsd(), snap, Profile);
        v.Should().Contain(x => x.Code == "MAX_DD_LIMIT");
    }

    // ─── Protection mode ─────────────────────────────────────────────────

    [Fact]
    public void Validate_ProtectionModeActive_BlocksEvenWithZeroDd()
    {
        var rm = MakeRm();
        rm.EnterProtectionMode("Manual halt", ProtectionCause.MaxDrawdown);
        var snap = Snapshot(100_000, dailyDd: 0m, maxDd: 0m);
        var v = rm.Validate(LongEurUsd(), snap, Profile);
        v.Should().Contain(x => x.Code == "PROTECTION_MODE_ACTIVE");
    }

    [Fact]
    public void OnDailyReset_FromDailyDDProtection_ResumesTrading()
    {
        var rm = MakeRm();
        rm.EnterProtectionMode("Daily DD breach", ProtectionCause.DailyDrawdown);
        rm.OnDailyReset(100_000);
        rm.CurrentState.InProtectionMode.Should().BeFalse();
        rm.CurrentState.TradingAllowed.Should().BeTrue();
    }

    [Fact]
    public void OnDailyReset_FromMaxDDProtection_StaysSuspended()
    {
        var rm = MakeRm();
        rm.EnterProtectionMode("Max DD breach", ProtectionCause.MaxDrawdown);
        rm.OnDailyReset(100_000);
        rm.CurrentState.InProtectionMode.Should().BeTrue();  // max DD does not reset daily
    }

    // ─── Position registration affects future validation ─────────────────

    [Fact]
    public void Validate_AfterRegisteringPositions_CountsCorrectly()
    {
        var rm = MakeRm();
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);

        // Profile.MaxConcurrentPositions = 5; now at 5
        var v = rm.Validate(LongEurUsd(), Snapshot(100_000), Profile);
        v.Should().Contain(x => x.Code == "MAX_POSITIONS");
    }

    [Fact]
    public void Validate_AfterDeregisteringPosition_AllowsAgain()
    {
        var rm = MakeRm();
        var id1 = Guid.NewGuid();
        rm.RegisterPosition(id1, "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);

        rm.DeregisterPosition(id1);  // now at 4
        var v = rm.Validate(LongEurUsd(), Snapshot(100_000), Profile);
        v.Should().NotContain(x => x.Code == "MAX_POSITIONS");
    }

    // ─── Full circuit: tracker → snapshot → validate ──────────────────────

    [Fact]
    public void Circuit_LossUpdatesTracker_SnapshotBlocksNextTrade()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000);
        var registry = new SymbolInfoRegistry();
        registry.Register(EurUsd());
        var rm = new RiskManager(tracker, registry, (_, _) => 1m,
            new NewsFilter(), new SessionFilter(), new StubClock(DateTime.UtcNow));
        rm.SetActiveRuleSet(FtmoRules);

        // Simulate equity dropping to $94,900 (beyond 5% daily DD floor of $95k)
        rm.UpdateEquityLevels(94_900m);
        var freshState = rm.CurrentState;

        var snap = Snapshot(94_900m, dailyDd: freshState.DailyDrawdownUsed, maxDd: freshState.MaxDrawdownUsed);
        var v = rm.Validate(LongEurUsd(), snap, Profile);

        v.Should().Contain(x => x.Code == "DAILY_DD_LIMIT",
            "equity dropped below FTMO daily floor — trading must be blocked");
    }

    [Fact]
    public void Circuit_WinFromNearLimit_AllowsContinuedTrading()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000);
        var registry = new SymbolInfoRegistry();
        registry.Register(EurUsd());
        var rm = new RiskManager(tracker, registry, (_, _) => 1m,
            new NewsFilter(), new SessionFilter(), new StubClock(DateTime.UtcNow));
        rm.SetActiveRuleSet(FtmoRules);

        // Equity near limit at $95,500 (0.5% above floor)
        rm.UpdateEquityLevels(95_500m);
        var state = rm.CurrentState;
        var snap = Snapshot(95_500m, dailyDd: state.DailyDrawdownUsed, maxDd: state.MaxDrawdownUsed);
        var v = rm.Validate(LongEurUsd(), snap, Profile);
        v.Should().NotContain(x => x.Code == "DAILY_DD_LIMIT");
    }
}
```

### 6.3 SimulatedBrokerAdapter — Equity Emission Tests

File: `tests/TradingEngine.Tests.Unit/Phase3ATests/SimulatedBrokerTests.cs`
Add these tests to the existing file:

```csharp
// ─── AccountUpdate emission on close ─────────────────────────────────────────

[Fact]
public async Task OnTickReceived_SlHit_EmitsAccountUpdate()
{
    var adapter = MakeAdapter(initialBalance: 100_000);
    var orderId = await SubmitAndFillLong(adapter, entry: 1.0800m, sl: 1.0750m, tp: 1.0900m);

    // Tick that hits SL
    var closeAccountUpdate = await CollectNextAccountUpdate(adapter, tick: new Tick(
        Symbol.Parse("EURUSD"), bid: 1.0750m, ask: 1.0751m, DateTime.UtcNow));

    closeAccountUpdate.Should().NotBeNull("SL hit must emit AccountUpdate");
    closeAccountUpdate!.Balance.Should().BeLessThan(100_000m, "loss must reduce balance");
}

[Fact]
public async Task OnTickReceived_TpHit_EmitsAccountUpdateWithProfit()
{
    var adapter = MakeAdapter(initialBalance: 100_000);
    await SubmitAndFillLong(adapter, entry: 1.0800m, sl: 1.0750m, tp: 1.0900m);

    var closeAccountUpdate = await CollectNextAccountUpdate(adapter, tick: new Tick(
        Symbol.Parse("EURUSD"), bid: 1.0900m, ask: 1.0901m, DateTime.UtcNow));

    closeAccountUpdate.Should().NotBeNull("TP hit must emit AccountUpdate");
    closeAccountUpdate!.Balance.Should().BeGreaterThan(100_000m, "win must increase balance");
}

[Fact]
public async Task OnTickReceived_MultiplePositions_BalanceAccumulates()
{
    var adapter = MakeAdapter(initialBalance: 100_000);

    // Open two longs
    await SubmitAndFillLong(adapter, entry: 1.0800m, sl: 1.0750m, tp: 1.0900m);
    await SubmitAndFillLong(adapter, entry: 1.0800m, sl: 1.0750m, tp: 1.0900m);

    // Both TP hit
    adapter.OnTickReceived(new Tick(Symbol.Parse("EURUSD"), 1.0900m, 1.0901m, DateTime.UtcNow));

    var updates = new List<AccountUpdate>();
    while (adapter.AccountStream.TryRead(out var u))
        updates.Add(u);

    updates.Should().HaveCountGreaterOrEqualTo(2);
    updates.Last().Balance.Should().BeGreaterThan(100_000m + 50m); // at least $50 profit
}
```

### 6.4 Simulation Scenarios — Drawdown Circuit End-to-End

File: `tests/TradingEngine.Tests.Simulation/Scenarios/DrawdownScenarios.cs` (new file)

```csharp
namespace TradingEngine.Tests.Simulation.Scenarios;

/// <summary>
/// End-to-end drawdown scenarios. These test the full circuit:
/// trade close → SimulatedBrokerAdapter emits AccountUpdate → EngineWorker updates DrawdownTracker
/// → next trade is blocked / allowed correctly.
///
/// These tests use EngineTestHarness which wires the real engine with a SimulatedBrokerAdapter.
/// </summary>
public sealed class DrawdownScenarios
{
    // ─── Scenario 1: 5% daily loss halts trading ────────────────────────────

    [Fact]
    public async Task FivePctDailyLoss_HaltsTradingForDay()
    {
        var harness = new EngineTestHarness(initialBalance: 100_000);

        // Inject 10 losing trades totalling $5,100 loss (just past 5% daily floor)
        for (var i = 0; i < 10; i++)
            harness.InjectLoss(amountUsd: 510m);

        await harness.RunAsync(TimeSpan.FromSeconds(5));

        harness.TradesAfterDailyLimitBreached.Should().Be(0,
            "no new positions should open after 5% daily DD floor ($95,000) is crossed");
        harness.DailyDdFraction.Should().BeGreaterOrEqualTo(0.05m);
    }

    // ─── Scenario 2: 10% max loss permanently halts trading ─────────────────

    [Fact]
    public async Task TenPctMaxLoss_PermanentlyHaltsTrading()
    {
        var harness = new EngineTestHarness(initialBalance: 100_000);

        harness.InjectLoss(amountUsd: 10_100m);  // past $90k floor

        await harness.RunAsync(TimeSpan.FromSeconds(5));

        // Simulate daily reset — should NOT restore trading
        harness.SimulateDailyReset();
        await harness.RunAsync(TimeSpan.FromSeconds(2));

        harness.TradesAfterMaxDdBreached.Should().Be(0,
            "max DD breach does not lift on daily reset");
        harness.IsInProtectionMode.Should().BeTrue();
    }

    // ─── Scenario 3: Win day recovers drawdown capacity ─────────────────────

    [Fact]
    public async Task WinSequence_DrawdownFractionStaysZero()
    {
        var harness = new EngineTestHarness(initialBalance: 100_000);

        for (var i = 0; i < 5; i++)
            harness.InjectWin(amountUsd: 500m);

        await harness.RunAsync(TimeSpan.FromSeconds(2));

        harness.DailyDdFraction.Should().Be(0m, "wins above initial balance → zero daily DD");
        harness.MaxDdFraction.Should().Be(0m);
    }

    // ─── Scenario 4: Two strategies contribute to same DD pool ──────────────

    [Fact]
    public async Task MultiStrategy_BothContributeToSameDdPool()
    {
        var harness = new EngineTestHarness(initialBalance: 100_000, strategyCount: 2);

        // Each strategy loses 2.6% — together they exceed 5% daily limit
        harness.InjectLoss(amountUsd: 2_600m, strategyId: "strat-1");
        harness.InjectLoss(amountUsd: 2_600m, strategyId: "strat-2");

        await harness.RunAsync(TimeSpan.FromSeconds(5));

        harness.TradesAfterDailyLimitBreached.Should().Be(0,
            "combined losses from two strategies must block ALL further trades");
    }

    // ─── Scenario 5: DrawdownScaler reduces lots as DD approaches limit ──────

    [Fact]
    public async Task DrawdownScaler_ReducesLotsAsApproachingLimit()
    {
        var harness = new EngineTestHarness(initialBalance: 100_000);

        var lotsAtBaseline = harness.LastComputedLots;

        // Lose 7% of max DD (scale threshold = 50% of 10% = at 5% DD, scaling starts)
        harness.InjectLoss(amountUsd: 5_100m);   // 5.1% max DD → above scale threshold
        await harness.RunAsync(TimeSpan.FromSeconds(2));

        harness.LastComputedLots.Should().BeLessThan(lotsAtBaseline,
            "DrawdownScaler must reduce lot size when DD exceeds scale threshold");
    }
}
```

**Note on EngineTestHarness:** The current harness in
`tests/TradingEngine.Tests.Simulation/Harness/EngineTestHarness.cs` does not expose
`TradesAfterDailyLimitBreached`, `DailyDdFraction`, `InjectLoss`, `InjectWin`,
`SimulateDailyReset`, or `LastComputedLots`. You must extend the harness to support
these. See §7 below.

### 6.5 PositionSizer Unit Tests — Lot Sizing Integrity

File: `tests/TradingEngine.Tests.Unit/RiskTests/PositionSizerTests.cs`
Replace:

```csharp
namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class PositionSizerTests
{
    // $100k account, 1% risk, 50 pips SL, $10/pip → $100 risk / ($10 × 50 pips) = 0.2 lots
    [Fact]
    public void Calculate_StandardInput_ReturnsCorrectLots()
    {
        var lots = PositionSizer.Calculate(
            equity: 100_000m,
            riskPercent: RiskPercent.Parse(0.01),
            stopLossDistance: new Pips(50),
            pipValue: 10m,
            drawdownScaleFactor: 1.0m,
            maxLots: 100m,
            brokerMinLots: 0.01m,
            brokerLotStep: 0.01m);

        lots.Should().Be(0.20m);
    }

    [Fact]
    public void Calculate_ResultFlooredToLotStep_NeverRoundsUp()
    {
        // Raw = 0.234 → floor to 0.01 step → 0.23
        var lots = PositionSizer.Calculate(
            equity: 100_000m, riskPercent: RiskPercent.Parse(0.01),
            stopLossDistance: new Pips(43), pipValue: 10m,
            drawdownScaleFactor: 1.0m, maxLots: 100m,
            brokerMinLots: 0.01m, brokerLotStep: 0.01m);

        lots.Should().BeLessOrEqualTo(0.23m);   // never rounds up to 0.24
    }

    [Fact]
    public void Calculate_CappedAtMaxLots()
    {
        var lots = PositionSizer.Calculate(
            equity: 100_000m, riskPercent: RiskPercent.Parse(0.50),  // 50% risk — extreme
            stopLossDistance: new Pips(5), pipValue: 10m,
            drawdownScaleFactor: 1.0m, maxLots: 5.0m,
            brokerMinLots: 0.01m, brokerLotStep: 0.01m);

        lots.Should().Be(5.0m);
    }

    [Fact]
    public void Calculate_AtMinLotsWhenEquityZero_ReturnsMinLots()
    {
        var lots = PositionSizer.Calculate(
            equity: 0m, riskPercent: RiskPercent.Parse(0.01),
            stopLossDistance: new Pips(50), pipValue: 10m,
            drawdownScaleFactor: 1.0m, maxLots: 100m,
            brokerMinLots: 0.01m, brokerLotStep: 0.01m);

        lots.Should().Be(0.01m);  // min lots, not zero, not exception
    }

    [Fact]
    public void Calculate_WithDrawdownScale_ReducesLots()
    {
        var fullLots = PositionSizer.Calculate(
            100_000m, RiskPercent.Parse(0.01), new Pips(50), 10m,
            drawdownScaleFactor: 1.0m, 100m, 0.01m, 0.01m);

        var scaledLots = PositionSizer.Calculate(
            100_000m, RiskPercent.Parse(0.01), new Pips(50), 10m,
            drawdownScaleFactor: 0.5m, 100m, 0.01m, 0.01m);

        scaledLots.Should().BeLessThan(fullLots);
        scaledLots.Should().BeApproximately(fullLots * 0.5m, 0.01m);
    }
}
```

---

## 7. EngineTestHarness Extensions Required

The existing harness (`tests/TradingEngine.Tests.Simulation/Harness/EngineTestHarness.cs`)
needs these additions to support the drawdown scenarios in §6.4:

```csharp
// Properties to read after RunAsync
public decimal DailyDdFraction => _riskManager.CurrentState.DailyDrawdownUsed;
public decimal MaxDdFraction => _riskManager.CurrentState.MaxDrawdownUsed;
public bool IsInProtectionMode => _riskManager.CurrentState.InProtectionMode;
public int TradesAfterDailyLimitBreached { get; private set; }
public int TradesAfterMaxDdBreached { get; private set; }
public decimal LastComputedLots { get; private set; }

// Inject a synthetic win/loss without going through strategy signal
// This writes an AccountUpdate directly to SimulatedBrokerAdapter
public void InjectLoss(decimal amountUsd, string strategyId = "default")
{
    _currentBalance -= amountUsd;
    _simAdapter.AccountWriter.TryWrite(new AccountUpdate(
        _currentBalance, 0m, _currentBalance, DateTime.UtcNow));
}

public void InjectWin(decimal amountUsd, string strategyId = "default")
{
    _currentBalance += amountUsd;
    _simAdapter.AccountWriter.TryWrite(new AccountUpdate(
        _currentBalance, 0m, _currentBalance, DateTime.UtcNow));
}

public void SimulateDailyReset()
{
    _riskManager.OnDailyReset(_currentBalance);
}
```

The harness must also intercept the Validate() call and count how many trade attempts were
blocked due to DD limits after the first breach. The cleanest approach: wrap `IRiskManager`
in a recording decorator inside the harness.

---

## 8. Definition of Done

All of the following must be true before this phase is complete:

- [ ] `dotnet build TradingEngine.sln` — zero warnings, zero errors
- [ ] `dotnet test TradingEngine.sln` — all tests pass, none skipped
- [ ] `DrawdownTrackerTests` — all 14 tests pass, including the FTMO-base tests
- [ ] `RiskManagerTests` — all tests pass including `Circuit_LossUpdatesTracker_SnapshotBlocksNextTrade`
- [ ] `SimulatedBrokerTests` — AccountUpdate emission tests pass
- [ ] `DrawdownScenarios` — all 5 end-to-end scenarios pass
- [ ] `PositionSizerTests` — all 5 tests pass
- [ ] Engine starts: `dotnet run --project src/TradingEngine.Host` — no startup exception
  (the `AddScoped` PersistenceService bug is fixed)
- [ ] Backtest run emits at least one AccountUpdate (verify via log: "Account update processed")
- [ ] Lot sizes are non-zero in backtest log after first AccountUpdate is processed

---

## 9. What NOT to Do in This Phase

- Do not add new strategy types (EmaAlignment, MeanReversion, SessionBreakout)
- Do not add the strategy composition interfaces (ISignalProvider, IEntryFilter, etc.)
- Do not add lot sizing variants (KellyFraction, AntiMartingale)
- Do not wire OrderDispatcher / PositionTracker into EngineWorker
- Do not touch NamedPipeBrokerAdapter reconnection
- Do not fix the C-2 TrendBreakoutStrategy cast or C-3 hardcoded EURUSD warm-up
- Do not add PositionLifecycleState tracking

Those are Phase 4B–4F. This phase is exclusively the money management circuit.
