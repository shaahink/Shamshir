# Shamshir — Open Issues

**Updated**: 2026-06-18
**Branch**: `iter/31-costs-journal`

Currently open bugs, design problems, and tech debt. Fixed items → `docs/RESOLVED-ISSUES.md`.
Roadmap items → `docs/NEXT-STEPS.md`.

---

## Bugs & correctness

### BUG-09 — Governor cooling-off counter never decrements in production
**Severity**: High — governor cooling-off state once entered persists until daily reset
**Found**: 2026-06-18 (kernel wiring audit)
**Root cause**: `TradingLoop.ProcessBarAsync` calls `signalGate?.OnBar()` which is typed as `ISignalGate`, not `ITradingGovernor`. However `TradingGovernorService.OnBar` is the method that decrements `_coolingOffBarsRemaining`.
**Files**: `TradingLoop.cs:81` calls `signalGate?.OnBar(bar.OpenTimeUtc)`; governor's `OnBar` at `TradingGovernorService.cs:177` is never called from production.
**Impact**: After a trade loss triggers governor cooling-off, the governor stays in that state until the next daily reset (22:00 UTC) regardless of how many bars pass.

### UNF-01 — STD-01: `await Task.CompletedTask` cargo-cult
**Severity**: Low
**Files**: `BarEvaluationHandler.cs`, `BacktestReplayAdapter.cs`, `EngineWorker.cs` (RecomputeIndicatorsAsync, WarmUpIndicatorsAsync)
Methods that don't await anything shouldn't be `async`. Either remove `async` and return `Task.CompletedTask` directly, or use `ValueTask`.

### UNF-02 — STD-02: `double` for price comparison in MeanReversionStrategy
**Severity**: Low — compiles, runs, but violates domain rule
**File**: `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs:55-56`
`Close` and `Low` are `decimal`. Explicit cast to `double` for division. Use `decimal` throughout.

### UNF-03 — STD-04: bare `catch { }` in ResolveHalfSpread
**Severity**: Low
**File**: `src/TradingEngine.Host/EngineWorker.cs:379`
Unknown symbol → spread fallback to `0.00005m` with no log. Should `LogWarning`.

### UNF-04 — STD-05: `IEnumerable<IStrategy>` enumerated multiple times
**Severity**: Low
**File**: `src/TradingEngine.Host/EngineWorker.cs`
`_strategies` is `IEnumerable<IStrategy>`. Called with `.Count()` at startup, iterated per bar, iterated again in warmup. Materialize to `IReadOnlyList<IStrategy>` in constructor.

### UNF-05 — STD-06: `CancellationToken` missing on async methods
**Severity**: Low
**Files**: `EngineWorker.cs` (`RecomputeIndicatorsAsync`, `WarmUpIndicatorsAsync`)
Code standard: CT required on every async method.

### UNF-06 — AGENT-04: `EngineRunContext` in Domain project (wrong layer)
**Severity**: Low
**File**: `TradingEngine.Domain/EngineRunContext.cs`
Pure infrastructure concept. Should be in `TradingEngine.Host` or `TradingEngine.Services`.

---

## Observability gaps

### OBS-01 — No bar flow visibility during backtest
Cannot observe: bars loaded, bars written to channel vs dropped, bars consumed by engine, whether processor is keeping up. Need Debug-level metrics.

### OBS-02 — No signal evaluation visibility
Cannot tell from UI: how many bars evaluated, how many had insufficient warmup, how many had conditions not met, how many signals fired but rejected by risk.

### OBS-03 — No order lifecycle visibility
Between `SIGNAL` and `TRADE_SAVED` there are several failure points (submit, fill/reject, open, close, persist). None surfaced in UI during the run. Only final trade count at end.

---

## Minor

### MIN-01 — `WinRateLast20`/`AvgRLast20` never updated
**File**: `MeanReversionStrategy.cs:88`

### MIN-02 — `SingleReader=true` missing on `BarEvaluationHandler` channel
**File**: `BarEvaluationHandler.cs:14`

### MIN-03 — `WarmUpIndicatorsAsync` is a misleading no-op
**File**: `EngineWorker.cs:366`

### MIN-04 — `BuildBarSnapshot` allocates new `List<Bar>` per timeframe per bar
**File**: `EngineWorker.cs:328`

### MIN-05 — `_processedExecutionIds` HashSet never pruned
**File**: `PositionTracker.cs:19`
Currently uses bounded LRU post-iter-24 fix. Verify this fully resolves the unbounded memory concern.

---

## Carry-forward from iter-31/32

See `docs/iterations/iter-31-32-combined/HANDOVER.md` for full details.

| Phase | What | Priority |
|-------|------|----------|
| **31-A2** | cBot emits `commission`/`swap` in close EXEC frame. `CTraderBrokerAdapter` maps them to `ExecutionEvent`. | Medium |
| **31-A3** | Report shows Commission/Swap/Gross/Net columns. Delete dead `equityDefinition` string. | Medium |
| **31-C2** | Live limit path end-to-end — verify `CTraderBrokerAdapter` limit branch works now that EntryPlanner populates `LimitPrice`. | Medium |
| **32-P4** | Strategy browse/edit UI — replace empty `Strategies.cshtml.cs` with list + detail/edit form. | High |
| **32-P5** | New-Backtest per-run override UI — run plan picker, knob tweaks, effective config preview. | High |
| **31-B2** | Monitor lossless journal — poll journal API instead of 30-item in-memory queue. Remove equity sparkline 500-frame freeze. | Low |
| **31-C3** | Set `mean-reversion.json` `orderEntry.method` → `"LimitOffset"` as worked example. | Low |
| **32-P6** | Wire `JsonExportService` to endpoint. Regenerate `InitialCreate` migration. | Low |
| **31-A4** | (Optional) Commission-aware risk budget — subtract round-turn commission from budget, gated behind config flag. | Optional |

---

## UI & Config flexibility (backlog)

See `docs/NEXT-STEPS.md` for full roadmap.

- **RW-01** — Settings page: view/edit every tunable constant
- **RW-02** — Inherited / layered config for backtests
- **RW-03** — Batch / multi-run backtest runner
- **RW-04** — Auto strategy mode (regime-based / performance-based selection)
- **RW-05** — Global symbol selection, end-to-end
