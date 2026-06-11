# Shamshir Trading Engine — Architecture & Debugging Handover

*For Claude, iter/16-ctrader-inproc branch on commit 1488937*

---

## 1. System Architecture

```
┌─────────────────────────────────────────────────────────┐
│  Web UI (Razor Pages)                                   │
│  src/TradingEngine.Web/                                 │
│                                                         │
│  BacktestOrchestrator.cs — the conductor                │
│    Start() → RunAsync() →                              │
│      if UseForBacktest=true:                            │
│        RunEngineNetMqAsync()   ← in-process cTrader     │
│      else:                                              │
│        RunEngineReplayAsync()  ← DB bar replay          │
└──────────────┬──────────────────────────────────────────┘
               │
     ┌─────────▼───────────┐
     │  Inner IHost         │  (created per backtest run)
     │  DI container with:  │
     │   - NetMQBrokerAdapter│
     │   - EngineWorker     │
     │   - All strategies   │
     │   - Risk manager     │
     │   - TradingDbContext │
     └─────────┬───────────┘
               │ NetMQ (PUB/SUB + ROUTER/DEALER)
     ┌─────────▼───────────┐
     │  ctrader-cli.exe    │  (external process via CliWrap)
     │  └─ TradingEngineCBot│
     │     (src.algo)       │
     │     .NET 6 cBot      │
     └─────────────────────┘
```

### Key files

| File | Role |
|------|------|
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Orchestrates backtest runs. `RunEngineNetMqAsync` (line ~403) is the cTrader path |
| `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` | NetMQ transport: SUB (receives bars/ticks/execs from cBot), ROUTER (sends orders to cBot) |
| `src/TradingEngine.Host/EngineWorker.cs` | Engine core. In Live mode: `Task.WhenAll(ProcessTicksAsync, ProcessBarsAsync, ProcessAccountUpdatesAsync, ProcessExecutionEventsAsync)` |
| `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` | cBot running inside ctrader-cli. Binds PUB, connects DEALER, subscribes to bars, handles order commands |
| `src/TradingEngine.CTraderRunner/CTraderCli.cs` | CliWrap wrapper for ctrader-cli.exe |
| `src/TradingEngine.Web/Program.cs` | Web app startup — creates main DI, ensures DB schema |

---

## 2. Logging & Tracing Architecture

### Two logging channels

| Channel | How | When visible |
|---------|-----|-------------|
| **SSE Progress** | `PushProgressEvent(runId, eventType, message)` → `BacktestProgressStore` → `GET /api/backtest/{runId}/stream` | DURING run, real-time |
| **Final log** | `EnqueueLog(runId, logLines, msg)` → `BacktestRunState.LogLines` → `GET /api/backtest/{runId}/logs` | AFTER run completes |

Key diagnostic events are pushed to BOTH via `PushProgressAndLog` (line ~71) — currently: `EXEC`, `REJECTED`, `NETMQ_CONNECTED`, `NETMQ_SENT`, `NETMQ_DROPPED`, `CBOT`.

### Progress event types

| Type | Source | Meaning |
|------|--------|---------|
| `BAR` | `EngineWorker.ProcessBarsAsync` / `RunBacktestLoopAsync` | A bar was processed |
| `SIGNAL` | Same | A strategy fired a trade signal |
| `ORDER` | Same | An order was dispatched |
| `EXEC` | `DrainExecutionStreamAsync` + `ProcessTicksAsync` | Engine received an execution event (fill) |
| `REJECTED` | Same | Engine received a rejection event |
| `NETMQ_CONNECTED` | `NetMQBrokerAdapter.OnStatusChange` | cBot's DEALER connected to engine's ROUTER |
| `NETMQ_SENT` | `NetMQBrokerAdapter.SubmitOrderAsync` | Engine sent an order to cBot via ROUTER |
| `NETMQ_DROPPED` | Same | Order dropped because cBot not connected |
| `CBOT` | `NetMQBrokerAdapter.OnSubReceive` (diag topic) | cBot diagnostic message forwarded from PUB |

### cBot tracing (internal to cTrader process)

