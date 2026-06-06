# Trading Engine — System Design v1

> **Document purpose:** This is the canonical reference for an AI coding agent iterating on this codebase. Read it fully before writing any code. Each section is ordered to reflect implementation dependency — work top to bottom within a section. Do not skip sections. Do not introduce patterns, libraries, or abstractions not listed here without flagging it as a deviation.

---

## 1. Locked Decisions

These are not open for discussion. Do not suggest alternatives.

| Concern | Decision |
|---|---|
| Runtime | .NET 10, C# 13 |
| Process model | Console (dev) + Windows Service (prod), single binary |
| Orchestration | .NET Aspire (dev only) |
| Broker adapter | cTrader cBot, C# 6, named pipes |
| Messaging (internal) | `System.Threading.Channels` for data streams; custom typed event bus for domain events |
| Persistence | EF Core + SQLite; provider-abstracted; Dapper for complex reads |
| Indicators | Skender.Stock.Indicators, wrapped — never exposed to domain |
| Configuration | Strongly-typed C# classes, JSON-backed per strategy/risk profile |
| Logging | Serilog (structured, sinks: console + file) |
| Prop firm baseline | FTMO — configurable rule set, FTMO as default preset |
| Money management | First-class, built into risk layer — not in strategies |
| Reporting | ASP.NET Core localhost web app, Aspire-managed in dev |
| Testing | xUnit, no cTrader required for any test |

---

## 2. Repository Layout

```
trading-engine/
├── src/
│   ├── TradingEngine.Domain/          # Pure domain — no infrastructure deps
│   ├── TradingEngine.Application/     # Use cases, engine orchestration
│   ├── TradingEngine.Infrastructure/  # EF Core, persistence, Skender adapter
│   ├── TradingEngine.Risk/            # Risk engine, money management, prop firm rules
│   ├── TradingEngine.Strategies/      # Strategy implementations
│   ├── TradingEngine.Services/        # SL/TP calc, trailing stop, position manager
│   ├── TradingEngine.Adapters.CTrader/ # C# 6 cBot project (separate, thin)
│   ├── TradingEngine.Host/            # Console + Windows Service host
│   └── TradingEngine.Web/             # ASP.NET Core web viewer
├── aspire/
│   └── TradingEngine.AppHost/         # .NET Aspire orchestration (dev only)
├── tests/
│   ├── TradingEngine.Tests.Unit/
│   ├── TradingEngine.Tests.Integration/
│   └── TradingEngine.Tests.Simulation/ # Backtest simulation, no cTrader
├── config/
│   ├── strategies/                    # JSON per strategy
│   ├── risk-profiles/                 # JSON per risk profile
│   └── prop-firms/                    # JSON per prop firm ruleset
├── docs/
├── .github/
│   └── workflows/
├── .editorconfig
├── Directory.Build.props
├── Directory.Packages.props           # Central package management
└── TradingEngine.sln
```

### Project dependency rules

```
Domain        ← no dependencies on other engine projects
Application   ← Domain
Risk          ← Domain, Application
Strategies    ← Domain, Application, Services
Services      ← Domain, Application
Infrastructure← Domain, Application, Risk
Host          ← Application, Infrastructure, Risk, Strategies, Services
Web           ← Application, Infrastructure (read side only)
AppHost       ← Host, Web (Aspire references only)
Tests.*       ← anything they test + test utilities
```

**Rule:** `Domain` must never reference `Infrastructure`, `Host`, `Web`, or any NuGet package except system packages and Serilog abstractions. Enforce this with `Directory.Build.props` analyser rules.

---

## 3. Domain Model

All domain types live in `TradingEngine.Domain`. Use C# `record` for immutable value objects, `record class` for entities that need identity. Use `sealed` on all implementations. No nulls in domain — use `required` properties and validate at boundaries.

### 3.1 Core value types

```csharp
// Symbol identifier — never use raw strings
public readonly record struct Symbol(string Value)
{
    public static Symbol Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new Symbol(value.ToUpperInvariant().Trim());
    }
    public override string ToString() => Value;
}

// Money — always carry currency, never use decimal alone for money
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);
    public Money Add(Money other)
    {
        if (other.Currency != Currency)
            throw new InvalidOperationException($"Currency mismatch: {Currency} vs {other.Currency}");
        return this with { Amount = Amount + other.Amount };
    }
}

// Price — typed, never confuse with a plain decimal
public readonly record struct Price(decimal Value);

// Pips — typed
public readonly record struct Pips(double Value);

// Risk percent — 0.01 = 1%
public readonly record struct RiskPercent(double Value)
{
    public static RiskPercent Parse(double value)
    {
        if (value is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Risk percent must be between 0 and 1");
        return new RiskPercent(value);
    }
}
```

### 3.2 Market data

```csharp
public enum Timeframe { M1, M5, M15, M30, H1, H4, D1, W1 }

public record Tick(
    Symbol Symbol,
    decimal Bid,
    decimal Ask,
    DateTime TimestampUtc)
{
    public decimal Spread => Ask - Bid;
    public decimal Mid => (Bid + Ask) / 2m;
}

public record Bar(
    Symbol Symbol,
    Timeframe Timeframe,
    DateTime OpenTimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    double Volume)
{
    public bool IsBullish => Close >= Open;
    public decimal Range => High - Low;
}

// What a strategy receives on each evaluation cycle
public record MarketContext(
    Symbol Symbol,
    Tick LatestTick,
    IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>> Bars,
    IReadOnlyDictionary<string, double> IndicatorValues, // keyed by indicator name
    DateTime EngineTimeUtc);
```

