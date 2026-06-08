# ITERATION 9 — Observability, Bar History, Multi-Symbol

---

## ⚠️ New Session Context — Read This First

This iteration is being started in a **fresh agent session**. The following changes were made in the prior design session but **are not yet committed**. Commit them before writing any new code.

### Uncommitted changes (verify with `git diff --stat`)

| File | Change made |
|------|-------------|
| `src/TradingEngine.Host/Program.cs` | Symbol registry path fixed: 4 `..` → 5 `..`. Was resolving to `src\config\symbols\defaults.json` (doesn't exist). Now resolves to `config\symbols\defaults.json` at solution root. EURUSD was never being registered — every signal dispatch threw `KeyNotFoundException`. |
| `src/TradingEngine.Host/EngineWorker.cs` | `ProcessBarsAsync` exception handling moved inside the `await foreach` loop. Previously, any exception (including the EURUSD `KeyNotFoundException`) would log "Bar processor crashed" and permanently exit the loop. Now per-bar errors log `BAR_PROC_ERR|` and the loop continues. |
| `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs` | Both test variants: removed vestigial `Engine__Broker__PipeName` env var and `Engine:Broker:PipeName` BacktestRunner config. Replaced with explicit `Engine__Broker__NetMQ__DataPort=15555` and `Engine__Broker__NetMQ__CommandPort=15556`. |

### Commit these before starting Phase 1

```
git add src/TradingEngine.Host/Program.cs
git add src/TradingEngine.Host/EngineWorker.cs
git add tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs
git commit -m "fix: symbol registry path, per-bar exception handling, remove vestigial PipeName from tests"
```

### What is and is not working right now

**Working (Iteration 8, fully committed):**
- NetMQ transport cBot ↔ engine
- 3-month test passes with `SIGNAL=1` (mean-reversion fires after 34 bars)
- NetMQBridgeTest passes without cTrader credentials
- 87 unit tests pass

**Not yet verified (pending the uncommitted fixes above):**
- `ORDER|` appearing after a signal — blocked by the symbol path bug. After committing and rebuilding, this should now work.
- `EXEC|` appearing after an order — follows from ORDER; cBot must receive command and reply.

**Known remaining issue (root cause identified, fix is Phase 3 below):**
- Only 34 `BAR_EVAL` lines for both 3-day and 3-month tests. `MarketData.GetBars(tf, symbol)` loads a fixed platform default of ~34 H1 bars. `HistoryBars` parameter fix is Phase 3.

### Engine rebuild required for the uncommitted fixes

The tests run with `--no-build`. The compiled engine binary must be current:

```
dotnet build src\TradingEngine.Host --no-incremental
```

The cBot `.algo` does NOT need rebuilding for these fixes — they are engine-side only. The `.algo` from Iteration 8 is still valid and will be used for Phases 1–2. **Phase 1 of this iteration adds new cBot code and DOES require rebuilding the `.algo`.**

---

## Mandatory Reading Before Writing Any Code

1. `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` — current cBot. Note: single symbol/timeframe, no `HistoryBars` parameter, no `diag` channel, `bar.OpenTime.ToString("o")` without UTC kind.
2. `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` — `OnSubReceive` and `DispatchMessage`. The `diag` short-circuit goes here.
3. `src/TradingEngine.Host/EngineWorker.cs` — `ProcessBarsAsync` (per-bar exception handling is present after the uncommitted fix). Confirm the equity zero-guard is absent — it is added in Phase 4.
4. `src/TradingEngine.Host/Program.cs` — symbol registry path is 5 levels up after the uncommitted fix. Confirm before running tests.
5. `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs` — current assertions stop at `SIGNAL`. Phase 5 extends them to `ORDER` and `EXEC`.
6. `docs/ITERATION-8-HANDOVER.md` — full prior-iteration context. Pay attention to the 34-bar finding and the "Message expected" zero-trade crash.

Do not start coding until you have read all six files above.

---

## Context

Iteration 8 completed NetMQ transport, bar-close strategy evaluation, and proved the pipeline end-to-end: the 3-month test passes with `SIGNAL=1`. Two critical issues block meaningful backtesting:

1. **No unified observability**: debugging "why does ORDER not appear?" requires cross-referencing cBot stdout (test console) against the engine log file. These are two separate outputs with no shared trace key. This costs significant iteration time — both for agents and for human review. **This is fixed first** because it makes every subsequent fix faster to verify.

2. **34-bar limit**: Both 3-day and 3-month tests receive exactly 34 `BAR_EVAL` lines. `MarketData.GetBars(tf, symbol)` loads a platform-default chart window (~34 H1 bars). Strategies that need 55+ bars (trend-breakout, ema-alignment) never evaluate. Mean-reversion fires only because it needs ~14–25 bars for RSI.

---

## Architecture After This Iteration

```
ctrader-cli (backtest)                    Engine process (TradingEngine.Host)
┌─────────────────────────────────┐       ┌────────────────────────────────────┐
│  TradingEngineCBot              │       │  NetMQBrokerAdapter                │
│                                 │       │                                    │
│  PUB socket (bind :DataPort) ───┼──────►│ SUB socket (connect :DataPort)     │
│    topic "bar"  → Bar OHLC     │       │   → Channel<Bar>                   │
│    topic "tick" → Bid/Ask      │       │   → Channel<Tick>                  │
│    topic "acct" → Balance      │       │   → Channel<AccountUpdate>         │
│    topic "exec" → Fills        │       │   → Channel<ExecutionEvent>        │
│    topic "diag" → trace lines  │       │   → _logger.LogInformation(CBOT|…) │  ← NEW
│                                 │       │                                    │
│  DEALER socket (connect         │       │  ROUTER socket (bind :CommandPort) │
│             :CommandPort) ◄─────┼───────│ commands                          │
│                                 │       │                                    │
│  SubscriptionManager (NEW)      │       │                                    │
│    per (symbol, timeframe) pair │       │                                    │
│    → bars.BarClosed → Publish  │       │                                    │
└─────────────────────────────────┘       └────────────────────────────────────┘
```

**Nothing below `IBrokerAdapter` changes.** `EngineWorker`, strategies, and risk layer are unaware of the `diag` channel or multi-symbol cBot internals.

---

## Phase 1 — Unified Pipeline Telemetry (`diag` channel)

**Do this first.** Costs one `.algo` rebuild (required for Phase 3 anyway). Makes every subsequent phase faster to debug.

**Goal**: every bar's complete journey — from cBot send through engine evaluation to order and execution — is visible in the single engine log file, correlated by `openTime`.

### 1a. cBot publishes `diag` topic

**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs`

Add a private helper that sends a plain string on the `diag` topic:

```csharp
private void Diag(string msg)
{
    if (_pub is null) return;
    _pub.SendMoreFrame("diag").SendFrame(msg);
}
```

Call it at the key lifecycle points:

```csharp
// In OnStart(), after GetBars() — BEFORE bars.BarClosed subscription:
Diag($"BAR_INIT|{SymbolName}|{TimeFrame.ShortName}|count={bars.Count}");

// In OnBarClosed(), AFTER the dedup check, BEFORE Publish("bar", ...):
Diag($"BAR_SENT|{bars.SymbolName}|{bars.TimeFrame.ShortName}|{DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc):o}|close={bar.Close:F5}|seq={_barEventCount}");

// In HandleSubmitOrder(), immediately after parsing parameters:
Diag($"CMD_RECV|submit_order|{clientOrderId}|{symbol}|{direction}|lots={lots:F4}");

// In HandleSubmitOrder(), after result — replace nothing, add alongside existing Print:
if (result?.IsSuccessful == true)
    Diag($"EXEC_SENT|{clientOrderId}|Filled|fill={result.Position.EntryPrice:F5}|lots={result.Position.VolumeInUnits / sym.LotSize:F4}");
else
    Diag($"EXEC_SENT|{clientOrderId}|Rejected|reason={result?.Error}");

// In OnStop(), first line:
Diag($"STOP|ticks={_tickCounter}|barEvents={_barEventCount}|dup={_duplicateCount}");
```

Keep all existing `Print()` calls unchanged — they appear in the test console for quick triage during startup before the engine is ready to receive `diag`.

### 1b. Engine handles `diag` topic

**File**: `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` — `OnSubReceive`

The `diag` frames are **plain strings**, not JSON. Short-circuit before `JsonDocument.Parse`:

```csharp
private void OnSubReceive(object? sender, NetMQSocketEventArgs e)
{
    try
    {
        var topic = e.Socket.ReceiveFrameString();
        var frame = e.Socket.ReceiveFrameString();

        if (topic == "diag")
        {
            _logger.LogInformation("CBOT|{Msg}", frame);
            return;
        }

        using var doc = JsonDocument.Parse(frame);
        DispatchMessage(topic, doc.RootElement);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "NETMQ|SUB_PARSE_ERR");
    }
}
```

### 1c. Rebuild `.algo`

```
dotnet clean src\TradingEngine.Adapters.CTrader
dotnet build src\TradingEngine.Adapters.CTrader -c Debug
```

`dotnet clean` is required because new code was added to the cBot without adding a new `[Parameter]`. The `.cbotset` cache may or may not be stale but cleaning is safe.

### 1d. What the log looks like after this phase

A single grep on any openTime string shows the full bar journey:

```
CBOT|BAR_SENT|EURUSD|H1|2024-03-15T09:00:00.0000000Z|close=1.09215|seq=847
BAR_EVAL|EURUSD|H1|openTime=2024-03-15 09:00|close=1.09215|bars=847|total=847
EVAL|mean-reversion|EURUSD|NO_SIGNAL
SIGNAL|mean-reversion|EURUSD|Long|sl=1.08900|tp=1.09500
ORDER|mean-reversion|abc123|Long|lots=0.01|entry=1.09215
CBOT|CMD_RECV|submit_order|abc123|EURUSD|Long|lots=0.0100
CBOT|EXEC_SENT|abc123|Filled|fill=1.09218|lots=0.0100
EXEC|abc123|Filled|fill=1.09218|lots=0.01
```

---

## Phase 2 — Verify Prior Fixes

With the `diag` channel in place, run the 3-month test to confirm the symbol path fix and exception handling are working.

```
set CTrader__CtId=seankiaa
set CTrader__PwdFile=C:\Users\shahi\Documents\ctrader.pwd
set CTrader__Account=5834367
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeMonth"
```

**Read the engine log and confirm all of the following:**

1. `CBOT|BAR_INIT|EURUSD|H1|count=34` — confirms diag channel is working
2. `CBOT|BAR_SENT|...` lines — confirms bars are being sent (should match BAR_EVAL count)
3. `SIGNAL|mean-reversion|...` — at least one
4. `ORDER|mean-reversion|...` — **must now appear** (symbol path fix enables this)
5. `CBOT|CMD_RECV|submit_order|...` — confirms cBot received the command
6. `CBOT|EXEC_SENT|...|Filled|...` — confirms cBot executed the order
7. `EXEC|...|Filled|...` — confirms engine received the execution event

**If ORDER appears but no CBOT|CMD_RECV**: the command was sent but `_router` didn't have the cBot identity. Check for `NETMQ|CMD_DROPPED` in the log.

**If CBOT|CMD_RECV appears but CBOT|EXEC_SENT shows Rejected**: read the rejection reason. Common causes: `lots=0` (equity guard needed — Phase 4b), or cTrader error (symbol not found, invalid SL/TP).

**Do not proceed to Phase 3 until ORDER and EXEC appear in the log.**

---

## Phase 3 — Bar History Fix

**Root cause**: `MarketData.GetBars(tf, symbol)` loads ~34 H1 bars regardless of backtest length. The `CBOT|BAR_INIT|count=34` line from Phase 1 now confirms this directly. Strategies needing 55+ bars (trend-breakout, ema-alignment, session-breakout) never fire.

### 3a. Add `HistoryBars` parameter

**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs`

