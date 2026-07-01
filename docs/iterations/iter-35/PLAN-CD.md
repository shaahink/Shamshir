# Iter-35 (cont.) — Part C + D: Surfaces (web · charts · live page · frontend) and Performance

**Status:** PLAN WRITTEN (not executed) — 2026-06-19
**Branch:** `iter/35-kernel`
**Audience:** the implementation agent (OpenCode/DeepSeek).
**Prereq:** **Part A + B are being finished by the owner first.** This plan assumes the kernel is (or is becoming) the production authority and that the **`StepRecord` journal** is the single per-run record. Where a C item depends on that, it is flagged **[needs kernel]**; items not so flagged are independent and can start immediately.
**Read first:** `docs/iterations/iter-35/PLAN.md` §5–6, `PLAN-FINISH-AB.md`, `docs/OPEN-ISSUES.md` (H22–H30, C11–C13, M17–M21, L1–L4), and the web-ui + Web API code named below.

> **Discipline.** Failing-test-first; each phase has a machine-checkable gate. Money math `decimal`; all journal/monitor timestamps = **sim-time**. Don't fabricate data on a surface — if the backing field doesn't exist, add it through the API/DTO, don't hardcode a default. Frontend specs (Vitest/Jasmine) accompany every behavioral change.

---

## Part C — Surfaces

