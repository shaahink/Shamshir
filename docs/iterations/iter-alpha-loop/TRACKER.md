# Shamshir — iter-alpha-loop Tracker (resume here)

**This is the machine-readable progress source.** The narrative docs
(`PLAN.md`) stay the human authority — this file is the strict checkpoint table +
handoff.

**Read order for a fresh session:** this file → `PLAN.md` → `LEDGER.md` →
`../../../AGENTS.md` → `../../../conductor-DEBT.md` → `../../reference/SYSTEM-REFERENCE.md`
(+ CODE-MAP, BACKTEST-ARCHITECTURE, TEST-ARCHITECTURE) → `../../../DECISIONS.md`.
**Branch:** `iter/alpha-loop`.

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **P4 PARITY GATE IS GREEN ON EURUSD — `VERDICT: PASS`, tolerance budget UNTOUCHED.** Every trade now matches cTrader byte-for-byte on entry, exit, stop, lots and both timestamps. Three defects, all found by making the venue tell us (PARITY-TRUTH-4): **F43** both replay venues filled resting orders at the ORDER'S OWN PRICE — the venue fills at the first of its four synthetic M1 O/H/L/C ticks to BREACH the level (so stops fill through, limits fill better); the short-exit spread was also being counted TWICE. **F44** venue symbol specs were captured in memory and never persisted, so the tape (which never meets a cBot) priced off fabricated symbols.json. **F45** swap read as money (it's PIPS), negated (it's already signed), and weekends charged (they aren't) — the tape CREDITED 1.37 where the venue CHARGED 41.26.
  NOTE the prior session's "fill at the bar close" stop model (PARITY-TRUTH-3 §4.2) was a number-fitting fudge and is REVERTED — it would have inflated every loss on trend days.
stage: **P0–P4 CLOSED. Ready for X0.** `research parity --tape <id> --ctrader <id>` (exit 1 on FAIL). Gate proven falsifiable — it returned FAIL twice this session on this same code path before the fixes landed, and it still fails XAUUSD on F48.
gate: build 0err/5warn · Unit 759/0/6 · Integration 121/0/0 · Sim-fast 144/0/0 · **live parity EURUSD PASS** (tape a89d37b5 / ctrader e497806d) · XAUUSD all-green except NetPnL (F48)