```csharp
[Parameter("History Bars", DefaultValue = "2000")]
public int HistoryBars { get; set; } = 2000;
```

### 3b. Use count overload in `OnStart()`

Replace:
```csharp
var bars = MarketData.GetBars(TimeFrame, SymbolName);
```

With:
```csharp
var bars = MarketData.GetBars(TimeFrame, SymbolName, HistoryBars);
```

### 3c. Rebuild `.algo` — **`dotnet clean` is mandatory here**

A new `[Parameter]` was added. The `.cbotset` cache maps parameter names to positions. If it is stale, cTrader silently ignores `--HistoryBars=2000` and uses its internal default.

```
dotnet clean src\TradingEngine.Adapters.CTrader
dotnet build src\TradingEngine.Adapters.CTrader -c Debug
```

### 3d. Verify

Rerun the 3-day test. The `CBOT|BAR_INIT|count=` line in the engine log should now show ~2000 (or however many bars of history are available). `BAR_EVAL` count should significantly exceed 34.

```
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeDays"
```

**Expected**: `CBOT|BAR_INIT|...|count=2000` (or close), `BAR_EVAL` count > 300. `EVAL|trend-breakout|EURUSD|NEED_BARS` debug lines should disappear once bar count exceeds 55.

---

## Phase 4 — Remaining Bug Fixes

