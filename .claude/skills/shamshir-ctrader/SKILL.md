---
name: shamshir-ctrader
description: Understand, build, debug, and extend the Shamshir cTrader backtest path — the cBot (.algo), NetMQ transport, engine-side adapter, kernel loop, desktop capture/listen mode, cache layer, and all write/read paths during a live or headless backtest. Load when asked about any cTrader integration, backtest execution, NetMQ messaging, cBot code, CTraderBrokerAdapter, listen mode, desktop capture, cTrader CLI, or why the UI is slow during a backtest.
---

# Skill: shamshir-ctrader

---

## Session Warmup (read FIRST)

### Files to read (in order)

1. `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:11-334` — cBot parameters, OnStart, OnBarClosed
2. `src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs:57-78` — socket wiring
3. `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:127-260` — hello/bar handling, channels
4. `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:929-1048` — headless launch path (RunEngineNetMqAsync)
5. `src/TradingEngine.Web/Services/CTraderListenService.cs:1-265` — desktop capture listen mode
6. `src/TradingEngine.Host/KernelBacktestLoop.cs:128-290` — kernel loop per-bar cycle
7. `src/TradingEngine.Infrastructure/Caching/RunDataCache.cs` — cache layer

### Project files

```
src/TradingEngine.Adapters.CTrader/   # cBot (.algo) — C# 10 / net6.0
src/TradingEngine.Infrastructure/     # Transport, adapter, persistence
src/TradingEngine.Host/               # Kernel loop, engine worker
src/TradingEngine.Web/                # Orchestrator, listen service, API, cache
```

---

## 1. Topology — who binds, who connects

```
cBot (TradingEngineCBot)                    Engine (Shamshir)

PUB socket       BINDS  tcp://*:{DataPort}  ←── SUB socket connects
DEALER socket    CONNECTS tcp://127.0.0.1:{CommandPort}  →  ROUTER socket binds tcp://*:{CommandPort}
```

| Role | Socket | Direction | Binding side | Default port | Purpose |
|------|--------|-----------|-------------|-------------|---------|
| cBot | PublisherSocket | BIND | cBot | 15555 | Sends ticks, account updates, diagnostics to engine SUB |
| Engine | SubscriberSocket | CONNECT | Engine | 15555 | Receives topic+frame from cBot PUB |
| Engine | RouterSocket | BIND | Engine | 15556 | Receives identity+json from cBot DEALER; sends commands back |
| cBot | DealerSocket | CONNECT | cBot | 15556 | Sends hello, bar frames; receives bar_done, commands |

**Headless path:** Engine allocates random ports → launches ctrader-cli with `--DataPort=X --CommandPort=Y`
**Desktop capture:** Engine binds on fixed ports (15555/15556) → user types same ports in cBot params → cBot connects

---

## 2. Message Flow — one bar cycle

```
cBot OnBarClosed fires:
  ┌─────────────────────────────────────────────────────────────────────┐
  │ DEALER ──► ROUTER:  {"type":"bar", "seq":42, "symbol":"EURUSD",   │
  │                        "period":"H1", "openTime":"...", OHLCV,      │
  │                        "account":{"balance":...,"equity":...}}      │
  │                                                                     │
  │ ROUTER → adapter.ReadRouterLoop → _barChannel.Write(bar)            │
  │                                    → KernelBacktestLoop reads       │
  │                                    → ProcessBarAsync:               │
  │                                       evaluate → order proposals    │
  │                                       → pump orders/fills/closes    │
  │                                       → equity/breach               │
  │                                       → trailing/breakeven          │
  │                                       → CompleteBarAsync()          │
  │                                                                     │
  │ ROUTER ──► DEALER: {"type":"bar_done","v":1,"seq":42,              │
  │                       "commands":[{submit_order,close_position,...}]│
  │                                                                     │
  │ cBot executes commands → sends back:                               │
  │ DEALER ──► ROUTER: {"type":"bar_result","seq":42,"execs":[...],    │
  │                       "account":{...}}                              │
  └─────────────────────────────────────────────────────────────────────┘

Side channel (PUB → SUB):
  PUB ──► SUB: {"type":"tick",...}          → DispatchSubMessage → TickStream
  PUB ──► SUB: {"type":"acct",...}          → DispatchSubMessage → AccountStream
```