### 3.3 Trade lifecycle

```csharp
public enum TradeDirection { Long, Short }

public enum OrderType { Market, Limit, Stop }

public enum OrderState
{
    Created,
    Submitted,
    Accepted,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected
}

// Produced by strategies — intent only, not an order
public record TradeIntent(
    Symbol Symbol,
    TradeDirection Direction,
    OrderType OrderType,
    Price? LimitPrice,         // null for market orders
    Price StopLoss,
    Price? TakeProfit,
    string StrategyId,
    string RiskProfileId,
    string Reason,             // human-readable signal reason, stored in audit log
    DateTime CreatedAtUtc);

// Lifecycle-tracked order owned by OrderManager
public record Order(
    Guid Id,
    TradeIntent Intent,
    decimal RequestedLots,
    OrderState State,
    Price? FillPrice,
    decimal FilledLots,
    DateTime CreatedAtUtc,
    DateTime? FilledAtUtc,
    string? RejectionReason)
{
    public bool IsTerminal =>
        State is OrderState.Filled or OrderState.Cancelled or OrderState.Rejected;
}

// Open position — mutable by position manager
public record Position(
    Guid Id,
    Guid OrderId,
    Symbol Symbol,
    TradeDirection Direction,
    decimal Lots,
    Price EntryPrice,
    Price CurrentStopLoss,
    Price? TakeProfit,
    DateTime OpenedAtUtc,
    string StrategyId)
{
    public decimal FloatingPnL(decimal currentPrice) =>
        Direction == TradeDirection.Long
            ? (currentPrice - EntryPrice.Value) * Lots * 100_000m
            : (EntryPrice.Value - currentPrice) * Lots * 100_000m;
}

// Immutable closed trade record — written to DB, never modified
public record TradeResult(
    Guid Id,
    Guid PositionId,
    Symbol Symbol,
    TradeDirection Direction,
    decimal Lots,
    Price EntryPrice,
    Price ExitPrice,
    Price StopLoss,
    Price? TakeProfit,
    DateTime OpenedAtUtc,
    DateTime ClosedAtUtc,
    Money GrossPnL,
    Money Commission,
    Money Swap,
    Money NetPnL,
    Pips PnLPips,
    double RMultiple,          // actual PnL / initial risk
    Pips MaxAdverseExcursion,  // MAE — worst point against trade
    Pips MaxFavorableExcursion,// MFE — best point in trade's favour
    string ExitReason,         // "TP", "SL", "TrailingStop", "ManualClose", "DDProtection"
    string StrategyId,
    string RiskProfileId,
    EngineMode Mode);          // Backtest / Paper / Live
```

### 3.4 Equity and drawdown

```csharp
public record EquitySnapshot(
    DateTime TimestampUtc,
    decimal Balance,
    decimal FloatingPnL,
    decimal Equity,            // Balance + FloatingPnL - commissions - swaps
    decimal PeakEquity,        // running peak for trailing DD
    decimal DailyStartEquity,  // snapshot at day open
    decimal CurrentDailyDrawdown,   // as fraction 0..1
    decimal CurrentMaxDrawdown,     // from peak, as fraction 0..1
    EngineMode Mode);

public enum EngineMode { Backtest, Paper, Live }
```

---

## 4. Core Interfaces

All interfaces live in `TradingEngine.Domain` or `TradingEngine.Application`. Infrastructure implements them.

### 4.1 Broker adapter

```csharp
// Implemented by TradingEngine.Adapters.CTrader (and future adapters)
// The engine ONLY knows this interface — never concrete broker types
public interface IBrokerAdapter
{
    // Push streams — adapter writes, engine reads
    ChannelReader<Tick> TickStream { get; }
    ChannelReader<Bar> BarStream { get; }
    ChannelReader<AccountUpdate> AccountStream { get; }
    ChannelReader<ExecutionEvent> ExecutionStream { get; }

    // Commands — engine writes, adapter executes
    Task SubmitOrderAsync(OrderRequest request, CancellationToken ct);
    Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct);
    Task CancelOrderAsync(Guid orderId, CancellationToken ct);
    Task ClosePositionAsync(Guid positionId, CancellationToken ct);

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    bool IsConnected { get; }
}

public record AccountUpdate(
    decimal Balance,
    decimal Equity,
    decimal FloatingPnL,
    DateTime TimestampUtc);

public record ExecutionEvent(
    Guid OrderId,
    OrderState NewState,
    Price? FillPrice,
    decimal FilledLots,
    string? RejectionReason,
    DateTime TimestampUtc);
```

### 4.2 Strategy

```csharp
public interface IStrategy
{
    string Id { get; }           // unique, stable, used in audit log
    string DisplayName { get; }
    IReadOnlyList<Timeframe> RequiredTimeframes { get; }
    int RequiredBarCount { get; } // warm-up bars needed before signals are valid

    // Called by engine on each cycle. Returns null = no signal.
    // MUST NOT throw. Log and return null on error.
    TradeIntent? Evaluate(MarketContext context);

    // Called when a trade opened by this strategy is closed
    void OnTradeResult(TradeResult result);

    // Called on engine start/mode switch to reset internal state
    void Reset();
}
```

### 4.3 Risk manager

