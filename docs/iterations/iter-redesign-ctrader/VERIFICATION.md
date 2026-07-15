# iter-redesign-ctrader — Pre-fix verification baseline (cTrader path)

**Branch:** `iter/redesign-ctrader` (cut from `iter/redesign`)
**Baseline evidence:** `docs/iterations/iter-redesign/VERIFICATION.md` (Opus review, 2026-06-30)
**Fix plan:** `docs/iterations/iter-redesign-ctrader/PLAN.md`

> The replay path is fixed. The cTrader path carries the identical symptoms the plan sets
> out to kill: open-book leak, all-`FORCE` exits, config divergence, empty equity, stalled
> live monitor. Every check below is regressed by `scripts/verify-ctrader-run.ps1` so the
> owner can verify a single cTrader run without reading code.

---

## 1. Symptom summary (from prior verification)

| # | Symptom | Root cause |
|---|---|---|
| V1 | `openRisk` grows unbounded ($3,221→$60,524); 3mo(6) < 1mo(11) | Engine never closes cTrader positions → book leaks non-terminal positions |
| V2 | All cTrader exits labeled `FORCE` | Double stop-management: engine detects SL/TP AND cTrader owns real stops; venue close reason never threaded to lifecycle |
| V3 | "Raw" run still fires Budget/Exposure limiters | Selecting the raw *risk profile* does not load the raw *prop-firm* toggles; two independent config paths diverge |
| V4 | `EquitySnapshots` table is empty for every run | `EquitySnapshotFlush` only called from `EngineRunner` path; `KernelBacktestLoop` never flushes equity |
| V5 | Live monitor stuck; "completed" while cTrader still running | Finalization on bar-stream end, not venue settlement; orphaned `ctrader-cli` processes |
| V6 | cTrader path never exercised by agent | Only the skipped `RequiresCTrader` test covers cTrader; replay was assumed-equivalent |

## 2. Oracle queries (what verify-ctrader-run.ps1 checks)

### Check 1 — `openRisk` never exceeds bound
```sql
SELECT substr(SimTimeUtc,1,16), DecisionReason
FROM Journal
WHERE RunId = @runId
  AND (DecisionReason LIKE '%openRisk=%' OR DecisionReason LIKE 'Budget%' OR DecisionReason LIKE 'MAX_%')
ORDER BY Seq;
```
**Fail:** `openRisk` > `MaxConcurrentPositions × perTradeWorstCase` at any bar.

### Check 2 — Exit reasons include real SL/TP (not all FORCE)
```sql
SELECT ExitReason, COUNT(*)
FROM TradeResults
WHERE RunId = @runId
GROUP BY ExitReason;
```
**Fail:** all rows are `FORCE` (no `SL`/`TP`/`PARTIAL`).

### Check 3 — 3-month run produces ≥ its trailing 1-month sub-window
```sql
SELECT COUNT(*) FROM TradeResults WHERE RunId = @runId AND ClosedAtUtc >= @subWindowStart;
```
**Fail:** trade count in full window < trade count in sub-window.

### Check 4 — Run completed cleanly
```sql
SELECT ExitCode, CompletedAtUtc FROM BacktestRuns WHERE RunId = @runId;
```
**Fail:** `ExitCode != 0` or `CompletedAtUtc == '0001-01-01 00:00:00'`.

### Check 5 — Equity snapshots exist
```sql
SELECT COUNT(*) FROM EquitySnapshots WHERE RunId = @runId;
```
**Fail:** count = 0.

## 3. Expected post-fix output

| Check | Pre-fix (iter-redesign) | Post-fix (this iteration) |
|---|---|---|
| 1 — openRisk | FAILS (unbounded growth) | PASS (bound ≤ limit) |
| 2 — exit reasons | FAILS (all FORCE) | PASS (SL/TP/PARTIAL present) |
| 3 — 3mo ≥ 1mo | FAILS (6 < 11) | PASS (full ≥ suffix) |
| 4 — run complete | FAILS (CompletedAtUtc = 0001) | PASS (ExitCode=0, wall-clock stamp) |
| 5 — equity snapshots | FAILS (0 rows) | PASS (non-empty, sim-time ordered) |

## 4. Dependencies on decisions

| D | Decision | Consequence for this iteration |
|---|---|---|
| D1 | Replay adapter also owns exits (unify) | P1.4: SL/TP detection moves to `BacktestReplayAdapter`; engine never detects exits for any venue |
| D2 | Allow stopless raw positions + UI warning | P3.2: raw mode makes SL/TP optional (not just strip-addons) |
| D3 | CI oracle: replay automated, cTrader owner-smoke | `verify-ctrader-run.ps1` is the owner's one-command check |
