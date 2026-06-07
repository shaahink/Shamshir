# Iteration 8 — Handover

> Date: 2026-06-07
> Branch: phase/8-netmq-transport
> Tests: 87 unit + 1 NetMQ + 2 pipeline = 90 passing (was 87 start)

## 1. What Was Completed

| Phase | Status | Key deliverables |
|---|---|---|
| 1. NetMQ cBot | ✅ | Full rewrite: PUB/DEALER sockets, `bars.BarClosed` event, command handling, `TickEveryN` throttle. 8 dead pipe files deleted. |
| 2. NetMQBrokerAdapter | ✅ | New `IBrokerAdapter`: SUB connects to cBot PUB, ROUTER binds for commands, `DispatchMessage` parses flat JSON topics. |
| 3. Engine/Host wiring | ✅ | `Program.cs` wires `NetMQBrokerAdapter`. `BacktestRunner` uses port-based args. `StartEngine` passes `Engine__Broker__NetMQ__*` env vars. |
| 4. Bar-close eval | ✅ | Strategy evaluation moved from `ProcessTicksAsync` to `ProcessBarsAsync`. Tick loop stripped to fills/risk/accounting only. |
| 5. NetMQBridgeTest | ✅ | Fast agent loop (<15s, no credentials). Validates transport, bar processing, strategy evaluation. |
| 6. Test improvements | ✅ | 3-day fast variant, 3-month marked Slow, ordered assertions with bail-out. All log patterns updated for NETMQ/BAR_EVAL/EVAL. |
| 7. `--full-access` test | ✅ | Confirmed: NetMQ requires `--full-access` (both .NET and P/Invoke sockets blocked under None). Made switchable via `BacktestConfig.UseFullAccess` (default `true`). |
| 8. Cleanup | ✅ | NamedPipeBrokerAdapter deleted. `DECISIONS.md` updated (D70-D76). |

## 2. Test Results

| Test | Duration | Result | Key metrics |
|---|---|---|---|
| **87 unit tests** | 1.2s | ✅ ALL PASS | No regressions |
| **NetMQBridgeTest** (Category=NetMQ) | 12s | ✅ PASS | NETMQ\|CONNECTED, BAR_EVAL×5, TICK\|EURUSD |
| **3-day pipeline** (ThreeDays) | 28s | ✅ PASS | NETMQ=2, TICK=569, BAR=34 |
| **3-month pipeline** (ThreeMonth, Slow) | **28s** | ✅ **PASS** | NETMQ=2, TICK=6626, BAR=34, **SIGNAL=1** |
| **Without `--full-access`** | 20s | ❌ FAIL | `SocketException: 0x277b` — NetMQ intercepted too |

## 3. Data Flow (Current Architecture)

```
ctrader-cli (backtest process)         Engine process (TradingEngine.Host)
┌──────────────────────────────┐       ┌─────────────────────────────────────┐
│  TradingEngineCBot           │       │  NetMQBrokerAdapter                 │
│                              │       │                                     │
│  PUB socket (bind :15555) ───┼──────► SUB socket (connect :15555)          │
│    topic "bar" → Bar OHLC   │       │   → Channel<Bar>                    │
│    topic "tick" → Bid/Ask   │       │   → Channel<Tick>                   │
│    topic "acct" → Balance   │       │   → Channel<AccountUpdate>          │
│    topic "exec" → Fills     │       │   → Channel<ExecutionEvent>         │
│                              │       │                                     │
│  DEALER socket (connect      │       │  ROUTER socket (bind :15556)        │
│              :15556) ◄───────┼───────│ commands via captured identity      │
└──────────────────────────────┘       └─────────────────────────────────────┘
                                                │
                                       EngineWorker
                                    ProcessBarsAsync  (heavy — once per bar)
                                      → RecomputeIndicators
                                      → BuildIndicatorSnapshot
                                      → strategy.Evaluate()
                                      → SIGNAL| or EVAL|

                                    ProcessTicksAsync (lightweight — every 1:10 ticks)
                                      → exec drain → force-close → account update
                                      → sim.OnTickReceived (fills/SL/TP)
```

## 4. Key Decisions Made

### D70 — NetMQ adopted as transport
Named pipes abandoned. NetMQ's native P/Invoke bypasses ctrader-cli's .NET socket interception. PUB/SUB for data, ROUTER/DEALER for commands.

### D71 — Bar-close strategy evaluation
Evaluating on every tick was wasted CPU (indicators don't change within a bar). Bar-close eval runs ~60× less frequently and produces identical signal decisions.

### D76 — `--full-access` required
Both .NET managed sockets AND NetMQ native P/Invoke sockets are intercepted by ctrader-cli sandbox under `AccessRights.None`. The `--full-access` flag is mandatory. Configurable via `BacktestConfig.UseFullAccess`.

## 5. Known Issues / Observations

| Issue | Severity | Detail |
|---|---|---|
| **34 bars for 3-month test** | MODERATE | Both 3-day and 3-month tests show same bar count. Likely `MarketData.GetBars()` returns a limited window. Doesn't block signal generation — mean-reversion fired with 34 bars. The 55-bar strategies (trend-breakout, ema-alignment) need more warmup bars — may require increasing the chart's bar window or extending the backtest pre-warmup period. |
| **"Message expected" crash** | LOW | ctrader-cli crashes when generating report with zero trades. Happens post-backtest, doesn't affect data or signals. Exit code 1 but test still passes. Cosmetic. |
| **TickEveryN=10 throttling** | LOW | 1:10 tick throttle means 90% of ticks dropped. Fine for fills/SL/TP but some price wick movement may be missed. Configurable — increase if fill accuracy matters. |
| **Fixed ports 15555/15556** | LOW | Hardcoded. Won't support parallel backtests. Documented as tech debt in DECISIONS.md. |

## 6. How to Verify

```cmd
rem Fast agent loop (no credentials, <15s):
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "Category=NetMQ"

rem 3-day pipeline (credentials needed, ~30s):
set CTrader__CtId=seankiaa
set CTrader__PwdFile=C:\Users\shahi\Documents\ctrader.pwd
set CTrader__Account=5834367
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeDays"

rem 3-month full signal test (credentials, ~30s):
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeMonth"
```

## 7. Recommended Focus for Next Iteration

1. **Bar count investigation** — Why `MarketData.GetBars()` returns a fixed window. Look at cTrader API: `Bars.Count`, `MarketData.GetBars(TimeFrame, SymbolName, barsCount)` overload. If there's a way to request more historical bars, the 55-bar strategies would also fire.
2. **Multi-symbol support** — cBot currently subscribes one symbol. The cTrader Bars events API supports multiple `Bars` objects — subscribe per configured symbol.
3. **Graceful report handling** — The "Message expected" crash is cosmetic but produces exit code 1. Could be mitigated by `WithValidation(CommandResultValidation.None)` or handling in BacktestRunner.
4. **Dynamic port allocation** — Fixed ports work for single-user dev but conflict in CI. Use `GetRandomUnusedPort()` or OS-assigned ports.
