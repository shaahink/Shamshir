# Shamshir — Open Issues

**Updated:** 2026-07-03
**Branch:** `iter/data-mgmt` (active) / `develop` (authoritative)
**Gates:** Unit 314/0/6 · Integration 94/0 · Golden 63/63 · build 0 · npm 0

> All prior issues (C1–C14, H1–H30, M1–M21, L1–L4, BUG-09, UNF-01–06, MIN-01–05, OBS-01–03, K-GAP-1–6, T1–T12)
> are resolved. See `docs/RESOLVED-ISSUES.md` for the full audit trail.

---

## RESOLVED in `iter/data-mgmt`

| # | Item | Status |
|---|------|--------|
| C1 | Short entries miss half-spread cost | NOT fixed (golden-sensitive) |
| A4.3 | Journal close-fill visibility | ✅ Fixed |
| — | Angular build race | ✅ Fixed (MSBuild target + PS 5.1 compat) |
| — | Tape limit expiry dual-res | ✅ Fixed |
| — | Shards pipeline + pending file ingest | ✅ Implemented |
| — | Tape speed control (0-10x + pause) | ✅ Implemented |
| — | Server-side tape data validation | ✅ Implemented |
| — | Data coverage date ranges in UI | ✅ Implemented |
| — | Market data reset in Settings | ✅ Implemented |
| — | Dead ConnectionStrings:Trading config | ✅ Removed |
| D1 | DB fragmentation | Open |
| D2 | Hardcoded defaults audit | Open |
| — | Limit order consistency audit | Documented at `docs/audit/LIMIT-ORDER-AUDIT.md` |

---

## CRITICAL — Correctness

### C1 · Short entries miss half-spread cost

**Severity:** Critical. **Status:** Open. **Golden blocks fix.**

| Where | Line | Current (buggy) | Correct |
|-------|------|-----------------|---------|
| `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs` | 275 | `: midPrice` | `: midPrice - halfSpread` |
| `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` | 196 | `: midPrice` | `: midPrice - halfSpread` |

Short trades fill at mid instead of bid — systematic optimistic bias ≈ 0.5 pip per entry. Long entries correctly fill at ask (`mid + halfSpread`). Exit pushes are already correct in both adapters.

**Fix:** change 2 lines. Re-baseline golden snapshot after.

---

## MEDIUM — Infrastructure

### D1 · Database fragmentation

**Severity:** Medium. **Status:** Open.

Multiple `trading.db` files scattered across:
- `src/TradingEngine.Web/data/trading.db`
- `data/trading.db`
- Test artifacts in random temp folders

**Fix:** single env-var `TRADING_DB_PATH` defaulting to `data/trading.db` relative to working directory. Config-only change.

### D2 · Hardcoded defaults audit

**Severity:** Low. **Status:** Open.

Grep for literal defaults (`EURUSD`, `H1`, `10000`) in `src/` and `web-ui/` that should derive from config. Some may be stale from old code paths.

---

## OWNER-ONLY — cTrader CLI required

| # | Item | Reference |
|---|------|-----------|
| **V2** | Download EURUSD H1+M1 for owner's real profile (1–6 months) | `docs/iterations/iter-tape-trust/PLAN.md:70` |
| **V3** | Speed baseline: cTrader path vs tape, same config/window | `docs/iterations/iter-tape-trust/PLAN.md:71` |
| **V4** | Tape vs cTrader reconcile — `LedgerReconciler.Compare`, commit diff | `docs/iterations/iter-tape-trust/PLAN.md:73` |
| **V5** | Engine-DB vs cTrader report comparison | `docs/iterations/iter-tape-trust/PLAN.md:79` |
| **M5** | cTrader trust — oracle set, drift alarm, per-bar recorded spread | `docs/iterations/iter-merge-plan/PLAN.md` |
| **cBot E2E** | `RequiresCTrader=true` E2E suite (cBot rebuilt, `.algo` fresh) | Previous OPEN-ISSUES.md |

---

## FUTURE — Next Iterations

| # | Item | Source |
|---|------|--------|
| **Q1** | Excursion recorder — per-trade per-exit-TF-bar excursion path | `docs/QUANT-ROADMAP.md §4` |
| **Q2** | Walk-forward harness — window splitter, per-window config freeze | `docs/QUANT-ROADMAP.md` |
| **F1** | Portfolio entity — named config with strategy rows + risk budgets | `docs/iterations/iter-master-plan/PLAN.md` |
| **G1** | Symbol scorecard — per-symbol costPerAtrPct, m1 coverage% | `docs/iterations/iter-master-plan/PLAN.md` |
| **F5** | Commission half-at-open split — golden re-baseline needed | `docs/NEXT-STEPS.md:93` |
| **P1** | Sell-limit halfSpread alignment | `docs/audit/LIMIT-ORDER-AUDIT.md` |
| **P3** | Limit-order integration tests | `docs/audit/LIMIT-ORDER-AUDIT.md` |