### Handshake (sent once on cBot OnStart, via DEALER → ROUTER)

```json
{
  "type": "hello",
  "v": 2,
  "symbols": ["EURUSD"],
  "periods": ["H1"],
  "subs": [{"sym": "EURUSD", "tf": "H1"}],
  "barsLoaded": 2000,
  "account": {"balance": 100000, "equity": 100000},
  "positions": [...],
  "mode": "backtest"  // v2 only — "backtest" or "live". Engine uses this for listen-mode session detection.
}
```

Engine response: `{"type":"hello_ack","v":1}` → sent via ROUTER.Send(identity, json)

cBot retries hello every 1s for 5s. If no ack → `CBOT|HELLO_TIMEOUT` → cBot stops.

---

## 3. cBot — TradingEngineCBot.cs

**File:** `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` (810 lines, partial class with `BuildInfo.g.cs`)

### Parameters (visible in cTrader Desktop UI)

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `_build` | auto-stamped | Git hash + date — read-only, confirms which build is loaded |
| `DataPort` | 15555 | PUB bind port |
| `CommandPort` | 15556 | DEALER connect port |
| `SymbolString` | "EURUSD" | Comma-separated symbols |
| `Periods` | "H1" | Comma-separated timeframes |
| `ReportPath` | "" | Where cBot writes its own report.json (resilient venue ledger) |
| `Diagnostics` | false | Enables CBOT|TIMING round-trip stats |
| `Verbose` | false | Enables per-tick PUB messages |
| `TickEveryN` | 10 | Throttle tick publishing |

### Key methods

| Method | Line | What it does |
|--------|------|-------------|
| `OnStart` | 83-172 | Binds PUB, connects DEALER, subscribes to bars, sends hello, waits for ack, subscribes to BarClosed |
| `OnBarClosed` | 193-334 | Sends bar via DEALER, blocks 30s for bar_done, executes commands, sends bar_result |
| `OnStop` | 722-763 | Flushes report, sends stats, disposes sockets with 2s linger |
| `OnDealerReceive` | 612-641 | Receives hello_ack (sets _connected), bar_done, shutdown. Writes to _inbox. |

### Build stamp

`BuildInfo.g.cs` is auto-generated by `scripts/stamp-cbot-build.ps1` at build time.
Contains `internal const string CbotBuildStamp = "v2.0.0 YYYY-MM-DD githash branch"`.
Shows as the `_build` parameter in cTrader Desktop.

### Deploy to cTrader Desktop

```powershell
# Build + stamp + copy to cTrader Sources folder:
.\scripts\deploy-cbot.ps1

# Manually:
dotnet build src/TradingEngine.Adapters.CTrader -p:AutoDeploy=true
```