The cBot uses two output methods:
- `Print(...)` — goes to ctrader-cli stdout + cTrader log file. NEVER reaches engine. Captured by CliWrap as `CTraderResult.StandardOutput`
- `Diag(msg)` — sends via NetMQ PUB with topic `"diag"`. Reaches engine's SUB → `OnSubReceive` → forwarded to progress as `CBOT` event

cBot diagnostic messages used:
- `HEARTBEAT|N` — slow-joiner mitigation during OnStart
- `BAR_SENT|symbol|tf|time|close=...|seq=N` — bar published via PUB
- `SUBSCRIBED|symbol|tf|loaded=N` — bar subscription complete
- `DEALER_RECV|queueDepth=N|jsonLen=N` — cBot received command via DEALER
- `TICK_DRAIN|tick=N|queued=N|processed=N` — cBot drained `_mainActions` queue on tick
- `CMD_RECV|submit_order|...` — cBot called HandleSubmitOrder
- `EXEC_SENT|orderId|Filled/Rejected|...` — cBot executed/failed order
- `CMD_ERR|msg` — exception during command processing

---

## 3. cBot ↔ Engine NetMQ Interaction

### Socket topology

```
cBot                          Engine
════                          ══════
PUB ──bind──► *:dataPort      SUB ──connect──► 127.0.0.1:dataPort
     ◄── bars, ticks,          ◄── bars, ticks,
         execs, diag               execs, diag

DEALER ──connect──► 127.0.0.1:commandPort
                                ROUTER ──bind──► *:commandPort
     ◄── orders (commands)          ──send──► orders
```

### Message flow for a trade

```
1. cBot OnBarClosed → PUB topic="bar" → engine SUB receives
2. Engine ProcessBarsAsync → strategy.Evaluate() → SIGNAL
3. Engine OrderDispatcher → broker.SubmitOrderAsync()
4. NetMQBrokerAdapter → ROUTER.SendMoreFrame(identity).SendFrame(json)
5. cBot DEALER → OnDealerReceive → _mainActions.Enqueue(HandleCommand)
6. cBot OnTick → _mainActions.TryDequeue → HandleCommand → HandleSubmitOrder
7. HandleSubmitOrder → ExecuteMarketOrder() → PublishExec()
8. cBot PUB topic="exec" → engine SUB receives
9. Engine ProcessExecutionEventsAsync → _executionEventChannel
10. Engine ProcessTicksAsync / DrainExecutionStreamAsync → PositionTracker.OnExecutionAsync
11. PositionTracker → TradeClosed event → TradePersistenceHandler → DB
```

### cBot order processing (key timing constraint)

Orders sent by the engine arrive via DEALER on the NetMQ poller thread. They're enqueued to `_mainActions` (a `ConcurrentQueue<Action>`). They are ONLY dequeued and processed inside `OnTick()` — the cTrader tick callback. If `OnTick` never fires, orders pile up in the queue forever.

The cBot publishes ticks at `TickEveryN` intervals (default: every 10th tick). In M1 backtest mode, M1 ticks are simulated.

---

## 4. Test Infrastructure

### Tests directory structure

```
tests/
  TradingEngine.Tests.Unit/          — 87 tests, component-level
  TradingEngine.Tests.Integration/   — 15 tests, DB + API level
  TradingEngine.Tests.Simulation/    — 12 tests, end-to-end pipeline
    Pipeline/
      FullBacktestPipelineTest.cs    — old BacktestRunner path (engine subprocess)
      NetMQBridgeTest.cs             — standalone engine subprocess with PUB/DEALER
      InProcessCtraderTest.cs        — in-process engine + ctrader-cli (1 day)
      CtraderPipelineDiagnosticTest.cs — comprehensive diag: 30-day, 3-day, M15, GBPUSD
      PortHelper.cs                  — dynamic port allocation (TcpListener)
    Scenarios/
      InProcessEngineSmokeTests.cs   — inner host DI starts/stops cleanly (no CLI)
      DrawdownScenarios.cs           — risk drawdown limits
    Harness/
      ReplayTestHarness.cs           — replay adapter test harness
```

