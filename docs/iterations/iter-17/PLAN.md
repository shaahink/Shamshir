# Iteration 17 — Deterministic Event-Driven Pipeline: Diagnosis & Redesign

**Branch**: `iter/17-deterministic-pipeline`
**Base**: `iter/16-ctrader-inproc` (commit `36aa433` or later)
**Blocks**: All future strategy/backtest work — nothing downstream is trustworthy until this lands
**Written**: 2026-06-11, from a read-only diagnostic session (no code was run or changed)

---

## Read first

- This document, top to bottom — the Diagnosis section explains *why* every phase exists
- `docs/iterations/iter-16/CLAUDE-HANDOVER.md` — architecture, logging channels, message flow, test inventory
- `docs/OPEN-ISSUES.md` — canonical issues log (several items here close items there; update it as you go)
- `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` — the transport under repair
- `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` — the cBot side of the protocol
- `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` — `RunEngineNetMqAsync` / `RunEngineReplayAsync` (the DI blocks Phase B collapses)
- `src/TradingEngine.Host/EngineWorker.cs` — engine loops (Live vs Backtest mode)
- `tests/TradingEngine.Tests.Simulation/Pipeline/CtraderPipelineDiagnosticTest.cs` — the test that passes while the UI fails

**Rules for the implementing agent:**
- Phases are ordered by dependency. Do not start C before A and B are verified.
- Every phase ends with its Verification block passing. Paste actual command output in HANDOVER.md.
- Never delete a resolved OPEN-ISSUES item — mark it `✅ Fixed (Iteration 17)`.
- All money/price arithmetic in `decimal`. No new `Thread.Sleep` synchronization anywhere.

---

# Part 1 — Diagnosis

## 1.1 The immediate bug: orders never reach the cBot

**Symptom** (web UI): `NETMQ_CONNECTED` ✅, `NETMQ_SENT` ✅ (dozens), but `CBOT DEALER_RECV`,
`TICK_DRAIN`, `CMD_RECV`, `EXEC` — never. Bars flow cBot→engine fine. Trades = 0.

**Root cause: NetMQ thread-affinity violation.**

`NetMQBrokerAdapter.ConnectAsync` (line ~57) registers `_router` in a `NetMQPoller` and calls
`RunAsync()` — the poller thread now owns that socket. But `SendCommandAsync` (line ~227) calls
`_router.SendMoreFrame(_cBotIdentity).SendFrame(json)` directly from the caller's thread — which is
`EngineWorker`'s bar-loop thread (a thread-pool thread). **NetMQ sockets are not thread-safe.** A
socket added to a poller must only be touched from the poller thread. Cross-thread sends on a
polled socket are undefined behavior: silently lost messages or socket-state corruption.

This matches the symptom signature exactly:
- Everything that happens *on* the poller thread works: SUB receives (bars, diag), ROUTER receive of `hello`.
- Only the foreign-thread sends vanish — logged as `NETMQ_SENT`, never delivered.

**Why the diagnostic test passes but the UI fails (the "identical code" mystery):**

The inner hosts are *not* identical. In the web path the poller thread is heavily loaded; in the
test it is nearly idle. Undefined behavior resolves differently under different schedules:

| Difference | Web (`RunEngineNetMqAsync`) | `CtraderPipelineDiagnosticTest` |
|---|---|---|
| Inner-host min log level | `Information` → `OnSubReceive` logs `NETMQ\|SUB_RAW` **per frame** on the poller thread | `Warning` → no per-frame logging |
| `adapter.OnStatusChange` | Wired → poller thread does JSON serialize + SSE channel writes per diag/status | Not wired |
| Database | Shared `data/trading.db` (second DI container over the web host's file) | Fresh temp DB + `EnsureCreated` |
| `CrossRateStore` | One instance for both registrations | **Two different instances** — singleton ≠ the `new CrossRateStore().Convert` inside the Func; rate updates invisible to lot sizing |
| Run timeout | 30 min | 10 min |

So a fix "verified by the test" says nothing about the UI. This is Phase B's reason to exist.

**Confirming experiment (do this first, Phase A0):** send a `ping` from inside `OnRouterReceive`
immediately after identity capture. That send happens *on the poller thread*. If ping arrives at
the cBot (`DEALER_RECV` appears) while orders still don't, thread-affinity is confirmed beyond doubt.

## 1.2 The fundamental flaw: two unsynchronized clocks

Even with the socket fixed, results will never be reproducible. There are two time domains —
cTrader's simulated backtest clock (running at maximum speed) and the engine's wall-clock async
processing — connected by fire-and-forget PUB/SUB with **no flow control**:

1. The cBot publishes bar N and immediately advances simulated time. The engine's order for bar N
   arrives at simulated bar N+k where k is a race outcome. Entry prices, SL/TP behavior, and trade
   count are nondeterministic *by construction*. This — not any single bug — is why test runs and
   UI runs have never agreed.
2. Backpressure cannot propagate over PUB/SUB, and the adapter channels use
   `BoundedChannelFullMode.DropOldest` — under load, **bars are silently discarded mid-backtest**.
3. Orders are only drained inside `OnTick`. After the last tick of the data range, queued commands
   (including `shutdown`) sit in `_mainActions` forever.
4. Startup is held together by sleeps: `Thread.Sleep(600)` + ten 500 ms heartbeats to outwait the
   PUB/SUB slow-joiner instead of a handshake.
5. Shutdown loses data: cBot `OnStop` disposes sockets immediately after `StopAsync` and calls
   `NetMQConfig.Cleanup(false)` (no linger) — final `exec` messages for end-of-run closes can be
   dropped. Another source of run-to-run variance.

**The fix is a lock-step protocol** (Phase C). cTrader runs the cBot on its main thread, so if the
bar handler blocks waiting for a reply, *the simulation pauses*. That is the lever for determinism:
the cBot must not advance past bar N until the engine confirms it has fully processed bar N and
returned any orders, and those orders must execute at bar N's simulated time, not "whenever".

## 1.3 Engine-side threading/async defects

- **Two competing consumers of `ExecutionStream`** (`EngineWorker.cs`): `ProcessExecutionEventsAsync`
  forwards events into `_executionEventChannel` (drained only when ticks arrive), while
  `DrainExecutionStreamAsync` — called from the bar loop — reads the *same* `ChannelReader`
  directly. Fills race between two paths and can sit unprocessed until the next tick.
- **`PositionTracker` is not thread-safe but is called concurrently** in Live mode (tick loop and
  bar loop both call `OnExecutionAsync`). Plain `Dictionary`s → corruption risk.
- **`EngineMode` is inferred by type-sniffing the adapter** (`_broker is SimulatedBrokerAdapter ||
  BacktestReplayAdapter ? Backtest : Live`). cTrader backtests therefore run in **Live** mode:
  four concurrent loops instead of one deterministic loop, no bar-based SL/TP exit evaluation, and
  equity snapshots persisted as if live. Mode must be explicit configuration.
- **Implicit open/close protocol in `PositionTracker`**: "a second `Filled` event for a known
  orderId means close". A duplicated entry-fill would be booked as a position close. The protocol
  needs explicit event types (Phase C wire format fixes this at the source).
- Fire-and-forget `_ = _eventBus.PublishAsync(...)` for `BarEvaluated`/`EquityUpdated` — events can
  be lost at shutdown (OPEN-ISSUES DESIGN-01 family).

## 1.4 Financial-correctness defects (silent wrong numbers)

- **`SymbolInfo` hardcoded to EUR/USD for every symbol** in both web DI blocks and the test —
  GBPUSD backtests run with EURUSD pip value/currencies → wrong lot sizing. (Domain rule violation.)
- **cBot in M1 data mode places orders with no stop loss**: `Symbol.Bid/Ask` are 0, the
  `rawSl < 500` clamp nulls `slPips`/`tpPips` (the code comment admits it). Positions are unprotected.
- **cBot `OnPositionClosed` fabricates the exit price** from `GrossProfit / VolumeInUnits` — wrong
  for non-USD-quote symbols; the engine then recomputes PnL from this fabricated price.
- **`NetMQBrokerAdapter.ClosePositionAsync` fallback synthesizes a fill at `Price(1m)`** — a
  literal price of 1.0 poisoning PnL stats.
- `TradeResult` zeroes commissions, swap, MAE/MFE, R-multiple — stats structurally incomplete.

## 1.5 Parity & hygiene

- The inner-host DI block is copy-pasted in **three places** (`RunEngineNetMqAsync`,
  `RunEngineReplayAsync`, `CtraderPipelineDiagnosticTest`) and has already drifted (§1.1 table).
- Three overlapping logging channels (`PushProgress`, `PushProgressEvent`, `PushProgressAndLog` +
  `EnqueueLog`) with hand-picked event-type allowlists; cBot `Print` output is lost unless the CLI
  result is parsed.
- Exit-code-1-treated-as-success via stdout string matching (`"Message expected"`) — a real cTrader
  quirk, but it currently blesses *every* exit-1.
- `_publishedBars` dedup HashSet grows unbounded in the cBot; `_processedExecutionIds` likewise
  engine-side (OPEN-ISSUES DESIGN-04).

---

# Part 2 — Target architecture (after this iteration)

```
┌────────────────────────────────────────────────────────────┐
│ Web UI / Tests / future CLI — all call the SAME factory    │
│            EngineHostFactory.Create(options)               │
└────────────────────────┬───────────────────────────────────┘
                         │ one composition root
              ┌──────────▼──────────┐
              │ Engine (inner host)  │  EngineMode = explicit option
              │ Backtest mode:       │
              │  SINGLE lock-step    │
              │  loop, no races      │
              └──────────┬──────────┘
                         │ IBrokerAdapter
        ┌────────────────┼──────────────────────┐
   BacktestReplay   CTraderLockStep        Simulated/Live
   (DB bars,        (NetMQ DEALER/ROUTER,  (unchanged)
    in-proc)         all sends on poller
                     thread via NetMQQueue)
                         │
                  ctrader-cli.exe (CliWrap)
                   └─ TradingEngineCBot
                      lock-step: blocks in bar
                      handler until bar_done
```

**Threading model (the contract every phase enforces):**
- One thread per NetMQ socket. Sockets registered in a poller are touched **only** by the poller
  thread. All cross-thread sends go through a `NetMQQueue<T>` registered in the same poller.
- In Backtest mode the engine is **single-threaded**: one loop consumes one ordered inbound stream
  (bars, execs, account updates in arrival order). No `Task.WhenAll` of competing consumers, no
  fire-and-forget that mutates state the loop later reads.
- In Live mode, `PositionTracker` access is serialized (single exec-processing loop owns it).
- Channels carrying correctness-critical data (bars, execs) never use `DropOldest`.

**Protocol (lock-step, all correctness traffic on DEALER↔ROUTER, JSON, protocol-versioned):**

```
cBot → engine   {type:"hello", v:1, symbols:[..], periods:[..], barsLoaded:N}
engine → cBot   {type:"hello_ack", v:1}            ← replaces sleeps/heartbeats

cBot → engine   {type:"bar", seq:N, symbol, period, openTime, o,h,l,c, volume,
                 simTime, account:{balance,equity}}
                cBot BLOCKS here (drains DEALER inbound) until:
engine → cBot   {type:"bar_done", seq:N, commands:[{submit_order,...},{close_position,...}]}
cBot executes commands synchronously at bar N's simulated time, then:
cBot → engine   {type:"bar_result", seq:N, execs:[{clientOrderId,state,fillPrice,
                 filledLots,reason,simTime}], account:{balance,equity}}
                (always sent, even with empty execs — it is the barrier)

cBot → engine   {type:"exec", ...}     ← async closes (SL/TP hit inside cTrader between
                                          bars); FIFO socket order keeps determinism
cBot → engine   {type:"stats", barsSent:N, cmdsReceived:N, ordersExecuted:N, execsSent:N}
                                       ← sent from OnStop, enables reconciliation
engine → cBot   {type:"shutdown"}
```

PUB/SUB survives only for telemetry (`diag`, optional ticks) — never for correctness data.
Determinism follows from: single ordered socket stream + simulation paused per bar + orders
executed at their own bar's simulated time. Same inputs → byte-identical trade list, in tests
and from the UI.

---

# Part 3 — Phases

## Phase A — Transport correctness (small, unblocks everything)

### A0 — Confirm the diagnosis (15 min, do not skip)

In `NetMQBrokerAdapter.OnRouterReceive`, immediately after `_cBotIdentity = identity;`, add a
direct same-thread send:

```csharp
_router!.SendMoreFrame(identity).SendFrame("""{"type":"ping"}""");
```

Run the UI backtest (or `CtraderPipelineDiagnosticTest` with web-equivalent logging at
`Information`). Expected: `CBOT DEALER_RECV` appears for the ping (poller-thread send works) while
order sends still don't arrive. Record the result in HANDOVER.md — it is the evidence base for A1.
Remove the experiment after recording (A1 supersedes it with `hello_ack`).

### A1 — Marshal all ROUTER sends onto the poller thread

**File**: `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs`

```csharp
private NetMQQueue<(byte[] Identity, string Json)>? _sendQueue;

// ConnectAsync:
_sendQueue = new NetMQQueue<(byte[], string)>();
_sendQueue.ReceiveReady += (_, e) =>
{
    while (e.Queue.TryDequeue(out var item, TimeSpan.Zero))
        _router!.SendMoreFrame(item.Identity).SendFrame(item.Json);   // poller thread — safe
};
_poller = new NetMQPoller { _sub, _router, _sendQueue };

// SendCommandAsync: replace the direct _router.SendMoreFrame(...) with:
_sendQueue.Enqueue((_cBotIdentity!, json));
```

Also in this change:
- **Pending-command queue**: if a command is submitted before `_cBotIdentity` is set, enqueue it in
  a `ConcurrentQueue<object>` instead of dropping. In `OnRouterReceive`, after identity capture and
  `hello_ack`, flush it (`FlushPendingCommands()` — this was already decided in iter-16 review and
  never implemented). Keep the `NETMQ_DROPPED` status only for genuinely impossible sends (disposed).
- **Drain loops in receive handlers**: NetMQ may signal once for multiple queued messages.
  `OnRouterReceive`: `while (e.Socket.TryReceiveFrameBytes(out var identity)) { var json = e.Socket.ReceiveFrameString(); Handle(identity, json); }`.
  `OnSubReceive`: same pattern with `TryReceiveFrameString` for the topic.
- Dispose `_sendQueue` in `DisconnectAsync`.

### A2 — Replace sleeps/heartbeats with a handshake

**Files**: `TradingEngineCBot.cs`, `NetMQBrokerAdapter.cs`

- cBot `OnStart`: delete `Thread.Sleep(600)` and the 10×500 ms heartbeat loop. Connect DEALER,
  start poller, then send `hello` and wait (bounded, e.g. 100 ms × 50 retries, resending `hello`
  every 10 retries) until `hello_ack` arrives. Fail loudly (`Print` + `Stop()`) if no ack — a
  backtest without an engine is meaningless.
- Engine `OnRouterReceive`, on `hello`: capture identity, reply `hello_ack` (via `_sendQueue`),
  invoke `OnConnected`, flush pending commands.
- cBot `HandleCommand`: add `case "hello_ack"` (sets a `_connected` flag).

### A3 — Stop losing messages at shutdown

**File**: `TradingEngineCBot.cs` (`OnStop`)

- Drain `_mainActions` fully before teardown (commands may still be queued if no tick followed).
- Send the `stats` message (see protocol) before disposing.
- Set linger so queued outbound frames flush: `_dealer.Options.Linger = TimeSpan.FromSeconds(2);`
  (same for `_pub`), and prefer `NetMQConfig.Cleanup(block: true)` — measure that it doesn't hang
  the CLI; if it does, linger on the sockets + `Cleanup(false)` is acceptable.

Engine side (`DisconnectAsync`): stop the poller *before* disposing sockets, and complete the
channel writers after the poller has fully stopped (`_poller.Stop()` is synchronous; keep that).

### A4 — Channel modes

In `NetMQBrokerAdapter`: bars and execs channels must not drop (`FullMode.Wait`, or unbounded for
backtest). `DropOldest` is acceptable only for ticks/account telemetry. (Lock-step in Phase C makes
overflow impossible anyway; this is defense in depth.)

### Verification (Phase A)

```powershell
dotnet build --no-incremental                                          # 0 errors
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "PingPong"   # PASS
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "EurUsd_H1_3Days"  # trades > 0
# UI run (credentials configured, CTrader:UseForBacktest=true):
#   progress stream MUST now show CBOT DEALER_RECV, CMD_RECV, EXEC_SENT, EXEC
#   and Trades > 0 for EURUSD H1 Jan-2024
```

The UI check is the acceptance gate — Phase A is not done until the *web path* shows `DEALER_RECV`.

---

## Phase B — One composition root, explicit mode, DI fixes

### B1 — `EngineHostFactory`

**New file**: `src/TradingEngine.Host/EngineHostFactory.cs`

```csharp
public sealed record EngineHostOptions
{
    public required string RunId { get; init; }
    public required EngineMode Mode { get; init; }              // explicit — no type-sniffing
    public required Func<IServiceProvider, IBrokerAdapter> AdapterFactory { get; init; }
    public required string DbPath { get; init; }
    public required string SolutionRoot { get; init; }
    public IReadOnlyList<SymbolInfo> Symbols { get; init; } = [];
    public IProgress<BacktestProgressEvent>? Progress { get; init; }
    public LogLevel MinLogLevel { get; init; } = LogLevel.Information;
}

public static class EngineHostFactory
{
    public static IHost Create(EngineHostOptions options) { /* the ONE DI block */ }
}
```

Move the entire inner-host DI block (services, event-bus subscriptions, risk rule-set wiring)
here, exactly once. `BacktestOrchestrator.RunEngineNetMqAsync`, `RunEngineReplayAsync`, and
`CtraderPipelineDiagnosticTest` all become thin callers. Delete their inline DI blocks.

Rules:
- Parity by construction: log level, `OnStatusChange` wiring, progress sinks become *options*,
  not copy-paste divergence. The diagnostic test must pass `MinLogLevel = Information` and a wired
  status callback so it exercises the same poller-thread load as the web.
- Fix the `CrossRateStore` double-instance bug as a side effect (register one instance, derive the
  `Func<string,string,decimal>` from that same instance).
- `EngineWorker`: replace adapter type-sniffing with `options.Mode` passed through DI. cTrader
  backtests run as `EngineMode.Backtest`.

### B2 — Real per-symbol `SymbolInfo` (config-driven)

**New file**: `config/symbols.json`, loaded via the existing `ConfigLoader` (same pattern as risk
profiles / strategy configs / prop firms — do NOT invent a new loading mechanism).

Catalog with correct values for at least EURUSD, GBPUSD, USDJPY (pip size, lot size, base/quote
currencies, typical spread). `EngineHostOptions.Symbols` resolves from the catalog by the symbols
in the run config. Delete every hardcoded `new SymbolInfo(symbol, ..., "EUR", "USD", ...)`.

**Fail fast**: if a run requests a symbol not present in the catalog, the engine refuses to start
with a clear error. Never fall back to a default `SymbolInfo` — silent wrong pip values are
exactly the bug class this exists to kill.

### B3 — Single execution-event consumer; serialize `PositionTracker`

**File**: `src/TradingEngine.Host/EngineWorker.cs`

- Remove the dual-consumer race: `DrainExecutionStreamAsync` must read from
  `_executionEventChannel` (the internal channel) — never from `_broker.ExecutionStream` directly.
  `ProcessExecutionEventsAsync` remains the *only* reader of `_broker.ExecutionStream`.
- Live mode: all `PositionTracker.OnExecutionAsync` calls happen from one loop (move the drain out
  of `ProcessTicksAsync`; ticks only update account/force-close state). If a second call site must
  remain, guard `PositionTracker` with a `SemaphoreSlim(1,1)` — but prefer single-loop ownership.
- Backtest mode is restructured in Phase C; here just ensure the existing `RunBacktestLoopAsync`
  uses the internal channel consistently.

### Verification (Phase B)

```powershell
dotnet build --no-incremental                                  # 0 errors
dotnet test tests/TradingEngine.Tests.Unit --no-build          # all pass
dotnet test tests/TradingEngine.Tests.Integration --no-build   # all pass
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "EurUsd_H1_3Days"  # trades > 0
# grep checks:
#   no `is SimulatedBrokerAdapter` / `is BacktestReplayAdapter` mode-sniffing in EngineWorker
#   exactly ONE place constructs the inner host DI (EngineHostFactory)
#   GBPUSD path resolves GBP/USD SymbolInfo (unit test it)
```

---

## Phase C — Lock-step deterministic protocol

### C0 — PROTOCOL.md + FakeCBot skeleton (before touching the real cBot)

Write `docs/iterations/iter-17/PROTOCOL.md` (ADR style): the message schemas from Part 2,
sequencing rules, error cases (timeout waiting for `bar_done`, unknown seq, engine crash mid-run),
and version field. Implementation follows the doc.

Then build the **FakeCBot harness** (`tests/TradingEngine.Tests.Simulation/Harness/FakeCBot.cs`)
as the protocol's *first* implementation: speaks the full PROTOCOL.md wire format over real NetMQ
(DEALER + optional PUB), no ctrader-cli, no credentials. Replays bar fixtures (embedded CSV/JSON),
honors lock-step (sends `bar`, waits for `bar_done`, fills scripted orders at next-bar-open, sends
`bar_result`), sends `stats` on completion. Two reasons it comes first: it pressure-tests the
protocol spec before the real cBot is modified, and it makes the Phase C determinism gate runnable
in CI without credentials. Phase E extends it (golden files, fault injection) — it does not create it.

### C1 — cBot side

**File**: `TradingEngineCBot.cs`

- Move bar emission from PUB to DEALER (`bar` message with `seq`, `simTime`, account snapshot).
- In the bar handler (`OnBarClosed`), after sending `bar`, **block** pumping a local inbox
  (populated by `OnDealerReceive` on the poller thread → `BlockingCollection`/`ConcurrentQueue` +
  wait) until `bar_done` for that seq arrives (timeout: configurable, default 30 s → `Print` + `Stop()`).
- Execute `bar_done.commands` synchronously, collect exec results, send `bar_result`.
- `ExecuteMarketOrder` SL/TP: stop deriving pips from `Symbol.Bid/Ask` (zero in M1 mode). Place
  the market order without SL/TP, then `ModifyPosition(pos, slPrice, tpPrice)` with **absolute
  prices** from the command. Remove the `< 500` clamp.
- `OnPositionClosed`: report the position's actual closing price (cTrader provides the closing
  deal/last price — use `pos.CurrentPrice` at close or the history deal price; do NOT reconstruct
  from GrossProfit). Include `pos.GrossProfit`/`pos.NetProfit` in the exec payload so the engine
  can use broker-truth PnL instead of recomputing.
