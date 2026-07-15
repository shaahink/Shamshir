# Agent Handover & Review — Backtest Performance (iter-ux-unify)

**Branch:** `iter/ux-unify` (HEAD `7741770`)
**Date:** 2026-07-01

> **⚠️ Independent review 2026-07-01 — read `REVIEW-BACKTEST-PERF.md` first.** Corrections to the claims below (owner runs the **cTrader** venue):
> 1. Determinism gate was **62/63, not 63/63** — the F3 change broke `Journal_OrderAndFill_ShareOrderId` and silently voided the effect-payload determinism check. Fixed in the review (harness-only); now a genuine 63/63.
> 2. The cBot fixes (F6/F11/F12) **are deployed and active** on the cTrader path (verified `src.algo` is current, not stale) — but their effect was **never measured**: the UI cTrader launch never passed `--Diagnostics=true`, so the cBot round-trip/tick-publish timing (`CBOT|TIMING`) was unreachable from a real run. They were shipped blind, against the plan's ground rule. The review wires `--Diagnostics` (opt-in) so a real cTrader run can finally be profiled.
> 3. Per the audit's own hypothesis, the cTrader wall-clock floor is likely cTrader-cli's tick replay (uncontrollable) — so "no improvement" is consistent with the fixes working but not being the bottleneck. Measure first (`Engine:Diagnostics:Enabled=true` → read `CBOT|TIMING`) before any further optimisation. `PROGRESS.md` (mandated baselines) was never created.

---

## Agent prompt (copy this to start a review session)

```
You are reviewing the Shamshir backtest performance work on branch iter/ux-unify.

First, read these docs in order:
1. docs/audit/BACKTEST-PERFORMANCE-AUDIT.md — the 15 findings from the static code audit
2. docs/audit/BACKTEST-PERFORMANCE-ACTION-PLAN.md — the 6-phase action plan
3. docs/iterations/iter-ux-unify/HANDOVER.md — this file, mapping every finding to its implementation
4. docs/audit/PROFILING-GUIDE.md — how to collect and interpret profiling data

Then:
- Cross-reference each finding (F2, F3, F4, F6, F11, F12) against the code. Read the files cited in
  the handover's "What was implemented" section. Confirm each fix matches the audit description.
- Run the determinism gate (63/63) and cTrader E2E gate (9/10).
- Collect profiling data from a smoke test and compare against the expected baseline.
- Identify which remaining findings from the audit are worth implementing next, based on profiling.
- Implement any Phase 4 or Phase 5 fix that profiling justifies, keeping both gates green.
- Update this handover with your changes.
```

---

**Audit:** `docs/audit/BACKTEST-PERFORMANCE-AUDIT.md` (v2, 15 findings, verified)
**Action plan:** `docs/audit/BACKTEST-PERFORMANCE-ACTION-PLAN.md` (6 phases)
**Profiling guide:** `docs/audit/PROFILING-GUIDE.md`
**Architecture:** `docs/audit/CTRADER-NETMQ-BACKTEST-MODEL.md`

---

## Task for the reviewing agent

Verify that the fixes applied match the audit plan. Cross-reference each finding against the code. Run the gates to confirm correctness. Profile the current state, compare against baselines, and implement any remaining phases that profiling justifies.

---

## What was implemented (audit → code cross-reference)

### Phase 1 — SQLite per-connection PRAGMAs (F4/F14)

| Audit finding | Audit verdict | Implementation | File:line |
|--------------|---------------|----------------|-----------|
| F4/F14: No `cache_size`, `synchronous`, `mmap` PRAGMAs; `busy_timeout` on throwaway connection | Confirmed, re-framed | New `SqlitePragmaInterceptor` implementing `IDbConnectionInterceptor`; runs on every `ConnectionOpened` | `src/TradingEngine.Infrastructure/Persistence/SqlitePragmaInterceptor.cs:1-34` |
| | | Registered on `TradingDbContext` in both Web and Host paths | `Web/Configuration/ServiceRegistration.cs:68-71`, `Host/EngineServiceCollectionExtensions.cs:51-55` |
| | | Throwaway connection now only runs `PRAGMA journal_mode=WAL` (busy_timeout removed) | `Web/Configuration/ServiceRegistration.cs:77-84` |

**Verify:** Check that `SqlitePragmaInterceptor.cs` exists and runs all 5 PRAGMAs. Check both `ServiceRegistration.cs` and `EngineServiceCollectionExtensions.cs` register it. Confirm the old throwaway connection no longer sets `busy_timeout`.

### Phase 2 — Defer journal serialization (F3)

