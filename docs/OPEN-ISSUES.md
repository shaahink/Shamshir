# Shamshir — Open Issues

**Updated:** 2026-07-03
**Branch:** `develop` (`d786d3f` — merged from `iter/tape-trust`)
**Gates:** Unit 314/0/6 · Integration 109/0 · Golden 63/63 · build 0 · npm 0

> All prior issues (C1–C14, H1–H30, M1–M21, L1–L4, BUG-09, UNF-01–06, MIN-01–05, OBS-01–03, K-GAP-1–6, T1–T12)
> are resolved. See `docs/RESOLVED-ISSUES.md` for the full audit trail.

---

## CRITICAL — Correctness

### C1 · Short entries miss half-spread cost

**Severity:** Critical. **Status:** Open. **Golden blocks fix.**

| Where | Line | Current (buggy) | Correct |
|-------|------|-----------------|---------|
| `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs` | 262 | `: midPrice` | `: midPrice - halfSpread` |
| `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` | 196 | `: midPrice` | `: midPrice - halfSpread` |

Short trades fill at mid instead of bid — systematic optimistic bias ≈ 0.5 pip per entry. Long entries correctly fill at ask (`mid + halfSpread`). Exit pushes are already correct in both adapters.

**Fix:** change 2 lines. Re-baseline golden snapshot after.

**Reference:** `docs/iterations/iter-tape-trust/HANDOVER.md:101`, `docs/audit/RECONCILE-FINDINGS.md`

---

## MEDIUM — Infrastructure

### D1 · Database fragmentation

**Severity:** Medium. **Status:** Open. **No golden risk.**

Multiple `trading.db` files scattered across:
- `src/TradingEngine.Web/data/trading.db`
- `data/trading.db`
- Test artifacts in random temp folders

**Fix:** single env-var `TRADING_DB_PATH` defaulting to `data/trading.db` relative to working directory. Config-only change.

**Reference:** `docs/NEXT-STEPS.md:86-91`

### D2 · Hardcoded defaults audit

**Severity:** Low. **Status:** Open. **No golden risk.**

Grep for literal defaults (`EURUSD`, `H1`, `10000`) in `src/` and `web-ui/` that should derive from config. Some may be stale from old code paths.

**Fix:** static audit, replace with config-derived values.

**Reference:** `docs/NEXT-STEPS.md:93-99`

### Angular build race

**Severity:** Low. **Status:** Open. **No golden risk.**

`dotnet build` intermittently fails with `StaticWebAssets... No file exists` because `RebuildAngularIfStale` in the Web csproj reruns `ng build`. Workaround: `-p:NgProjectDir=C:/nonexistent-skip` after `npm run build`.

**Fix:** pin `RebuildAngularIfStale` to content-based staleness check.

**Reference:** `docs/iterations/iter-merge-plan/NEXT-ITERATION.md §1.4`

---

## OWNER-ONLY — cTrader CLI required

| # | Item | Reference |
|---|------|-----------|
| **V2** | Download EURUSD H1+M1 for owner's real profile (1–6 months) | `docs/iterations/iter-tape-trust/PLAN.md:70` |
| **V3** | Speed baseline: cTrader path vs tape, same config/window | `docs/iterations/iter-tape-trust/PLAN.md:71` |
| **V4** | Tape vs cTrader reconcile — `LedgerReconciler.Compare`, commit diff | `docs/iterations/iter-tape-trust/PLAN.md:73` |
| **V5** | Engine-DB vs cTrader report comparison | `docs/iterations/iter-tape-trust/PLAN.md:79` |
| **M5** | cTrader trust — oracle set, drift alarm, per-bar recorded spread | `docs/iterations/iter-merge-plan/PLAN.md` |
| **cBot E2E** | `RequiresCTrader=true` E2E suite (cBot rebuilt, `.algo` fresh) | `docs/OPEN-ISSUES.md:596-607` (archived) |

---

## FUTURE — Next Iterations

| # | Item | Source |
|---|------|--------|
| **Q1** | Excursion recorder — per-trade per-exit-TF-bar excursion path (high/low vs entry). Foundation for exit calibration grid. | `docs/QUANT-ROADMAP.md §4` |
| **Q2** | Walk-forward harness — window splitter, per-window config freeze, stitched OOS curve, P(pass) as sweep metric. | `docs/QUANT-ROADMAP.md` |
| **F1** | Portfolio entity — named config with strategy rows + risk budgets. New-Backtest "Run portfolio" one-click. | `docs/iterations/iter-master-plan/PLAN.md` |
| **G1** | Symbol scorecard — per-symbol costPerAtrPct, m1 coverage%, gap frequency. Sortable table in Data Manager. | `docs/iterations/iter-master-plan/PLAN.md` |
| **F5** | Commission half-at-open split — golden re-baseline needed. | `docs/NEXT-STEPS.md:93` |

---

## What to read first (next session)

1. **This file** (`docs/OPEN-ISSUES.md`) — all remaining work
2. **`docs/audit/PROGRESS.md`** — what's been done, gate numbers, branch history
3. **`docs/iterations/iter-merge-plan/NEXT-ITERATION.md`** — session handover with branch decision
4. **`docs/reference/SYSTEM-REFERENCE.md`** — system overview (§1 mandatory)
5. **`AGENTS.md`** — build commands, architecture, current state
