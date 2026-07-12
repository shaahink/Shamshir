# Shamshir — iter-alpha-loop Tracker (resume here)

**This is the machine-readable progress source.** The narrative docs
(`PLAN.md`) stay the human authority — this file is the strict checkpoint table +
handoff.

**Read order for a fresh session:** this file → `PLAN.md` → `LEDGER.md` →
`../../../AGENTS.md` → `../../../conductor-DEBT.md` → `../../reference/SYSTEM-REFERENCE.md`
(+ CODE-MAP, BACKTEST-ARCHITECTURE, TEST-ARCHITECTURE) → `../../../DECISIONS.md`.
**Branch:** `iter/alpha-loop`.

## Handoff  (overwrite this block, ≤12 lines, no history)

last: F38 (stale bar), F40 (rested order), F34 (currency) all shipped in prior session. **This session:** account reverted to 5834367, `Account:Currency`=EUR (venue-declared), `CTraderConnectionOptions` centralizes creds (no more raw `_config["CTrader:Account"]` in 4 places), gap-through extended to bar-close-through-stop in both `TapeReplayAdapter` and `BacktestReplayAdapter` with instrumentation.
stage: **P4 — gate built, gap-through fix applied.** `research parity --tape <id> --ctrader <id>` (exit 1 on FAIL). Pre-registered budget UNTOUCHED per PLAN §P4. Short-exit spread mismatch (tape uses configured 1-pip spread, cTrader uses ~0.15-pip actual spread) still open — needs venue-declared spread propagated to tape.
gate: build 0err/5warn · Unit 747/0/6 · Integration 121/0/0 · Sim-fast 144/0/0
next: (1) Fix tape spread source for Short exits (use venue-declared spread, not configured 1 pip). (2) Run compare-both on XAUUSD 2026-05-11→2026-06-11 (96tape/44ctrader, the most trade-dense non-BTCUSD window). (3) BTCUSD TRADES_LOST — re-run after F38 stale-bar fix; trace TradePersistenceBarrier if still lossy.

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
| P0 | Cost-sign truth: unified negative convention, cBot partial-close fix, invariant tests | DONE | de52441 | Costs unified (Net=Gross+Comm+Swap); TradeCostCalculator+TradeResultFactory+TradingEngineCBot fixed; 2 invariant tests; 4 sign tests updated; gate 721/0/6·121/0/0·144/0/0 |
| P1 | Venue-declared symbol specs: cBot symbol_spec, VenueSymbolSpec entity, correct commission model, half-at-open | DONE | 393ff67 56871de 83519da | M51 migration; cBot emits specs for all subscribed symbols; CTraderBrokerAdapter parses + upserts registry; CommissionType dispatch (AbsPerLot/UsdPerMillion); half-at-open in all 3 adapters; per-trade reconcile deltas; gate 721/0/6·121/0/0·144/0/0 |
| P0+P1.QA | Static audit + LIVE cTrader verification; found+fixed F24 (MaxSlPips collapse, 100% signal rejection); filed F25-F29 | DONE | b418a98 | evidence/p1-symbol-specs.md; live repro RunIds e907e647/921ce1e4 (broken) -> f22e51bb/261bb748 (fixed); gate re-verified 721/0/6·121/0/0·144/0/0 |
| P2 | Limit-entry parity: resting-order contract doc + F30 (fine-bar expiry decrement) + F31 (cBot fill-reporting) + D11 flip | DONE | 15c7f85 | docs/reference/RESTING-ORDER-CONTRACT.md; evidence/p2-limit-entry-parity.md; RestingOrderContractTests.cs (4 tests); live repro a59183c1/02c56355 (0 ctrader trades) -> 26664e81/438b5977 (12/12); gate 725/0/6·121/0/0·144/0/0 |
| P3 | Exit + spread parity: gap-through/exit-spread verified correct + F32 (spread-number mismatch, same class as P1/P2 gaps) | DONE | (pending) | evidence/p3-exit-spread-parity.md; TapeReplaySpreadOverrideTests.cs (3 tests); live repro da7b3427/7c2be39b (13/12, tighter matching spread); gate 728/0/6·121/0/0·144/0/0 |
| F38 | **Stale bar (MASTER)** — cBot published `bars.Last(1)`, one bar behind; every cTrader order placed a bar late, every limit already marketable on arrival | DONE | (this commit) | PARITY-TRUTH-3.md §1; cBot `barClock[]` measured 8h→**4h** on H4; EURUSD entries 18+ pips → **0.00–0.20 pips**, open timestamps identical; live 792829b1/d64d9488 |
| F40 | Rested order (`"Pending"`) unparseable → adapter dropped the whole `bar_result` batch; fallback arm would have booked it as a **zero-price fill** | DONE | (this commit) | `OrderState.Pending` + PositionTracker early-return before dedup; per-exec isolation in `HandleBarResult`; RestingOrderExecutionTests.cs (3 tests) |
| F34 | **Currency as a config value** — `Account:Currency`; `CrossRateStore` → USD-pivot table fed from market data (was 2 stale literals); commission converted to account currency | DONE | (this commit) | PARITY-TRUTH-3.md §3; **proved by flipping to EUR live** → lots **identical** on both venues (closes old "F6 sizing divergence"); commission 17% → **0.5%**; CrossRateTests.cs (14 tests) |
| P4 | **Parity as a permanent gate** — `research parity` verb + API + pre-registered tolerance budget + one VERDICT line, exit 1 on FAIL | DONE (gate built; reports FAIL, correctly) | (this commit) | ParityGateService.cs; PARITY-TRUTH-3.md §5. FAILs = EUR account (owner) + tape's optimistic stop fills. **Tolerances NOT widened** per PLAN §P4 |
| P4.1 | Account centralized + reverted to 5834367; `Account:Currency`=EUR; `CTraderConnectionOptions` DI wiring | DONE | (this commit) | `CTraderConnectionOptions.cs`; wired into BacktestOrchestrator, DownloadJobService, BacktestRunner; docs updated |
| P4.2 | Gap-through extended to bar-close-through-stop in both adapters + LogDebug instrumentation | DONE | (this commit) | `TapeReplayAdapter.cs:617-640`; `BacktestReplayAdapter.cs:422-454`; PARITY-TRUTH-3.md §4.2 updated |
| — | **Investigation method** written up as a normative reference for future agents/models | DONE | (this commit) | `docs/reference/INVESTIGATION-METHOD.md` — R1–R9, derived from why 4 sessions missed F33/F38 |

## Quick commands

```powershell
dotnet build TradingEngine.slnx
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
```