```csharp
public interface IRiskManager
{
    // Returns approved lot size, or throws RiskViolationException if blocked
    decimal CalculateLotSize(TradeIntent intent, EquitySnapshot equity, RiskProfile profile);

    // Validates intent against all active constraints. Returns list of violations (empty = pass).
    IReadOnlyList<RiskViolation> Validate(TradeIntent intent, EquitySnapshot equity);

    // Current engine risk state — read by web viewer and audit log
    RiskState CurrentState { get; }

    // Called on each account update
    void OnEquityUpdate(EquitySnapshot snapshot);
}

public record RiskViolation(string Code, string Message);

public record RiskState(
    bool TradingAllowed,
    bool InProtectionMode,
    string? ProtectionReason,
    decimal DailyDrawdownUsed,   // fraction 0..1
    decimal MaxDrawdownUsed,
    decimal DailyDrawdownLimit,
    decimal MaxDrawdownLimit,
    DateTime? ProtectionUntilUtc);
```

### 4.4 Position manager

```csharp
// Owns post-entry trade management: trailing stop, breakeven, partial close
public interface IPositionManager
{
    // Called on every tick for each open position
    // Returns modification requests (engine applies them)
    IReadOnlyList<PositionModification> Evaluate(
        Position position,
        Tick currentTick,
        IReadOnlyList<Bar> recentBars);

    void RegisterPosition(Position position, PositionManagementConfig config);
    void DeregisterPosition(Guid positionId);
}

public abstract record PositionModification(Guid PositionId);
public record MoveStopLoss(Guid PositionId, Price NewStopLoss) : PositionModification(PositionId);
public record PartialClose(Guid PositionId, decimal CloseLots, string Reason) : PositionModification(PositionId);
public record ClosePosition(Guid PositionId, string Reason) : PositionModification(PositionId);
```

### 4.5 Data provider (persistence abstraction)

```csharp
// Provider abstraction — SQLite implements this; flat file or others can too
public interface IDataProvider
{
    ITradeRepository Trades { get; }
    IEquityRepository Equity { get; }
    IOrderRepository Orders { get; }
    IEventLogRepository EventLog { get; }
    IBarRepository Bars { get; }
}

public interface ITradeRepository
{
    Task SaveAsync(TradeResult trade, CancellationToken ct);
    Task<IReadOnlyList<TradeResult>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<TradeResult>> GetByStrategyAsync(string strategyId, CancellationToken ct);
}

public interface IBarRepository
{
    // Bulk write — called from buffered channel consumer, never inline on tick
    Task BulkInsertAsync(IReadOnlyList<Bar> bars, CancellationToken ct);
    Task<IReadOnlyList<Bar>> GetAsync(Symbol symbol, Timeframe tf, DateTime from, DateTime to, CancellationToken ct);
}

public interface IEventLogRepository
{
    // Append-only — no update or delete methods exposed
    Task AppendAsync(EngineEvent evt, CancellationToken ct);
    Task<IReadOnlyList<EngineEvent>> GetRecentAsync(int count, CancellationToken ct);
}
```

### 4.6 Market data provider (backtest vs live)

```csharp
public interface IMarketDataProvider
{
    // Returns async stream of ticks for the given symbol
    IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct);
    IAsyncEnumerable<Bar> StreamBarsAsync(Symbol symbol, Timeframe tf, CancellationToken ct);

    // For backtest: controls playback speed and date range
    // For live: no-op
    Task SeekAsync(DateTime from, DateTime to, CancellationToken ct);
}
```

### 4.7 Clock (testability — never use DateTime.Now directly)

```csharp
public interface IEngineClock
{
    DateTime UtcNow { get; }
}

// Live implementation — uses broker time when available, system fallback
public sealed class BrokerClock(IBrokerAdapter adapter) : IEngineClock
{
    public DateTime UtcNow => adapter.IsConnected
        ? adapter.BrokerTimeUtc
        : DateTime.UtcNow;
}

// Test implementation
public sealed class StubClock(DateTime initialTime) : IEngineClock
{
    private DateTime _now = initialTime;
    public DateTime UtcNow => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
```

---

## 5. Event Bus

No reflection. No magic. Explicit registration. Backed by `System.Threading.Channels`.

```csharp
// Domain events — raised by engine components, consumed by subscribers
// Add new events here as the engine grows
public abstract record EngineEvent(DateTime OccurredAtUtc);

public record TradeOpened(Position Position, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);
public record TradeClosed(TradeResult Result, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);
public record TradeBlocked(TradeIntent Intent, IReadOnlyList<RiskViolation> Violations, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);
public record DrawdownBreached(RiskState State, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);
public record ProtectionModeEntered(string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);
public record EquityUpdated(EquitySnapshot Snapshot, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

// Handler interface — implemented by anything that cares about engine events
public interface IEventHandler<in TEvent> where TEvent : EngineEvent
{
    Task HandleAsync(TEvent evt, CancellationToken ct);
}

// Bus — typed, no dynamic dispatch
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct) where TEvent : EngineEvent;
    void Subscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : EngineEvent;
}
```

Implementation uses one `Channel<EngineEvent>` per registered event type. Subscribers are invoked sequentially per event type to preserve ordering. Do not use `Task.WhenAll` for subscribers of the same event — ordering must be deterministic.

---

## 6. Money Management and Risk

`TradingEngine.Risk` owns all position sizing and constraint enforcement. Strategies never size positions — they only express intent and direction.

### 6.1 Risk profiles

Defined in `config/risk-profiles/<name>.json`, deserialised into:

