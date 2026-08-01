# Shamshir Trading Engine — System Reference

**Written**: 2026-06-18 · **Rewritten**: 2026-07-16 (post iter-alpha-loop close + structural-edge S0/S1.1)
**For**: Any implementing agent needing to understand the full system

> Historical versions of this document described the pre-iter-36 imperative architecture
> (TradingLoop/OrderDispatcher production path, PipelineEvents journal, 4 strategies, Razor Pages
> UI). All of that is gone or test-only. This version describes the system as it is on
> 2026-07-16. If a section here contradicts code, the code wins — file a doc fix.

---

## 1. What Is This System?

Shamshir is a **prop-firm algorithmic trading engine** (.NET 10 / C# 13). It runs automated
trading strategies with FTMO-style risk rules, position sizing, and drawdown tracking, plus a
**research platform** (experiments, scoring, walk-forward, parity gates) built on top of a fast
recorded-data venue. The engine is **strategy-agnostic** and **venue-agnostic** — the same kernel
runs identically across backtest and live, with venue differences behind `IBrokerAdapter`.

### The production engine: one kernel loop

Since the iter-36 cutover there is exactly **one engine**: the pure kernel, driven by
`KernelBacktestLoop.RunFromBrokerAsync` for every venue and mode.

```
venue (IBrokerAdapter)                 Host shell                        Pure kernel (TradingEngine.Engine)
  bars/ticks/execs/account   →   KernelBacktestLoop per-bar cycle:
                                   BarEvaluator (strategies → OrderProposed)
                                   → Kernel.Decide(state, event) ──────→  PreTradeGate (gate)
                                   → EffectExecutor (orders, journal,     KernelSizing (lots)
                                     trade close, risk feedback)          EngineReducer (positions, SL/TP)
                                   → KernelFeedback (venue → events)      DrawdownReducer (DD, resets)
                                   KernelTrailingEvaluator (BE/trail)     GovernorMachine (loss bands)
                                   KernelEquitySnapshot / DailyDdGuard /
                                   TimeFlatten / WeekendFlatten evaluators
```

- **The kernel is pure**: no `DateTime.UtcNow`, no `Guid.NewGuid()`, no I/O anywhere in
  `src/TradingEngine.Engine/` — enforced by a source-scanning test (`EnginePurityTests`) and an
  architecture test (Engine references only Domain). All side effects are `EngineEffect`
  descriptions executed by `EffectExecutor`.
- **Deterministic**: `DeterminismTests` re-runs scenarios and asserts byte-identical journals.
  `PositionId == OrderId` by construction. Run identity is content-addressed
  (`DatasetId`/`ConfigSetId`/`Seed` on `BacktestRunEntity`).
- **One journal**: the lossless **StepRecord** stream (`ChannelJournalWriter`, Wait-mode channel
  → `ScopedStepRecordSink` → `SqliteStepRecordSink`). The old `PipelineEvents`/`BarEvaluations`
  writers are deleted (D83). API: `GET /api/runs/{id}/journal` (+ `/journal/export` NDJSON).
- The old imperative classes (`OrderDispatcher`, `KernelOrderGate`, `AccountProcessor`) live only
  in `tests/TradingEngine.Tests.Support` as the golden regression oracle (D81).

### Per-bar cycle (KernelBacktestLoop.ProcessBarAsync)

1. Advance venue (`OnBarObserved`) and drain prior feedback (executions, account updates)
2. Reconcile venue positions (cTrader is `VenueManaged` — the venue owns SL/TP execution)
3. Prop-firm day/week/month roll checks
4. Evaluate strategies (`BarEvaluator` → `OrderProposed` events)
5. Kernel gate + sizing → order effects → venue submit
6. SL/TP/exit detection, equity → drawdown/breach, trailing/breakeven evaluation
7. `CompleteBarAsync` (cTrader: sends buffered commands in one `bar_done` envelope)

### Multi-symbol / multi-timeframe

`RunPlan` is row-based: `[{StrategyId, Symbol, Timeframe, PackId}]` — a run executes N rows on
one account (this is also what a future portfolio run is). `StrategyBankService.GetActive`
filters by run plan + regime; indicator snapshots are keyed
`(symbol, timeframe, type, period, param)` to prevent cross-strategy bleed. Risk is unified
across all rows: concurrent-position, exposure, and budget caps are account-global.

---

## 2. Position Sizing

Sizing lives in the kernel (`KernelSizing.Calculate` + `ComputeScaleFactor`), invoked from the
gate path — not in a dispatcher.

### Lot size formula

```
riskAmount = equity × riskPerTradePercent
pipValue   = PipCalculator.PipValuePerLot(symbol, price, crossRate)
rawLots    = riskAmount / (slPips × pipValue)
scaledLots = rawLots × drawdownScaleFactor
clamped    = min(scaledLots, maxLots)
stepped    = floor(clamped / lotStep) × lotStep      ← floor, never round
finalLots  = max(stepped, minLots)
```

### Pip value (three-branch)

```
quoteCurrency == accountCurrency  (EURUSD, USD account):  pipValue = pipSize × contractSize
baseCurrency  == accountCurrency  (USDJPY, USD account):  pipValue = (pipSize × contractSize) / price
cross pair    (EURGBP, USD account):                      pipValue = pipSize × contractSize × crossRate(quote→USD)
```

### Drawdown scaler

Linear interpolation between 1.0 (below threshold) and `scaleFloor` (at limit):

```
ddRatio = currentDrawdown / maxDrawdownLimit
ddRatio <= threshold → 1.0;  ddRatio >= 1.0 → scaleFloor
else → 1.0 − ((ddRatio − threshold) / (1 − threshold)) × (1 − scaleFloor)
```

### Lot sizing methods (`LotSizingMethod`)

`PercentRisk` (default) · `FixedLots` · `FixedDollarRisk` · `KellyFraction` · `AntiMartingale`.
Optional `SizeModifierPipeline` (ATR regime / time-of-day / confidence) — all disabled in the
shipped risk profiles.

---

## 3. Risk Management

### PreTradeGate (in-kernel, the single pre-trade gate)

One pure evaluation per proposal (`src/TradingEngine.Engine/Kernel/PreTradeGate.cs`):
protection mode; governor verdict; SL validation (`MaxSlPips` / `MaxSlAtrMultiple` ceiling —
`MaxSlPips <= 0` means no limit); global + per-strategy `MaxConcurrentPositions`; global
`MaxExposure` + per-currency / opt-in `ExposureGroups` caps; a risk **budget with heat**
(open-risk accounting); and a **worst-case drawdown projection** (if every open SL and the
candidate's SL all hit, do we breach the daily/total floor?). Rejections journal with a
`GuardResult` reason (e.g. `SL_TOO_WIDE:...`).

Known gap: the gate's worst-case commission estimate treats `CommissionPerLotPerSide` as flat
per-lot dollars regardless of `CommissionType` (F26) — wrong order of magnitude for per-million
symbols; sizing-side conservatism only, ledger costs are correct.

### Governor (`GovernorMachine`, in-kernel)

Loss bands, cooling-off, streak handling, profit-lock. Config `config/governor.json`. ON by
default in research runs.

### Breach handling

Equity events → `Kernel.DecideEquity` → drawdown state → breach effects. Daily-DD guard,
time-flatten, and weekend-flatten evaluators run in the loop (`Host/Kernel*Evaluator.cs`).
`forceCloseOnBreach` is a rule-set option (off for FTMO sets — FTMO breach = account failure,
not auto-flatten).

### Position management add-ons

Per-strategy defaults (see §6) or **packs** (DB-seeded by `AddOnPackSeeder`, e.g.
`runner-aggressive` = breakeven + AtrMultiple trail + Ride (ADX-relaxed trail widening) +
PartialTp 50%@1R, all `Mode: Auto`). Trailing methods (`TrailingMethod`): `StepPips`,
`AtrMultiple`, `BreakevenThenTrail`, `Structure`, `SteppedR`, `None`. Evaluated by
`KernelTrailingEvaluator` → `StopLossModifyRequested` through the reducer.

**F71 (open):** `TakeProfit.Method = "None"` is a dead knob for the hand-rolled strategy families
(`trend-breakout`, `rsi-divergence`, `macd-momentum` call `SlTpHelpers`/own params directly and
never read `Method`) — "disable TP" is not currently expressible for them via config, even though
`EffectiveConfigJson` will happily record it. See `iter-structural-edge/LEDGER.md` S1.1.

---

## 4. FTMO Prop-Firm Rules & Risk Profiles

### Rule sets (`config/prop-firms/`)

| Rule | ftmo-standard | ftmo-aggressive | raw |
|------|---------------|-----------------|-----|
| Max daily loss | **5%** | 8% | off |
| Max total loss | **10%** (Fixed base) | 15% | off |
| Weekly / monthly soft caps | 4% / 8% | — | off |
| Profit target | **10%** | 20% | off |
| Min trading days | 4 | 4 | 0 |
| Equity definition | Balance + floating − fees/swaps | same | same |
| Daily reset | 22:00 Prague | same | same |
| News block (High impact) | 30 min before / 15 after | same | off |
| Weekend holding | No (no-open 20:00 UTC Fri, close 21:00) | same | allowed |
| ForceCloseOnBreach | No | No | No |

`raw` disables every toggle (governor, exposure, budget, max positions included) — diagnostics
only, never research.

### Risk profiles (`config/risk-profiles/`)

| Profile | Risk/trade | Daily DD | Total DD | MaxSlPips | Max SL ATR× | Exposure | Max positions | DD scale (threshold→floor) |
|---------|-----------|----------|----------|-----------|-------------|----------|---------------|---------------------------|
| conservative | **0.25%** | 3% | 6% | 50 | 2.5 | 3% | 2 | 0.5 → 0.5 |
| standard (research default) | **0.5%** | 4% | 8% | 100 | 5.0 | 5% | 3 | 0.5 → 0.5 |
| aggressive | **2.0%** | 5% | 10% | 150 | 7.5 | 10% | 5 | 0.75 → 0.25 |
| raw | 5.0% | off | off | 500 | 25 | 50% | 20 | none |

Both layers apply: `RiskProfile` (per-strategy) and `PropFirmRuleSet` (account guardrails, the
hard floor). **No portfolio profile exists yet** — `standard`'s caps saturate immediately with
many concurrent cells (known gap for any portfolio phase).

---

## 5. Venues

Venue is a per-run custom param (`CustomParams["Venue"]`), resolved by the `IVenueRunner` seam
(`Web/Services/Venues/`): `ReplayVenueRunner` (everything credential-free) and
`CTraderVenueRunner` (`"ctrader"`, explicit opt-in). Default when unset: `"replay"`.

| Venue param | Adapter | Bars from | Used for |
|---|---|---|---|
| `tape` | `TapeReplayAdapter` | `IMarketDataStore` (canonical downloaded history, deduped; 14 symbols × 6 TFs, auto-synced) | **All scored research.** Sub-second runs |
| `replay` (default) | `BacktestReplayAdapter` | legacy `Bars` table | Legacy/dev path |
| `sim` / `simulated` | `SimulatedBrokerAdapter` (`Infrastructure/Venues/`) | CSV + synthetic ticks | Simulation-test harnesses |
| `ctrader` | `CTraderBrokerAdapter` (`Infrastructure/Venues/CTrader/`) + cBot over NetMQ | cTrader itself | Parity guard, E2E, eventual live |

### Fill semantics (tape) — measured, not assumed

Normative doc: `docs/reference/RESTING-ORDER-CONTRACT.md` (corrected against six recorded
cTrader fills, F43). cTrader replays each M1 bar as four synthetic ticks (O/H/L/C); a resting
order fills at **the first tick to breach its level, never at the level itself** (stops fill
through, limits fill better). One shared implementation: `VenueFillModel.FirstBreachingTick`,
spread applied via `SpreadConvention` (ask = bid + spread on the buy side; exit levels already
on the exit side of the book get no second spread). `HonestFills` (default ON) enforces honest
entry timing. Limit entries are the research default (D11) — entry price is reproducible by
construction.

### Costs — one convention (D9/D10)

**Costs are NEGATIVE; `Net = Gross + Commission + Swap`** — invariant-tested on every
`TradeResult` row, both venues. Commission honours the venue-declared `CommissionType` (this
broker: USD per million USD notional, charged per side at entry and exit prices; research runs
use $30/M round-turn). Swap = venue-declared signed per-night rates × nights held (weekends
free, triple Wednesday). Symbol economics come from the venue: the cBot emits `symbol_spec` on
connect; `SymbolInfoRegistry.MergeVenueSpec` merges it (everything except spread — F24);
`config/symbols.json` is a loudly-logged fallback only. Caveats: specs are in-memory
process-lifetime only (F25 — `VenueSymbolSpecs` table exists but is never written);
`SwapCalculationType` is captured but not dispatched on (F28).

### Parity (tape vs cTrader)

Permanent gate with a pre-registered tolerance budget (trade count exact, entry ≤1 tick, lots
exact, exit ≤1 tick on ≥95%, commission ≤2%, swap ≤5%, net ≤1% of gross). EURUSD: `VERDICT:
PASS`. Known residuals: F47 (venue prices commission at one reference spot — not matched, by
decision), F48 (XAUUSD net ~1.37% — pip cross-rate timing; open). Any owner-facing candidate
needs a parity verdict ≤ 14 days old. See `docs/iterations/iter-alpha-loop/PARITY-TRUTH*.md`
and `evidence/p1-p3*.md`.

### cTrader specifics

Lock-step protocol (§12); headless launches use **dynamic ports** via `CTraderProcessOwner`
(PID-owned, orphan-reaped); desktop capture (`CTraderListenService`) listens on fixed
15555/15556. The cBot writes its own resilient ledger (`shamshir-report.json`) so venue truth
survives CLI crashes. cTrader runs are strictly serial; tape runs pool concurrently.

---

## 6. Strategy Bank (9 families)

Interface: `IStrategy.Evaluate(MarketContext) → TradeIntent?` with declared
`RequiredIndicators`/`RequiredBarCount`. Configs seeded from `config/strategies/*.json`
(DB canonical). All families default to **LimitOffset** entries and `standard` risk. Scored
research so far: H1/H4 only.

| Family | Thesis | Key params | SL / TP | Own add-ons (PackId-null baseline, F69) |
|---|---|---|---|---|
| trend-breakout | N-bar high/low breakout above trend EMA, ADX-confirmed | lookback 20, MA 50 | 1.5×ATR / 2R | BE@1R + trail 2.5×ATR |
| mean-reversion | RSI 30/70 extreme + outer-BB rejection snaps back | RSI 14, BB 20/2σ | 1.5×ATR / 1R | none |
| session-breakout | Break of 05–07 UTC range in 07–09 window; flatten 12:00 | UTC session times | 1.5×ATR / 2R | BE@1R + trail 2.0×ATR |
| ema-alignment | First pullback to fast EMA after 20/50 crossover | 20/50 | 1.5×ATR / 2R | BE@1R + trail 2.5×ATR |
| macd-momentum | MACD histogram zero-cross on trend side of SMA(200), ADX≥20 | 12/26/9 | 2×ATR / 3R | BE@1R + trail 2.5×ATR |
| rsi-divergence | Pivot-based price/RSI divergence, entry on pivot break | RSI 14, lookback 50 | 1.5×ATR / 2R | none |
| bb-squeeze | BB contraction resolves in breakout direction | BB 20/2σ, threshold 0.8 | swing-point / 2.5R | BE@1R + trail 2.0×ATR |
| super-trend | SuperTrend flip confirmed by ADX≥20 | ATR 10 ×3.0 | swing-point / 2R | BE@1R + trail 2.5×ATR |
| mtf-trend | H1 RSI pullback resuming H4/EMA(200) trend | EMA 200, RSI 45/55 | swing-point / 2R | BE@1R + Structure trail (10-bar) |

History worth knowing: `rsi-divergence` and `ema-alignment` originally shipped with fake logic
(self-comparing RSI; always-true state condition) and were rewritten before any scored research.
These are infrastructure-exercise hypotheses, not curated alpha; census evidence (F68) puts the
bank at ≈ +0.02R/trade pooled, with `mean-reversion` best (+0.10R) and `mtf-trend` worst
(−0.22R).

---

## 7. Research Platform

The reason the tape venue exists. Everything is server-side + CLI-drivable.

- **Experiments**: one `Experiment` row per batch (Name, Hypothesis, **SpecJson = the
  pre-registration**); one `ExperimentRun` per scored cell (`BacktestRunId`, `VariantLabel`,
  fold fields, `ScoreJson`). Cell = strategy × symbol × TF × pack × risk. One cell per run (D13).
- **Scoring** (`SetupScoreService`, versioned): composite = Expectancy 30 + FTMO-survival 25 +
  Drawdown 15 + Consistency 15 + Robustness/OOS 15. Validity floor: ≥20 trades, DataQuality
  PASS, completed, zero warnings — else score = null **with reason**. **sv2** (current):
  survival = real `ChallengeSimulator` rolling 30-day windows over the run's actual daily
  equity, `PassRate = Pass/Windows`, incomplete counts as non-pass. sv1 rows (the 075d5240
  census) are frozen history — survival there was a placeholder (F63).
- **Walk-forward**: `WalkForwardController` + `WalkForwardBackgroundService` +
  `Experiments/WalkForwardSplitter` — 6 rolling folds, train/test split, per-fold param freeze;
  OOS ratio = Σ test profit / Σ chosen-params train profit (F62); cull gate < 0.5 → parked
  (`StrategyCellParks`, never deleted). Carries `PackId`/`RiskProfileId` into folds.
- **Challenge sim**: `ChallengeSimulator` (`Risk/Compliance/`) + `ChallengeSimulationService` —
  FTMO-standard semantics (target / daily cap / max loss / min days) over real daily equity;
  `GET /api/runs/{runId}/challenge-sim`. `PassProbabilityEstimator` = Monte Carlo forward
  projection (different question).
- **Split-half persistence** (F64 machinery): `SplitHalfPersistenceService`,
  `GET /api/experiments/persistence`, CLI verb `research persistence` — prints the selection-
  persistence table for any experiment. Python equivalents in `tools/research/`.
- **Parity**: `ParityGateService` + `LedgerReconcileService` / `LedgerReconciler` — per-trade
  reconcile + tolerance verdict (`research parity`).
- **Exit lab**: excursion recording (opt-in `RecordExcursions`) + `ExitReplayer`
  (`Services/ExitLab/`) — replay exit rules offline against recorded excursion paths (the
  entry-stream-identical exit comparison; the run-level factorial measures whole-system effects
  instead).
- **Sweeps**: `SweepController` + `SweepRunnerService` (tape-only, queued).
- **ResearchCli** (`src/TradingEngine.ResearchCli`): verb-based HTTP client + playbook executor
  (`research doctor|run|score|scoreboard|parity|persistence|...`), machine `VERDICT:` lines.
  Known quirk: bare `research score <runId>` mis-parses (CliArgs joins two positionals) — use
  options/API.
- **Discipline** (enforced by plan + ledger convention, see `docs/iterations/*/PLAN.md`):
  pre-register before running, ≤8 variants/session, evaluate at family level on
  **position-level dollars** (row-level ExpectancyR is inflated by PartialTp row-splitting —
  F70), split-half + sign-consistency + walk-forward for any survival claim, embargo windows
  touched exactly once.

---

## 8. Web UI & API

**UI**: Angular SPA (`web-ui/`), served single-origin by `TradingEngine.Web` (SPA + Scalar API
docs + REST). Dev port **5134**. `NgServeHost` proxies `ng serve` in dev; production builds land
in `wwwroot` (`npm --prefix web-ui run build` — a stale wwwroot vs Angular src breaks
`dotnet build` static assets). Live monitor uses SignalR push from in-memory run state (zero DB
reads); read APIs are cache-first (`RunDataCache`, write-through from the persistence handlers).

**API controllers** (`Web/Api/`): `RunsController` (runs, journal, duplicate, challenge-sim),
`TradesController`, `BarsController`, `ExperimentsController` (+ scoreboard, persistence),
`WalkForwardController`, `SweepController`, `ExitLabController`, `EntryQualityController`,
`BlockBootstrapController`, `BacktestAnalyticsController` (reconcile), `DataManagerController`
(coverage/sync), `CtraderListenController`, `VenueSessionsController`, `StrategiesController`,
`RiskProfilesController`, `PropFirmRulesController`, `AddOnPacksController`,
`GovernorController`, `ScoreboardController`, `ResearchPipelinesController`, `SystemController`
(health), `ExportController`, `LogController`, `PhaseTrackerController`.

---

## 9. Config System

DB is canonical; JSON is seed + export. `StrategyConfigSeeder` seeds `config/strategies/*.json`
on first run (idempotent); `AddOnPackSeeder` seeds packs. `EffectiveConfigResolver` deep-merges
stored default ← per-run overrides ← run plan; the resolved `EffectiveConfigJson` is persisted
on the run. Risk profiles / prop-firm rules / symbols load from JSON (`ConfigLoader`), with
symbol economics overridden by venue-declared specs at runtime (§5). Other config:
`governor.json`, `regime.json` (regime detection — `AtrBasedRegimeDetector`), `rotation.json`,
`sizing-policy.json`, `position-management.json`, `exposure-groups.json`, `news/`,
`experiments/`, `compare-both/`, `r4-embargo/`.

Known audit-trail caveat (F61): the API's displayed effective config does not apply legacy
`UsePackId`/`StripAddOns` custom params (execution is correct; display can lie about packs on
old-style runs).

---

## 10. Test Infrastructure

Baseline at 2026-07-16 (structural-edge S0): **build 0 err · Unit 767 pass / 6 skip ·
Integration 153 · Simulation-fast 144** — all credential-free.

| Tier | What | Notes |
|------|------|-------|
| Architecture | Reflection invariants: Engine references only Domain; purity (no UtcNow/NewGuid/ILogger in Engine) | seconds |
| Unit | Pure logic: reducers, gate, sizing, costs, fill model (`VenueFillModelTests` pinned to recorded venue fills), swap model, challenge simulator | ~seconds |
| Integration | Real DI + SQLite + WebApplicationFactory: API, scoring (`SetupScoreSv2Tests`), persistence, split-half | ~10s+ |
| Simulation (fast) | Full engine on harnesses: golden journeys, determinism, replay E2E, resting-order contract | `RequiresCTrader!=true` |
| cTrader E2E | Real compiled cBot under ctrader-cli over NetMQ incl. ledger reconciliation | `RequiresCTrader=true`, credentials + desktop install; see `ctrader-e2e` skill |

Golden gates: `golden-snapshot.json` (kernel behaviour), determinism suite, cost-sign invariant.
**Credential-free green does not prove cTrader behaviour** — venue-path changes need a live
compare-both smoke (doctrine after F24; see `docs/reference/INVESTIGATION-METHOD.md`).

---

## 11. Key Files Reference

### Kernel (`src/TradingEngine.Engine/`)
| File | Purpose |
|------|---------|
| `Kernel/Kernel.cs` | Top-level router: `Decide(state, event)`; static helpers (`EvaluateDrawdownBreach`, `DetectSlTpExit`) |
| `EngineReducer.cs` | Pure state machine `Apply(state, event) → (state', effects)` |
| `PositionLifecycle.cs` | Position FSM: Intended → Submitted → Open → Reducing → Closed |
| `Kernel/PreTradeGate.cs` | The single pre-trade gate (protection, governor, SL, exposure, budget/heat, worst-case DD) |
| `Kernel/KernelSizing.cs` | Sizing math + drawdown scale factor |
| `DrawdownReducer.cs` | Daily/weekly/monthly/max DD, resets, velocity |
| `GovernorMachine.cs` | Governor state machine |
| `Kernel/KernelDriver.cs` / `Kernel/ChannelJournalWriter.cs` | Replay driver / lossless StepRecord journal |

### Host shell (`src/TradingEngine.Host/`)
| File | Purpose |
|------|---------|
| `KernelBacktestLoop.cs` | THE production loop (all venues, backtest + live) |
| `EngineRunner.cs` / `EngineWorker.cs` / `EngineHostFactory.cs` | Build + run the inner engine host |
| `BarEvaluator.cs` | Strategies → `OrderProposed` |
| `EffectExecutor.cs` / `KernelFeedback.cs` | Effects out / venue events in |
| `KernelTrailingEvaluator.cs`, `KernelEquitySnapshot.cs`, `KernelDailyDdGuardEvaluator.cs`, `KernelTimeFlattenEvaluator.cs`, `KernelWeekendFlattenEvaluator.cs` | Per-bar evaluators |
| `StrategyBankService.cs` / `StrategyRegistry.cs` / `ConfigLoader.cs` / `IndicatorSnapshotService.cs` | Strategy activation + config + indicators |
| `TradingLoop.cs` | Golden-oracle shell only (not production) |

### Venues & data (`src/TradingEngine.Infrastructure/`)
| File | Purpose |
|------|---------|
| `Adapters/TapeReplayAdapter.cs` | Research venue (IMarketDataStore-fed, resting orders, honest fills) |
| `Adapters/VenueFillModel.cs` / `Adapters/SpreadConvention.cs` | First-breaching-tick fill rule / bid-ask convention |
| `Adapters/BacktestReplayAdapter.cs` | Legacy replay venue (Bars table) |
| `Venues/CTrader/CTraderBrokerAdapter.cs` | Engine side of the cTrader lock-step |
| `Venues/SimulatedBrokerAdapter.cs` | Synthetic tick venue (tests) |
| `Transport/NetMq/NetMqMessageTransport.cs` | SUB + ROUTER sockets, poller |
| `SymbolInfoRegistry.cs` | Symbol economics; venue-spec merge (process singleton — beware cross-run state) |
| `Reconcile/LedgerReconciler.cs` | Per-trade tape-vs-venue reconcile |
| `Persistence/TradePersistenceHandler.cs`, `Persistence/EquityPersistenceHandler.cs`, `Persistence/Repositories/SqliteStepRecordSink.cs` | Write paths (all push to `RunDataCache`) |
| `Caching/RunDataCache.cs` | Write-through run cache shared Web ↔ inner host |
| `Indicators/SkenderIndicatorService.cs`, `Indicators/AtrBasedRegimeDetector.cs` | Indicators, regime |

### Services & risk (`src/TradingEngine.Services/`, `src/TradingEngine.Risk/`)
| File | Purpose |
|------|---------|
| `Services/PipCalculator.cs` | Distance, pip value, gross PnL |
| `Services/Helpers/TradeCostCalculator.cs` | Gross/commission/swap/net (D9 convention) |
| `Services/EntryPlanner.cs` | Order-entry policy (market/limit-offset, SL/TP re-derivation) |
| `Services/SLTPCalculation/SlTpCalculator.cs` + `SlTpResolver.cs` | SL/TP methods (note F71: some strategies bypass these) |
| `Services/ExitLab/ExitReplayer.cs`, `Services/Helpers/ExcursionTracker.cs` | Exit lab |
| `Risk/Compliance/ChallengeSimulator.cs`, `Risk/Compliance/PassProbabilityEstimator.cs` | Challenge windows / Monte Carlo P(pass) |
| `Risk/RiskManager.cs` etc. | Legacy imperative risk (oracle-path; kernel gate is production) |

### Web (`src/TradingEngine.Web/`)
| File | Purpose |
|------|---------|
| `Services/BacktestOrchestrator.cs` | Run queue/lifecycle/finalize (decomposed 2026-07: run-scoped services in `Services/Runs/`, venue execution behind `Services/Venues/IVenueRunner`) |
| `Services/Runs/*` | RunRegistry, RunRecordStore, RunConfigAssembler, RunMarketContextLoader, RunProgressProjector, EngineHostLifecycle, `ILiveRunReader` + query classes (RunListQuery/RunDetailQuery/RunDataQuery/RunBarNarrativeQuery) |
| `Services/Venues/ReplayVenueRunner.cs` / `CTraderVenueRunner.cs` | Venue execution seam |
| `Services/SetupScoreService.cs`, `ChallengeSimulationService.cs`, `SplitHalfPersistenceService.cs`, `SweepRunnerService.cs`, `WalkForwardBackgroundService.cs`, `ParityGateService.cs`, `LedgerReconcileService.cs` | Research services |
| `Services/CTraderListenService.cs`, `CTraderProcessOwner.cs`, `AutoSyncService.cs` | Desktop capture, CLI process ownership, data auto-sync |
| `Configuration/AddOnPackSeeder.cs` | Pack seeding |

### Research tooling
| Path | Purpose |
|------|---------|
| `src/TradingEngine.ResearchCli/` | `research` CLI (verbs + playbooks + VERDICT lines) |
| `src/TradingEngine.Experiments/` | ExperimentRunner, VariantScorer, WalkForwardSplitter, report writer |
| `tools/research/` | `quant_research.py`, `split_half.py` (+ README) — DB-direct analysis, committed S0 |
| `playbooks/` | ResearchCli playbooks |

---

## 12. Lock-Step Protocol (cTrader path)

```
cBot → engine   {type:"hello", v:2, symbols:[..], periods:[..], subs:[..], barsLoaded:N,
                 account:{..}, positions:[..], mode:"backtest"|"live"}
engine → cBot   {type:"hello_ack", v:1}          (cBot retries 1s×5 → HELLO_TIMEOUT stop)

cBot → engine   {type:"bar", seq:N, symbol, period, openTime, OHLCV, account:{..}}
cBot BLOCKS (30s) until:
engine → cBot   {type:"bar_done", v:1, seq:N, commands:[{submit_order|close_position|modify_order|cancel_order}...]}
cBot executes commands at simulated time, then:
cBot → engine   {type:"bar_result", seq:N, execs:[..], account:{..}}

cBot → engine   {type:"exec", ...}    ← venue-initiated executions (SL/TP) between bars
PUB → SUB side channel: {type:"tick"|"acct"|diagnostics}
cBot → engine   {type:"stats", ...};  engine → cBot {type:"shutdown"}
```

cBot emits `symbol_spec` on connect (§5). Full details: `shamshir-ctrader` skill +
`docs/iterations/iter-17/PROTOCOL.md` (original spec; v2 additions above).

---

## 13. Build and Test Commands

```powershell
npm --prefix web-ui run build      # if Angular src is newer than wwwroot (static-assets gotcha)
dotnet build

dotnet test tests/TradingEngine.Tests.Unit                                        # 767 pass / 6 skip
dotnet test tests/TradingEngine.Tests.Integration                                 # 153
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true" # 144 (fast tier)
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~Determinism"
dotnet test tests/TradingEngine.Tests.Architecture

dotnet run --project src/TradingEngine.Web        # http://localhost:5134

dotnet ef migrations add <Name> --startup-project src/TradingEngine.Web --project src/TradingEngine.Infrastructure
```

---

## 14. Key Design Decisions (summary)

Full registry: `DECISIONS.md` (D1–D97+). Research-era decisions live in the iteration plans
(`iter-alpha-loop/PLAN.md` D1–D14, `iter-structural-edge/PLAN.md` D1–D8).

| Decision | Value |
|----------|-------|
| Money math | `decimal` everywhere; lot rounding = `Math.Floor`, never `Round` |
| Engine | One pure kernel; effects as data; imperative twins are test oracles (D81) |
| Journal | Single lossless StepRecord stream (D83); PipelineEvents deleted |
| Cost convention | Costs NEGATIVE; `Net = Gross + Commission + Swap` (D9), invariant-tested |
| Symbol economics | Venue-declared (`symbol_spec`); `symbols.json` = loud fallback (D10) |
| Research entries | LimitOffset default (D11) — reproducible entry prices |
| Research venue | Tape only for volume; cTrader = parity guard + live (alpha-loop D1) |
| Cell integrity | One cell per run; below-floor → null-with-reason (D13/D3) |
| Scoring | Versioned (sv1 frozen, sv2 current); formula changes = new version |
| Parity | Permanent gate, pre-registered tolerances, verdict ≤14 days old (D12) |
| Config | DB canonical; JSON seed/export only |
| Time | `IEngineClock`; sim-time from events — never `DateTime.UtcNow` in engine |
| Channels | `Wait` for orders/trades/journal; `DropOldest` only for analytics |