**OWNER DECISIONS (2026-07-12), both logged:**
- **F47 (cTrader's per-lot commission artifact): ACCEPT + DOCUMENT.** Do NOT match it — pricing commission at one reference spot makes a backtest's costs depend on WHEN it ran. The gate now exempts it, but only when the venue's own data proves the artifact (see F47ex).
- **M1 tick-synthesis realism bias: PARKED.** The tape faithfully reproduces cTrader's 4-ticks-per-bar synthesis, artifacts included — limits/TPs fill BETTER than their level (one XAUUSD TP filled 27 pts through target). Parity is honest (both legs share the model) but the VENUE is not modelling reality. **X0's absolute PnL inherits this optimistic bias; relative strategy ranking stays valid.** Revisit with tick data. PARITY-TRUTH-4 §5.

next (X0 session): (1) **Start X0** — parity is locked; the bias above is documented, not a blocker. (2) F48 (the only open parity defect): XAUUSD gross differs 1.37% purely on pip *valuation* — `PipCalculator.PipValuePerLot` uses the time-varying `getCrossRate("USD","EUR")` for XAUUSD while cTrader values each trade at its own rate at that moment. Per-trade ratio drifts ⇒ a rate-timing issue. Invisible on a USD account. (3) BTCUSD parity + F36 (trade capture on the cTrader leg is still lossy: `TRADES_LOST`). (4) Capturing a venue spec for a symbol requires ONE cTrader run on it (populates VenueSymbolSpecs); the tape then prices off the broker on every later run.

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
| P4.2r | **REVERTED** the "fill at the bar close" stop model — it was fitted to one trade and would fill a bar that dips through your stop then trends at the FAR END of the move, inflating every trend-day loss | DONE | (this commit) | PARITY-TRUTH-4 §1; replaced by the measured `VenueFillModel` |
| F43 | **Resting orders fill at the first breaching M1 tick, NOT at their own price** — stops fill THROUGH, limits fill BETTER. Plus the short-exit spread was counted TWICE (bar already shifted to the ask side for detection, then spread re-added to the fill) | DONE | (this commit) | `VenueFillModel.cs` shared by both replay adapters; **`VenueFillModelTests` pins it against SIX REAL cTrader fills** (EURUSD+XAUUSD, both directions, limit entry + SL + TP); PARITY-TRUTH-4 §1 |
| F44 | **Venue symbol specs never persisted** — captured in memory on the cTrader leg only, so the tape priced commission/swap off fabricated symbols.json (a EURUSD long *earning* 0.5/night that the broker *charges* 2.445 for) | DONE | (this commit) | `IVenueSymbolSpecStore` + `SqliteVenueSymbolSpecStore`; `EngineHostOptions.VenueSymbolSpecs` wired at BOTH orchestrator sites (the F34 trap); VenueSymbolSpecs table now populated |
| F45 | **Swap wrong three ways**: rate read as MONEY (it is PIPS — venue declares `SwapCalculationType=Pips`), then NEGATED (it is already signed: negative = trader pays), and Sat/Sun rollovers CHARGED (no broker finances a shut market — that is why Wednesday is triple) | DONE | (this commit) | `TradeCostCalculator`; **`VenueSwapModelTests` pins it against the venue's THREE REAL swap charges**; PARITY-TRUTH-4 §3 |
| F46 | **Closing commission billed at the ENTRY price** — each side must be billed on its own notional at its own price. Invisible on EURUSD (0.53%), worth ~10% on XAUUSD | DONE | (this commit) | `TradeCostCalculator`; `ClosingCommission_isBilledAtTheExitPrice_notTheEntryPrice` |
| F47 | **cTrader prices backtest commission at ONE reference spot, not per trade** — constant **-20.67 EUR/lot round-turn** across an 18% gold move; same lots at different prices → identical commission. **Their artifact, not ours — do NOT match it** (it makes a backtest's cost depend on WHEN it was run) | **OPEN — owner decision** | — | PARITY-TRUTH-4 §4b; ctrader `a66fe9c4` |
| **P4** | **PARITY GATE GREEN — EURUSD `VERDICT: PASS`, budget untouched** | **DONE** | (this commit) | tape `a89d37b5` / ctrader `e497806d`: TradeCount 3:3 · EntryPrice **0.0 ticks** · Lots exact · ExitPrice **100% within, 0.0 ticks** · Commission 0.53% · Swap 0.44% · NetPnL 0.45% |
| **P4-xau** | **XAUUSD (14 trades, price 3369→4039): prices/lots/swap ALL GREEN** — the fill + swap models generalise across scale untouched. Commission EXEMPT (F47, owner-accepted); NetPnL still fails on F48 | PARTIAL | (this commit) | tape `2c5a3f5e` / ctrader `a66fe9c4`: EntryPrice **0.0 ticks** · ExitPrice **100% within, 0.0 ticks** · Lots exact · Swap 1.37% · Commission EXEMPT · NetPnL 1.37% (F48) |
| F47ex | **F47 exemption is EARNED FROM VENUE DATA, not granted** — the gate fires it only when the venue's commission-per-lot stayed flat (<2%) while trade prices moved (>5%), needs ≥4 trades, and it prints the measured divergence + backs it out of NetPnL. A blanket exemption would hide a real commission bug forever | DONE | (this commit) | `ParityGateService.VenueCommissionIsPriceIndependent`; XAUUSD reports `[PASS] Commission EXEMPT (F47) — was ≤2% → 9.88%` with a loud NOTE |
| F48 | **PnL currency conversion — the LAST divergence.** XAUUSD gross differs 1.37% though prices+lots are IDENTICAL, so the pip *count* matches and only the pip's *worth* differs. `PipCalculator.PipValuePerLot` takes the `getCrossRate("USD","EUR")` branch for XAUUSD (time-varying CrossRateStore) vs the price-accurate `rawPipValue / currentPrice` branch for EURUSD. Per-trade ratio DRIFTS (1.0087→1.0167) ⇒ a rate **timing** issue, not a flat offset. Invisible on a USD account | **OPEN** | — | PARITY-TRUTH-4 §4b; tape `2c5a3f5e` vs ctrader `a66fe9c4` |

## Quick commands

```powershell
dotnet build TradingEngine.slnx
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
```
