# ITERATION 7 — Pipe Fix, Protocol Consolidation, Observability

## Mandatory Reading Before Touching Any Code

Read these files in order. The plan's instructions match their current state exactly — verify before implementing.

1. `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` — the cBot entry point; understand the pipe wiring, what `_pipe.Connect()` does, and why all publishers are silent when disconnected
2. `src/TradingEngine.Adapters.CTrader/PipeMessage.cs` + `MessageSerializer.cs` — understand the double-serialization: `Payload` is a `string`, not an object
3. `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs` — the engine-side pipe server; `ProcessMessage` attempts to navigate a JSON String element as if it were an Object (the parse bug)
4. `src/TradingEngine.Host/DataFeedService.cs` — `FeedTicksAsync` calls `sim.OnTickReceived(tick)` AND writes to the channel (double-call bug)
5. `src/TradingEngine.Host/EngineWorker.cs` — `ProcessTicksAsync` line 155: also calls `sim.OnTickReceived(tick)` — this is the second call
6. `src/TradingEngine.CTraderRunner/BacktestRunner.cs` — `WaitForEngineReadyAsync` is just `Task.Delay(5000)`; `RunAsync` uses `ReadToEndAsync` post-exit
7. `src/TradingEngine.Host/Program.cs` — `StubClock(DateTime.UtcNow)` is set at startup and never advances
8. `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs` — current test structure and assertions

Do not read or modify any other files until Phase 1 is complete and passing.

---

## Context

The cTrader CLI backtest path has never reliably moved data between the cBot and the engine. Two root-cause categories exist: (1) the named pipe connection silently fails inside `ctrader-cli`, cause unknown (security context, namespace, timing); (2) even if the pipe connected, a double-serialization bug in the `PipeMessage` protocol would make every message unparseable on the engine side. On top of this, the simulated path has a double `OnTickReceived` call that double-executes fills and SL/TP hits.

Iteration 6's supposed fix (FileBrokerAdapter + Print-based stdout) was never written to disk. The codebase is stuck between two incomplete designs. File-based IPC is explicitly off the table.

This iteration: fix the known code bugs first, then instrument the pipe path to get a definitive root cause, fix it, and build a fast `PipeConnectivityTest` as the agent's primary iteration loop before touching the full ctrader-cli pipeline.

---

## Phase 1 — Pre-work: Trivial Correctness Bugs

All are one-to-three line changes. Fix all before touching any pipe code.

### 1a. Double `OnTickReceived` in DataFeedService

**File**: `src/TradingEngine.Host/DataFeedService.cs` — `FeedTicksAsync`, lines 60–63

**Problem**: `DataFeedService.FeedTicksAsync` writes to `sim.TickWriter` AND calls `sim.OnTickReceived(tick)`. Then `EngineWorker.ProcessTicksAsync` reads from the channel and calls `sim.OnTickReceived(tick)` again (line 155–156). Every tick triggers fills and SL/TP logic TWICE.

**Fix**: Delete `sim.OnTickReceived(tick);` from `DataFeedService.FeedTicksAsync`. The only call should be in `EngineWorker.ProcessTicksAsync`.

```csharp
// DataFeedService.FeedTicksAsync — AFTER fix (remove the OnTickReceived call)
private async Task FeedTicksAsync(Symbol symbol, CancellationToken ct)
{
    await foreach (var tick in marketData.StreamTicksAsync(symbol, ct))
    {
        if (broker is SimulatedBrokerAdapter sim)
            await sim.TickWriter.WriteAsync(tick, ct);
    }
}
```

### 1b. StubClock never advances

**File**: `src/TradingEngine.Host/Program.cs` — lines 105–108

**Problem**: `new StubClock(DateTime.UtcNow)` at startup never advances. Session filters, daily reset, strategy time checks all see engine-start time across a 3-month backtest.

**Fix**: Use `BrokerClock` for ALL modes. `SimulatedBrokerAdapter.BrokerTimeUtc` is already updated on every `OnTickReceived` call. `BrokerClock` (`src/TradingEngine.Domain/Clock/BrokerClock.cs`) falls back to `DateTime.UtcNow` when disconnected, which is correct for startup.

```csharp
// Program.cs — remove the Backtest/else split; always register BrokerClock:
builder.Services.AddSingleton<IEngineClock, BrokerClock>();
```

Delete the `if (mode == EngineMode.Backtest) ... else` block for clock registration entirely.