### 4a. UTC timestamp: `bar.OpenTime` kind

**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` — `OnBarClosed`

**Problem**: `bar.OpenTime.ToString("o")` emits `DateTimeKind.Unspecified` — no `Z` suffix. The engine's `GetDateTime().ToUniversalTime()` treats `Unspecified` as local machine time. On UTC+2 servers all bar `OpenTimeUtc` values shift 2 hours forward; session filters may reject signals.

**Fix** (one line in `OnBarClosed`, and in the `Diag` call):

```csharp
var openTimeUtc = DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc).ToString("o");
```

Use `openTimeUtc` in both the `Diag()` call and the `Publish("bar", ...)` payload:

```csharp
openTime = openTimeUtc,
```

`Server.TimeInUtc` (used for ticks, account, exec) is already UTC — do not change those.

### 4b. Equity guard before order dispatch

**File**: `src/TradingEngine.Host/EngineWorker.cs` — `ProcessBarsAsync`, inside the strategy foreach

**Problem**: `_currentEquity` starts zero-initialized. In rare timing cases a signal fires before any `AccountUpdate` is consumed, producing 0-lot orders that cTrader silently rejects. `CBOT|EXEC_SENT|...|Rejected|reason=Invalid volume` would be the symptom.

**Fix**: add a guard immediately before dispatch:

```csharp
var equity = Volatile.Read(ref _currentEquity);
if (equity.Balance == 0)
{
    _logger.LogWarning("DISPATCH_SKIP|{Strategy}|{Symbol}|reason=equity not initialized",
        strategy.Id, bar.Symbol.Value);
    continue;
}
var orderCtx = await _orderDispatcher.DispatchAsync(intent, equity, bar.Close, _broker, ct);
```

### 4c. Zero-trade crash in ctrader-cli

**File**: `src/TradingEngine.CTraderRunner/BacktestRunner.cs`

**Problem**: ctrader-cli exits with code 1 and "Message expected" or "Object reference" in stderr when the backtest ends with zero completed trades (report generation fails). `BacktestResult.ExitCode = 1` causes noise but the test passes because exit code is not asserted.

**Fix**: normalize the known crash to exit code 0:

```csharp
var isKnownCrash = cliProcess.ExitCode != 0
    && (stderr.Contains("Message expected") || stderr.Contains("Object reference"));

