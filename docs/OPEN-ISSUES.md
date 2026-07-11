# Shamshir — Open Issues

**Updated:** 2026-07-10
**Branch:** `iter/parity-pipeline` (active) / `develop` (authoritative)
**Gates:** build 0err/5warn · Unit 716/0/6 · Integration 121/0/0 · Sim-fast 144/0/0 · golden clean

> All prior issues (C1–C14, H1–H30, M1–M21, L1–L4, BUG-09, UNF-01–06, MIN-01–05, OBS-01–03, K-GAP-1–6, T1–T12,
> F1–F16, R1–R10) are resolved or tracked in `docs/resolved/RESOLVED-ISSUES.md`.

---

## RESOLVED

| # | Item | Resolution |
|---|------|------------|
| C1 | Short entries miss half-spread cost | P0.2 full-spread convention (iter/parity-pipeline) |
| D1 | Database fragmentation | P1.1 DbPathResolver — single repo-root DB path |
| Q1 | Excursion recorder | P3.1 ExcursionPathCodec + per-trade MAE/MFE capture |
| Q2 | Walk-forward harness | P3.3 WalkForwardController + background service + SignalR |
| — | Angular build race | ✅ Fixed (MSBuild target + PS 5.1 compat) |
| — | Tape limit expiry dual-res | ✅ Fixed |
| — | Shards pipeline + pending file ingest | ✅ Implemented |
| — | Tape speed control (0-10x + pause) | ✅ Implemented |
| — | Server-side tape data validation | ✅ Implemented |
| — | Data coverage date ranges in UI | ✅ Implemented |
| — | Market data reset in Settings | ✅ Implemented |
| — | Dead ConnectionStrings:Trading config | ✅ Removed |
| — | F1 ¼-sizing | P0.1 — sizes off config balance |
| — | F2 entry latency | P0.4 — entry-latency instrumentation |
| — | F5 fake-failed status | P0.2 — completed-with-warnings status |
| — | F6 vanishing trades | P0.3 — trade-persistence integrity barrier |
| — | F9 config propagation | P1.2 — JSON edits reach runtime DB |
| — | F10 two databases | P1.1 — single DbPathResolver |
| — | F17 kernel event persistence | A1 — event persistence verified |
| — | B1 compare-both dates | Fixed (PropertyNameCaseInsensitive) |
| — | B2 cTrader deadlock | Fixed (channels complete in read-loop finally) |
| — | B3 compare-both recursion | Fixed (CustomParams.Remove + manual state) |

---

## MEDIUM — Infrastructure

### D2 · Hardcoded defaults audit

**Severity:** Low. **Status:** Open.

Grep for literal defaults (`EURUSD`, `H1`, `10000`) in `src/` and `web-ui/` that should derive from config. Some may be stale from old code paths.

---

## OWNER-ONLY — cTrader + Research Ops

| # | Item | Reference |
|---|------|-----------|
| **V2** | Download EURUSD H1+M1 for owner's real profile (1–6 months) | `docs/audit/RECONCILE-FINDINGS.md` |
| **V3** | Speed baseline: cTrader path vs tape, same config/window | `docs/audit/RECONCILE-FINDINGS.md` |
| **V4** | Tape vs cTrader reconcile — `LedgerReconciler.Compare`, commit diff | `docs/audit/RECONCILE-FINDINGS.md` |
| **V5** | Engine-DB vs cTrader report comparison | `docs/audit/RECONCILE-FINDINGS.md` |
| **M5** | cTrader trust — oracle set, drift alarm, per-bar recorded spread | P2.2 headline gate (compare-both) |
| **P2.2** | Headline compare-both gate — owner to review reconcile verdict | `docs/iterations/iter-parity-pipeline/TRACKER.md` |

---

## FUTURE

| # | Item | Source |
|---|------|--------|
| **F1** | Portfolio entity — named config with strategy rows + risk budgets | `docs/iterations/iter-alpha-loop/PLAN.md` |
| **G1** | Symbol scorecard — per-symbol costPerAtrPct, m1 coverage% | `docs/iterations/iter-alpha-loop/PLAN.md` |
| **F5** | Commission half-at-open split — golden re-baseline needed | `docs/iterations/iter-alpha-loop/PLAN.md` |
| **P1** | Sell-limit halfSpread alignment | `docs/audit/LIMIT-ORDER-AUDIT.md` |
| **P3** | Limit-order integration tests | `docs/audit/LIMIT-ORDER-AUDIT.md` |