```csharp
public record RiskProfile(
    string Id,
    string DisplayName,
    double RiskPerTradePercent,      // e.g. 0.01 = 1% of equity per trade
    double MaxDailyDrawdownPercent,  // override prop firm if tighter
    double MaxTotalDrawdownPercent,
    double MaxExposurePercent,       // total open risk across all positions
    double DrawdownScaleThreshold,   // start reducing size at this DD level e.g. 0.5 (50% of limit)
    double DrawdownScaleFloor,       // minimum size multiplier e.g. 0.5 (50% of normal)
    int MaxConcurrentPositions,
    bool AllowHedging,
    string PropFirmRuleSetId);       // links to prop firm JSON
```

### 6.2 Lot size calculation

```csharp
// In TradingEngine.Risk.PositionSizer
// Called by IRiskManager.CalculateLotSize
// Formula: lots = (equity * riskPercent) / (stopLossDistance * pipValue)
public static decimal Calculate(
    decimal equity,
    RiskPercent riskPercent,
    Pips stopLossDistance,
    decimal pipValue,         // per lot, in account currency
    decimal drawdownScaleFactor, // 0..1 applied when DD threshold crossed
    decimal maxLots,
    decimal brokerMinLots,
    decimal brokerLotStep)
{
    var riskAmount = equity * (decimal)riskPercent.Value;
    var rawLots = riskAmount / ((decimal)stopLossDistance.Value * pipValue);
    var scaledLots = rawLots * drawdownScaleFactor;
    var clampedLots = Math.Min(scaledLots, maxLots);

    // Round DOWN to broker lot step — never round up (increases risk)
    var steppedLots = Math.Floor(clampedLots / brokerLotStep) * brokerLotStep;
    return Math.Max(steppedLots, brokerMinLots);
}
```

### 6.3 FTMO rule set (default preset)

```json
// config/prop-firms/ftmo-standard.json
{
  "id": "ftmo-standard",
  "displayName": "FTMO Standard Challenge",
  "maxDailyLossPercent": 0.05,
  "maxTotalLossPercent": 0.10,
  "profitTargetPercent": 0.10,
  "equityDefinition": "BalancePlusFloatingMinusFeesAndSwaps",
  "dailyResetTimeUtc": "00:00:00",
  "dailyResetTimezone": "Europe/Prague",
  "allowTradesDuringNews": false,
  "allowWeekendHolding": false,
  "newsWindowMinutesBefore": 30,
  "newsWindowMinutesAfter": 15,
  "protectionResetPolicy": "NextTradingDay",
  "forceCloseOnBreach": false
}
```

### 6.4 Validation order in risk manager

On each `TradeIntent`, validate in this order. Return all violations, not just the first:

1. Protection mode active → block
2. Daily drawdown limit reached → block
3. Max drawdown limit reached → block
4. Max concurrent positions reached → block
5. Max exposure exceeded → block
6. News window active (if rule enabled) → block
7. Outside trading hours → block
8. Weekend hold restriction → block
9. All pass → calculate lot size

---

## 7. Persistence Layer

### 7.1 EF Core setup

Two `DbContext` classes — separate concerns and query patterns:

```csharp
// Write side — trades, orders, positions, events
public class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<TradeResultEntity> Trades => Set<TradeResultEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<PositionEntity> Positions => Set<PositionEntity>();
    public DbSet<EngineEventEntity> Events => Set<EngineEventEntity>();
    public DbSet<EquitySnapshotEntity> EquitySnapshots => Set<EquitySnapshotEntity>();
}

// Read side for web viewer — separate context, no-tracking by default
public class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    // Same DB, read-optimised queries — add views/indexes here
}
```

**EF Core rules:**
- All entities are flat (no navigation property chains in hot paths)
- Use `AsNoTracking()` on all read queries that don't need change tracking
- Never load a collection to count it — use `CountAsync()`
- Value objects (`Money`, `Price`, `Pips`) are mapped as owned entities or value converters — never store as JSON columns (breaks querying)
- All timestamps stored as UTC, `datetime` column type

### 7.2 Buffered write pipeline for bars and ticks

High-frequency data must never block the engine loop. Use a `Channel` as a write buffer:

```csharp
// In TradingEngine.Infrastructure
public sealed class BufferedBarWriter : IAsyncDisposable
{
    private readonly Channel<Bar> _channel =
        Channel.CreateBounded<Bar>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // never block on full
            SingleWriter = false,
            SingleReader = true
        });

    // Called from engine loop — non-blocking
    public bool Enqueue(Bar bar) => _channel.Writer.TryWrite(bar);

    // Background consumer — runs on dedicated thread
    public async Task ConsumeAsync(IBarRepository repo, CancellationToken ct)
    {
        var batch = new List<Bar>(500);
        await foreach (var bar in _channel.Reader.ReadAllAsync(ct))
        {
            batch.Add(bar);
            if (batch.Count >= 500 || _channel.Reader.Count == 0)
            {
                await repo.BulkInsertAsync(batch, ct);
                batch.Clear();
            }
        }
    }
}
```

**Do not** `await` database writes inline in the tick/bar processing path. Always enqueue.

### 7.3 Dapper for reporting queries

Use Dapper (not EF Core) for:
- Trade performance aggregations (win rate, profit factor, MAE/MFE distribution)
- Equity curve time-series for chart rendering
- Any query joining more than two tables
- Any query with `GROUP BY` or window functions