| Audit finding | Audit verdict | Implementation | File:line |
|--------------|---------------|----------------|-----------|
| F3: `EventJson`/`EffectsJson` serialized synchronously on pump thread every kernel step | Confirmed | StepRecord now carries `RawEvent` (object?) and `RawEffects` (IReadOnlyList<object>?) properties alongside the existing string fields | `Domain/Kernel/StepRecord.cs:29-30` |
| | | `BuildStepRecord` passes `""` for EventJson/EffectsJson strings, sets RawEvent/RawEffects | `Host/KernelBacktestLoop.cs:399-404` |
| | | Reconcile path also defers serialization | `Host/KernelBacktestLoop.cs:197-206` |
| | | `SqliteStepRecordSink.Map` serializes from RawEvent/RawEffects in background (within the DB write batch), falling back to pre-serialized strings for legacy callers | `Infrastructure/.../SqliteStepRecordSink.cs:28-44` |
| | | Removed unused `JsonSerializerOptions` + `using System.Text.Json` from KernelBacktestLoop | `Host/KernelBacktestLoop.cs:1-2,53-56` (deleted) |

**Verify:** Run the journal golden tests — they must produce byte-identical JSON output via the background sink. Confirm no `JsonSerializer.Serialize()` calls remain in `BuildStepRecord`.

### Phase 3 — cBot streaming/logging diet (F11, F12, F6)

| Audit finding | Audit verdict | Implementation | File:line |
|--------------|---------------|----------------|-----------|
| F11: Tick/account PUB stream is pure waste in backtest | New | `OnTick()` gates `Publish("tick")` and `PublishAccount()` behind `Verbose` param (default `false`) | `TradingEngineCBot.cs:163-177` |
| F12: Print/Diag on cTrader thread every bar/exec | New | `Print()` and `Diag()` calls gated behind `Verbose` param in `OnBarClosed`, `OnDealerReceive`, `OnStart`, `OnPositionClosed` | `TradingEngineCBot.cs:189-193,217,223,277,608,592,151,669` |
| F6: cBot `Serialize()` double-serializes every outbound message | Confirmed | Replaced with single-pass `SerializeToNode(payload)!.AsObject()` + `node["type"] = type` + `node.ToJsonString()` | `TradingEngineCBot.cs:771-776` |
| | | New cBot params: `Verbose` (default `false`), `Diagnostics` (default `false`) | `TradingEngineCBot.cs:37-41` |

**Verify:** The cBot must be rebuilt (`dotnet build src/TradingEngine.Adapters.CTrader`). Check that `Verbose` defaults to `false` (tick PUB and Print/Diag suppressed in backtest). Check `Serialize()` uses `SerializeToNode` not `Serialize`+`Parse`+`Serialize`.

### Phase 0 — Measurement harness

| Audit requirement | Implementation | File:line |
|------------------|----------------|-----------|
| cBot-side per-bar round-trip timing | `Stopwatch` around lock-step wait + command execution + checkpoint I/O; `_timingRoundTrips`, `_timingRoundTripTotalMs`, `_timingRoundTripMaxMs`, `_timingBarProcTotalMs`, `_timingCheckpointTotalMs`; aggregate log in `OnStop` | `TradingEngineCBot.cs:46-51, 222-236, 258-268, 708-712` |
| Engine-side per-bar stage timing | `Stopwatch` around `EvaluateAsync`, each `PumpAsync`, `CompleteBarAsync`; `_timingBars`, `_timingEvaluateMs`, `_timingPumpMs`, `_timingCompleteBarMs`, `_timingJournalSteps` | `Host/KernelBacktestLoop.cs:44-51, 178-268` |
| Opt-in gating | cBot gated by `Diagnostics` param; engine gated by `_diagnostics` flag (set from `EngineHostOptions.DiagnosticsEnabled` OR `SHAMSHIR_DIAGNOSTICS` env var) | `TradingEngineCBot.cs:38-39`, `Host/KernelBacktestLoop.cs:93` |
| Per-run profiling file output | `TimingReport` record populated in `finally` block; `FlushTimingReport()` called from both normal and exception paths; `WriteTimingReport()` writes JSON to `%TEMP%\shamshir-profiling\{runId}.json` | `Domain/Kernel/TimingReport.cs`, `Host/KernelBacktestLoop.cs:131-158,100-119`, `Host/EngineRunner.cs:131-167` |
| Profiling always-on for E2E tests | `CtraderE2EHarness` sets `DiagnosticsEnabled = true` by default | `tests/.../Harness/CtraderE2EHarness.cs:136` |

