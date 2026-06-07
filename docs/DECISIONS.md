# Shamshir — Decision Record

## D1–D69
_(Recorded in prior sessions — see git history)_

---

## D70 — NetMQ adopted as cBot↔engine transport
**Made**: 2026-06-07  
**Status**: ✅ Final

Named pipes are abandoned. ctrader-cli's sandbox intercepts .NET managed socket APIs (`NamedPipeClientStream`, `TcpClient`). NetMQ uses native P/Invoke (ZeroMQ) which bypasses the interception. PUB/SUB for data streaming, ROUTER/DEALER for bidirectional commands.

`--full-access` CLI flag is still required — even NetMQ's native sockets are blocked under `AccessRights.None`. The flag is configurable via `BacktestConfig.UseFullAccess` (default `true`).

## D71 — Strategy evaluation moved to bar close
**Made**: 2026-06-07  
**Status**: ✅ Final

Indicators only change on bar close. Evaluating on every tick produced identical results within a bar. `ProcessBarsAsync` now runs strategy evaluation once per bar. `ProcessTicksAsync` handles fills, risk, and account updates only. This eliminated the per-tick bottleneck (~2-3 ticks/sec → unlimited).

## D72 — World ACL pipe security (legacy)
**Made**: 2026-06-07  
**Status**: ✅ Superseded by D70

`NamedPipeServerStreamAcl.Create` with `WorldSid FullControl` was added to NamedPipeBrokerAdapter to allow ctrader-cli sandbox to connect. No longer relevant — NamedPipeBrokerAdapter deleted, replaced by NetMQBrokerAdapter.

## D73 — `bars.BarClosed` event for bar data
**Made**: 2026-06-07  
**Status**: ✅ Final

cBot uses `MarketData.GetBars().BarClosed` event instead of `OnBar()` Robot override. `BarClosed` fires for all historical bars (including pre-backtest warmup), where `OnBar()` only fires during the backtest replay window. Bars with `Open == 0` (BarOpened placeholders) are filtered out.

## D74 — Fixed ports (15555/15556) for NetMQ
**Made**: 2026-06-07  
**Status**: ⚠️ Tech debt — log for future dynamic allocation

Ports are hardcoded. Fine for single-user. Parallel runs would conflict. Noted for future iteration.

## D75 — TickEveryN = 10 throttling
**Made**: 2026-06-07  
**Status**: ✅ Final

Ticks published 1 in 10 (configurable via cBot `TickEveryN` parameter). Ticks are used for fills/SL/TP only, not strategy signals. 1:10 throttle is sufficient for fill simulation.

## D76 — `--full-access` required (all socket APIs blocked)
**Made**: 2026-06-07  
**Status**: ✅ Confirmed

Testing proved: both .NET managed sockets AND NetMQ native P/Invoke sockets are intercepted by ctrader-cli sandbox under `AccessRights.None`. The `--full-access` flag is mandatory for any socket-based IPC. Configurable via `BacktestConfig.UseFullAccess`.