The `.algo` lands in `%USERPROFILE%\Documents\cAlgo\Sources\Robots\`.
Restart cTrader Desktop to see `TradingEngineCBot` in the bot picker.

---

## 4. Engine Side — from transport to kernel

### NetMqMessageTransport (`Infrastructure/Transport/NetMq/`)

- **Constructor:** Takes `dataEndpoint` (SUB connect) and `commandEndpoint` (ROUTER bind)
- **ConnectAsync:** Creates SUB + ROUTER + NetMQQueue, starts poller
- **Channels:** `SubMessages` ChannelReader `(string Topic, string Json)` — consumed by adapter.ReadSubLoop
- **Channels:** `RouterMessages` ChannelReader `(byte[] Identity, string Json)` — consumed by adapter.ReadRouterLoop
- **Send(identity, json):** Enqueues to NetMQQueue → ROUTER.SendMoreFrame(identity).SendFrame(json)

### CTraderBrokerAdapter (`Infrastructure/Venues/CTrader/`)

- **ReadRouterLoop:** Handles `"hello"` → ack + reconcile + **fires OnSessionStarted** (v2), `"bar"` → writes to BarStream channel, `"bar_result"` → handles exec results, `"exec"` → venue-initiated executions, `"stats"` → reconcile
- **ReadSubLoop (DispatchSubMessage):** Handles `"tick"` → TickStream, `"acct"` → AccountStream
- **Command buffering:** SubmitOrder/ClosePosition/ModifyOrder/CancelOrder buffer commands per-bar. `CompleteBarAsync` sends all buffered commands in one `bar_done` envelope.
- **ExitMode:** `VenueManaged` — cTrader owns SL/TP, engine reconciles to venue's open set per-bar.

### KernelBacktestLoop (`Host/`)

Per-bar cycle (`ProcessBarAsync`):
1. Advance venue (`OnBarObserved`)
2. Drain prior feedback (ExecutionStream + AccountStream)
3. Reconcile venue positions (if VenueManaged)
4. Prop-firm day/week/month roll check
5. Evaluate bar (strategies)
6. Pump proposals → fills
7. Pump BarClosed → exit detection
8. Equity → drawdown/breach check
9. Trailing/breakeven evaluation
10. `CompleteBarAsync` → sends bar_done with buffered commands to cBot

---

## 5. Two Launch Paths

### Headless (CLI-launched)

```
Web UI "Start Backtest" → BacktestOrchestrator.Start()
  → AllocatePorts() (random ephemeral)
  → EngineHostFactory.Create() with AdapterFactory → transport + adapter on random ports
  → Start engine host → kernel waits for bars
  → Launch ctrader-cli with --DataPort=X --CommandPort=Y
  → cBot starts, connects to engine's ports
  → Bars flow → kernel processes
  → CLI exits → engine finalizes → run completed
```

### Desktop Capture (listen mode)

```
Web UI "Start Listening" → CTraderListenService.StartListeningAsync()
  → Mint RunId, write placeholder run (status="awaiting-session")
  → EngineHostFactory.Create() with AdapterFactory → transport + adapter on FIXED ports 15555/15556
  → Start engine host → kernel waits for bars
  → User opens cTrader Desktop, adds TradingEngineCBot, sets DataPort=15555 CommandPort=15556
  → User runs backtest → cBot OnStart → sends hello v2 with mode=backtest
  → Engine receives hello → adapter fires OnSessionStarted(sessionInfo)
  → ListenService updates placeholder run → status="running"
  → Bars flow → kernel processes
  → Backtest ends / user stops → engine finalizes
  → User clicks "Stop Listening" → host stopped, run completed
```

### Coexistence

Both paths can run simultaneously — they use different ports (random vs fixed 15555/15556).
The monitor page (`/runs/{id}/monitor`) works identically for both via SignalR.

---

## 6. Write/Read Paths During a Backtest

### Writes (kernel → DB + cache)

| What | Writer | Frequency | Channel | Cache push? |
|------|--------|-----------|---------|-------------|
| Journal (StepRecord) | SqliteStepRecordSink | Batches of 500 | Wait, 50K cap | YES — after SaveChangesAsync |
| Trades | TradePersistenceHandler | Per trade close | Wait, 1K cap | YES — after SaveTradeAsync |
| Equity | EquityPersistenceHandler | Every 5s, batches of 100 | DropOldest, 10K cap | YES — after batch save |
| Bars | BufferedBarWriter | Every 500 bars | DropOldest, 10K cap | YES — after bulk insert |
| Run summary | SqliteBacktestRunRepository | Start + end only | N/A | No |

### Reads (UI → cache or DB)

| Endpoint | Cache-first? | Notes |
|----------|-------------|-------|
| `GET /api/runs` | IMemoryCache, 2s expiry | Most-hit endpoint |
| `GET /api/runs/{id}/trades` | RunDataCache | Falls back to DB for completed runs |
| `GET /api/runs/{id}/equity` | RunDataCache | Falls back to DB |
| `GET /api/runs/{id}/bars` | RunDataCache (journal) | Capped at 5,000 events |
| `GET /api/runs/{id}/journal` | RunDataCache | Falls back to DB |
| `GET /api/runs/{id}/daily-pnl` | DB only | Computed from trades |
| `GET /api/runs/{id}/analytics` | DB only | Computed from trades |
| **Monitor (live)** | **SignalR push — zero DB** | All data from in-memory `BacktestRunState` |

---

## 7. Cache Layer — RunDataCache

**Interface:** `TradingEngine.Domain.Interfaces.IRunDataCache`
**Implementation:** `TradingEngine.Infrastructure.Caching.RunDataCache`

```
┌─ Writers (inner host) ────────────────────────────────────────┐
│  SqliteStepRecordSink    ──► DB ──► IRunDataCache.AppendJournal│
│  TradePersistenceHandler ──► DB ──► IRunDataCache.AppendTrade  │
│  EquityPersistenceHandler──► DB ──► IRunDataCache.AppendEquity │
│  BufferedBarWriter       ──► DB ──► IRunDataCache.AppendBar    │
└────────────────────────────────────────────────────────────────┘
                          │
                          ▼  (shared singleton, passed via EngineHostOptions)