### Test inventory

| Test | What it proves | CLI needed | Trades check |
|------|---------------|------------|-------------|
| `ReplayBacktest_FullPipeline` | Replay path works with seeded bars | No | No (only bar evals) |
| `InProcessEngineSmokeTests` | Inner host DI resolves + NetMQ starts/stops | No | No |
| `InProcessCtraderTest` | In-process engine + ctrader-cli 1-day EURUSD | Yes | No (only bar evals) |
| `CtraderPipelineDiagnosticTest` EURUSD 30-day | Web mirror — produces trades | Yes | **Yes (20 trades)** |
| `CtraderPipelineDiagnosticTest` EURUSD 3-day | Short window — produces trades | Yes | **Yes (8 trades)** |
| `CtraderPipelineDiagnosticTest` GBPUSD 30-day | Cross pair — produces trades | Yes | **Yes (38 trades)** |
| `CtraderPipelineDiagnosticTest` M15 3-day | Short timeframe — 0 signals | Yes | **0 trades (expected)** |
| `ThreeDays_PipeAndDataFlow` | Old BacktestRunner path EURUSD+GBPUSD | Yes | No (pipe only) |
| `NetMQBridgeTest` | Engine subprocess receives bars via NetMQ | No | No |

**Critical: `CtraderPipelineDiagnosticTest` PASSES with trades > 0 for EURUSD/GBPUSD 30-day. But the web UI path (identical code via `RunEngineNetMqAsync`) produces 0 trades.**

---

## 5. cTrader CLI Integration

### CTraderCli class

`src/TradingEngine.CTraderRunner/CTraderCli.cs` — wraps `ctrader-cli.exe` via CliWrap:

```csharp
public async Task<CTraderResult> BacktestAsync(string algoPath, string[] extraArgs, CancellationToken ct)
```

Prepends `["backtest", algoPath]` to extraArgs. Captures stdout/stderr. Returns `CTraderResult(ExitCode, StdOut, StdErr, IsKnownPostBacktestCrash)`.

### CLI syntax

```
ctrader-cli.exe backtest <cbot.algo> --start=DD/MM/YYYY --end=DD/MM/YYYY
  --symbol=NAME --period=tf --data-mode=m1
  --ctid=ID --pwd-file=PATH --account=NUM
  --DataPort=N --CommandPort=N --SymbolString=X --Periods=X
  --full-access [--balance=N] [--commission=N] [--spread=N]
```

### Known CLI issues