- `OnTick` no longer drains a command queue (commands execute inside the bar barrier). Tick PUB
  publishing may remain as telemetry.
- Keep `_publishedBars` dedup but make it a bounded ring or clear per-symbol on rollover.

### C2 — Engine side

**Files**: `NetMQBrokerAdapter.cs` (consider renaming `CTraderLockStepAdapter`), `EngineWorker.cs`,
`IBrokerAdapter` (extend, don't break other adapters)

- Adapter surfaces one ordered inbound stream: `ChannelReader<EngineInbound>` where
  `EngineInbound = Bar | Exec | AccountUpdate | Stats | Connected` in socket arrival order
  (single writer: the poller thread). Existing typed streams can remain as views, but the backtest
  loop consumes the unified stream.
- `SubmitOrderAsync`/`ClosePositionAsync` during bar processing **buffer** commands; a new
  `CompleteBarAsync(seq)` flushes them as `bar_done {seq, commands}` (through the send queue).
- `EngineWorker` Backtest mode = single loop:
  ```
  foreach inbound:
    Bar       → update bars/indicators → evaluate strategies → dispatch orders (buffered)
                → CompleteBarAsync(seq)
    Exec      → PositionTracker.OnExecutionAsync   (same thread — no locks needed)
    Account   → HandleAccountUpdate                (deterministic equity curve)
    Stats     → reconcile (Phase D)
  ```
  `bar_result.execs` arrive as `Exec` inbounds before the next `Bar` (FIFO socket guarantee), so
  position state is always current when the next bar is evaluated. Remove the bar-loop's
  `Task.Delay`/drain heuristics.
- Make state-mutating event publishes awaited within the loop (`BarEvaluated` persistence can stay
  channel-buffered, but nothing the loop later *reads* may be fire-and-forget).
- Delete the `Price(1m)` synthetic-fill fallback in `ClosePositionAsync` — with the barrier, "cBot
  not connected" during a run is a hard error, not something to paper over with a fake fill.
- `BacktestReplayAdapter` should implement the same unified-inbound surface so the replay path and
  the cTrader path run the *identical* engine loop. **Caution: the replay adapter is a production
  path, not experimental** — it is the `else` branch of `BacktestOrchestrator.RunAsync`, used by
  the UI whenever `CTrader:UseForBacktest=false` (the intended dev default per iter-16 Phase A2).
  Make the unified stream *additive*: existing typed streams keep working until the engine loop
  migrates, and `ReplayBacktest_FullPipeline` + a UI run in replay mode are an explicit C2
  regression gate.

### C3 — Explicit position lifecycle in the protocol

Replace the "second Filled = close" inference: `exec` messages carry
`kind: "entry_fill" | "close"` (+ `positionId`). `PositionTracker` branches on `kind`, not on
dictionary state. Duplicate events become detectable and loggable instead of being misbooked.

### Verification (Phase C)

```powershell
dotnet build --no-incremental
dotnet test tests/TradingEngine.Tests.Unit --no-build
# Determinism gate (the point of the phase) — credential-free via FakeCBot (C0):
#   run the FakeCBot fixture backtest TWICE → identical trade lists (count, entries, exits, PnL)
# Replay regression gate (C2):
#   ReplayBacktest_FullPipeline passes; UI run with UseForBacktest=false works as before
# Venue confirmation (secondary, needs credentials):
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "EurUsd_H1_3Days"  # trades > 0
#   run the SAME range from the UI → trade list identical to the test run (compare DB rows by RunId)
# Throughput gate: 30-day H1 EURUSD completes in comparable time to iter-16 (lock-step adds
#   round-trips but removes sleeps; record before/after wall time in HANDOVER.md)
```

---

## Phase D — Observability: journal + reconciliation + UI funnel

### D1 — Per-run pipeline journal

**New**: `PipelineEvents` table (EF migration — no raw SQL in Program.cs):
`(Id, RunId, Seq, Stage, CorrelationId, SimTimeUtc, WallTimeUtc, DetailJson)`.

Stages: `BAR_SENT, BAR_RECV, BAR_EVAL, SIGNAL, RISK_REJECTED, ORDER_SENT, CMD_RECV, EXEC_DONE,
EXEC_RECV, POSITION_OPEN, POSITION_CLOSE, TRADE_SAVED, CONNECTED, STATS, ERROR`.

`clientOrderId` is the correlation id from `ORDER_SENT` onward; bar `seq` before that. Writer is a
channel-backed background flusher (pattern: `BarEvaluationHandler`), with a final drain on dispose.

### D2 — One logging path

`BacktestOrchestrator`: replace the `PushProgress`/`PushProgressEvent`/`PushProgressAndLog`/
`EnqueueLog` quartet with a single `JournalWriter` that (a) persists to `PipelineEvents`,
(b) tees to the SSE stream, (c) feeds the final log view from the same data. The SSE payload keeps
`{eventType, message}` shape for UI compatibility. cBot `diag` PUB messages become journal rows
(stage from the diag prefix).

### D3 — End-of-run reconciliation

On `stats` inbound (or CLI exit), compare cBot counters vs engine counters and write a
reconciliation block to the journal + final log:

```
RECONCILE bars: sent=419 recv=419 ✓ | cmds: sent=12 recv=12 ✓ | execs: sent=24 recv=24 ✓
```

Any mismatch → `ERROR` stage row naming the broken hop. (This table would have found the iter-16
bug in one run: `cmds: sent=34 recv=0 ✗`.)

### D4 — UI funnel

Backtest detail/progress page: aggregate journal by stage into a funnel
(`419 bars → 76 evaluated → 30 signals → 12 passed risk → 12 sent → 12 filled → 11 closed → 11 saved`),
plus per-strategy and rejection-reason breakdowns. Covers OBS-02/03/05 from OPEN-ISSUES.

### Verification (Phase D)

UI run shows the funnel live; `PipelineEvents` rows queryable by RunId; reconciliation line appears
in the final log; intentionally break one hop locally (comment out a send) and confirm the
reconciliation names it.

---

## Phase E — Test suite that proves parity and determinism

### E1 — `FakeCBot` extensions

The harness itself was built in C0. This phase extends it:
- Richer bar fixtures (e.g. full EURUSD H1 Jan-2024 subset) as embedded resources.
- Configurable faults for negative tests: drop `bar_done` (engine timeout path), duplicate exec,
  out-of-order seq, disconnect mid-run.

### E2 — Deterministic E2E (credential-free, CI-able)

Via `EngineHostFactory` + `FakeCBot`: assert the **exact** trade list (golden file: entries, exits,
PnL to the cent) — not `trades > 0`. Plus a determinism test: two runs of the same fixture produce
identical journals (hash the trade rows). This is the regression gate AGENT-03 asked for.

### E3 — Protocol contract tests

One test class, parameterized over both transports (NetMQ adapter ↔ FakeCBot, and
`BacktestReplayAdapter`'s in-proc inbound stream): handshake, bar barrier, exec ordering, shutdown
stats, pending-command flush. Keeps you free to swap NetMQ later without re-deriving the rules.

### E4 — Venue smoke test

Exactly one credentialed `ctrader-cli` test remains (trait `Category=Venue`): 3-day EURUSD H1,
asserts trades > 0 AND reconciliation clean. Everything else runs without credentials. Update
`CtraderPipelineDiagnosticTest` to call `EngineHostFactory` (it may shrink to near-nothing).

### Verification (Phase E)

```powershell
dotnet test tests/TradingEngine.Tests.Unit --no-build
dotnet test tests/TradingEngine.Tests.Integration --no-build
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "Category!=Venue"   # no credentials needed
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "Category=Venue"    # with credentials
```

---

## Phase F — Hack & debt cleanup (after the heart beats)

| Item | Action |
|---|---|
| Exit-code-1 string sniffing | Keep (real cTrader quirk) but isolate in `CTraderCli`, log loudly with the matched marker, and only bless exit-1 when the marker matches AND reconciliation is clean |
| `GetTradeStatsAsync` drawdown | Compute from the equity curve (`EquitySnapshots`, now deterministic per C2 account updates), not closed-trade sequence |
| `TradeResult` zeroed fields | Populate commissions/NetPnL from cBot `bar_result`/`exec` payloads (C1 includes broker PnL) |
| Raw `ALTER TABLE` in `Program.cs` | Move to EF migrations (OPEN-ISSUES AGENT-02 / STD-07) |
| `_processedExecutionIds` unbounded | Prune on close (DESIGN-04) — partially addressed by C3 explicit lifecycle |
| `await Task.Delay(5_000)` in `RunEngineReplayAsync` | Replace with completion signal from the unified inbound stream |
| OPEN-ISSUES.md | Mark everything fixed here `✅ Fixed (Iteration 17)`; add any new findings |
| `docs/iterations/README.md` | Update status table (16 → completed-with-known-bug, 17 → this) |

---

# Part 4 — Sequencing, risk, and what NOT to do

```
A (transport fix) ──► B (composition root) ──► C (lock-step) ──► D (observability) ──► E (tests) ──► F (cleanup)
        │                                          ▲
        └── A is verifiable alone; UI must show DEALER_RECV before B starts
```

**Shipping strategy — merge in slices, do not hold A hostage to C:**
- **PR 1 = Phase A alone.** Merge as soon as the UI gate passes. It restores the feedback loop
  (orders flow, UI shows trades again — nondeterministically) and gives a working baseline that
  C's determinism claim can be demonstrated against.
- **PR 2 = Phase B.** Small, mechanical, makes C testable through the same factory the UI uses.
- **PR 3 = Phase C** (protocol change, biggest blast radius — both sides of the wire change together).
- **D–F** as one or more follow-up PRs.

- **A before C**: lock-step is pointless over a transport that loses sends. A alone likely makes
  the UI produce trades again (nondeterministically) — that's a milestone, not the finish line.
- **B before C**: the lock-step engine loop must be tested through the same factory the UI uses,
  or iter-16's "test passes, UI fails" repeats.
- **D after C**: the journal schema keys on `seq`/correlation ids the protocol introduces.
- **Do not** attempt to keep the old PUB-bar path and lock-step side by side behind a flag — the
  cBot and adapter become a matrix of modes nobody can reason about. Replace, don't accrete.
- **Do not** add sleeps to fix any race found during this work. Every wait must be a handshake,
  a channel read, or a bounded retry with explicit failure.
- Suspected flakes / divergence between test and UI runs are **diagnostic signal**, not noise to
  retry away — capture the journal/reconciliation output and put it in HANDOVER.md.

# Definition of Done (iteration gate)

1. UI backtest (EURUSD H1, Jan 2024) produces trades; journal shows the full funnel; reconciliation clean.
2. The same backtest run twice from the UI and once from the test suite yields **identical trade lists**.
3. Full test suite passes credential-free except the single `Category=Venue` smoke test.
4. Zero `Thread.Sleep`-based synchronization in cBot/adapter; zero cross-thread NetMQ socket use.
5. Exactly one composition root; `EngineMode` explicit everywhere.
6. OPEN-ISSUES.md and iterations README updated; HANDOVER.md written with verification outputs.