┌─ Readers (Web host) ──────────────────────────────────────────┐
│  RunQueryService.GetRunTradesAsync  → cache ?? DB             │
│  RunQueryService.GetRunEquityAsync  → cache ?? DB             │
│  RunQueryService.GetRunBarsAsync    → cache ?? DB (capped 5K) │
└────────────────────────────────────────────────────────────────┘
```

**Key facts:**
- Singleton shared between Web DI and inner host DI via `EngineHostOptions.RunDataCache`
- Write-through: DB save succeeds → cache is updated in same call
- Journal ring-buffered at 10,000 entries per run
- Completed runs remain in cache until `Evict(runId)` is called

---

## 8. Common Gotchas

| Symptom | Cause | Fix |
|---------|-------|-----|
| cBot not appearing in cTrader picker | `.algo` not in Sources folder, or cTrader not restarted | Run `scripts/deploy-cbot.ps1`, restart cTrader Desktop |
| "Full Access denied" | cBot blocked by cTrader | Grant Full Access when prompted; cBot requires it for NetMQ sockets |
| `CBOT|HELLO_TIMEOUT` | Engine not listening on expected ports | Verify engine is running, ports match |
| `CBOT|BAR_TIMEOUT` | Engine took >30s to process a bar | Check engine logs for exceptions; check SQLite lock contention |
| UI unresponsive during backtest | SQLite lock contention from parallel reads | Cache layer should prevent this (iter-cache-reads); verify RunDataCache is registered |
| Port already in use | Another listener or cBot instance running | Stop listener first; or change ports in both cBot params + engine config |
| `bar_done` never received | Transport disconnected mid-bar | cBot times out after 30s, stops; check NetMQ connectivity |
| cBot `_build` shows "(build not stamped)" | Stamp script didn't run before build | Run `scripts/stamp-cbot-build.ps1` manually, rebuild |
| Determinism test regressed | cBot or kernel change altered journal | Run `dotnet test --filter "RequiresCTrader!=true&FullyQualifiedName~Determinism"` |

---

## 9. Test Reference

### Unit tests (credential-free, fast)

```powershell
dotnet test tests/TradingEngine.Tests.Unit   # 290+ pass
```

### Cache tests (credential-free, fast)

```powershell
dotnet test tests/TradingEngine.Tests.Unit --filter "FullyQualifiedName~Cache"
```

### Simulation tests (credential-free, medium)

```powershell
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true"
```

### cTrader E2E tests (require credentials, slow)

```powershell
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"
```

Credentials are in `src/TradingEngine.Web/appsettings.Development.json` → `CTrader` section.

### Determinism gate (must stay green)

```powershell
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~Determinism"
```

---

## 10. File Map

### cBot & Transport
| File | Purpose |
|------|---------|
| `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` | cBot: parameters, OnStart, OnBarClosed, OnStop |
| `src/TradingEngine.Adapters.CTrader/BuildInfo.g.cs` | Auto-stamped build version (generated) |
| `src/TradingEngine.Adapters.CTrader/ShamshirTradeLogger.cs` | cBot's own trade ledger (report.json) |
| `scripts/deploy-cbot.ps1` | Build + deploy .algo to cTrader Sources |
| `scripts/stamp-cbot-build.ps1` | Generate BuildInfo.g.cs with git hash |
| `src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs` | SUB + ROUTER + NetMQ poller |
| `src/TradingEngine.Domain/Interfaces/IMessageTransport.cs` | Transport interface |

### Engine Adapter & Kernel
| File | Purpose |
|------|---------|
| `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` | Adapter: channels, hello, bar, commands, buffering |
| `src/TradingEngine.Domain/Interfaces/IBrokerAdapter.cs` | Broker adapter interface + session handler |
| `src/TradingEngine.Domain/Venues/SessionInfo.cs` | Session metadata from cBot hello (symbol, period, mode, balance) |
| `src/TradingEngine.Host/KernelBacktestLoop.cs` | Per-bar kernel loop: evaluate, pump, equity, trailing |
| `src/TradingEngine.Host/EngineRunner.cs` | Engine runner: builds loop, warms indicators, runs |
| `src/TradingEngine.Host/EngineWorker.cs` | Background service wrapping EngineRunner |
| `src/TradingEngine.Host/EngineHostFactory.cs` | Creates inner IHost for engine |

### Orchestration & Listen Mode
| File | Purpose |
|------|---------|
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Run lifecycle, port allocation, CLI launch, progress |
| `src/TradingEngine.Web/Services/CTraderListenService.cs` | Desktop capture: listen on fixed ports, mint RunId on session |
| `src/TradingEngine.Web/Services/CtraderListenConfig.cs` | DTO for listen mode config |
| `src/TradingEngine.Web/Api/CtraderListenController.cs` | `POST /api/ctrader/listen/start|stop`, `GET status` |
| `src/TradingEngine.Web/Api/VenueSessionsController.cs` | Venue session history read API |

### Cache
| File | Purpose |
|------|---------|
| `src/TradingEngine.Domain/Interfaces/IRunDataCache.cs` | Cache interface |
| `src/TradingEngine.Infrastructure/Caching/RunDataCache.cs` | Cache implementation (write-through) |
| `src/TradingEngine.Web/Services/RunQueryService.cs` | Cache-first reads + IMemoryCache for runs list |
| `src/TradingEngine.Domain/EngineHostOptions.cs` | Carries `IRunDataCache? RunDataCache` to inner host |

### Persistence (writers that push to cache)
| File | Purpose |
|------|---------|
| `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteStepRecordSink.cs` | Journal writes → cache push |
| `src/TradingEngine.Infrastructure/Persistence/TradePersistenceHandler.cs` | Trade writes → cache push |
| `src/TradingEngine.Infrastructure/Persistence/EquityPersistenceHandler.cs` | Equity writes → cache push |
| `src/TradingEngine.Infrastructure/Caching/BufferedBarWriter.cs` | Bar writes → cache push |
| `src/TradingEngine.Host/ScopedStepRecordSink.cs` | Scope-per-flush bridge, resolves cache from scope |
| `src/TradingEngine.Infrastructure/Persistence/SqlitePragmaInterceptor.cs` | SQLite PRAGMAs: WAL, cache_size, mmap, busy_timeout |

### Docs
| File | Purpose |
|------|---------|
| `docs/iterations/iter-ctrader-capture/PLAN.md` | Desktop capture design |
| `docs/iterations/iter-ctrader-capture/DESKTOP-SETUP.md` | How to install cBot in cTrader Desktop |
| `docs/iterations/iter-cache-reads/PLAN.md` | Cache layer design |
| `docs/iterations/iter-cache-reads/HANDOVER.md` | Cache handover + carry-forward |
| `docs/iterations/iter-redesign-ctrader/PLAN.md` | Earlier cTrader redesign (VenueManaged, reconciliation) |