### C1 — Web run lifecycle (no kernel dependency)
**Findings:** `RunsController.Cancel` calls `StopAllAsync()` ignoring `runId` (C12); replay path discards the caller's `CancellationToken` and spins its own 30-min CTS (C11); two controllers share `[Route("api/backtest")]` (C13); pre-run setup runs **before** the try/finally so a throw sticks the run in `"starting"` forever (H22); `StrategyOverrides` never reaches the engine (H24); `BarCount++` races (H25); `_runs`/`_lastSentTicks` never purged (H27).
**Do:**
- Per-run **CTS registry** keyed by `runId`; `Cancel(runId)` cancels only that run (delete `StopAllAsync`). Link the user token into the replay path with the timeout CTS (`CancellationTokenSource.CreateLinkedTokenSource`) — don't discard it.
- Move `ResolveEffectiveConfigJsonAsync`/`WriteStartRecordAsync` **inside** the try; `finally` always completes `_progressStore` and sets a terminal status (no stuck `"starting"`).
- Resolve the `api/backtest` route collision (rename one controller's route).
- `StrategyOverrides` on `StartRunRequest` → `cfg`/run-plan → `EffectiveConfigResolver` → the run's **ConfigSet** (H24/H23). `Interlocked` for `BarCount` (H25). Purge `_runs` + `_lastSentTicks` on run completion (H27).
**Test-first:** cancelling run A leaves run B running; a throw in setup marks the run `failed` (not `starting`); a `StrategyOverrides` value changes the resolved ConfigSet hash.
**Gate:** `grep -n "StopAllAsync" src/.../RunsController.cs` → 0; lifecycle tests green.

### C2 — Per-trade chart (explicit owner ask: "hasn't happened yet")
**Findings:** the pieces exist (`CandleChartComponent`, `BarsController`, `TradeDetailComponent`) but it fails on **NEW-6**: `TradeSummary` has no `timeframe`, so `trade-detail.component.ts:63` always queries H1. No SL/TP markers; trade rows don't link to the detail route; no cost columns (M21).
**Do:**
- Add `timeframe` to the trade DTO **and persist it on the trade** (carry it through `PublishTradeClosed`/`TradeResult`); frontend uses `t.timeframe` (drop `|| 'H1'`).
- Add **SL/TP price lines** + **time-anchored entry/exit markers** on `CandleChartComponent` (`BarResponse.Time` unix-seconds already matches `b.time*1000`).
- Wire trade-list rows → `/trades/:id`. Add cost columns (Gross/Comm/Swap/Net) (M21). Meaningful empty-state when no bars fall in the window.
**Test-first:** a known non-H1 trade renders candles with entry/exit/SL/TP at the right times.
**Gate:** `grep -rn "timeframe || 'H1'" web-ui/src` → 0; trade-detail spec green.

### C3 — Live monitor page (UX the owner specified) **[needs kernel — streams the StepRecord tail]**
**Findings:** journal flickers/resets scroll — `journalEntries.set(mapped.slice(-200))` **replaces** the array (L2); `breachBanner` never clears (L3); the elapsed `setInterval` is never cleared (NEW-7); equity chart double-`setData`/no-op `forEach`/`showBalance` (L1).
**Do:**
- Render an **append-only, `seq`-keyed, virtualized** journal list. Merge incoming `StepRecord`s by `seq` (dedupe); **never** replace the array with a tail slice. **Stick-to-bottom**: auto-scroll to newest only when the user is already near the bottom; otherwise hold `scrollTop` and show a "↓ jump to latest (N new)" affordance.
- Clear `breachBanner` on recovery / non-breach terminal status (L3). Clear the elapsed `setInterval` in `ngOnDestroy` (NEW-7). Fix `EquityChartComponent` (single `setData`, drop the no-op `forEach`, react to `showBalance`) (L1).
- Add a live **open-positions** table + **per-strategy counters**; an optional live **price+entries** mini-chart (reuse `CandleChartComponent` fed from `/api/bars` up to sim-time + entry markers from the stream).
- **Download journal on finish:** "Download journal (NDJSON)" on the report page hitting `/api/runs/{id}/kernel-journal/export`, plus a rendered run summary.
**Test-first:** two consecutive progress frames (2nd shorter) leave the journal monotonically growing and scroll stable; a breach then recovery clears the banner.
**Gate:** `grep -rn "slice(-200)" web-ui/.../run-monitor` → 0; live-monitor specs green.

### C4 — Frontend / strategy data-correctness (no kernel dependency)
**Findings:** MAE/MFE scatter discards `x` (H28); cost reconciliation uses per-term `abs` → false MISMATCH (H29); journal filter has a bogus `'BAR'` kind and is missing real kinds (H30); export CSV returns header only (M20); `RunSummary` omits cost fields (M21); journal API loads the whole set then `.AsEnumerable()` filters in memory (M17).
**Do:**
- `scatter-chart.component.ts`: plot `(d.x, d.y)` = `(MAE, MFE)` (H28). `run-report.component.ts:136`: `Gross - Comm - Swap - Net`, no per-term `abs` (H29). Journal filter: drop `'BAR'`; add `GOVERNOR`, `ENTRY_EXPIRED`, `CANCELLED`, and the new kernel kinds (H30).
- `ExportController`: query trades and emit real rows (M20). Add cost fields to the Angular `RunSummary` interface + the list page (M21).
- `GET /api/runs/{id}/journal` (and the kernel-journal endpoint): **SQL-paged by `seq`/`afterSeq`** — no whole-set `.AsEnumerable()` (M17).
- Strategy nits: `MeanReversion.RequiredBarCount` includes `RsiPeriod`; drop unused BollingerBands; `TrendBreakout` routes SL/TP via `SlTpResolver`/config (H30-strategy); fix the `double` price compare (UNF-02) and `WinRateLast20`/`AvgRLast20` never-updated (MIN-01) if cheap. Wire or hide dashboard placeholders.
**Test-first:** scatter maps both axes; reconciliation shows MATCH on correct costs; export returns > 1 line.
**Gate:** frontend + strategy tests green; export returns more than the header row.

---

## Part D — Performance (validate on the finished kernel)
> A2/A5 already removed the worst hot-path costs (per-strategy `Dictionary` copies, `RemoveAt(0)`, full indicator recompute). Part D **verifies** and adds the rest.
**Findings/Do:**
- Confirm **zero** per-strategy `Dictionary` copies and per-strategy `Sum(Count)` in the hot path (NEW-5); incremental indicators only for **active** strategies; the O(1) ring buffer is in place (A5). Replace fire-and-forget `PublishAsync` exception-swallowing on the hot path with an observed path (M14).
- Journal batched on the lossless channel sized so the producer rarely blocks; apply an indicator-sampling policy so journaling doesn't dominate.
- **DB indices** (regenerate one migration): `TradeResults(RunId)`, `TradeResults(PositionId)`, `EquitySnapshots(RunId,TimestampUtc)`, `Journal(RunId,Seq)`. `GetRecentBars` passes a read-locked view, not a `ToList()` per position per bar (`TradingLoop.GetRecentBars`).
**Test-first:** a benchmark over a fixed N-bar series asserting allocations/bar + wall-time below a recorded baseline; **golden output unchanged**.
**Gate:** benchmark ≥ the target improvement (record before/after in HANDOVER); golden test unchanged; indices present in the migration.

---

## Sequencing
```
C1 web-lifecycle ─┐
C2 trade-chart ───┤ (independent — start now)
C4 frontend ──────┘
C3 live-page ───────[needs kernel StepRecord stream]
                    └──────────────► D perf (after C + the finished kernel)
```
- C1/C2/C4 are independent of the kernel cutover and may start immediately. **C3 depends on the StepRecord journal** being the live source (Part A). D runs last so the benchmark measures the finished engine.

## Definition of Done
- All phase gates green; frontend specs + backend tests green; golden test unchanged.
- The per-trade chart renders entry/exit/SL/TP for any timeframe; the live monitor journal appends without flicker/scroll-reset and offers an NDJSON download; cancel targets a single run; no stuck `"starting"`.
- Cost data is visible on the run list, report, and trade detail; export returns real rows; the journal API is SQL-paged.
- DB indices present; perf benchmark beats the recorded baseline with golden output unchanged.
- `docs/OPEN-ISSUES.md` reconciled (C/D resolved → `RESOLVED-ISSUES.md`); HANDOVER records per-phase deltas + perf before/after.
```