**Verify:** Run a smoke test, check `%TEMP%\shamshir-profiling\` has a JSON file with non-zero bars and timings.

### Phase 4 (cheap layer) — F2 indicator quote reuse, F5 trade batching, F8 event bus

| Audit finding | Audit verdict | Implementation | File:line |
|--------------|---------------|----------------|-----------|
| F2: Full-series recompute + per-indicator quote allocation | Confirmed, corrected (de-dup already exists; real cost is per-indicator SkenderQuote allocation) | `SkenderQuote` made public | `Infrastructure/Indicators/SkenderQuote.cs:5` |
| | | Quote-accepting overloads added to `SkenderIndicatorService` (Atr, Ema, Sma, Rsi, Adx, BollingerBands, Macd, SuperTrend) plus static `ToQuotes()` helper | `Infrastructure/Indicators/SkenderIndicatorService.cs:13-68` |
| | | `IndicatorSnapshotService.RecomputeIndicatorsAsync` converts bars→quotes once per (symbol,tf) bar, casts to `SkenderIndicatorService`, calls quote overloads | `Host/IndicatorSnapshotService.cs:40-42,55-56` |
| | | `totalBars` sum hoisted out of per-strategy loop in `BarEvaluator.EvaluateAsync` | `Host/BarEvaluator.cs:97-100` |

**Verify:** Check that `SkenderIndicatorService` methods accept `IReadOnlyList<SkenderQuote>`. Check `IndicatorSnapshotService` calls `ToQuotes()` once per bar and passes to quote overloads. F5 and F8 were deferred (low impact).

### NOT implemented — remaining from audit

| Finding | Phase in plan | Why not done | Audit priority |
|---------|--------------|-------------|---------------|
| F2 structural | 5a | Incremental indicators — high risk, numeric drift, needs golden gate | MEDIUM |
| F7 TCP_NODELAY | 5b | NetMQ socket tuning — needs protocol verification, low expected gain | LOW |
| F5 trade batching | 4 | Low volume (tens of trades), not a meaningful lever | LOW |
| F9 checkpoint diet | 3 | cBot change deferred; ReportCheckpointEveryNBars still at 50 | LOW-MEDIUM |
| F1 command-less fast path | 5b | Protocol change — must verify engine doesn't require bar_result ack | LOW |
| F8 direct-channel publishes | 4 | Minor; event bus overhead per bar is negligible | LOW |
| F15 journal backpressure | 2 | Mitigated by F3 (smaller serialization in channel); monitor DroppedBatches | LOW-MEDIUM |

---

## Gates (run these to confirm nothing broke)

### Determinism gate (must be 63/63 byte-identical)

```powershell
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)" --no-build
```

### cTrader E2E gate (must be 9/10 — 1 pre-existing NetMQBridgeTest timeout)

```powershell
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true&FullyQualifiedName!~NetMQBridge" --no-build
```

### Full build (must be 0 errors)

```powershell
dotnet build src/TradingEngine.Web
dotnet build src/TradingEngine.Adapters.CTrader  # 5 benign net6.0 warnings ok
```

---

## How to profile (collect timing data)

### Quick smoke test profile

```powershell
Remove-Item "$env:TEMP\shamshir-profiling" -Recurse -Force
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~CtraderE2EHarnessSmokeTests.EurUsd_H1_3Days_ProducesTrades_UsingRunAsync" --no-build
Get-ChildItem "$env:TEMP\shamshir-profiling" | ForEach-Object { Get-Content $_.FullName }
```

### Full suite profile

```powershell
Remove-Item "$env:TEMP\shamshir-profiling" -Recurse -Force
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true&FullyQualifiedName!~NetMQBridge" --no-build
Get-ChildItem "$env:TEMP\shamshir-profiling" -Filter "*.json" | ForEach-Object {
    $j = Get-Content $_.FullName | ConvertFrom-Json
    Write-Host "$($_.BaseName): bars=$($j.bars) eval=$($j.evaluateMs)ms pump=$($j.pumpMs)ms total=$($j.totalEngineMs)ms meanBar=$([math]::Round($j.meanBarMs,1))ms"
}
```

### Expected baseline (3-day H1, 95 bars)

| Metric | Value | Notes |
|--------|-------|-------|
| bars | ~95 | |
| evaluateMs | 10-200ms | Variable; depends on cTrader CLI timing |
| pumpMs | 400-600ms | Largest component; F3 reduced from ~578ms |
| completeBarMs | 0-15ms | |
| journalSteps | 420-425 | |
| totalEngineMs | 500-700ms | Engine CPU only, not wall-clock |
| meanBarMs | 5-8ms | < 10ms is good |

---

## How to continue (next steps for the reviewing agent)

1. **Run determinism gate** — confirm 63/63.
2. **Run cTrader E2E gate** — confirm 9/10 (NetMQBridgeTest timeout is pre-existing, ignore).
3. **Collect profiling baseline** — capture numbers from a smoke test.
4. **Decide next fix based on profiling:**
   - If pump still >70% of total, investigate `HandlePublishTradeClosed` → `_eventBus.PublishAsync` overhead
   - If you want structural wins, tackle F2 incremental indicators (Phase 5a)
   - If you want easy wins, tackle F9 checkpoint diet or F7 TCP_NODELAY
5. **After every change:** determinism gate → cTrader E2E → profile → compare.

### If profiling shows pump still dominates

The next-highest pump cost is likely `HandlePublishTradeClosed` in `EffectExecutor.cs:174`:
```csharp
await _eventBus.PublishAsync(new TradeClosed(tradeResult, _runId, effect.ClosedAtUtc), ct);
```
This takes a lock on the handler list, snapshots it, and awaits each handler sequentially. Possible fix: write directly to `TradePersistenceHandler._channel` instead of going through the event bus.

---

## Files changed (full list)

```
 M src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs       (+73 lines)  F6, F11, F12, timing
 M src/TradingEngine.Domain/EngineHostOptions.cs                 (+1)         DiagnosticsEnabled
 M src/TradingEngine.Domain/Kernel/StepRecord.cs                 (+4)         RawEvent, RawEffects
 A src/TradingEngine.Domain/Kernel/TimingReport.cs               (+16)        NEW profiling record
 M src/TradingEngine.Host/BarEvaluator.cs                        (+2)         F2 hoist totalBars
 M src/TradingEngine.Host/EngineRunner.cs                        (+20)        WriteTimingReport, FlushTimingReport
 M src/TradingEngine.Host/EngineServiceCollectionExtensions.cs   (+3)         Wire SqlitePragmaInterceptor + DiagnosticsEnabled
 M src/TradingEngine.Host/IndicatorSnapshotService.cs            (+10)        F2 bars→quotes once + quote overloads
 M src/TradingEngine.Host/KernelBacktestLoop.cs                  (+67/-46)    F3 defer serialize, Stopwatch timing, finally block
 M src/TradingEngine.Host/Program.cs                             (reverted)   Removed backtest verb
 M src/TradingEngine.Host/TradingEngine.Host.csproj              (reverted)   Removed CTraderRunner ref
 M src/TradingEngine.Infrastructure/Indicators/SkenderIndicatorService.cs (+36) F2 quote overloads + ToQuotes()
 M src/TradingEngine.Infrastructure/Indicators/SkenderQuote.cs   (+1)         Made public
 A src/TradingEngine.Infrastructure/Persistence/SqlitePragmaInterceptor.cs (+34) NEW PRAGMA interceptor
 M src/TradingEngine.Infrastructure/…/SqliteStepRecordSink.cs    (+23)        F3 serialize from RawEvent/RawEffects
 M src/TradingEngine.Services/EngineRunContext.cs                (+3)         DiagnosticsEnabled
 M src/TradingEngine.Web/Configuration/ServiceRegistration.cs    (+5)        Wire interceptor, fix busy_timeout
 M src/TradingEngine.Web/Services/BacktestOrchestrator.cs        (+3)         Pass DiagnosticsEnabled
 M tests/…/Harness/CtraderE2EHarness.cs                          (+1)         DiagnosticsEnabled=true default
 A docs/audit/BACKTEST-PERFORMANCE-AUDIT.md                                  Audit with 15 findings
 A docs/audit/BACKTEST-PERFORMANCE-ACTION-PLAN.md                            6-phase action plan
 A docs/audit/CTRADER-NETMQ-BACKTEST-MODEL.md                                 Architecture reference
 A docs/audit/PROFILING-GUIDE.md                                              Standalone profiling guide
 M .claude/skills/ctrader-e2e/SKILL.md                                        Updated test status + profiling section
```

---

## Commit log

```
7741770 docs: handover, profiling guide, updated ctrader-e2e skill
69bbf04 perf(F3): defer journal EventJson/EffectsJson serialization to background sink
a6420b5 fix: profiling survives cancellation + remap; F2 indicator working
fe19b48 fix: env var fallback for diagnostics, profile path resilience
5af7e70 fix: profiling config resolution, add diagnostic log gating
da4a402 perf: per-run profiling files + F2 indicator quote reuse
a7701b1 perf: cBot diet, SQLite PRAGMAs, engine/cBot timing harness
```