if (cliProcess.ExitCode != 0 && !isKnownCrash)
    _logger.LogError("ctrader-cli failed. Code={Code} Stderr={Stderr}", cliProcess.ExitCode, stderr);
else if (isKnownCrash)
    _logger.LogWarning("ctrader-cli exited with known post-backtest crash (zero trades). Code={Code}", cliProcess.ExitCode);

return new BacktestResult
{
    RunId        = runId,
    ExitCode     = isKnownCrash ? 0 : cliProcess.ExitCode,
    ErrorMessage = isKnownCrash ? null : (cliProcess.ExitCode != 0 ? stderr : null),
};
```

---

## Phase 5 — Multi-Symbol/Timeframe Subscription

**Goal**: cBot subscribes to multiple `(symbol, timeframe)` pairs via parameters. The engine already handles multi-symbol in `_bars[Symbol][Timeframe]` — no engine changes needed.

### 5a. Add `Symbols` and `Periods` parameters

**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs`

```csharp
[Parameter("Symbols", DefaultValue = "EURUSD")]
public string Symbols { get; set; } = "EURUSD";

[Parameter("Periods", DefaultValue = "H1")]
public string Periods { get; set; } = "H1";
```

Remove the use of the Robot's built-in `TimeFrame` property for bar subscription. It is still available but no longer used for `GetBars`.