### 1c. Unbounded channels in adapters

**Files**: `src/TradingEngine.Infrastructure/Adapters/SimulatedBrokerAdapter.cs` (lines 7–11) and `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs` (lines 16–19)

**Problem**: All four channels use `Channel.CreateUnbounded<T>()`. Code standard mandates `BoundedChannelFullMode.DropOldest` for market data streams.

**Fix** for both adapters — replace the four channel declarations:
```csharp
private readonly Channel<Tick> _tickChannel =
    Channel.CreateBounded<Tick>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
private readonly Channel<Bar> _barChannel =
    Channel.CreateBounded<Bar>(new BoundedChannelOptions(2_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
private readonly Channel<AccountUpdate> _accountChannel =
    Channel.CreateBounded<AccountUpdate>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
private readonly Channel<ExecutionEvent> _executionChannel =
    Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });
```
Execution stays `Wait` (never drop orders, standard rule).

### 1d. VolumeInUnits conversion in cBot publishers

**Files**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` — `SendInitialState`, `OnPositionOpened`, `OnPositionClosed`

**Problem**: `pos.VolumeInUnits / 100000.0` is hardcoded. Wrong for non-FX (gold=100 units/lot, BTC=1 unit/lot).

**Fix**: Replace with `pos.VolumeInUnits / symbol.LotSize` where `symbol = Symbols.GetSymbol(pos.SymbolName)`. If symbol is null, fall back to 100000. This is the same approach `OrderCommandHandler.LotsToVolume` uses with `VolumeInUnitsStep`.

```csharp
private static double VolumeToLots(double volumeInUnits, cAlgo.API.Internals.Symbol symbol)
{
    var lotSize = symbol?.LotSize ?? 100_000.0;
    return lotSize > 0 ? volumeInUnits / lotSize : volumeInUnits / 100_000.0;
}
```

### 1e. Guid correlation bug on position close

**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` — `OnPositionClosed`, line 133

**Problem**: `Guid.Parse(pos.Id.ToString())` — cTrader `pos.Id` is an int (e.g., `12345678`), not a UUID. `Guid.Parse("12345678")` will throw `FormatException`. The correlation of close events with the engine's `ClientOrderId` is completely broken.

**Fix**: Use the position's `Label` to find the pending client order ID from `_pendingClientOrderIds`, the same way `OnPositionOpened` does. If no matching label, emit a new Guid (position tracker will handle unmatched closes gracefully):

```csharp
private void OnPositionClosed(PositionClosedEventArgs args)
{
    if (!_running) return;
    var pos = args.Position;
    var clientOrderId = _pendingClientOrderIds.Count > 0 ? _pendingClientOrderIds.Dequeue() : Guid.NewGuid();
    _executionPublisher?.Publish(clientOrderId, "Filled", pos.EntryPrice,
        VolumeToLots(pos.VolumeInUnits, Symbols.GetSymbol(pos.SymbolName)), null, Server.TimeInUtc);
    _accountPublisher?.Publish(Account.Balance, Account.Equity,
        Account.Equity - Account.Balance, Server.TimeInUtc);
}
```

### 1f. EngineMode type-check leak (EngineWorker)

**File**: `src/TradingEngine.Host/EngineWorker.cs` — line 73 and line 95

Line 73: `_engineMode = _broker is SimulatedBrokerAdapter ? EngineMode.Backtest : EngineMode.Live;`  
Line 95: `if (_broker is NamedPipeBrokerAdapter pipeAdapter) pipeAdapter.OnClientConnected = ResetState;`

The NamedPipeBrokerAdapter callback registration should be moved to the adapter's `ConnectAsync` — see Phase 3. For now, leave line 95 as-is and just add `FileBrokerAdapter` handling when that exists. **Defer full EngineMode cleanup to a future iteration** — too broad for this scope. Note it as tech debt only.

---

## Phase 2 — Pipe Diagnostics Layer

Add observability BEFORE attempting any fix. The agent needs evidence, not guesses.

### 2a. Structured diagnostic Print in cBot

**File**: `src/TradingEngine.Adapters.CTrader/PipeClient.cs`

Wrap `Connect()` to print a structured diagnostic line:

```csharp
public bool Connect(int timeoutMs = 5000)
{
    try
    {
        _pipe?.Dispose();
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _pipe.Connect(timeoutMs);
        _running = true;
        return true;
    }
    catch (Exception ex)
    {
        // Note: Print is not available here (not in a Robot). Use Console.Error for CLI capture.
        // This is surfaced by the caller (TradingEngineCBot.OnStart) via Print().
        _lastConnectError = $"{ex.GetType().Name}: {ex.Message} | Win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
        return false;
    }
}

public string? LastConnectError { get; private set; }
private string? _lastConnectError;
```

Then in `TradingEngineCBot.OnStart()`:

```csharp
if (_pipe.Connect(5000))
{
    Print($"PIPE_DIAG|CONNECTED|pipe={PipeName}|pid={System.Diagnostics.Process.GetCurrentProcess().Id}");
    StartReadLoop();
    SendInitialState();
}
else
{
    Print($"PIPE_DIAG|FAILED|pipe={PipeName}|error={_pipe.LastConnectError ?? "timeout"}|pid={System.Diagnostics.Process.GetCurrentProcess().Id}");
    _pipe.RetryConnect();
}
```

Add similar `PIPE_DIAG|` lines for `OnPipeDisconnected`, `OnReconnected`.

### 2b. Structured logging in NamedPipeBrokerAdapter

**File**: `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs` — `ConnectAsync`

After `_pipeServer = new NamedPipeServerStream(...)`:
```csharp
_logger?.LogInformation("PIPE_SERVER|CREATED|pipe={PipeName}|path=\\\\.\\pipe\\{PipeName}|pid={Pid}",
    _pipeName, _pipeName, Environment.ProcessId);
```

After `WaitForConnectionAsync`:
```csharp
_logger?.LogInformation("PIPE_SERVER|CLIENT_CONNECTED|pipe={PipeName}", _pipeName);
```

### 2c. BacktestRunner surface stdout to log

**File**: `src/TradingEngine.CTraderRunner/BacktestRunner.cs`

After `await cliProcess.WaitForExitAsync(ct)`:
```csharp
// Log all PIPE_DIAG lines from stdout — these come from cBot Print() calls
foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
{
    if (line.Contains("PIPE_DIAG|"))
        _logger.LogInformation("cBot: {Line}", line.Trim());
}
```

---

## Phase 3 — Named Pipe Connection Fix

### 3a. Add permissive pipe security descriptor (engine side)

**Why**: ctrader-cli likely runs the cBot process in a restricted security context (different integrity level or restricted Job Object). The default `NamedPipeServerStream` security descriptor only allows same-user access. A world-accessible ACL is needed.

**Step 1**: Add NuGet package to `src/TradingEngine.Infrastructure/TradingEngine.Infrastructure.csproj`:
```xml
<PackageReference Include="System.IO.Pipes.AccessControl" Version="5.0.0" />
```

**Step 2**: Modify `NamedPipeBrokerAdapter.ConnectAsync` and `TryReconnectAsync` to use `NamedPipeServerStreamAcl.Create()`:

```csharp
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

private static NamedPipeServerStream CreatePipeServer(string pipeName)
{
    var security = new PipeSecurity();
    security.AddAccessRule(new PipeAccessRule(
        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        PipeAccessRights.FullControl,
        System.Security.AccessControl.AccessControlType.Allow));

    return NamedPipeServerStreamAcl.Create(
        pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous,
        inBufferSize: 65536,
        outBufferSize: 65536,
        security);
}
```

Replace all `new NamedPipeServerStream(...)` calls in `ConnectAsync` and `TryReconnectAsync` with `CreatePipeServer(_pipeName)`.

Log the security descriptor creation: `_logger?.LogInformation("PIPE_SERVER|SECURITY|WorldFullControl=true|pipe={PipeName}", _pipeName)`.

**Note**: On Linux (if ever tested), `PipeSecurity` is a no-op. The code compiles and runs but has no effect.

### 3b. Proper readiness probe in BacktestRunner

**File**: `src/TradingEngine.CTraderRunner/BacktestRunner.cs` — `WaitForEngineReadyAsync`

Replace the blind `Task.Delay(5000)` with a real probe that tries to connect to the pipe from the BacktestRunner process. If THIS succeeds but the cBot inside ctrader-cli still can't, we know it's a ctrader-cli isolation issue:

```csharp
private static async Task WaitForEngineReadyAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
{
    var deadline = DateTime.UtcNow + timeout;
    var attempt = 0;
    while (DateTime.UtcNow < deadline)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            probe.Connect(300); // 300ms per attempt
            return; // success — server is listening
        }
        catch { }
        attempt++;
        await Task.Delay(200, ct);
    }
    throw new TimeoutException($"Engine pipe '{pipeName}' not ready after {timeout.TotalSeconds:F0}s ({attempt} probes)");
}
```

### 3c. PipeClient retry with detailed diagnostics

**File**: `src/TradingEngine.Adapters.CTrader/PipeClient.cs` — `RetryConnect`

Print a diagnostic on EACH retry attempt and final failure:

```csharp
public void RetryConnect()
{
    Print($"PIPE_DIAG|RETRY_START|pipe={_pipeName}|maxRetries={MaxRetries}");
    for (var i = 0; i < MaxRetries; i++)
    {
        Thread.Sleep(RetryDelays[i]);
        if (Connect(5000))
        {
            Print($"PIPE_DIAG|RETRY_SUCCESS|pipe={_pipeName}|attempt={i+1}");
            OnReconnected?.Invoke();
            return;
        }
        Print($"PIPE_DIAG|RETRY_FAILED|pipe={_pipeName}|attempt={i+1}|error={LastConnectError}");
    }
    Print($"PIPE_DIAG|GIVE_UP|pipe={_pipeName}|allAttemptsFailed");
}
```

Note: `Print()` in `PipeClient` can't call `Robot.Print()` directly (not a Robot). Options:
- Accept an `Action<string>? log` in constructor (nullable, optional)
- Or expose `LastConnectError` and log from `TradingEngineCBot`

The cleaner approach: add `Action<string>? DiagLog = null;` field to `PipeClient`, set it from `TradingEngineCBot.OnStart()` to `msg => Print(msg)`.

---

## Phase 4 — Protocol Fix: Double Serialization Bug

**This is a critical correctness bug that would prevent any message from being parsed even if the pipe connects.**

### 4a. Understand the bug

`PipeMessage.Payload` is `string`. Publishers serialize the payload as a JSON string and assign it to `Payload`. When `PipeMessage` is serialized by Newtonsoft, the result is:
```json
{"Type":"Bar","Payload":"{\"Symbol\":\"EURUSD\",...}"}
```
`Payload` is a **JSON string** in the outer document.

On the engine side, `ProcessMessage` calls `GetCaseInsensitiveProperty(doc.RootElement, "Payload")` which returns a `JsonElement` with `ValueKind = String`. Then `CIProp(barPayload, "Symbol")` tries `barPayload.TryGetProperty("Symbol", ...)` on a String element → returns false → throws `KeyNotFoundException` → caught and logged as a warning. **No message is ever parsed successfully.**

### 4b. Fix: Two-pronged approach

**Fix A — cBot side** (preferred, cleaner): Change `PipeMessage.Payload` from `string` to `object`. Newtonsoft will serialize it as an embedded JSON object:

```csharp
// PipeMessage.cs — cBot project
public class PipeMessage
{
    public string Type { get; set; } = "";
    public object Payload { get; set; } = new object(); // Was: string
    
    // ToByteArray, FromByteArray, FromJson remain the same
    // Newtonsoft now embeds Payload as JSON object, not escaped string
}
```

All publishers pass anonymous objects to `Payload` directly — `MessageSerializer.Serialize(new { ... })` should be removed, the anonymous object passed directly:

```csharp
// BarPublisher.cs — AFTER fix
public void Publish(string symbol, string timeframe, DateTime openTime,
    double open, double high, double low, double close, double volume)
{
    _pipe.Send(new PipeMessage
    {
        Type = "Bar",
        Payload = new
        {
            Symbol = symbol,
            Timeframe = timeframe,
            OpenTimeUtc = openTime.ToString("o"),
            Open = open, High = high, Low = low, Close = close, Volume = volume
        }
    });
}
```

Apply same pattern to `TickPublisher`, `AccountUpdatePublisher`, `ExecutionEventPublisher`.

**Fix B — Engine side** (fallback/backward compat): In `NamedPipeBrokerAdapter.ProcessMessage`, handle when Payload is a JSON string by re-parsing it:

```csharp
private static JsonElement ResolvePayload(JsonDocument doc)
{
    var payloadElem = GetCaseInsensitiveProperty(doc.RootElement, "Payload");
    if (payloadElem.ValueKind == JsonValueKind.String)
    {
        // Legacy: payload was double-serialized as escaped string
        using var inner = JsonDocument.Parse(payloadElem.GetString()!);
        return inner.RootElement.Clone(); // Clone because inner will be disposed
    }
    return payloadElem;
}
```

