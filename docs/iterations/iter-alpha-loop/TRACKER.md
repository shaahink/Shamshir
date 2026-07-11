# Shamshir — iter-alpha-loop Tracker (resume here)

**This is the machine-readable progress source.** The narrative docs
(`PLAN.md`) stay the human authority — this file is the strict checkpoint table +
handoff.

**Read order for a fresh session:** this file → `PLAN.md` → `LEDGER.md` →
`../../../AGENTS.md` → `../../../conductor-DEBT.md` → `../../reference/SYSTEM-REFERENCE.md`
(+ CODE-MAP, BACKTEST-ARCHITECTURE, TEST-ARCHITECTURE) → `../../../DECISIONS.md`.
**Branch:** `iter/alpha-loop`.

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **P0–P3 WERE NOT THE PROBLEM. See `PARITY-TRUTH-2.md`** (owner-delegated deep audit 2026-07-12).
  Three master defects sat underneath all of P0–P3, missed because *every* prior parity cell was XAUUSD
  — the one symbol where the biggest of them looks plausible. **F33:** the cBot passed absolute SL/TP
  *prices* into cAlgo parameters that mean *pip distance* (`stopLossPips`), so every cTrader stop ever
  placed was at `price × pipSize` from entry — a 1.2-pip stop on EURUSD, a 7,708-pt stop on BTCUSD (a
  long ran 7,000 pts past its stop, −29,500). Fixed with `ProtectionType.Absolute`. **F34:** the venue
  account is **EUR**-denominated, the engine models USD — a constant 0.86376 on every cTrader money
  figure. Now venue-declared + warned; **not resolved** (needs a USD account). **F39:** `commissionPerMillion`
  was substituted for the per-lot rate, so the tape billed $30/LOT not $30/million — 8.6× overcharge.
  Also F35 (OrderEntryMethod fabricated), and the compare-both cTrader leg never ran the trade-persistence
  barrier and never merged its warnings — the one leg parity is measured against had no integrity checks.
stage: **P4 — Parity as a permanent gate — NOT STARTED** (now measurable: the venues actually agree)
gate: build 0err/5warn · Unit 730/0/6 · Integration 121/0/0 · Sim-fast 144/0/0
verified live (both cells re-run from the owner's own manual tests, EURUSD + BTCUSD):
  · EURUSD 3:3 trades, **stops identical to the tick**, **exit timestamps identical**, commission within
    **0.85%** (was: cTrader +74 vs tape −2,032; stops firing at −1.7 pips)
  · BTCUSD **6:6** trades, commission within 4.4% (was: 73,179 vs 9,037 gross — an 8× gap)
  · venue-intent invariant `protectionMismatches = 0`; `VENUE_CURRENCY_MISMATCH:EUR` now surfaces
next: **F38** — the largest remaining divergence: `LimitPrice == SignalPriceMid` in the journal, i.e. the
  configured 5.25-pip LimitOffset is **never applied**, so the order is marketable on one venue and rests
  on the other (tape fills at 1.15973, cTrader at 1.16156). Then P4. **Parity cells must include a
  currency pair AND a high-priced symbol** — never XAUUSD alone; that is what hid F33 for four sessions.

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

## Quick commands

```powershell
dotnet build TradingEngine.slnx
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
```