### 5b. `SubscriptionManager` in `OnStart()`

Replace the single `GetBars` + `BarClosed` lines with:

```csharp
private readonly List<Bars> _subscriptions = new();

private void SubscribeAll()
{
    var symbols = Symbols.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var periods  = Periods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    foreach (var sym in symbols)
    foreach (var period in periods)
    {
        var tf   = ParseTimeFrame(period);
        var bars = MarketData.GetBars(tf, sym, HistoryBars);
        bars.BarClosed += OnBarClosed;
        _subscriptions.Add(bars);
        Diag($"SUBSCRIBED|{sym}|{period}|loaded={bars.Count}");
    }
}

private static TimeFrame ParseTimeFrame(string s) => s.ToUpperInvariant() switch
{
    "M1"  => TimeFrame.Minute,
    "M5"  => TimeFrame.Minute5,
    "M15" => TimeFrame.Minute15,
    "M30" => TimeFrame.Minute30,
    "H1"  => TimeFrame.Hour,
    "H4"  => TimeFrame.Hour4,
    "D1"  => TimeFrame.Daily,
    "W1"  => TimeFrame.Weekly,
    _     => throw new ArgumentException($"Unknown timeframe: {s}")
};
```

In `OnStart()`, replace `var bars = MarketData.GetBars(...)` + `bars.BarClosed += OnBarClosed` with a single call to `SubscribeAll()`.

### 5c. Multi-symbol dedup

`_prevBarOpen` / `_prevBarClose` dedup is global — two different symbols with the same `OpenTime` would collide. Replace with a set:

```csharp
// Remove fields:
// private DateTime _prevBarOpen = DateTime.MinValue;
// private double _prevBarClose;

// Add field:
private readonly HashSet<(string symbol, string tf, DateTime openTime)> _publishedBars = new();
```

In `OnBarClosed`, replace the `if (bar.OpenTime > _prevBarOpen)` block:

```csharp
var key = (bars.SymbolName, bars.TimeFrame.ShortName, bar.OpenTime);
if (!_publishedBars.Add(key)) { _duplicateCount++; return; }
```

### 5d. Use `args.Bars.TimeFrame.ShortName` in `OnBarClosed`

Each `Bars` object knows its own timeframe. Replace any remaining use of the Robot's `TimeFrame.ShortName` with `bars.TimeFrame.ShortName` (where `bars = args.Bars`).

### 5e. BacktestRunner passes Symbols/Periods

**File**: `src/TradingEngine.CTraderRunner/BacktestConfig.cs`

```csharp
public string[] Symbols { get; init; } = ["EURUSD"];
public string[] Periods { get; init; } = ["H1"];
```

**File**: `src/TradingEngine.CTraderRunner/BacktestRunner.cs` — `BuildArgs`

```csharp
sb.Append($" --Symbols={string.Join(",", cfg.Symbols)}");
sb.Append($" --Periods={string.Join(",", cfg.Periods)}");
```

### 5f. Rebuild `.algo` — `dotnet clean` is mandatory

New `[Parameter]` attributes were added (`Symbols`, `Periods`).

```
dotnet clean src\TradingEngine.Adapters.CTrader
dotnet build src\TradingEngine.Adapters.CTrader -c Debug
```

---

## Phase 6 — Test Assertions and Dynamic Ports

### 6a. Full round-trip assertions

**File**: `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs`

Extend log analysis and assertions in both test variants:

```csharp
var orderLines  = allLines.Where(l => l.Contains("ORDER|")).ToList();
var execLines   = allLines.Where(l => l.Contains("EXEC|") && !l.Contains("EXEC_SENT")).ToList();
var cbotDiag    = allLines.Where(l => l.Contains("CBOT|")).ToList();

Console.WriteLine($"  CBOT| diag lines: {cbotDiag.Count}");
Console.WriteLine($"  ORDER lines:      {orderLines.Count}");
Console.WriteLine($"  EXEC  lines:      {execLines.Count}");

// Ordered — each is a prerequisite for the next:
if (!netmqConnected.Any())
    Assert.Fail("cBot never connected via NetMQ");
if (!barLines.Any())
    Assert.Fail("No BAR_EVAL lines. Check CBOT|BAR_SENT and CBOT|BAR_INIT in log.");
barLines.Count.Should().BeGreaterThan(50,
    "strategies need warmup bars — if failing, check HistoryBars parameter and .cbotset cache");
signalYes.Should().NotBeEmpty("at least one strategy should signal over the test period");
orderLines.Should().NotBeEmpty("a signal must produce an ORDER — check equity guard and DispatchAsync");
execLines.Should().NotBeEmpty("an ORDER must produce an EXEC — check CBOT|EXEC_SENT in log");
```

### 6b. Dynamic port allocation

**Problem**: hardcoded ports 15555/15556 cause `address already in use` when a previous engine process hasn't released the port, or when parallel test runs overlap.

**File**: `tests/TradingEngine.Tests.Simulation/Pipeline/PortHelper.cs` (new file)

```csharp
internal static class PortHelper
{
    public static (int dataPort, int commandPort) AllocatePair()
    {
        using var a = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        using var b = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        a.Start(); b.Start();
        var p1 = ((System.Net.IPEndPoint)a.LocalEndpoint).Port;
        var p2 = ((System.Net.IPEndPoint)b.LocalEndpoint).Port;
        a.Stop(); b.Stop();
        return (p1, p2);
    }
}
```

In both test variants, replace hardcoded `15555`/`15556` with:

```csharp
var (dataPort, commandPort) = PortHelper.AllocatePair();
```

Pass these to the engine subprocess env vars and the `BacktestRunner` config. The cBot receives them via `--DataPort` and `--CommandPort` already wired in `BacktestRunner.BuildArgs`.

---

## Verification Sequence

Run in order. Each must pass before the next.

**Step 1 — Commit pending changes and rebuild engine**
```
git add src/TradingEngine.Host/Program.cs src/TradingEngine.Host/EngineWorker.cs
git add tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs
git commit -m "fix: symbol registry path, per-bar exception handling, remove vestigial PipeName"
dotnet build src\TradingEngine.Host --no-incremental
```

**Step 2 — No regressions**
```
dotnet test tests\TradingEngine.Tests.Unit --no-build
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "Category!=Pipeline"
```

**Step 3 — Rebuild `.algo` after Phase 1**
```
dotnet clean src\TradingEngine.Adapters.CTrader
dotnet build src\TradingEngine.Adapters.CTrader -c Debug
```

**Step 4 — NetMQBridgeTest (transport sanity, <20s, no credentials)**
```
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "Category=NetMQ"
```

**Step 5 — Phase 2 gate (prior fixes + diag channel)**
```
set CTrader__CtId=seankiaa
set CTrader__PwdFile=C:\Users\shahi\Documents\ctrader.pwd
set CTrader__Account=5834367
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeMonth"
```
Confirm in engine log: `CBOT|BAR_INIT`, `ORDER|`, `CBOT|CMD_RECV`, `EXEC|`.

**Step 6 — After Phase 3 (bar history fix)**

Rerun Step 5. `CBOT|BAR_INIT|...|count=` should now be ~2000. `BAR_EVAL` count > 300. All four strategies should evaluate (no `NEED_BARS` for strategies with ≤ 2000 bar requirement).

**Step 7 — Full assertions after Phase 6**
```
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeMonth"
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeDays"
```
All six assertions must pass: NetMQ connected, bars > 50, signals > 0, orders > 0, execs > 0.

---

## Critical Files