Then in `ProcessMessage`:
```csharp
var payload = ResolvePayload(doc);
// Use `payload` instead of per-type `GetCaseInsensitiveProperty(doc.RootElement, "Payload")`
```

Implement BOTH A and B. Fix A eliminates the double-serialization going forward. Fix B makes the engine resilient to any old format.

### 4c. MessageSerializer cleanup

**File**: `src/TradingEngine.Adapters.CTrader/MessageSerializer.cs`

`Serialize` is no longer needed by publishers after Fix A. Keep `Deserialize<T>` (used by `OrderCommandHandler`). Add a note that `Serialize` is only for command payloads now, or remove it.

---

## Phase 5 — New Test: PipeConnectivityTest (Fast Agent Loop)

This test validates the pipe transport WITHOUT requiring ctrader-cli. It is the primary agent iteration tool: takes <15 seconds, no cTrader credentials needed, definitively tells you if the engine pipe is working.

**File**: `tests/TradingEngine.Tests.Simulation/Pipeline/PipeConnectivityTest.cs` (new file)

```csharp
[Trait("Category", "Pipe")]
public sealed class PipeConnectivityTest
{
    [Fact(Timeout = 30_000)]
    public async Task EngineAcceptsPipeConnection_FromTestProcess()
    {
        // Start engine in Live mode
        var runId = Guid.NewGuid().ToString("N")[..8];
        var pipeName = $"shamshir-pipe-{runId}";
        var workDir = Path.Combine(Path.GetTempPath(), "shamshir-pipe", runId);
        Directory.CreateDirectory(workDir);
        var logPath = Path.Combine(workDir, "engine.log");
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var engineProj = Path.Combine(solutionRoot, "src", "TradingEngine.Host", "TradingEngine.Host.csproj");

        using var engineProcess = Process.Start(new ProcessStartInfo("dotnet",
            $"run --project \"{engineProj}\" --no-build")
        {
            UseShellExecute = false, CreateNoWindow = true,
            Environment =
            {
                ["Engine__Mode"] = "Live",
                ["Engine__Broker__PipeName"] = pipeName,
                ["SERILOG_FILE_PATH"] = logPath,
            },
        })!;

        try
        {
            // Wait for pipe server using the same readiness probe as BacktestRunner
            var ready = false;
            for (var i = 0; i < 30 && !ready; i++)
            {
                await Task.Delay(500);
                try
                {
                    using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    probe.Connect(200);
                    ready = true;
                }
                catch { }
            }

            ready.Should().BeTrue("engine pipe server should be ready within 15s");

            // Send a single Tick message over the pipe
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(2000);
            client.IsConnected.Should().BeTrue();

            var msg = new PipeMessage
            {
                Type = "Tick",
                Payload = new { Symbol = "EURUSD", Bid = 1.08500, Ask = 1.08502, TimestampUtc = DateTime.UtcNow.ToString("o") }
            };
            var bytes = msg.ToByteArray(); // Uses the same framing as PipeClient
            await client.WriteAsync(bytes);
            await client.FlushAsync();
            await Task.Delay(1000); // let engine process it
        }
        finally
        {
            if (!engineProcess.HasExited) engineProcess.Kill(entireProcessTree: true);
            await engineProcess.WaitForExitAsync(CancellationToken.None);
        }

        // Read engine log
        await Task.Delay(500);
        var lines = File.Exists(logPath) ? await File.ReadAllLinesAsync(logPath) : [];
        var pipeConnected = lines.Any(l => l.Contains("PIPE_SERVER|CLIENT_CONNECTED") || l.Contains("Pipe connected"));
        var tickLogged = lines.Any(l => l.Contains("TICK|EURUSD"));

        Console.WriteLine($"[PipeTest] Pipe connected: {pipeConnected}");
        Console.WriteLine($"[PipeTest] Tick logged: {tickLogged}");
        Console.WriteLine($"[PipeTest] Log lines: {lines.Length}");
        foreach (var line in lines.Where(l => l.Contains("PIPE") || l.Contains("TICK")))
            Console.WriteLine($"  {line}");

        pipeConnected.Should().BeTrue("engine should log a client connection event");
        tickLogged.Should().BeTrue("engine should log the TICK| line after processing");
    }
}
```

