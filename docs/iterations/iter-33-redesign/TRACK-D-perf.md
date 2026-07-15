# Track D — Backtest Performance

**Worktree:** `git worktree add ../shamshir-perf iter/33-track-d-perf`
**Starts after:** Phase 0 (it needs the typed event/stats backbone and a stable measurement point).
**Coordinate with Track B:** D edits engine/host hot paths (`Host`, `Infrastructure`). To avoid
collisions, **land D's engine-side changes before Track B reorgs those files**, or fold D into B's
sequence. D does *not* touch the Application/API slices.
**Source:** NEXT-STEPS D3.

**Boundary vs P0.5 (read this):** "the engine must not be slowed by DB/IO, but must not lose data" is a
**correctness** property owned by **Phase 0 P0.5** (non-blocking + lossless channel boundary). Track D is
**speed**: how fast the bar loop itself runs (settle delays, indicator recompute, flush throughput, UI
frame caps). D must never trade away P0.5's lossless guarantee for speed — the reconciliation gate stays
green.

**Method:** measure first, change second, measure again. No speculative optimization without a bars/sec
number before and after.

---

## D1 — Establish the measurement

1. Add a bars/sec + wall-time instrument to the replay path (the easiest to profile — credential-free,
   deterministic). Emit it as a typed metric (from P0.4), not a log string.
2. Capture a baseline on a fixed run (e.g. EURUSD H1 over a known range): bars/sec, total wall time,
   and a rough breakdown (data load, indicator compute, strategy eval, persistence flush, settle).

**Gate:** a repeatable baseline number recorded in this file's changelog.

---

## D2 — Remove the obvious waste

1. **The 5-second settle:** `BacktestOrchestrator.RunEngineReplayAsync` has a hard
   `await Task.Delay(5_000)`. Replace with a real completion signal (await the persistence handlers'
   drain / channel completion) so the run ends when work is done, not after a fixed sleep.
2. **Persistence batching:** the per-bar flush handlers (`BarEvaluationHandler`, `PipelineEventWriter`,
   `EquityPersistenceHandler`, `TradePersistenceHandler`) — batch writes and avoid EF change-tracking
   overhead on bulk inserts (`AsNoTracking`/bulk where applicable). Confirm channel modes (analytics =
   DropOldest, trades = Wait) are still honoured.

**Gate:** baseline run wall-time drops by the settle (≥5s) + measurable flush improvement; reconciliation
gate still green (no dropped trades/equity).

---

## D3 — Indicator hot path

1. Profile `IndicatorSnapshotService` / per-bar recompute. Avoid recomputing full indicator series each
   bar where an incremental update suffices; verify cache keys `(symbol, tf, type, period, param)` are
   hit, not missed (tie in IndicatorCacheKey tests).
2. `BuildBarSnapshot` allocates a new `List<Bar>` per timeframe per bar (OPEN-ISSUES MIN-04) — reuse
   buffers.

**Gate:** bars/sec up vs D1 baseline with identical trade output (reconciliation gate green).

---

## D4 — Live feed efficiency (overlaps 31-B2)

1. Replace the 30-item in-memory `RecentJournal` ring with lossless journal-API polling/streaming
   (also a correctness fix — feeds Track A monitor + C1/A7).
2. Remove the 500-frame equity sparkline freeze.

**Gate:** monitor shows lossless journal under a long run; no frame cap.

---

## Track D exit gate

- A documented before/after bars/sec improvement on the fixed baseline run.
- Identical trade/stat output before vs after (reconciliation gate green).
- No regression in Unit/Integration/Simulation(credential-free).