| File | Change |
|---|---|
| `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` | `diag` channel (Phase 1), UTC timestamp (Phase 4a), `HistoryBars` param (Phase 3), `SubscriptionManager` (Phase 5) |
| `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` | `diag` short-circuit in `OnSubReceive` (Phase 1) |
| `src/TradingEngine.Host/EngineWorker.cs` | Equity guard before `DispatchAsync` (Phase 4b) |
| `src/TradingEngine.CTraderRunner/BacktestRunner.cs` | Zero-trade crash handling (Phase 4c), `Symbols`/`Periods` in `BuildArgs` (Phase 5e) |
| `src/TradingEngine.CTraderRunner/BacktestConfig.cs` | `Symbols` and `Periods` arrays (Phase 5e) |
| `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs` | Full round-trip assertions, dynamic ports (Phase 6) |
| `tests/TradingEngine.Tests.Simulation/Pipeline/PortHelper.cs` | New file — dynamic port allocation (Phase 6b) |
| `docs/DECISIONS.md` | D77–D80 |

---

## Decisions to Record in DECISIONS.md

**D77 — `MarketData.GetBars` count overload required**
`GetBars(tf, symbol)` loads ~34 H1 bars by default in backtest. `GetBars(tf, symbol, count)` forces the history depth. Default 2000. Made configurable via `HistoryBars` cBot parameter.

**D78 — `bar.OpenTime` must be explicitly declared UTC**
`bar.OpenTime` returns `DateTimeKind.Unspecified`. `ToString("o")` emits no `Z`. On UTC+N machines this produces wrong `OpenTimeUtc` in the engine. Fix: `DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc)` before serialization.

**D79 — `diag` PUB topic for unified observability**
cBot publishes plain-string trace lines on a `diag` topic. Engine's `NetMQBrokerAdapter` logs them as `CBOT|…` in the engine log file. Creates a single correlated log for the full bar journey: sent → evaluated → signaled → ordered → executed.

**D80 — Multi-symbol via cBot parameters**
cBot accepts comma-separated `Symbols` and `Periods` parameters. `SubscriptionManager` creates one `bars.BarClosed` subscription per `(symbol, timeframe)` pair. Engine's `_bars` dictionary handles multi-symbol without changes. Dedup uses `HashSet<(symbol, tf, openTime)>`.

---

## Notes for Implementing Agent

1. **`dotnet clean` before every `.algo` rebuild that adds or renames `[Parameter]`**. The `.cbotset` cache maps parameter positions. A stale cache silently ignores new parameters and uses internal defaults. Symptom: `HistoryBars` has no effect, `CBOT|BAR_INIT|count=34` persists.

2. **`diag` frames are plain strings, not JSON**. The `OnSubReceive` short-circuit for `topic == "diag"` must come BEFORE `JsonDocument.Parse(frame)`. Do not attempt to parse diag frames as JSON.

3. **`args.Bars.TimeFrame` in `OnBarClosed`** — each `Bars` object exposes its own timeframe. Use `args.Bars.TimeFrame.ShortName`, not the Robot's top-level `TimeFrame.ShortName`, which would be wrong for secondary subscriptions.

4. **`_prevBarOpen`/`_prevBarClose` dedup breaks for multi-symbol**. Two symbols can have identical `OpenTime` values (both fire at `2024-01-15T00:00:00`). The `HashSet<(symbol, tf, openTime)>` fix in Phase 5c is required.

5. **Do not change `IBrokerAdapter` or any strategy code**. All changes are in the transport layer (cBot, NetMQBrokerAdapter) and engine routing (EngineWorker equity guard).

6. **Equity guard (Phase 4b) may not be necessary in practice** — ticks flood in during warmup replay long before bar 55. But it's a correctness fix and should be added regardless. If `CBOT|EXEC_SENT|...|Rejected|reason=Invalid volume` appears in the log, the equity guard is why.

7. **PortHelper TOCTOU race**: OS may reuse the allocated port between `Stop()` and the engine's `Bind()`. Retry once on failure. This is acceptable for test use.

8. Write `docs/ITERATION-9-HANDOVER.md` at completion. Include: verified test results, `CBOT|BAR_INIT|count=` before/after Phase 3, whether all six round-trip assertions pass, and remaining issues.
