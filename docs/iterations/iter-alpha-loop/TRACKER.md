# Shamshir — iter-alpha-loop Tracker (resume here)

**This is the machine-readable progress source.** The narrative docs
(`PLAN.md`) stay the human authority — this file is the strict checkpoint table +
handoff.

**Read order for a fresh session:** this file → `PLAN.md` → `LEDGER.md` →
`../../../AGENTS.md` → `../../../conductor-DEBT.md` → `../../reference/SYSTEM-REFERENCE.md`
(+ CODE-MAP, BACKTEST-ARCHITECTURE, TEST-ARCHITECTURE) → `../../../DECISIONS.md`.
**Branch:** `iter/alpha-loop`.

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **R2 COMPLETE** — parity guard executed (4 iterations). v4: 2-month windows on clean (XAUUSD-tb) and
  problematic (USDCAD-tb) cells. XAUUSD: 14v15 (+7.1%). USDCAD: 13v13 (0% — exact match).
  The 33% divergence from v3 was a short-window artefact — converges to 0% on 2 months.
  Divergence investigation document: docs/iterations/iter-alpha-loop/R2-DIVERGENCE-INVESTIGATION.md.
  F22 (H4 sparse-window), F23 (F2 cascading) filed. Old F6 regression is DEAD (0/26 pairs show tape overcount).
  Owner gate: agent recommends PROCEED to R3 — divergence is F2 (known, small, window-dependent).
stage: **R2 — Parity guard — DONE**
gate: build 0err/5warn · Unit 716/0/6 · Integration 121/0/0 · Sim-fast 144/0/0 · golden clean · parity PASS (13:13 on 2m)
next: **R3 — Refinement loop** (owner decision — investigation doc at R2-DIVERGENCE-INVESTIGATION.md)

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = an artifact path produced by a run this
phase (a code path is not evidence).

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| R0.0 | Session setup — housekeeping, AGENTS.md, TRACKER.md, ctrader-quickstart F21, PLAN.md fixes | DONE | — | AGENTS.md + ctrader-quickstart.md updated; TRACKER.md + LEDGER.md created |
| R0.1a | F20 — CTraderListenService.cs → DbPathResolver | DONE | — | CTraderListenService.cs:105 now uses `DbPathResolver.ResolveTradingDbPath()` |
| R0.1b | F21 — GET /api/system/health endpoint + doc fixes | DONE | — | SystemController.health returns `{status, dbPath, version}`; quickstart port →5134, kill-by-PID |
| R0.1c | F19 — barrier false-positive: scope to ctrader venue only | DONE | — | BacktestOrchestrator.cs:522 barriers only on venue=ctrader |
| R0.1d | F18 — compare-both child spawn: write start record at spawn, keep in _runs | DONE | — | RunCompareBothAsync writes WriteStartRecordAsync immediately; _runs.TryRemove removed from finally |
| R0.2a | research score verb + scoreboard + API | DONE | — | SetupScoreService + POST /api/experiments/score + GET /api/experiments/{id}/scoreboard + CLI verbs |
| R0.2b | research doctor verb + API | DONE | — | GET /api/system/doctor + `research doctor` CLI verb |
| R0.2c | DataQuality market-hours aware | DONE (VERIFIED-EXISTING) | — | SqliteMarketDataStore.StraddlesWeekend already filters FX weekend gaps since P6.1 |
| R0.truth | Truth gate: live verify tape run validate --forbid-warnings | DONE | — | Tape run f353fbd7: status=completed, exitCode=0, warnings=null, 5 trades. Doctor: PASS 0 issues. Health: PASS. Score: PASS on 2c9551d1 (28td, composite=68.5). All endpoints live-verified. |
| R0.gates | Gate battery: build + Unit + Integration + Sim-fast + golden | DONE (RE-VERIFIED) | — | 0err/5warn · 716/0/6 · 121/0/0 · 144/0/0 · golden 61/0/0 |
| R0.qa | QA session: static analysis + fix + refactor | DONE | — | 6 findings (SA-1 through SA-6): 5 fixed (untracked file, dead code, silent catch, bogus sort, namespace inconsistency), 1 observed (WarningsJson string check). All fixes compile + gates re-run clean. |
| R1.0 | StrategyId filter: extend scoring for per-strategy cells | DONE | — | SetupScoreService.ScoreRunAsync now accepts optional strategyId; ScoreRequest.StrategyId added; dedup uses VariantLabel+ExperimentId+BacktestRunId |
| R1.1 | Batched sweep: 28 tape runs (14 sym x {H1,H4}) all 9 strategies | DONE | — | 28 runs completed, 0 warnings, 31 min wall time. All runs serialize correctly with per-strategy TradeResult rows. |
| R1.2 | Score all 252 cells against baseline-sv1 experiment | DONE | — | 4 scored (>=20 trades), 248 below-floor, 0 failed. 100% coverage. 4 ExperimentRuns in DB. |
| R1.3 | Scoreboard artifacts | DONE | — | evidence/scoreboard-s1.md + evidence/scoreboard-s1.csv committed. Top: XAUUSD/H4/trend-breakout=100.0 |
| R2.0 | Audit fixes (C1, S1, S2, S5) — dead code, FoldRole, variantLabel, UpdatedAtUtc | DONE | — | SetupScoreService.cs, ExperimentRunEntity.cs, BacktestOrchestrator.cs |
| R2.1 | Compare-both configs: 6 configs created for top-3 cells x 2 windows | DONE | — | config/compare-both/*.json (6 new files) |
| R2.2 | Parity guard runs: 6 compare-both executed (3 cells x 2 windows) | DONE | — | 6 tape+6 cTrader runs, all terminal, no stuck runs, F18/F19/B1-3 verified |
| R2.3 | Reconcile: all 6 pairs reconciled; V4 table filled | DONE | — | docs/audit/RECONCILE-FINDINGS.md V4 table; 1 cell tradable (1:1 count, $271 delta per F1+F2) |
| R2.4 | Owner gate: BLOCKED (1 cell 33%); agent recommends PROCEED | DONE | — | RECONCILE-FINDINGS.md Owner gate block; F22/F23 filed |
| R2.5 | Gate battery re-verified | DONE | — | 0err/5warn · 716/0/6 · 121/0/0 · 144/0/0 · golden clean |
| R2.6 | v4: 2-month windows — XAUUSD-tb (14v15, 7.1%) + USDCAD-tb (13v13, 0%) | DONE | — | XAUUSD: 9f0ea5e5/197598ab; USDCAD: e29c5dfe/00aaba6a; divergence convergent at scale |
| R2.7 | Divergence investigation document | DONE | — | docs/iterations/iter-alpha-loop/R2-DIVERGENCE-INVESTIGATION.md (full trace + methodology + recommendations) |

## Quick commands

```powershell
dotnet build TradingEngine.slnx
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
```