```csharp
// In TradingEngine.Infrastructure.Reporting
public sealed class TradeReportQueries(IDbConnection db)
{
    public async Task<PerformanceSummary> GetSummaryAsync(
        DateTime from, DateTime to, string? strategyId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                COUNT(*) AS TotalTrades,
                SUM(CASE WHEN NetPnL > 0 THEN 1 ELSE 0 END) AS Wins,
                SUM(NetPnL) AS TotalNetPnL,
                MIN(NetPnL) AS MaxSingleLoss,
                MAX(MaxAdverseExcursion) AS WorstMAE,
                AVG(CAST(DurationSeconds AS REAL) / 3600.0) AS AvgHoldHours
            FROM Trades
            WHERE ClosedAtUtc BETWEEN @from AND @to
              AND (@strategyId IS NULL OR StrategyId = @strategyId)
            """;
        return await db.QuerySingleAsync<PerformanceSummary>(
            sql, new { from, to, strategyId },
            commandTimeout: 30);
    }
}
```

### 7.4 Provider abstraction

The `IDataProvider` implementation for SQLite lives in `TradingEngine.Infrastructure.Persistence.Sqlite`. If a different backend is needed, create a new assembly implementing `IDataProvider` and register it in DI. The engine sees only `IDataProvider`.

Register in host:
```csharp
builder.Services.AddSqliteDataProvider(connectionString);
// Future: builder.Services.AddFlatFileDataProvider(basePath);
```

---

## 8. Engine Modes

The engine core is identical across all three modes. The mode determines which `IBrokerAdapter` and `IMarketDataProvider` are injected.

```
Backtest  →  HistoricalDataProvider  +  SimulatedBrokerAdapter
Paper     →  LiveBrokerAdapter        +  SimulatedBrokerAdapter  (live data, simulated fills)
Live      →  LiveBrokerAdapter        +  CTraderBrokerAdapter    (live data, real fills)
```

`SimulatedBrokerAdapter` applies the slippage and spread model from the risk profile. It responds to `SubmitOrderAsync` with a synthetic fill event on the next tick. This makes backtest and paper mode produce realistic — not perfect — execution.

### 8.1 Engine lifecycle (simplified)

```
Start
  → Load config and validate
  → Register strategies
  → Connect broker adapter (if live/paper)
  → Seed indicators (warm-up bars)
  → Start event bus consumer
  → Start buffered writers
  → Enter main loop:
      On Tick:
        → Update indicators
        → Update equity snapshot
        → Check risk state
        → For each strategy: Evaluate(context)
        → For each intent: RiskManager.Validate → CalculateLotSize → Submit
        → For each position: PositionManager.Evaluate → Apply modifications
      On Bar:
        → BufferedBarWriter.Enqueue
      On ExecutionEvent:
        → OrderManager.OnExecution
        → If filled: open position, publish TradeOpened
        → If closed: compute TradeResult, publish TradeClosed, save to DB
  → On shutdown: flush channels, close positions (if configured), disconnect
```

---

## 9. cTrader Adapter

Lives in `TradingEngine.Adapters.CTrader`. This is a **separate project targeting C# 6 / .NET Framework 4.x** to match the cTrader cBot runtime. It has no references to the engine core.

### 9.1 What the cBot does

1. Receives ticks and bars from cTrader API
2. Serialises them to a simple binary or JSON format
3. Writes to named pipe (`\\.\pipe\trading-engine`)
4. Reads order commands from the same pipe
5. Executes order commands via cTrader API
6. Nothing else

### 9.2 Named pipe protocol

Define a minimal message protocol. Each message is length-prefixed (4-byte int) followed by UTF-8 JSON:

```csharp
// Message types — C# 6 compatible (no records, no pattern matching)
public class PipeMessage
{
    public string Type { get; set; }  // "Tick", "Bar", "AccountUpdate", "OrderCommand"
    public string Payload { get; set; } // JSON of the specific type
}
```

The engine side (`TradingEngine.Infrastructure.Adapters.NamedPipeBrokerAdapter`) deserialises pipe messages into domain types and pushes to the appropriate `Channel<T>`. The engine never knows it's reading from a pipe — it only sees `IBrokerAdapter`.

### 9.3 C# 6 constraint rules

The cBot project must not use:
- Records (`record`, `record struct`)
- Pattern matching (`switch` expressions, `is` with patterns)
- Nullable reference types (`?` annotations)
- `required` properties
- `init` setters
- Top-level statements
- Any C# 7+ features

Put a `<LangVersion>6</LangVersion>` in the cBot project file and treat compile errors as the guardrail.

---

## 10. Services Layer

### 10.1 Skender adapter

Skender.Stock.Indicators uses its own `IQuote` type. Wrap it completely:

```csharp
// In TradingEngine.Infrastructure (depends on Skender NuGet)
internal sealed class SkenderQuote(Bar bar) : IQuote
{
    public DateTime Date => bar.OpenTimeUtc;
    public decimal Open => bar.Open;
    public decimal High => bar.High;
    public decimal Low => bar.Low;
    public decimal Close => bar.Close;
    public decimal Volume => (decimal)bar.Volume;
}

// Public indicator adapter — domain sees only this
public interface IIndicatorService
{
    double Atr(IReadOnlyList<Bar> bars, int period);
    double Ema(IReadOnlyList<Bar> bars, int period);
    double Sma(IReadOnlyList<Bar> bars, int period);
    (double Upper, double Middle, double Lower) BollingerBands(IReadOnlyList<Bar> bars, int period, double stdDev);
    double Rsi(IReadOnlyList<Bar> bars, int period);
    // Add as needed — never expose Skender types outside this service
}
```

**Important:** Skender computes indicators from the full list each call. Cache results keyed by `(symbol, timeframe, indicatorName, period, barCount)` with a short TTL. Do not call Skender on every tick — call it on each new closed bar.

