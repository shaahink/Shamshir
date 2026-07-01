# Profiling Guide — Shamshir Backtest Performance

**Date:** 2026-07-01
**Why this exists:** So future agents (and humans) know how to collect and interpret engine timing data without re-discovering the architecture.

---

## Quick start

Every cTrader E2E test run automatically writes a profiling JSON to `%TEMP%\shamshir-profiling\`. No flags needed.

```powershell
# Clear previous runs
Remove-Item "$env:TEMP\shamshir-profiling" -Recurse -Force

# Run a smoke test (~40s)
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~CtraderE2EHarnessSmokeTests.EurUsd_H1_3Days_ProducesTrades_UsingRunAsync"

# Read the profile
Get-ChildItem "$env:TEMP\shamshir-profiling" | ForEach-Object { Get-Content $_.FullName }
```

Output example:
```json
{
  "bars": 95,
  "evaluateMs": 164,
  "pumpMs": 446,
  "completeBarMs": 11,
  "journalSteps": 422,
  "totalEngineMs": 621,
  "meanBarMs": 6.54,
  "meanEvaluateMs": 1.73,
  "meanPumpMs": 4.69,
  "meanCompleteBarMs": 0.12,
  "meanJournalStepsPerBar": 4.44
}
```

---

## Field reference

| Field | What it measures | Healthy range (H1, 3-day) |
|-------|-----------------|---------------------------|
| `bars` | Bars processed by engine | ~95 for 3-day H1 |
| `evaluateMs` | `BarEvaluator.EvaluateAsync()` — strategy + indicator compute | < 200ms |
| `pumpMs` | All `PumpAsync()` calls — kernel decisions + journal + effects + venue drain | < 600ms |
| `completeBarMs` | `_venue.CompleteBarAsync()` — sending bar_done to cBot via NetMQ | < 20ms |
| `journalSteps` | Total `_journal.Append()` calls | 400-450 for 95 bars |
| `totalEngineMs` | evaluate + pump + completeBar | < 1000ms |
| `meanBarMs` | totalEngineMs / bars | < 10ms (good), < 5ms (excellent) |
| `meanJournalStepsPerBar` | journalSteps / bars | 3-5 |

---

## What the numbers mean

### Wall-clock vs engine time

The engine's `totalEngineMs` is CPU time inside the .NET process. The test's wall-clock time includes:
- cTrader CLI downloading/validating credentials (~5s)
- cTrader CLI replaying tick data (~20-30s for 3 days H1)
- cBot processing each bar and executing orders
- Network round-trips (NetMQ over loopback)

Wall-clock is **not** the engine's bottleneck. Engine CPU time is the part we optimize.

### Bottleneck identification

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| `evaluateMs` > 200ms for 95 bars | Indicators recomputing from scratch | F2 (quotes once) already applied; F2 incremental next |
| `pumpMs` > 600ms for 95 bars | Journal serialization on hot thread | F3 already applied; remaining is effect execution |
| `completeBarMs` > 50ms | NetMQ Nagle/delayed-ACK on loopback | F7 TCP_NODELAY |
| `journalSteps` > 500 for 95 bars | Too many kernel events per bar | Normal; depends on strategy activity |
| `meanBarMs` > 15ms | Accumulation of all above | Check breakdown, apply relevant fix |

### Variance between runs

Timing varies ±20% between identical runs because:
- cTrader CLI timing depends on network/market data download
- The cBot runs on cTrader's event thread (not our process)
- The OS scheduler interleaves cTrader CLI and engine processes

Run 2-3 times and use the median or best-of-run for comparison.

---

## Architecture: how timing works

```
KernelBacktestLoop.ProcessBarAsync()  ← Stopwatch wraps
  ├─ PumpAsync (advance drain)        → _timingPumpMs
  ├─ _evaluator.EvaluateAsync()       → _timingEvaluateMs
  ├─ PumpAsync (proposals)            → _timingPumpMs
  ├─ PumpAsync (BarClosed)            → _timingPumpMs
  ├─ PumpAsync (equity)               → _timingPumpMs
  ├─ PumpAsync (trailing)             → _timingPumpMs
  ├─ _venue.CompleteBarAsync()        → _timingCompleteBarMs
  └─ _onBarProcessed (fire-and-forget)

EngineRunner.RunAsync()
  └─ loop.RunFromBrokerAsync()        ← TimingReport set in finally
     └─ FlushTimingReport()           ← writes %TEMP%\shamshir-profiling\{runId}.json
```

`TimingReport` is set in a `finally` block so it survives `OperationCanceledException` (the engine is often stopped before the bar stream completes).

---

## Files involved

| File | Role |
|------|------|
| `src/TradingEngine.Domain/Kernel/TimingReport.cs` | Record holding timing data + computed fields |
| `src/TradingEngine.Host/KernelBacktestLoop.cs` | Stopwatch wraps around each stage |
| `src/TradingEngine.Host/EngineRunner.cs` | FlushTimingReport + WriteTimingReport (profiling file I/O) |
| `tests/.../Harness/CtraderE2EHarness.cs` | Sets DiagnosticsEnabled=true on every EngineHostOptions |

---

## Commands cheat sheet

```powershell
# One smoke test + profile
Remove-Item "$env:TEMP\shamshir-profiling" -Recurse -Force
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~CtraderE2EHarnessSmokeTests.EurUsd_H1_3Days_ProducesTrades_UsingRunAsync"
Get-ChildItem "$env:TEMP\shamshir-profiling" | ForEach-Object { $j = Get-Content $_.FullName | ConvertFrom-Json; Write-Host "bars=$($j.bars) eval=$($j.evaluateMs)ms pump=$($j.pumpMs)ms total=$($j.totalEngineMs)ms meanBar=$([math]::Round($j.meanBarMs,1))ms" }

# Full suite + aggregate
Remove-Item "$env:TEMP\shamshir-profiling" -Recurse -Force
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true&FullyQualifiedName!~NetMQBridge"
Get-ChildItem "$env:TEMP\shamshir-profiling" -Filter "*.json" | ForEach-Object { $j = Get-Content $_.FullName | ConvertFrom-Json; Write-Host "$($_.BaseName.Split('-')[0]): bars=$($j.bars) eval=$($j.evaluateMs)ms pump=$($j.pumpMs)ms total=$($j.totalEngineMs)ms" }
```