**Framing**: Do NOT add a ProjectReference to `TradingEngine.Adapters.CTrader` (it targets net6.0 and pulls in cTrader.Automate). Instead, add a static test helper `PipeFraming` in the test project that replicates the 4-byte length prefix framing:

```csharp
// tests/TradingEngine.Tests.Simulation/Pipeline/PipeFraming.cs
internal static class PipeFraming
{
    public static byte[] Encode(string type, object payload)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { Type = type, Payload = payload });
        var utf8 = System.Text.Encoding.UTF8.GetBytes(json);
        var result = new byte[4 + utf8.Length];
        Buffer.BlockCopy(BitConverter.GetBytes(utf8.Length), 0, result, 0, 4);
        Buffer.BlockCopy(utf8, 0, result, 4, utf8.Length);
        return result;
    }
}
```

Add `Newtonsoft.Json` NuGet to `TradingEngine.Tests.Simulation.csproj` for this helper.

---

## Phase 6 — FullBacktestPipelineTest Improvements

**File**: `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs`

### 6a. Shorter test period for faster iteration

Add a fast variant that runs 3 days of data (gets results in ~10s instead of 60s):

```csharp
[Trait("Category", "Pipeline")]
[Fact(Timeout = 60_000)]
public async Task EurUsdH1_ThreeDays_VerifiesPipeAndDataFlow()
{
    var cfg = new BacktestConfig
    {
        Symbol = "EURUSD", Period = "h1",
        Start = new DateTime(2024, 1, 15),
        End = new DateTime(2024, 1, 18), // 3 days only
        Balance = 100_000,
    };
    // same test body, assertions: pipe connected + ticks > 0 + bars > 0
}
```

Mark the original 3-month test with `[Trait("Category", "Slow")]` — excluded from default runs:
```
dotnet test --filter "Category!=Slow"   # standard run
dotnet test --filter "ThreeMonth"        # explicit full run
```

### 6b. Ordered assertions with early bail-out

Replace the single `signalYes.Should().NotBeEmpty(...)` with an ordered chain:

```csharp
// Must pass IN ORDER — each is a prerequisite for the next
if (pipeConnected.Count == 0)
{
    Assert.Fail($"Pipe never connected. Check PIPE_DIAG lines:\n{string.Join('\n', allLines.Where(l => l.Contains("PIPE_DIAG")))}");
    return;
}
if (tickLines.Count == 0)
{
    Assert.Fail($"No ticks received. Pipe connected but no data flowed. BAR lines={barLines.Count}");
    return;
}
barLines.Count.Should().BeGreaterThan(0, "bars must arrive before strategies can evaluate");
signalYes.Should().NotBeEmpty("at least one signal over the test period");
```

### 6c. Fail fast if pipe never connects

Add a 30-second partial check:
```csharp
// Before launching backtest — wait for pipe OR fail fast
var pipeReady = false;
for (var i = 0; i < 30; i++)
{
    await Task.Delay(1000);
    if (File.Exists(logPath))
    {
        var partial = await File.ReadAllTextAsync(logPath);
        if (partial.Contains("Pipe connected") || partial.Contains("CLIENT_CONNECTED"))
        { pipeReady = true; break; }
    }
}
if (!pipeReady)
{
    // ... kill engine, read log, fail with diagnostic
}
```

---

## Phase 7 — Clean up cBot

