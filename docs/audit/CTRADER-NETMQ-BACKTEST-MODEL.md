# cTrader/NetMQ Backtest Model — Quick Reference

> **Predominant backtest path.** The credential-free `BacktestReplayAdapter` path exists but the cTrader path is the production-equivalent default when `CTrader:UseForBacktest=true`.

---

## Architecture at a glance

```
ctrader-cli.exe (external process, replays tick data for date range)
    └→ cBot (TradingEngineCBot.cs, net6.0, compiled .algo)
        ├─ PUB socket :15555 → NetMqMessageTransport.Sub (bars/ticks/acct on SubChannel)
        └─ DEALER socket → NetMqMessageTransport.Router :15556 (bar/bar_result/execs/stats on RouterChannel)
            └→ CTraderBrokerAdapter (CTraderBrokerAdapter.cs)
                ├─ BarStream → KernelBacktestLoop.RunFromBrokerAsync()
                ├─ ExecutionStream → PumpAsync() feedback drain
                └─ AccountStream → PumpAsync() mark-to-market drain
                    └→ EngineRunner → KernelBacktestLoop → Kernel → EffectExecutor
```

## Key files

| Layer | File | Role |
|-------|------|------|
| cBot | `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` | cTrader algorithm — bar publishing, command exec, exec generation |
| Transport | `src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs` | Engine-side NetMQ sockets (SUB + ROUTER + send queue) |
| Adapter | `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` | IBrokerAdapter over NetMQ — bar/exec/account channels |
| Loop | `src/TradingEngine.Host/KernelBacktestLoop.cs` | Per-bar kernel processing (evaluate → proposals → pump → equity → trailing) |
| Evaluator | `src/TradingEngine.Host/BarEvaluator.cs` | Indicator recompute + strategy evaluation per bar |
| Effects | `src/TradingEngine.Host/EffectExecutor.cs` | Kernel effects → broker commands (submit/modify/close) |
| Runner | `src/TradingEngine.Host/EngineRunner.cs` | Wiring, `ReportBar()` hook, equity/flush orchestration |
| Orchestrator | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Run lifecycle, progress/SignalR, `RunEngineNetMqAsync` |
| CLI | `src/TradingEngine.CTraderRunner/BacktestRunner.cs` | ctrader-cli.exe subprocess launch + report parse |
| Journal | `src/TradingEngine.Engine/Kernel/ChannelJournalWriter.cs` | Lossless StepRecord journal (50K channel, 500/batch, Wait mode) |

## Channel map

| Stream | Capacity | Mode | Rationale |
|--------|----------|------|-----------|
| cBot→Engine Sub (bars/ticks) | 10,000 | DropOldest | High-volume inbound |
| cBot→Engine Router (cmds/execs) | 2,000 | Wait | Lossless command replies |
| CTraderBroker BarStream | 2,000 | Wait | Lossless live bars |
| CTraderBroker TickStream | 10,000 | DropOldest | High-volume, lossy ok |
| CTraderBroker AccountStream | 1,000 | Wait | Lossless account updates |
| CTraderBroker ExecutionStream | 1,000 | Wait | Lossless execution events |
| JournalWriter StepRecords | 50,000 | Wait | Must be lossless |
| TradePersistence | 1,000 | Wait | Lossless trade persistence |
| BarPersistence (BufferedBarWriter) | 10,000 | DropOldest | Analytics, lossy ok |
| EquityPersistence | 10,000 | DropOldest | Analytics, lossy ok |

## cBot → Engine lock-step protocol

1. **cBot.OnBarClosed()** → sends bar JSON via DEALER → engine's Router
2. **cBot blocks** in synchronous `while(deadline) { TryTake(100ms) }` waiting for `bar_done`
3. **Engine KernelBacktestLoop** processes bar → evaluates signals → pumps kernel → then calls `CompleteBarAsync()` which sends `bar_done` + buffered commands back via DEALER
4. **cBot receives bar_done** → executes commands (submit_order, close_position, etc.) → sends `bar_result` with exec results
5. **Engine CTraderBrokerAdapter** receives `bar_result` → parses execs → writes to `_execChannel` → drained by next bar's `PumpAsync()`

## DB writes (all backgrounded)

- **Journal:** `ChannelJournalWriter` → `ScopedStepRecordSink` (new scope per 500-batch) → `SqliteStepRecordSink.AddRange` → `SaveChangesAsync`
- **Trades:** `TradePersistenceHandler` (1-at-a-time, Wait mode) → `SqliteTradeRepository`
- **Bars:** `BarPersistenceHandler` → `BufferedBarWriter` (500/batch, DropOldest) → `SqliteBarRepository.BulkInsertAsync`
- **Equity:** `EquityPersistenceHandler` (5s timer, 100 max) → `SqliteEquityRepository.SaveBatchAsync`
- **No synchronous DB writes in the per-bar hot path.** All persistence is offloaded to background channels.

## SQLite config

- `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;` — set by Web host startup (`ServiceRegistration.cs:82`)
- Engine Host path (`EngineServiceCollectionExtensions.cs:53`) runs **no PRAGMAs** — uses separate `trading-backtest.db`
- **NO `cache_size` configured** — SQLite defaults to 2MB page cache
- **NO `mmap_size`, `synchronous`, `temp_store`** PRAGMAs configured anywhere

## NetMQ defaults (no explicit tuning)

- Ports: 15555 (data/PUB→SUB), 15556 (command/ROUTER↔DEALER)
- Engine side: `NetMQQueue` for outbound sends (non-blocking enqueue, poller drain)
- cBot side: direct `SendFrame()` calls on cTrader thread (no send queue)
- Linger: 2 seconds on cBot PUB + DEALER sockets
- No `SendHighWatermark` / `ReceiveHighWatermark` / `SendBuffer` / `ReceiveBuffer` configured

## Known performance hotspots

See full audit at `docs/audit/BACKTEST-PERFORMANCE-AUDIT.md`. Quick summary:

| # | Hotspot | File:Line |
|---|---------|-----------|
| F1 | cBot 100ms blocking poll for bar_done (30s timeout) | `TradingEngineCBot.cs:219-302` |
| F2 | Indicators recomputed from scratch every bar | `BarEvaluator.cs:67` |
| F3 | JSON serialization on every kernel decision step | `KernelBacktestLoop.cs:346-348` |
| F4 | SQLite cache_size=2MB (default), Host path no WAL | `ServiceRegistration.cs:82` |
| F5 | Trade persistence 1-per-SaveChangesAsync | `TradePersistenceHandler.cs:37` |
| F6 | cBot double-serialize in `Serialize()` | `TradingEngineCBot.cs:765-773` |