1. `--report`/`--report-json` args use `/` (forward slash) paths, NOT Windows `\`. Backslashes cause `"Parameter contains invalid characters"` error, killing the entire backtest. These args have been **removed** — cTrader writes reports to `{algo-dir}/data/src/{run-guid}/Backtesting/` natively.

2. cTrader generates `events.json`, `report.html`, `log.txt`, `parameters.cbotset` in the native Backtesting directory. These are **only populated when actual trades occur** (when `ExecuteMarketOrder` succeeds). Empty runs produce 0-byte `events.json`.

3. Exit code 1 is normal (known post-backtest crash with `"Message expected"` or `"Object reference"` in stdout). Treated as success in `RunEngineNetMqAsync`.

### CLI binary location

- Searched by `CTraderCliLocator.Locate()`: `%LocalAppData%\Spotware\cTrader\{installationId}\ctrader-cli.exe`
- Current: `C:\Users\shahi\AppData\Local\Spotware\cTrader\abb70432efbee65d18af69e79fe8efe1\ctrader-cli.exe`

### cBot binary

- Compiled from `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` (targets .NET 6)
- Output: `src/TradingEngine.Adapters.CTrader/bin/Debug/net6.0/src.algo`
- Also copied to: `%USERPROFILE%\OneDrive\Documents\cAlgo\Sources\Robots\src.algo`

---

## 6. Current Issue — Detailed Analysis

### Symptom

Web UI shows: `Bars: 70+ | Signals: 30+ | Trades: 0`. Progress shows `NETMQ_CONNECTED`, `NETMQ_SENT` (dozens), `BAR`, `SIGNAL`, `ORDER`. But **never** `EXEC`, `REJECTED`, `DEALER_RECV`, or `TICK_DRAIN`.

### Evidence chain (from latest log run 12:50 UTC)

| Event | Seen? | Implication |
|-------|-------|------------|
| `CBOT HEARTBEAT|1..10` | ✅ | cBot OnStart ran, PUB bound |
| `CBOT SUBSCRIBED\|loaded=419` | ✅ | cBot subscribed to bars, data loaded |
| `CBOT BAR_SENT\|seq=1..76` | ✅ | Bars flowing cBot→engine via PUB |
| `NETMQ_CONNECTED` | ✅ (after heartbeats, before bars) | cBot DEALER connected to engine ROUTER |
| `NETMQ_SENT` | ✅ (dozens) | Engine sent orders via ROUTER |
| `CBOT DEALER_RECV` | ❌ **NEVER** | cBot DEALER never received a single order |
| `CBOT TICK_DRAIN` | ❌ **NEVER** | cBot OnTick never drained _mainActions |
| `CBOT CMD_RECV` | ❌ **NEVER** | cBot HandleCommand never called |
| `CBOT EXEC_SENT` | ❌ **NEVER** | cBot never executed any order |
| `EXEC` / `REJECTED` | ❌ **NEVER** | Engine never received any fill/rejection |

### Inconclusive diagnostics

- `CBOT TICK_DRAIN` was added to the cBot's `OnTick` but never appeared. This could mean:
  - (A) `OnTick` never fires in cTrader backtest mode
  - (B) `OnTick` fires but cBot's `Diag()` isn't publishing to PUB
  - (C) `OnTick` fires but `_mainActions.Count` was 0 at the time (orders arrived between ticks and were processed before the queue check)

- `DEALER_RECV` was added to `OnDealerReceive` but never appeared. This directly proves that **the cBot's DEALER never received messages from the engine's ROUTER**.

### Root cause hypotheses

| # | Hypothesis | Evidence |
|---|-----------|----------|
| H1 | Engine's ROUTER.SendMoreFrame(send incorrectly formatted identity) | The cBot connects (hello gets through engine's ROUTER → identity is set in `_cBotIdentity`). But the ROUTER needs the EXACT identity bytes to address the cBot. If identity is corrupted during capture (`identity.ToArray()` on line 158), subsequent sends fail silently |
| H2 | CliWrap's argument array format causes ctrader-cli to not start the cBot properly | Manual testing with PowerShell `Start-Process` produced 68 CBOT lines correctly. But the EXACT same code in `CTraderCli` might have subtle differences (working directory, env vars) |
| H3 | Engine ROUTER binds on wrong interface or port | Config shows `tcp://*:{commandPort}` and cBot connects to `tcp://127.0.0.1:{commandPort}`. The `*` bind should accept loopback connections. Ports are dynamically allocated per run, no conflicts |
| H4 | cBot's `OnDealerReceive` is never registered/activated | The poller `_poller.RunAsync()` starts the DEALER receive loop. If the poller doesn't start before the engine sends orders, messages are lost |

### What the diagnostic test does differently

`CtraderPipelineDiagnosticTest` (which produces trades) creates the inner host with **identical DI** as `RunEngineNetMqAsync`. It launches the CLI with the same argument format via `CTraderCli.BacktestAsync`. Yet it produces trades (20 for EURUSD 30-day).

**The diagnostic test uses a fresh temp DB per run.** The web app uses `data/trading.db`. Both point to the same file path, but the inner host's `TradingDbContext` is a **separate DI registration** — two DI containers pointing to the same SQLite file. This is fine for schema but might cause subtle conflicts.

---

## 7. Proposed Next Diagnostic

The missing piece: does the engine's ROUTER successfully deliver ANY message to the cBot?