### 10.2 SL/TP calculator

```csharp
public interface ISlTpCalculator
{
    Price CalculateStopLoss(
        Price entryPrice,
        TradeDirection direction,
        SlMethod method,
        SlParameters parameters,
        IReadOnlyList<Bar> recentBars);

    Price? CalculateTakeProfit(
        Price entryPrice,
        Price stopLoss,
        TradeDirection direction,
        TpMethod method,
        TpParameters parameters);
}

public enum SlMethod { FixedPips, AtrMultiple, SwingHigh, SwingLow }
public enum TpMethod { None, FixedPips, RRMultiple, AtrMultiple }
```

### 10.3 Trailing stop

```csharp
public interface ITrailingStopService
{
    // Returns new SL price if it should move, null if no change
    Price? Evaluate(
        Position position,
        Tick currentTick,
        TrailingConfig config,
        IReadOnlyList<Bar> recentBars);
}

public record TrailingConfig(
    TrailingMethod Method,   // StepPips, AtrMultiple, BreakevenThenTrail
    double StepPips,
    double AtrMultiple,
    double BreakevenTriggerR); // activate breakeven at e.g. 1.0R profit
```

---

## 11. Web Viewer

`TradingEngine.Web` is an ASP.NET Core app. In dev, Aspire manages it alongside the engine host. In prod, it runs as a separate process on localhost.

### 11.1 API surface (minimal, read-mostly)

```
GET /api/trades?from=&to=&strategyId=&mode=
GET /api/trades/{id}                        → full TradeResult + signal metadata
GET /api/performance?from=&to=&strategyId=  → PerformanceSummary (Dapper query)
GET /api/equity?from=&to=                   → time-series for equity curve chart
GET /api/risk/state                         → current RiskState (live stream via SSE)
GET /api/events?tail=100                    → recent engine events
GET /api/export/trades.csv?from=&to=        → CSV export
```

Server-sent events (SSE) on `/api/risk/state` push live equity and risk updates to the browser without polling. Use `IEventBus` subscription on the web side, do not query the DB on every tick.

### 11.2 Chart data contract

The trade detail endpoint returns enough for the chart explorer to render without extra calls:

```json
{
  "trade": { ... TradeResult fields ... },
  "signal": {
    "strategyId": "trend-breakout",
    "trigger": "Break of high",
    "filtersEvaluated": [
      { "name": "MA filter", "passed": true, "value": "bullish" },
      { "name": "Session filter", "passed": true, "value": "London open" }
    ],
    "indicatorsAtEntry": {
      "EMA20": 1.0839,
      "ATR14": 0.0021
    }
  },
  "riskAtEntry": {
    "dailyDdUsed": 0.021,
    "maxDdUsed": 0.048,
    "sizeScaleFactor": 1.0,
    "lots": 0.1
  },
  "bars": [ ... N bars around the trade for chart rendering ... ]
}
```

### 11.3 Aspire setup

```csharp
// aspire/TradingEngine.AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

var engine = builder.AddProject<Projects.TradingEngine_Host>("engine")
    .WithEnvironment("ENGINE_MODE", "Backtest");

var web = builder.AddProject<Projects.TradingEngine_Web>("web")
    .WithReference(engine)
    .WithEndpoint(port: 5200, scheme: "http");

builder.Build().Run();
```

Aspire gives structured logs, traces, and health checks from both services in the Aspire dashboard at `http://localhost:15888` during dev. Wire `ILogger` and `ActivitySource` (OpenTelemetry) in both projects — Aspire picks them up automatically.

---

## 12. Testing Strategy

No test requires cTrader. No test requires a running database by default.

### 12.1 Unit tests (`TradingEngine.Tests.Unit`)

- Pure domain logic: lot size calculator, risk validation rules, trailing stop logic, SL/TP calculator
- Use `StubClock` for any time-dependent test
- Use `StubBrokerAdapter` that satisfies `IBrokerAdapter` with in-memory channels
- Test every `RiskViolation` code with a dedicated test — these are the prop firm contract
- Test `PositionSizer.Calculate` with table-driven tests covering: normal, DD threshold, min lot floor, step rounding

```csharp
// Example pattern for table-driven tests
public static TheoryData<PositionSizerTestCase> SizerCases => new()
{
    new(Equity: 10_000, Risk: 0.01, SL: 20, PipValue: 10, Scale: 1.0, Expected: 0.05m),
    new(Equity: 10_000, Risk: 0.01, SL: 20, PipValue: 10, Scale: 0.5, Expected: 0.02m), // DD scaling
    new(Equity:  1_000, Risk: 0.01, SL: 50, PipValue: 10, Scale: 1.0, Expected: 0.01m), // floor
};
```

### 12.2 Integration tests (`TradingEngine.Tests.Integration`)

- Use SQLite in-memory (`:memory:`) — fast, no file system
- Test full trade lifecycle: intent → risk check → lot size → order → fill → TradeResult saved
- Test event bus: publish event, assert handler called with correct payload
- Test buffered bar writer: enqueue N bars, verify DB contains them after flush

### 12.3 Simulation tests (`TradingEngine.Tests.Simulation`)

- Load real or synthetic OHLCV data from CSV files in `tests/data/`
- Run full backtest through `SimulatedBrokerAdapter`
- Assert: trade count, win rate within expected range, no DD rule violations, all events logged
- These tests validate the engine end-to-end without any network or broker