**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs`

### 7a. Remove dead code / references

- Remove comments referencing `FilePublisher` (never implemented)
- Remove `_readThread` field — replace with named `Task` (the background thread is C# 5 style; we have LangVersion 10 now)
- Keep all pipe code — it's needed for Live trading too

### 7b. Add lifecycle Print diagnostics

Add `Print($"CBOT|START|symbol={SymbolName}|tf={TimeFrame.ShortName}")` in `OnStart`.
Add `Print($"CBOT|STOP")` in `OnStop`.
Add `Print($"CBOT|BAR|{args.Bars.Last(1).OpenTime:o}|close={args.Bars.Last(1).Close:F5}")` in `OnBarClosed` (once per bar, for agent verification).
Add `Print($"CBOT|TICK|{Symbol.Bid:F5}|{Symbol.Ask:F5}")` in `OnBarsTick` (throttled — only every 100th tick, or only first/last).

### 7c. Remove TcpBrokerAdapter and PipeClient TCP fallback

- Delete `src/TradingEngine.Infrastructure/Adapters/TcpBrokerAdapter.cs` (was "kept for reference", never used)
- Revert `PipeClient.cs` to original named-pipe only (remove the TCP fallback that was added during Iteration 6 experiments)
- Update `DECISIONS.md` with D70: "TCP transport removed — blocked by ctrader-cli sandbox"

---

## Verification Steps (for agent)

### Step 1 — Pre-work complete
```
dotnet test tests\TradingEngine.Tests.Unit --no-build
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "Category!=Pipeline"
```
All existing non-pipeline tests must pass.

### Step 2 — PipeConnectivityTest (PRIMARY agent loop)
```
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "Category=Pipe"
```
Expected: 1 passed in <15s. Output shows `Pipe connected: True`, `Tick logged: True`.

If this FAILS: check engine log for PIPE_SERVER lines. If no `PIPE_SERVER|CREATED` → engine didn't start. If `PIPE_SERVER|CREATED` but no `CLIENT_CONNECTED` → security fix needed.

### Step 3 — Full pipeline (requires cTrader credentials + compiled src.algo)
```
set CTrader__CtId=seankiaa
set CTrader__PwdFile=C:\Users\shahi\Documents\ctrader.pwd
set CTrader__Account=5834367
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeDays"
```
Expected: 1 passed in <60s. Output shows PIPE_DIAG|CONNECTED in cBot lines.

If pipe connects but no ticks arrive: check protocol (Phase 4 fix). Look for "Pipe message parse error" warnings in engine log.

### Step 4 — Full 3-month test
```
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeMonth"
```
Expected: 1 passed in <120s. SIGNAL|YES count > 0.

---

## Critical Files

| File | Change |
|------|--------|
| `src/TradingEngine.Host/DataFeedService.cs` | Remove `OnTickReceived` call |
| `src/TradingEngine.Host/Program.cs` | Always use `BrokerClock` |
| `src/TradingEngine.Infrastructure/Adapters/SimulatedBrokerAdapter.cs` | Bound channels |
| `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs` | Bound channels + world ACL |
| `src/TradingEngine.Infrastructure/TradingEngine.Infrastructure.csproj` | Add `System.IO.Pipes.AccessControl` |
| `src/TradingEngine.CTraderRunner/BacktestRunner.cs` | Real readiness probe + stdout PIPE_DIAG logging |
| `src/TradingEngine.Adapters.CTrader/PipeMessage.cs` | `Payload` → `object` |
| `src/TradingEngine.Adapters.CTrader/PipeClient.cs` | DiagLog callback + error capture |
| `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` | Lifecycle Prints + VolumeToLots fix |
| `src/TradingEngine.Adapters.CTrader/TickPublisher.cs` | Remove MessageSerializer.Serialize wrapper |
| `src/TradingEngine.Adapters.CTrader/BarPublisher.cs` | Remove MessageSerializer.Serialize wrapper |
| `src/TradingEngine.Adapters.CTrader/AccountUpdatePublisher.cs` | Remove MessageSerializer.Serialize wrapper |
| `src/TradingEngine.Adapters.CTrader/ExecutionEventPublisher.cs` | Remove MessageSerializer.Serialize wrapper |
| `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs` | `ResolvePayload` fix + diagnostic logging |
| `tests/TradingEngine.Tests.Simulation/Pipeline/PipeConnectivityTest.cs` | NEW — fast pipe test |
| `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs` | Short period + ordered assertions |

## Notes for Implementing Agent

1. **Do not** add `FileBrokerAdapter`. File-based IPC is off the table.
2. **Do not** add `TcpBrokerAdapter` — delete the existing one.
3. `PipeConnectivityTest` must pass before touching `FullBacktestPipelineTest`.
4. The `System.IO.Pipes.AccessControl` NuGet may already be included on .NET 10 Windows — try without the package first; if `NamedPipeServerStreamAcl` is not found, add the package.
5. `LangVersion` in the cBot project is already 10 (not 6 as the memory docs say) — modern C# features are allowed.
6. The `EngineTestHarness` calls `sim.OnTickReceived` directly (it bypasses the channel write entirely) — removing the call from `DataFeedService` does NOT affect harness tests. The harness is correct as-is.
7. Update `DECISIONS.md` with D70 (TCP removed), D71 (double-serialization protocol fix), D72 (world ACL pipe security).
8. Write `docs/ITERATION-7-HANDOVER.md` at completion.
