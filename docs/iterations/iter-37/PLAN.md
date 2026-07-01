# Iter-37 — Frontend Finish on the Real Kernel (journal · replay/duplicate · report · monitor · trades · settings)

**Status:** PLAN WRITTEN (not executed) — 2026-06-20 (rev-2; now sits on the iter-36 kernel cutover)
**Branch base:** `iter/36-kernel-cutover` (after iter-36 lands) → cut `iter/37-frontend-finish`
**Audience:** the implementation agent (OpenCode/DeepSeek).
**Prereq — iter-36 done:** the backtest runs **through `KernelDriver`**, `EngineState` is the single authority, there is **one lossless `StepRecord` journal** (subsumes per-bar evaluations, carries `orderId`/`violations[]`/costs/per-strategy verdicts), **real replay/duplicate** runs through `BacktestReplayAdapter`, and `Run = (Dataset, ConfigSet, Seed)` is persisted with `ParentRunId` lineage. This iteration surfaces all of that and bashes the remaining Angular gaps so the owner can do **proper testing**.

> **Goal (owner's words):** *"a final iteration can bash all front end missings before I do proper testing."* No backend authority changes — only surfaces and the thin DTO/endpoint fills they need over **data the kernel now produces**. Never fabricate on a surface; if a field is missing, add it through the API/DTO.

---

## 0. Ground truth
Components exist: `dashboard`, `events`, `compliance`, `runs/{run-list,new-backtest,run-monitor,run-report,run-analyzer}`, `trades/{trade-list,trade-detail}`, `strategies/{strategy-list,strategy-detail}`, `risk-profiles`, `prop-firm-rules`, `governor`, `settings`; services `runs.service.ts`, `core/signalr/run-hub.service.ts`. Data now available: the **StepRecord journal** (`GET /api/runs/{id}/journal`, SQL-paged, structured), its **NDJSON export**, `Trades`, `EquitySnapshots`, dataset/config/lineage on each run, and the `POST /api/runs/{id}/duplicate` endpoint.

## 1. Discipline
Failing-test-first (Jasmine/Vitest) per phase; machine-checkable gate. Sim-time on every displayed timestamp; preserve numeric precision (don't round before display). `shamshir-ui` (Playwright, extend the 13 checks) is the smoke loop. Run a real seed-bar backtest and inspect the data before building a surface on it.

## 2. Part F — Surfaces

### F1 — Unified journal view (on the StepRecord journal)
**Do:** render the journal off the StepRecord endpoint as an **append-only, `seq`-keyed** list. **Join ORDER + its FILL/EXPIRY by `orderId`** into one row. Render `violations` as readable names (no `[object Object]`). Show commission/swap/net on CLOSE rows. Fix the filter (H30): drop the bogus `'BAR'` kind; add `GOVERNOR`/`ENTRY_EXPIRED`/`CANCELLED`/`BREACH`/`TRAIL`/`BREAKEVEN`/`PARTIAL`. Label each row with strategy+symbol (resolve the "signal flooding" question — multi-strategy/multi-symbol vs dedupe).
**Test-first:** an ORDER+FILL with the same `orderId` collapse to one row; a REJECTED row shows named violations.
**Gate:** journal spec green; no `[object Object]` rendered.

### F2 — Per-bar "why" panel + rejection funnel
**Do:** new tab/section reading the per-strategy verdicts now carried on `BarClosed` StepRecords (a `bar-evaluations` projection endpoint over the journal, or a funnel endpoint): a paginated per-bar table (`simTime, symbol, strategy, hadSignal, direction, reason, key indicators`) + a rejection-reason histogram. The owner's top diagnostic ("at bar X, strategy Y had no signal because RSI=…").
**Test-first:** funnel totals equal the run's bar count; filtering by strategy narrows rows.
**Gate:** per-bar spec green.

### F3 — Download journal (NDJSON) + replay/duplicate UI + lineage
**Do:** "Download journal (NDJSON)" button on the report page (now returns real data). A **"Duplicate with changes"** action on a finished run → opens `new-backtest` prefilled with the source dataset (symbols/period/range/balance) + config, lets the user change strategy set / risk profile / overrides, launches via `POST /api/runs/{id}/duplicate`. Show **run lineage** (duplicate of run X) + the dataset/config short-hashes ("same data, different config") on the report.
**Test-first:** the duplicate form posts the source `runId` + changed fields; the new run appears in the list with a parent link; the download yields a non-empty file.
**Gate:** duplicate-flow + download specs green (E2E: a duplicate run over the same dataset with a different strategy appears listed).

### F4 — Run report: one-page, LLM-readable narrative (NEXT-STEPS A1/A2)
**Do:** complete `run-report.component.ts`: header (symbols/TFs/period/strategies + effective params/risk profile/prop-firm ruleset/balance + dataset/config hashes + lineage), final stats (net/gross, commission/swap totals, win rate, profit factor, max DD, R distribution, per-strategy breakdown, breach timeline), deep-link into F2's funnel. Fix **H29** (`Gross − Comm − Swap − Net`, no per-term `abs`) and **H28** (MAE/MFE scatter plots `(x,y)`). "Export report (Markdown/JSON)" (server renders from the same data).
**Test-first:** report computes profit factor and `net == gross − comm − swap` on a fixture; scatter maps both axes.
**Gate:** report spec green; renders all sections for a seed run with no `NaN`/`undefined`.

### F5 — Live monitor: no flicker, stick-to-bottom, recovery (L1/L2/L3/NEW-7)
**Do (off `run-hub.service.ts` + the StepRecord tail):** render the live journal **append-only, `seq`-keyed, virtualized** — **merge** by `seq`, never `set(slice(-200))` (L2). **Stick-to-bottom**: auto-scroll only when already near bottom; else hold `scrollTop` + show "↓ jump to latest (N new)". Clear `breachBanner` on recovery / non-breach terminal status (L3). Clear the elapsed `setInterval` in `ngOnDestroy` (NEW-7). Fix `EquityChartComponent`: single `setData`, drop the no-op `forEach`, react to `showBalance` (L1). Add a live **open-positions table** (from `EngineState`/journal) + **per-strategy counters**.
**Test-first:** two consecutive progress frames (2nd shorter) leave the journal monotonically growing and scroll stable; breach→recovery clears the banner.
**Gate:** `grep -rn "slice(-200)" web-ui/.../run-monitor` → 0; live-monitor specs green.

### F6 — Per-trade chart: entry/exit/SL/TP for any timeframe (C2 / NEW-6)
**Do:** verify the trade DTO carries `timeframe` + costs (add through the API if missing). `trade-detail`: use `t.timeframe` (drop `|| 'H1'`); SL/TP price lines + time-anchored entry/exit markers on `CandleChartComponent`; trade-list rows link to `/trades/:id` with cost columns (M21); meaningful empty-state.
**Test-first:** a non-H1 trade renders candles with entry/exit/SL/TP at the right times.
**Gate:** `grep -rn "timeframe || 'H1'" web-ui/src` → 0; trade-detail spec green.

### F7 — Settings: toggles + validation + export (B1 feature finish, E3)
**Do:** finish `settings` + `prop-firm-rules` + `risk-profiles` + `governor` edit pages over `GET/PUT /api/prop-firms` / `/api/risk-profiles` / governor, bound to the 9 `ProtectionToggles` + limits; **validate-before-save** via a `POST .../validate` endpoint (block save on errors). DB is source of truth. `POST /api/config/export` (DB→`config/**.json`) wired to a button. **No dead toggles** (wire the consumer or hide). Fix **M18** (read governor from DB) + **M19** (no bare `catch{}`).
**Test-first:** an invalid profile is blocked with a field error; a valid `PUT` round-trips and a later run reflects the new limit (assert via the journal risk snapshot — now real, the kernel is in the run path).
**Gate:** settings spec green; no toggle without a backing consumer.

### F8 — New-backtest + dashboard + export hygiene (E2, M20)
**Do:** `new-backtest`: per-strategy overrides + resolved-config preview before launch. Dashboard: wire or hide every placeholder (no fake numbers). `ExportController` CSV emits real rows (**M20**). Cost-inclusive KPIs on the run list (M21).
**Gate:** `export` returns > 1 line; new-backtest spec green; no fabricated placeholder.

## 3. Sequencing
```
F1 journal ─► F2 per-bar-why ─► F3 download+duplicate   (the kernel-journal surfaces — start here)
F4 report ─┐
F6 trade ──┤ (independent)
F7 settings┤
F8 new-bt ─┘
F5 live-monitor ── (StepRecord tail)
```
F1–F3 first (they exercise the new kernel journal). The rest parallelize.

## 4. Definition of Done
- Every run is **fully explainable and testable in the UI**: unified journal (orders+fills joined, named violations, costs); per-bar "why" funnel; NDJSON download; **duplicate-with-a-different-strategy** with lineage; report narrative + stats; flicker-free recovering live monitor; per-trade SL/TP chart any timeframe; validated settings (DB source of truth, no dead toggles); real CSV export; no fabricated placeholders.
- All frontend specs + backend/unit/golden/arch/determinism suites green; `shamshir-ui` E2E extended and green.
- `OPEN-ISSUES.md` reconciled (UI items → `RESOLVED-ISSUES.md`); HANDOVER records per-phase deltas.
- The owner can do **proper end-to-end testing**.

## 5. Risks
- **Don't fabricate** — verify each field against a real seed-run before building on it (the iter-25/28 regression).
- **Scope:** surfaces + thin DTO fills only; no backend authority changes.
- **Out of scope:** new strategies; live cTrader e2e; performance/indicators.