```csharp
// Skeleton simulation test pattern
[Fact]
public async Task TrendBreakoutStrategy_OnBullishMarket_GeneratesPositivePnL()
{
    var engine = EngineTestHarness.Create()
        .WithStrategy(new TrendBreakoutStrategy(config))
        .WithRiskProfile(RiskProfile.Conservative)
        .WithHistoricalData("data/eurusd-h1-2024.csv")
        .WithPropFirmRules(PropFirmPresets.FtmoStandard)
        .Build();

    var result = await engine.RunBacktestAsync(
        from: new DateTime(2024, 1, 1),
        to: new DateTime(2024, 3, 31));

    Assert.True(result.NetPnL > 0);
    Assert.True(result.MaxDrawdown < 0.08); // well inside 10% FTMO limit
    Assert.Empty(result.PropFirmViolations);
}
```

---

## 13. Configuration

### 13.1 Strongly-typed config classes

```csharp
// Registered via Options pattern
public record EngineOptions
{
    public required EngineMode Mode { get; init; }
    public required string ActiveRiskProfileId { get; init; }
    public required IReadOnlyList<string> ActiveStrategyIds { get; init; }
    public required string PropFirmRuleSetId { get; init; }
    public required BrokerOptions Broker { get; init; }
}

public record BrokerOptions
{
    public required string PipeName { get; init; }  // named pipe name
    public required int ConnectionTimeoutMs { get; init; }
    public required int ReconnectDelayMs { get; init; }
}
```

### 13.2 JSON strategy config example

```json
// config/strategies/trend-breakout.json
{
  "id": "trend-breakout",
  "displayName": "Trend Breakout v1",
  "enabled": true,
  "symbols": ["EURUSD", "GBPUSD"],
  "riskProfileId": "conservative",
  "parameters": {
    "lookbackBars": 20,
    "maPeriod": 50,
    "atrPeriod": 14,
    "slAtrMultiple": 1.5,
    "tpRrMultiple": 2.0,
    "trailingMethod": "AtrMultiple",
    "trailingAtrMultiple": 1.0
  }
}
```

Config is loaded at startup and validated. Unknown keys throw. Missing required keys throw. Strategies fail fast on bad config — never silently default.

---

## 14. Code Standards

These apply to all code written for this project. Violations block PR merge.

### 14.1 C# rules

- C# 13 features are encouraged: primary constructors, collection expressions, `params ReadOnlySpan<T>`
- `record` for immutable value objects. `class` for services with lifecycle
- `sealed` on all non-abstract implementation classes
- `required` on all non-optional properties in records
- `CancellationToken` as last parameter on every async method, no exceptions
- No `async void`. Background work uses `IHostedService` or `Task.Run` with explicit error handling
- No `Task.Result` or `.Wait()`. Async all the way
- No `DateTime.Now` or `DateTime.UtcNow` directly — inject `IEngineClock`
- No `Thread.Sleep` — use `await Task.Delay()`
- No `catch (Exception)` without logging and rethrowing or returning a typed error
- Prefer `ArgumentException.ThrowIfNullOrWhiteSpace()`, `ArgumentOutOfRangeException.ThrowIfNegative()` over manual null checks at public boundaries
- No `dynamic`
- No `object` parameters in domain code
- Decimal for all money and price arithmetic — never `double` or `float`
- `double` is acceptable for indicator values and non-monetary metrics

### 14.2 Naming

- Interfaces: `IFoo`
- Implementations: `FooService`, `FooManager`, `FooCalculator` — never just `Foo`
- Domain events: past tense — `TradeOpened`, `DrawdownBreached`
- Config records: `FooOptions`
- Test classes: `FooTests`, test methods: `MethodName_Condition_ExpectedResult`

### 14.3 Logging

Use structured logging everywhere. No string interpolation in log calls:

```csharp
// Wrong
_logger.LogInformation($"Trade closed: {tradeId} PnL={pnl}");

// Correct
_logger.LogInformation("Trade closed. TradeId={TradeId} NetPnL={NetPnL}", tradeId, pnl);
```

Log level guidance:
- `Trace` — per-tick data, indicator values
- `Debug` — strategy evaluations, risk checks
- `Information` — trade opened/closed, mode changes, startup/shutdown
- `Warning` — risk violations, blocked trades, connection retries
- `Error` — unhandled exceptions, data inconsistencies
- `Critical` — DD breach, protection mode entered, engine cannot continue

### 14.4 File organisation

One primary type per file. File name matches type name. No partial classes except for EF Core generated code.

---

## 15. Git Workflow

### 15.1 Branch structure

```
main          → production-ready only, protected, no direct commits
develop       → integration branch, protected, PR required
feature/*     → new features (feature/risk-engine, feature/trailing-stop)
fix/*         → bug fixes (fix/dd-calculation-rounding)
refactor/*    → no behaviour change (refactor/event-bus-generics)
chore/*       → tooling, deps, config (chore/update-skender)
```

### 15.2 Commit messages

Format: `type(scope): description`

Types: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`
Scope: `domain`, `risk`, `strategy`, `persistence`, `web`, `adapter`, `infra`

Examples:
```
feat(risk): add drawdown scale factor to position sizer
fix(persistence): prevent duplicate bar insert on reconnect
test(simulation): add FTMO violation detection scenario
```

### 15.3 PR rules

- Every PR targets `develop`, not `main`
- PR must have at least one test covering the change
- PR description must state: what changed, why, and what was tested
- `main` is updated only via a release PR from `develop`, tagged with semver

---

## 16. CI/CD

GitHub Actions. Two workflows:

### 16.1 Pull request workflow (`.github/workflows/pr.yml`)

Triggers on: PR opened or updated targeting `develop`

```yaml
jobs:
  build-and-test:
    runs-on: windows-latest   # Windows Service target
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.x'
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-build -c Release --logger "trx" --collect:"XPlat Code Coverage"
      - uses: codecov/codecov-action@v4  # optional but useful
  lint:
    runs-on: windows-latest
    steps:
      - run: dotnet format --verify-no-changes
