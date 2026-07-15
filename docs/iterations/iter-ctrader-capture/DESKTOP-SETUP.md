# cTrader Desktop Setup — TradingEngineCBot

**Last updated:** 2026-07-01 (iter-ctrader-capture, P6)

This doc covers how to get our `TradingEngineCBot` (the `.algo` file) into cTrader Desktop so it appears in the bot picker and can talk to the Shamshir engine over NetMQ.

---

## 1. Build + Deploy (one command)

```powershell
# Build the cBot with auto-stamped git hash, then copy to cTrader's Sources folder:
.\scripts\deploy-cbot.ps1

# Or manually:
dotnet build src/TradingEngine.Adapters.CTrader -p:AutoDeploy=true
```

The `.algo` file lands in `%USERPROFILE%\Documents\cAlgo\Sources\Robots\`.

The build is **auto-stamped** with the current git hash and date. This shows as an **`_build` parameter** in the cBot's settings panel in cTrader Desktop, so you can verify exactly which commit is loaded.

---

## 2. Verify it appears in cTrader Desktop

1. **Restart cTrader Desktop** (it only re-scans Sources on startup)
2. Right-click any chart → **Add Bot** (or Ctrl+F)
3. Search for **`TradingEngineCBot`** (named after the class; the `.algo` file is called `src.algo`)
4. Select it → the parameter panel opens

You should see these parameters:

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `_build` | `v2.0.0 YYYY-MM-DD githash branch` | Auto-stamped version — **read-only**, tells you which build is loaded |
| `DataPort` | `15555` | Port the cBot PUB binds to (engine's SUB connects here) |
| `CommandPort` | `15556` | Port the cBot DEALER connects to (engine's ROUTER binds here) |
| `SymbolString` | `EURUSD` | Comma-separated symbols |
| `Periods` | `H1` | Comma-separated timeframes |

**If `TradingEngineCBot` doesn't appear:**
- Check `%USERPROFILE%\Documents\cAlgo\Sources\Robots\src.algo` exists and is recent
- cTrader only scans this folder at startup — restart the app
- If the folder doesn't exist, create it and re-deploy

---

## 3. Grant Full Access

When you add the bot to a chart, cTrader will prompt:

> **"This cBot requires Full Access. Allow?"**

Click **Yes**. The cBot needs `FullAccess` for:
- Binding/connecting NetMQ sockets (TCP)
- Reading account balance/equity
- Placing/modifying/cancelling orders
- Accessing open positions

If you deny, the bot won't start.

---

## 4. Run a Desktop Capture Session

### On the Shamshir Web UI:

1. Open the **cTrader Desktop Capture** page (`/ctrader-sessions`)
2. Click **Start Listening**
3. Note the ports shown (default: `15555` data / `15556` command)

### In cTrader Desktop:

1. Add `TradingEngineCBot` to a chart (e.g., EURUSD H1)
2. Set parameters: `DataPort=15555` `CommandPort=15556` (match the engine's ports)
3. **Right-click the chart → Backtesting**
4. Pick a date range → click **Start**

### What happens:

- cTrader desktop starts the backtest → cBot `OnStart` fires
- cBot binds PUB on 15555, connects DEALER to 15556
- cBot sends `hello` (v=2 with `mode=backtest`) to engine
- Engine mints a RunId, creates a run, starts the kernel
- The Web UI updates to show "Session active" with a link to the live monitor
- Bars flow from cTrader → NetMQ → engine → kernel → trades

### After the backtest completes:

- The cBot stops (cTrader backtest ends or you click Stop)
- The engine finalizes the run
- The run appears in the runs list with full report, equity, trades, bar-decisions

---

## 5. Re-deploying after code changes

After changing the cBot source (`TradingEngineCBot.cs`):

```powershell
.\scripts\deploy-cbot.ps1
# Restart cTrader Desktop so it picks up the new .algo
```

The `_build` parameter will update to the new git hash on the next build, so you can confirm in cTrader that the latest version is loaded.

---

## 6. Troubleshooting

| Symptom | Check |
|---------|-------|
| cBot not in picker | Restart cTrader Desktop; check `src.algo` exists in `Documents\cAlgo\Sources\Robots\` |
| cBot shows `_build = (build not stamped)` | Run `.\scripts\stamp-cbot-build.ps1` then rebuild |
| "Connection refused" in cBot output | Is the engine listener running? Click **Start Listening** in the web UI first |
| "Full Access" denied | cTrader blocks sockets — grant access or check cBot settings |
| Port already in use | Stop the listener first (web UI), or change ports in both engine + cBot |
| cBot says `CBOT|HELLO_TIMEOUT` | Engine isn't listening on the expected ports — verify ports match |