**Ping-on-connect test**: In `NetMQBrokerAdapter.OnRouterReceive`, immediately after `_cBotIdentity` is set (line ~165), send a `{type: "ping"}` command. The cBot already handles `case "ping": break;` (line 166). If ping is received:
- cBot's `OnDealerReceive` fires → `Diag("DEALER_RECV|...")` → `CBOT DEALER_RECV` appears in progress
- cBot's `HandleCommand` fires → `Print("CBOT|CMD|ping|...")` → appears in CLI stdout

If ping works but orders don't, the issue is in the order JSON format or the ROUTER identity for subsequent messages. If ping doesn't work, the ROUTER socket itself isn't delivering.

To implement:
1. Add `SendCommandAsync(new { type = "ping" }, ct)` in `OnRouterReceive` after identity capture
2. The cBot doesn't need code changes

---

## 8. Key Code Snippets

### NetMQBrokerAdapter identity capture (line ~156-166)
```csharp
private void OnRouterReceive(object? sender, NetMQSocketEventArgs e)
{
    var identity = e.Socket.ReceiveFrameBytes();
    var json = e.Socket.ReceiveFrameString();
    if (_cBotIdentity is null)
    {
        _cBotIdentity = identity;  // <-- identity stored here
        OnConnected?.Invoke();
        OnStatusChange?.Invoke("NETMQ_CONNECTED", "cBot connected via ROUTER");
        // PROPOSED: SendCommandAsync(new { type = "ping" }, ct) here
    }
}
```

### ROUTER send (line ~198-206)
```csharp
private Task SendCommandAsync(object command, CancellationToken ct)
{
    if (_router is null || _cBotIdentity is null)
    {
        OnStatusChange?.Invoke("NETMQ_DROPPED", ...);
        return Task.CompletedTask;
    }
    var json = JsonSerializer.Serialize(command, ...);
    _router.SendMoreFrame(_cBotIdentity).SendFrame(json);
    return Task.CompletedTask;
}
```

### cBot DEALER receive (line ~149-154)
```csharp
private void OnDealerReceive(object? sender, NetMQSocketEventArgs e)
{
    if (!e.Socket.TryReceiveFrameString(out var json) || json is null) return;
    var captured = json;
    _mainActions.Enqueue(() => HandleCommand(captured));
    Diag($"DEALER_RECV|queueDepth={_mainActions.Count}|jsonLen={captured.Length}");
}
```

### cBot OnTick queue drain (line ~88-96)
```csharp
protected override void OnTick()
{
    _tickCounter++;
    var queued = _mainActions.Count;
    var processed = 0;
    while (_mainActions.TryDequeue(out var action))
    {
        try { action(); processed++; }
        catch (Exception ex) { Print($"CBOT|CMD_ERR|{ex.Message}"); }
    }
    if (queued > 0)
        Diag($"TICK_DRAIN|tick={_tickCounter}|queued={queued}|processed={processed}");
    // ... publish tick at TickEveryN intervals
}
```

---

## 9. Useful Commands

```powershell
# Build
dotnet build --no-incremental

# Fast tests (no cTrader needed)
dotnet test tests/TradingEngine.Tests.Unit --no-build
dotnet test tests/TradingEngine.Tests.Integration --no-build

# Simulation tests (some need credentials)
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "Category!=Pipeline&Category!=Slow"

# Diagnostic test (web mirror, needs credentials)
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "EurUsd_H1_30Days"

# Manual cTrader CLI test
$cli = "C:\Users\shahi\AppData\Local\Spotware\cTrader\abb70432efbee65d18af69e79fe8efe1\ctrader-cli.exe"
$algo = Resolve-Path "src\TradingEngine.Adapters.CTrader\bin\Debug\net6.0\src.algo"
& $cli backtest $algo --start=15/01/2024 --end=16/01/2024 --symbol=EURUSD --period=h1 --data-mode=m1 --ctid=seankiaa --pwd-file="C:\Users\shahi\Documents\ctrader.pwd" --account=5834367 --full-access

# Check cTrader backtest logs
Get-ChildItem "src\TradingEngine.Adapters.CTrader\bin\Debug\net6.0\data\src" -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object { Write-Host "--- $($_.Name) ---"; Get-Content (Join-Path $_.FullName "Backtesting\log.txt") }
```