```

### 16.2 Release workflow (`.github/workflows/release.yml`)

Triggers on: push to `main`

```yaml
jobs:
  release:
    runs-on: windows-latest
    steps:
      - run: dotnet publish src/TradingEngine.Host -c Release -r win-x64 --self-contained
      - run: dotnet publish src/TradingEngine.Web  -c Release -r win-x64 --self-contained
      # Package as zip, create GitHub Release, attach artifacts
```

---

## 17. AI Agent Implementation Guidelines

This section is written for the AI agent iterating on this codebase. Read every point before making changes.

### 17.1 What to implement first (order matters)

1. `TradingEngine.Domain` — all types and interfaces from sections 3 and 4. No logic yet.
2. `TradingEngine.Risk` — `PositionSizer`, `RiskManager`, FTMO rule set. Unit-testable with no infrastructure.
3. `TradingEngine.Tests.Unit` — test every risk validation code and sizer formula.
4. `TradingEngine.Infrastructure` — EF Core entities, SQLite provider, `BufferedBarWriter`, `NamedPipeBrokerAdapter`.
5. `TradingEngine.Services` — `IndicatorService` (Skender wrapper), `SlTpCalculator`, `TrailingStopService`.
6. `TradingEngine.Strategies` — `TrendBreakoutStrategy` as the validation strategy.
7. `TradingEngine.Tests.Simulation` — `EngineTestHarness`, first backtest simulation test.
8. `TradingEngine.Host` — wire everything with DI, `IHostedService` for engine loop.
9. `TradingEngine.Web` — API endpoints, SSE, basic chart data endpoint.
10. `TradingEngine.Adapters.CTrader` — cBot with named pipe. Last, because engine must be testable without it.

### 17.2 Common mistakes to avoid

**Decimal vs double:** Use `decimal` for all price, money, and lot size arithmetic. `double` for indicator values and statistical metrics only. Never mix them in arithmetic — cast explicitly and comment why.

**Channel backpressure:** When creating a `BoundedChannel`, always set `FullMode`. `DropOldest` for market data (stale data is worse than lost data). `Wait` for command channels (never drop an order command). Never leave `FullMode` at default.

**EF Core tracking:** All read queries in reporting use `AsNoTracking()`. Only write operations use tracked entities. If you see `.Include()` in a reporting query, that is a red flag.

**Skender caching:** Do not call `bars.GetEma(period)` on every tick. Skender iterates the full list each call. Call it once per closed bar and cache the result keyed by bar count.

**Strategy state:** Strategies may hold internal state (indicator history, last signal). This state must be reset via `Reset()` on mode switch or engine restart. If a strategy holds state that isn't reset, backtest results will bleed into live state.

**Named pipe reconnection:** The engine must handle the cBot disconnecting and reconnecting (cTrader restarts, VPS reboots). Implement exponential backoff retry in `NamedPipeBrokerAdapter`. Never crash the engine on a pipe disconnection.

**Clock:** Never use `DateTime.Now` or `DateTime.UtcNow` directly. Always use `IEngineClock.UtcNow`. This is what makes simulation tests time-controllable.

**Lot size rounding:** Always round DOWN to the broker's lot step, never up. Rounding up increases risk beyond the specified percent. The formula in section 6.2 is correct — do not simplify it.

**Risk validation in strategies:** Strategies must not check risk state. If a strategy skips signalling because "it looks like we're near the DD limit", that is a violation of separation of concerns. The risk manager blocks the intent — the strategy doesn't need to know.

**C# 6 in cBot:** The cBot project must compile with `<LangVersion>6</LangVersion>`. Any C# 7+ feature in that project is a build error. Do not reference any engine project from the cBot — only primitive types and the pipe protocol.

**Money type:** Never store money as a plain `decimal` in the domain. Always use `Money(Amount, Currency)`. When computing cross-currency PnL, the conversion must be explicit and traceable. Do not silently assume account currency.

**Event log:** The `IEventLogRepository` has no update or delete method. Do not add one. If a correction is needed, append a compensating event.

**Test data:** Place all test CSV data in `tests/data/`. Commit it to the repo. Tests must not download data at runtime.

**Aspire in prod:** The `TradingEngine.AppHost` project is dev-only. It must not be referenced from `Host` or `Web`. Aspire service defaults (`AddServiceDefaults()`) should be called in `Host` and `Web` — this enables telemetry in both dev and prod without Aspire itself running in prod.

---

## 18. Definition of Done (per iteration)

Before marking any iteration complete, verify:

- [ ] All new public types have XML doc comments (`///`)
- [ ] All new async methods accept `CancellationToken`
- [ ] All new risk validation codes have a unit test
- [ ] No Skender types appear outside `TradingEngine.Infrastructure`
- [ ] `dotnet format` passes with no changes
- [ ] All tests pass: `dotnet test`
- [ ] No new `DateTime.Now` or `DateTime.UtcNow` calls
- [ ] Simulation test still passes after changes
- [ ] Event log has an entry for every domain event in the scenario

---

*Version: 1.0 — first iteration. Finalize, then begin implementation in order listed in section 17.1.*
