# Iteration 19 — Capital Guardian, Research Lab, Trade Intelligence & Live UI

**Status**: PLANNED — owner decisions locked 2026-06-12
**Branch**: `iter/19-guardian-research-ui` (one branch per PR, see Part 4)
**Builds on**: iter-17 (deterministic lock-step pipeline) + iter-18 (risk overhaul, 9-strategy bank, Blazor skeleton)
**Spans**: multiple sprints — four PRs, each independently shippable

---

## Decisions Log (owner-confirmed, 2026-06-12)

1. **No pyramiding.** One position per trend, per strategy+symbol+direction. Trend participation
   comes from *riding* (smarter trailing, later exits), not *adding*. Re-entry suppression prevents
   the "enter the same trend on every bar" failure mode.
2. **Research harness = batch + scoring.** Experiment entity in DB, batch runner over explicit
   variant lists, walk-forward splits, composite score (FTMO pass probability + expectancy +
   max DD + fold consistency). Driven by an agent via CLI and REST API. No auto-optimizer this
   iteration — agents propose variants, the harness measures them.
3. **All three capability streams ship** (FTMO survival, research harness, trade intelligence)
   plus the UI maturity stream. Planned as 4 PRs over 2–3 sprints.
4. **Thresholds: robust defaults, no tuning culture.** The owner explicitly does not want a
   value-tuning rabbit hole. See "Anti-Overfitting Principles" below — this section is binding
   on the implementing agent.
5. **Loss enforcement is broken today and gets fixed FIRST (Phase G0).** Past backtests breached
   the daily-loss limits. Root causes were located on 2026-06-12 (see §1.2 gap 0): the replay
   adapter reports flat equity forever, `EnterProtectionMode` has no callers, daily breaches never
   flatten positions, and the exposure pre-check is a no-op for market orders. PR1 opens with G0
   and a regression test that fails on pre-G0 code.
6. **Per-day protection ledger.** Every protective decision (state change, blocked entry, size
   reduction, flatten, breach) is journaled per trading day — DB + API + UI drill-down — so any
   bad day can be explained in seconds, in backtests and live alike.
7. **Entry/exit playbook.** All 9 strategies' entry/exit handling is quant-reviewed and
   consolidated onto baked-in best-practice baselines per asset class × timeframe. "Adaptive"
   means ATR/volatility-scaled *by construction* — never optimizer-fitted (P1–P5 apply).
8. **Sizing policy.** Fixed-fractional risk plus a budget-aware cap: a new trade's worst-case
   loss may only commit a fraction of the *remaining* daily-loss budget, so size shrinks
   automatically as a day deteriorates; total portfolio heat capped as a multiple of per-trade risk.

## Anti-Overfitting Principles (binding)

These rules exist because the edge of this system is *selectivity and survival*, not market
prediction. Every implementing agent must follow them:

- **P1 — Thresholds scale with the rules, not the market.** Every governor threshold is expressed
  as a *fraction of the active PropFirmRuleSet limit* (e.g., "soft-stop at 0.6 × MaxDailyLossPercent"),
  never as an absolute percent. Changing the prop-firm ruleset re-derives all behavior; nothing
  needs re-tuning.
- **P2 — Bands, not curves.** Discrete state bands (Normal / Reduced / SoftStop) with 2–3 breakpoints.
  No continuous functions, no decay coefficients, no per-symbol overrides. Fewer knobs = less to overfit.
- **P3 — New parameters need experiment evidence.** From PR2 onward, no new tunable parameter may be
  added to a strategy or position-management config unless an Experiment report shows the variant
  beats baseline *consistently across walk-forward folds* (not just in aggregate). Attach the
  experiment ID in the PR description.
- **P4 — Prefer parameter plateaus.** When an experiment compares values, pick the middle of a flat
  region of the score surface, never the peak of a spike.
- **P5 — Defaults are behavioral, config is an escape hatch.** Everything below is configurable via
  JSON, but the defaults are chosen for robustness and the expectation is that they are *not* touched
  without P3 evidence.

---

## Read First

- `docs/iterations/iter-18/PLAN.md` Parts 1–2 — current architecture (risk services, strategy bank,
  size-modifier pipeline, Blazor hybrid).
- `docs/iterations/iter-18/HANDOVER.md` — what was actually delivered (note: Blazor pages are
  functional but minimal; `BacktestDashboard.razor` is a stub of the E2 spec).
- `docs/iterations/iter-17/PROTOCOL.md` + `docs/iterations/iter-18/PROTOCOL-DELTA.md` — NetMQ
  lock-step protocol. Phase T3 adds one command (`close_partial`); a new PROTOCOL-DELTA is required.
- `docs/OPEN-ISSUES.md` — issues log; update statuses as items are fixed.

**Hard rules (unchanged from iter-17/18):**
- `decimal` for all money/price arithmetic. `double` is acceptable only for indicator math.
- `IEngineClock` everywhere; no `DateTime.UtcNow` in engine code.
- `TradingEngine.Domain` has zero infrastructure dependencies.
- cBot project targets **net6.0 / C# 10** — no C# 11+ constructs in `TradingEngine.Adapters.CTrader`.
- Single composition root: `EngineHostFactory`. All new services registered there only.
- `CancellationToken` on every async method. No fire-and-forget for financial events.
- EF migrations only — no raw SQL schema changes.

---

# Part 1 — System Analysis

## 1.1 What exists (do not rebuild)

| Capability | Where | State |
|---|---|---|
| Daily/weekly/monthly DD blocks, grace period | `PropFirmComplianceService` | Working |
| Monte Carlo FTMO pass probability | `PassProbabilityEstimator` | Working — reuse for scoring |
| Size modifiers (drawdown, ATR regime, time-of-day, confidence) | `SizeModifierPipeline` + 4 modifiers | Working |
| Currency exposure caps | `CurrencyExposureTracker` | Working |
| Strategy bank, regime detector, rotation | `StrategyBankService`, `AtrBasedRegimeDetector` | Working |
| Unified SL/TP/breakeven/trailing | `SlTpResolver`, `PositionManager` | Working — ATR trail is already high-water (chandelier-style) |
| In-process deterministic backtest | `BacktestReplayAdapter` + lock-step protocol | Working — production path |
| Per-run progress channel | `BacktestProgressStore` | Exists but barely consumed by UI |
| Equity snapshots in DB | `EquityPersistenceHandler`, `GetEquityAsync` | Working |
| Blazor hybrid + lightweight-charts interop | 5 pages, 6 components | Skeleton only |

## 1.2 Gaps this iteration closes

0. **Loss limits are NOT enforced in backtests (critical — explains the past violations).**
   Audit findings, 2026-06-12:
   - `BacktestReplayAdapter.FeedBarsAsync` emits `new AccountUpdate(_initialBalance, _initialBalance, 0, …)`
     on **every bar** (`BacktestReplayAdapter.cs:92`) — equity is hardcoded flat, so `DrawdownTracker`
     computes 0% drawdown for the entire run and `DAILY_DD_LIMIT` can mathematically never fire.
     Realized PnL never reaches the balance either.
   - `RiskManager.EnterProtectionMode` has **zero callers** — protection mode is dead code.
   - `_forceClosePending` is set only for `ProtectionCause.MaxDrawdown` + `ForceCloseOnBreach`;
     a daily breach never flattens open positions.
   - The `MAX_EXPOSURE` pre-check derives the entry price as `intent.LimitPrice ?? intent.StopLoss`
     (`RiskManager.cs:96`) — for market orders the SL distance is measured from itself (0 pips),
     so the new-position risk is 0 and the check is a no-op. It also assumes `1.0m` lots.
   - All checks gate **new entries only**, and only at `>=` the hard limit: floating losses on
     open positions are invisible and unbounded.
1. **No stop/continue intelligence.** Compliance blocks at the *hard* FTMO limit. Nothing slows the
   machine down *before* it gets there, nothing reacts to losing streaks, nothing locks in a good day.
   A bad day can ride all the way to the hard limit.
2. **No measurement loop.** Every "is X better than Y" question requires a human running single
   backtests by hand and eyeballing. No batch runs, no walk-forward, no comparable score, no record
   of what was tried. An agent cannot do research against this system.
3. **Naive trend behavior.** A trending market makes a strategy fire the same signal bar after bar
   (only suppressed implicitly by risk caps). Exits do not distinguish "strong trend, give it room"
   from "trend dying, tighten up". No partial profit taking.
4. **UI is a stub.** `BacktestDashboard.razor` posts a request and shows a run ID. The equity chart
   is never fed, there is no trade feed, no log panel, no bar progress, no per-strategy view, and an
   empty `catch { }` swallows failures. The iter-18 E2 component hierarchy was specified but not built.

---

# Part 2 — Architecture Changes

## 2.1 New Domain Interfaces

### `Interfaces/ITradingGovernor.cs`

The single authority for "should we be trading right now, and at what size". Sits *above*
RiskManager's per-trade checks: RiskManager answers "is this trade legal", the governor answers
"should this machine be trading at all".

```csharp
public interface ITradingGovernor
{
    GovernorDecision Evaluate(GovernorContext context);
    GovernorSnapshot GetSnapshot();
    void OnTradeClosed(TradeResult result);
    void OnBar(DateTime barOpenTimeUtc);     // advances cooling-off counters
    void OnDailyReset();
    void OnWeeklyReset();
}
```

### `Interfaces/IExperimentRunner.cs` (in Application layer, not Domain)

```csharp
public interface IExperimentRunner
{
    Task<ExperimentResult> RunAsync(ExperimentSpec spec, CancellationToken ct);
}
```

### `Interfaces/ISignalGate.cs`

```csharp
public interface ISignalGate
{
    SignalGateResult Check(string strategyId, string symbol, TradeDirection direction, DateTime barTimeUtc);
    void OnPositionOpened(string strategyId, string symbol, TradeDirection direction, DateTime barTimeUtc);
    void OnPositionClosed(string strategyId, string symbol, TradeDirection direction, ExitReason reason, DateTime barTimeUtc);
}
```

## 2.2 New Domain Records

### `RiskAndEquity/GovernorTypes.cs`

```csharp
public enum GovernorTradingState
{
    Normal,        // full size
    Reduced,       // size multiplier applied (loss band or streak)
    SoftStop,      // no new trades today; existing positions managed to completion
    CoolingOff,    // no new trades for N bars after a loss streak
    ProfitLocked,  // daily profit banked; no new trades today
    HardStop       // compliance hard limit (existing behavior, surfaced here for UI)
}

public record GovernorDecision(
    bool AllowNewTrades,
    decimal SizeMultiplier,          // 1.0m in Normal
    GovernorTradingState State,
    string Reason);                  // human-readable, journaled

public record GovernorContext(
    decimal DayRealizedPnLPercent,   // signed, % of day-start equity
    decimal DayStartEquity,
    decimal CurrentEquity,
    int ConsecutiveLosses,
    PropFirmRuleSet Rules);

public record GovernorSnapshot(
    GovernorTradingState State,
    decimal SizeMultiplier,
    int ConsecutiveLosses,
    decimal DayRealizedPnLPercent,
    decimal DistanceToDailyLimitFraction,  // 0.0 = untouched, 1.0 = at hard limit
    string Reason);
```

### `RiskAndEquity/GovernorOptions.cs`

All fractions are **of the active prop-firm limit** (P1). Defaults below are the shipping defaults.

```csharp
public record GovernorOptions
{
    public bool Enabled { get; init; } = true;

    // Loss bands: fractions of MaxDailyLossPercent consumed → size multiplier.
    // Day loss < 0.4×limit → 1.0 | 0.4–0.6×limit → 0.5 | ≥ 0.6×limit → SoftStop (0.0)
    public double[] LossBandFractions { get; init; } = { 0.4, 0.6 };
    public double[] LossBandMultipliers { get; init; } = { 1.0, 0.5 };  // length = bands.Length

    // Loss streak: reduce after 3 consecutive losses, cooling-off after 5.
    public int StreakReduceAt { get; init; } = 3;
    public double StreakMultiplier { get; init; } = 0.5;
    public int StreakPauseAt { get; init; } = 5;
    public int CoolingOffBars { get; init; } = 24;        // on the engine's primary timeframe

    // Profit lock: when the day's realized gain reaches 0.6 × MaxDailyLossPercent
    // (e.g. +3% on a 5% daily-loss account), bank the day: no new trades.
    // Rationale: symmetric to the loss soft-stop band; reuses the same fraction, adds no new constant.
    public bool ProfitLockEnabled { get; init; } = true;
    public double ProfitLockFraction { get; init; } = 0.6;
}
```

**Multiplier composition rule**: band multiplier × streak multiplier, floor at the most restrictive
state. SoftStop / CoolingOff / ProfitLocked always win over multipliers. The governor's multiplier
enters the existing `SizeModifierPipeline` as a fifth modifier (`GovernorSizeModifier`) so sizing
composes exactly like the iter-18 modifiers; the *block* decision is checked by `RiskManager` as a
new validation check (before cheaper checks, fail fast).

### `Trading/ReentryOptions.cs` (added to `IStrategyConfig` strategy JSONs under `reentry`)

```csharp
public record ReentryOptions
{
    public bool BlockWhileSameDirectionOpen { get; init; } = true;  // the no-pyramiding rule
    public int CooldownBarsAfterSl { get; init; } = 5;   // stopped out → the idea was wrong; wait
    public int CooldownBarsAfterTp { get; init; } = 2;   // took profit → trend may resume; shorter wait
    public int CooldownBarsAfterEntry { get; init; } = 3; // throttles flip-flop entries
}
```

### `PositionManagement/PositionManagementOptions.cs` — extend (do not break existing configs)

```csharp
// TrailingOptions gains:
public string Method { get; init; } = "None";   // existing: None | StepPips | AtrMultiple | BreakevenThenTrail
                                                // new:      Structure | SteppedR
public int StructureLookbackBars { get; init; } = 10;   // swing-point window for Structure trail
public double[] SteppedRLevels { get; init; } = { 1.0, 2.0, 3.0 };   // at +1R → BE, +2R → +1R, +3R → +2R

// Ride mode — trend-strength-aware exit gating (new sub-record on PositionManagementOptions):
public record RideOptions
{
    public bool Enabled { get; init; }
    public double AdxFloor { get; init; } = 25;   // while ADX ≥ floor, trailing uses RelaxedAtrMultiple
    public double RelaxedAtrMultiple { get; init; } = 3.0;  // wide trail in strong trend
    // when ADX < floor, trailing reverts to the configured Method/AtrMultiple (tighten as trend dies)
}

// Partial take-profit (new sub-record):
public record PartialTpOptions
{
    public bool Enabled { get; init; }
    public double TriggerRMultiple { get; init; } = 1.0;
    public double CloseFraction { get; init; } = 0.5;     // close half, ride the rest
}
```

### Research domain (`TradingEngine.Application/Experiments/`)

Experiments are an *application* concern (they orchestrate backtests); only the persisted entities
live in Persistence. Nothing goes in Domain.

```csharp
public record ExperimentSpec(
    string Name,
    string Hypothesis,
    string[] Symbols,
    string[] Timeframes,
    string[] Strategies,
    DateOnly From, DateOnly To,
    WalkForwardSpec? WalkForward,           // null = single full-range run per variant
    VariantSpec[] Variants,                 // explicit list — never a combinatorial grid
    ScoringWeights Scoring,
    int MaxRuns = 64);                      // hard cap; spec validation rejects beyond this

public record WalkForwardSpec(int Folds = 4, double TrainFraction = 0.7);

public record VariantSpec(string Label, Dictionary<string, JsonElement>? Overrides);
// Overrides are JSON-path-style keys applied over the loaded strategy/risk config, e.g.
//   "positionManagement.trailing.method": "Structure"
//   "reentry.cooldownBarsAfterSl": 8

public record ScoringWeights(
    double PassProbability = 0.4,
    double ExpectancyR = 0.3,
    double MaxDrawdown = 0.2,       // scored inversely
    double FoldConsistency = 0.1);  // 1 − normalized std-dev of fold scores

public record VariantScore(
    string Label, double Composite, double PassProbability, double ExpectancyR,
    double MaxDrawdownPercent, double FoldConsistency, int TotalTrades,
    IReadOnlyList<FoldScore> Folds);
```

**Persistence entities** (`Experiments`, `ExperimentRuns` tables, EF migration `AddExperiments`):
- `Experiment`: Id (guid), Name, Hypothesis, SpecJson, Status (Pending/Running/Completed/Failed), CreatedUtc, CompletedUtc.
- `ExperimentRun`: Id, ExperimentId (FK), BacktestRunId (FK to existing BacktestRuns), VariantLabel,
  FoldIndex, FoldRole (Train/Test), ScoreJson. Every backtest the harness launches is a normal
  `BacktestRuns` row — the existing Trade Explorer can open any experiment run.

## 2.3 New Domain Events

- `GovernorStateChanged(GovernorTradingState From, GovernorTradingState To, string Reason, DateTime AtUtc)`
  — published on every transition; consumed by the journal, `BacktestProgressStore`, and the UI timeline.
- `PositionPartiallyClosed(Guid PositionId, decimal ClosedLots, decimal RemainingLots, decimal FillPrice, DateTime AtUtc)`.

## 2.4 Protocol Delta (Phase T3 only)

New engine→cBot command `close_partial { positionId, lots }`; cBot replies with a normal `exec`
carrying the partial-close fill and remaining volume. Must be implemented in:
`NetMQBrokerAdapter`, `TradingEngineCBot.cs` (net6.0/C# 10!), `FakeCBot`, `BacktestReplayAdapter`,
`SimulatedBrokerAdapter`. `PositionTracker` must handle a close exec that does not fully close
(reduce lots, keep position open, emit `PositionPartiallyClosed`, do NOT deregister from
`PositionManager`). Document in `docs/iterations/iter-19/PROTOCOL-DELTA.md`.

> ⚠ This is the riskiest item in the iteration — it touches the dedup logic fixed in iter-18
> (`TryWriteExec`, `_commandCloses`). The partial-close exec must not be mistaken for a duplicate
> full close. Write the regression test first (see T3 verification).

---

# Part 3 — Phased Implementation Plan

Phases are grouped by PR. Each phase ends with a verification gate; do not proceed past a red gate.

---

## Phase G — Loss Enforcement + Trading Governor (PR1, "the machine must not blow the account")

### G0 — Loss-Enforcement Audit Fixes (do this FIRST)

The governor is worthless if the equity it watches is fake. Fix the chain bottom-up:

1. **Mark-to-market equity in `BacktestReplayAdapter`.** The adapter already fills and closes its
   own simulated positions — it must track them: balance updates on every close (realized PnL);
   equity = balance + Σ floating PnL of open positions marked at the current bar close. Emit a
   truthful `AccountUpdate` per bar. The existing `EquityPersistenceHandler` then records a real
   curve with no further changes; OBS-04 in OPEN-ISSUES is then *actually* fixed.
2. **Breach watchdog.** `EngineWorker.HandleAccountUpdate` is the single equity choke point. After
   `UpdateEquityLevels`: if daily DD ≥ hard daily limit × `FlattenAtFraction` (default 0.9 —
   flatten *before* FTMO's line, not on it), or max DD likewise → `EnterProtectionMode(cause)`
   (finally giving it callers) and set force-close for **both** `DailyDrawdown` and `MaxDrawdown`
   causes. The flatten path reuses the existing `ConsumeForceClosePending` loop
   (`EngineWorker.cs:164`). Every watchdog action writes a ledger entry (G3).
3. **Fix the exposure pre-check** (`RiskManager.cs:96`): entry price = current mid (the caller
   already has it — extend `Validate`'s inputs), and risk computed on the lots that will actually
   be submitted, not `1.0m`.
4. **Budget-aware entry guard**: reject a new trade when
   `totalOpenWorstCaseRisk + newWorstCaseRisk > RemainingDailyBudget × BudgetUseFraction`, where
   `RemainingDailyBudget = (hardDailyLimit − dailyDdUsed) × daily-DD base equity`. This guarantees
   by arithmetic that even if every open position hits its SL, the day cannot breach. Rejection
   reason `"BUDGET:<numbers>"` (appears in the signal audit and the ledger).

New config record (P1/P2 compliant — three knobs, all fractions, `config/sizing-policy.json`):

```csharp
public record SizingPolicyOptions
{
    public double FlattenAtFraction { get; init; } = 0.9;   // watchdog flatten at 90% of hard daily limit
    public double BudgetUseFraction { get; init; } = 0.25;  // one trade commits ≤ 25% of remaining daily budget
    public double MaxPortfolioHeatRiskMultiples { get; init; } = 3.0; // Σ open worst-case risk ≤ 3 × per-trade risk
}
```

Sizing best practices baked in alongside: `RiskPerTradePercent` defaults reviewed to 0.5% (standard)
/ 0.25% (conservative) in the risk profiles; `CalculateLotSize` result is additionally capped so the
trade fits the budget guard (size down before rejecting — reject only when even min lots don't fit).

**Verification G0 (gate)**:
- Replay over a known losing script produces a *declining* equity curve, equity ≠ balance while
  positions are open (kills the flat-equity bug).
- A scripted catastrophic day triggers the watchdog: positions flattened, day's realized loss ≤
  hard daily limit. **This regression test must FAIL when run against pre-G0 code** — it is the
  proof that the past backtest violations are fixed.
- Unit test pinning the market-order exposure path (non-zero risk computed).
- Budget guard arithmetic test: with 60% of the daily budget consumed, a trade risking > 25% of
  the remainder is rejected/downsized.

### G1 — `TradingGovernorService` (`TradingEngine.Risk/Governor/`)

State machine per the records in §2.2. Pure, clock-free logic: all inputs arrive via
`GovernorContext` / `OnTradeClosed` / `OnBar` / resets. Internal state: current band, consecutive-loss
counter, cooling-off bar countdown, day PnL accumulator (fed from the same source `DrawdownTracker`
uses — do not invent a second PnL bookkeeping path; inject and reuse).

State precedence (highest wins): HardStop > SoftStop > ProfitLocked > CoolingOff > Reduced > Normal.
Daily reset returns SoftStop/ProfitLocked/Reduced→Normal and clears the band, but the consecutive-loss
counter **survives the day boundary** (a streak is a streak) — only a winning trade resets it.

### G2 — Wiring

- `GovernorSizeModifier` added to `SizeModifierPipeline` (reads `GovernorDecision.SizeMultiplier`).
- New first check in `RiskManager`: `GovernorAllowsTrading` — on failure, rejection reason
  `"GOVERNOR:" + decision.Reason` (shows up in BarEvaluations signal audit).
- `EngineWorker`: call `governor.OnBar(...)` once per primary-timeframe bar; hook `OnDailyReset`/
  `OnWeeklyReset` into the existing iter-18 reset scheduling; `OnTradeClosed` from the same place
  `DrawdownTracker` learns about closes.
- `GovernorOptions` loaded from `config/governor.json` via `ConfigLoader`; registered in
  `EngineHostFactory` (singleton).
- Publish `GovernorStateChanged` via `IEventBus`; persist to the PipelineEvents journal.

### G3 — Surfacing + Daily Protection Ledger

- `GET /api/governor/state` returns `GovernorSnapshot` (used by UI Phase U; trivial controller now).
- `GovernorStateChanged` events forwarded into `BacktestProgressStore` channels so backtests show
  governor activity live.
- **`DailyProtectionLedger`** (Persistence; `AddProtectionLedger` migration). The owner's
  requirement: per-day protection decision history, queryable in code and visible in the UI, so
  "why did it fail on this particular day" is answerable in seconds. One row per trading day per
  run (`RunId` nullable → live mode):
  `Date, StartEquity, MinEquity, EndEquity, MaxDailyDdUsedFraction, FinalGovernorState,
  BreachOccurred, TradesOpened, TradesClosed, SignalsBlocked` — plus child table
  `ProtectionLedgerEntries`: `AtUtc, Category (StateChange | EntryBlocked | SizeReduced |
  Flatten | Breach | Reset), Reason, EquityAtTime, DailyDdUsedFraction`.
  Writers: governor transitions, `GOVERNOR:`/`BUDGET:` rejections, watchdog flattens, daily resets
  (which close the day's row). Written through a handler on the event bus — never fire-and-forget.
- `GET /api/protection/days?runId=` (day summaries) and `GET /api/protection/days/{date}?runId=`
  (full decision timeline) — consumed by the U2 daily protection view.

### Verification G (gate)

- Unit tests on the state machine: band transitions at exact fraction boundaries; precedence order;
  streak counter survives daily reset; cooling-off counts bars not wall time; profit lock engages and
  releases on daily reset; multiplier composition (band × streak).
- **Simulation test (the headline test of PR1)**: scripted replay where a strategy is forced to lose
  repeatedly (FakeCBot fills every SL). Assert: (a) realized daily loss never exceeds
  `LossBandFractions[^1] × MaxDailyLossPercent` plus one worst-case open trade's risk;
  (b) sizes halve in the middle band; (c) trading resumes next day; (d) with `Enabled=false` the
  same script loses more — proving the governor is causal.
- `dotnet test` fully green; `DIValidationTests` extended with `ITradingGovernor`.

---

## Phase R — Research Harness (PR2, "measure before you improve")

### R1 — Entities + migration

`Experiment`, `ExperimentRun` entities, `AddExperiments` EF migration, repository
(`IExperimentRepository`) in Persistence. Fresh-DB migration test extended.

### R2 — `ConfigOverrideApplier` (`TradingEngine.Application/Experiments/`)

Applies `VariantSpec.Overrides` (dot-path keys) onto the in-memory config objects *after*
`ConfigLoader` loads them and *before* `EngineHostFactory.Create`. Implementation: serialize loaded
config to `JsonNode`, apply path overrides, deserialize back — avoids reflection fragility. Unknown
path = hard validation error (fail the experiment up front, never silently ignore a typo).

### R3 — `ExperimentRunner` + `WalkForwardSplitter`

- `WalkForwardSplitter`: date range → `Folds` contiguous (train, test) windows, anchored expanding
  or rolling — use rolling with `TrainFraction` per fold; deterministic, unit-tested on known dates.
- `ExperimentRunner`: validates spec (bars exist in `Bars` table for every symbol/timeframe/range —
  fail fast with a per-symbol report if not; run count ≤ MaxRuns); then for each variant × fold:
  build config via R2, run the **in-process replay path** (same entry point the dashboard uses — no
  new backtest plumbing), tag the `BacktestRuns` row with ExperimentId/variant/fold, collect results.
  Sequential execution (determinism > speed this iteration); honest progress via
  `BacktestProgressStore` under the experiment's id.

### R4 — Scoring + report

- `VariantScorer`: per test-fold — pass probability via existing `PassProbabilityEstimator` fed with
  the fold's trade sequence; expectancy in R; max DD% from the fold equity curve; then composite per
  `ScoringWeights` with `FoldConsistency = 1 − (stddev of fold composites / mean)` clamped to [0,1].
  **Train folds are scored but excluded from the composite** — they exist to expose train/test gaps
  in the report (overfitting smell).
- `ExperimentReportWriter`: writes `docs/experiments/<name>-<shortid>/REPORT.md` — hypothesis,
  variant score table (sorted by composite), per-fold breakdown, train-vs-test gap column, and a
  one-line verdict per variant ("beats baseline in 4/4 test folds" / "inconsistent — rejected").
  Also emits `report.json` next to it (machine-readable, for agents).

### R5 — Agent interfaces

- CLI verbs on `TradingEngine.Host`: `experiment run <spec.json>`, `experiment report <id>`,
  `experiment list`. Exit code 0 only if all runs completed.
- REST: `POST /api/experiments` (spec body → 202 + id), `GET /api/experiments`,
  `GET /api/experiments/{id}` (status + scores), `GET /api/experiments/{id}/report` (markdown).
- Example specs checked into `config/experiments/` (e.g. `trailing-method-comparison.json` — used
  as PR3's evidence, see below).

### Verification R (gate)

- Unit: splitter date math; override applier (set/typo/nested); scorer on synthetic trade lists with
  hand-computed expected scores.
- **E2E test**: 2-variant × 2-fold experiment over a small cached bar range runs end-to-end in CI,
  produces deterministic identical scores on two consecutive runs (determinism assertion), report
  files exist and parse.
- Trade Explorer can open an experiment's BacktestRun (FK integrity).

---

## Phase T — Trade Management Intelligence (PR3, "ride trends, don't chase them")

> P3 applies from here on: every behavior added in this phase must ship with an experiment report
> under `docs/experiments/` demonstrating fold-consistent non-inferiority vs baseline. The harness
> from PR2 is the reviewer.

### T0 — Entry/Exit Playbook (quant consolidation before cleverness)

A quant review of all 9 strategies' entry/exit handling, consolidated onto one baked-in baseline.
`config/playbook.json` defines defaults per **(asset class × timeframe)** — asset classes: FX
majors, JPY crosses (extensible to metals/indices later); timeframes: M30, H1, H4, D1. Strategy
JSONs inherit the playbook and may deviate only with P3 experiment evidence.

Baked-in best practices (all volatility-relative — this is the owner's "adaptive numbers,
nothing unachievable": parameters adapt via ATR by construction, no optimizer anywhere):

- **Stops**: ATR-relative only — `max(SlAtrMultiple × ATR(14), structure stop)`; fixed-pip stops
  are removed from the baseline. JPY crosses and higher timeframes get structurally wider
  multiples (a regime fact, not a tunable): majors H1 ≈ 1.5×, JPY H1 ≈ 2.0×, anything D1 ≈ 2.5×.
- **Targets**: trend-following strategies ≥ 2R or trail-out (riding per T2); mean-reversion fixed
  ≈ 1.5R — MR has a natural target (the mean), so ride-mode and trailing do NOT apply to it.
  Breakeven at +1R everywhere.
- **Entry hygiene** (selectivity — the stated edge of this system): ATR-percentile floor (no
  entries when ATR(14) is below its 25th percentile over the trailing 90 days — dead market, spread
  dominates); spread guard (spread ≤ 10% of SL distance); session windows per class on intraday
  timeframes (majors: London + NY; JPY: Tokyo + London overlap; D1 exempt).
- **Consolidation**: duplicated SL/TP/exit code inside individual strategies moves into
  `SlTpResolver` / shared helpers; after T0 a strategy contains *only* its signal logic plus config.

**Audit deliverable**: `docs/iterations/iter-19/PLAYBOOK-AUDIT.md` — a table per strategy: current
entry/exit parameters vs playbook baseline, each deviation either removed or kept with an
experiment ID justifying it. This document is the quant review the owner asked for, in writing.

### T1 — `SignalGateService` (`TradingEngine.Services/`)

Implements `ISignalGate` per §2.1, keyed `(strategyId, symbol, direction)`. Wired into
`EngineWorker` between strategy evaluation and risk validation: a gated signal is recorded in
BarEvaluations with rejection reason `"REENTRY:<cause>"` (visible in the signal audit — this answers
"why didn't it trade" forever after). `ReentryOptions` read from each strategy config; defaults per
§2.2 apply when the JSON block is absent (all 9 existing strategy JSONs gain the block explicitly).
Bar counting uses bar-open times on the strategy's own timeframe, not wall clock.

### T2 — Trailing upgrades (`PositionManager` + `TrailingHelpers`)

- `Structure` method: trail to most recent confirmed swing low (long) / swing high (short) within
  `StructureLookbackBars`, minus/plus the existing breakeven buffer. Swing = local extreme with one
  lower/higher bar on each side (simple, robust 3-bar fractal; do not add a configurable fractal width).
- `SteppedR` method: ratchet SL to `SteppedRLevels[i−1]` R when price reaches `SteppedRLevels[i]` R.
- `RideOptions` gate: when enabled and ADX(14, position timeframe) ≥ `AdxFloor`, the active trailing
  method computes with `RelaxedAtrMultiple` (wide); when ADX drops below floor, revert to configured
  tightness. ADX via existing `IIndicatorService.Adx` (iter-18). This is the "is riding still a good
  idea" intelligence: strong trend → give room; dying trend → tighten.
- All trailing remains monotonic (SL only ever moves in the favorable direction) — assert in tests.

### T3 — Partial take-profit + `close_partial` protocol

Per §2.4. Order of work: (1) regression test proving a partial-close exec is not deduped as a
duplicate full close; (2) `BacktestReplayAdapter` + `FakeCBot` + `SimulatedBrokerAdapter` support;
(3) `PositionTracker` partial handling + `PositionPartiallyClosed` event; (4) cBot command handler
(C# 10 only); (5) `PartialTpOptions` evaluation in `PositionManager` (fires once per position);
(6) PROTOCOL-DELTA.md. The partial close books realized PnL on the closed lots immediately —
`DrawdownTracker`/governor must see it as a (partial) trade result with correct sign.

### T4 — Evidence experiments (uses PR2)

Run and commit three experiments (EURUSD+GBPUSD, H1, ≥ 12 months, 4 folds):
1. `trailing-method-comparison` — baseline vs Structure vs SteppedR vs AtrMultiple+Ride.
2. `reentry-cooldowns` — defaults vs no-gate (proves the gate doesn't destroy expectancy; the
   no-pyramiding rule itself is an owner decision, not up for experiment).
3. `partial-tp` — off vs `1R/50%`.
Reports referenced in the PR description. If a feature loses consistently, **ship it disabled by
default** and say so — the feature still has value as a measured, available option.

### Verification T (gate)

- Unit: gate state transitions per exit reason; swing detection on hand-built bar arrays; SteppedR
  ratchet exactness; ride-gate switching at the ADX floor; monotonic-SL property test across all
  methods; partial-TP single-fire.
- Simulation: synthetic 100-bar trend → with gate on, exactly one position; chandelier+ride exits
  later than tight ATR trail on the same data; partial TP produces two execs and correct summed PnL.
- Full suite green incl. iter-18 dedup regression tests.

---

## Phase U — UI Maturity (PR4, "see what the machine is doing")

> Independent of phases G/R/T in code (it consumes their events/APIs where present, degrades
> gracefully where absent). **Can be built in parallel by a second agent** once PR1 merges, if
> sprint capacity allows.

### U1 — Live Backtest Dashboard (rewrite `BacktestDashboard.razor` to the iter-18 E2 spec, plus)

- Consume `BacktestProgressStore.GetChannel(runId)` via `await foreach` + `InvokeAsync(StateHasChanged)`,
  throttled: UI batches events and re-renders at most every 250 ms (a 50k-bar replay must not melt
  the SignalR circuit — batch, don't per-event render).
- Components: **BarProgress** (bars processed / total, current sim time, bars/sec);
  **LiveEquityCurve** (fed incrementally, not refetched); **LiveTradeFeed** (each trade as it
  closes: time, strategy, dir, lots, entry→exit, PnL, R, exit reason — color-coded);
  **LiveLogPanel** (structured events, filterable by category: Signal / Order / Risk / Governor /
  System and by level; replaces "bad log showing"); **GovernorBanner** (current state + reason,
  from `GovernorStateChanged` events); **StreamingOhlcChart** — extend `OhlcTradeChart` interop with
  `appendBars(batch)` so candles draw as the replay progresses (every batch, not every bar).
- Multi-select symbol/timeframe to match `Run.cshtml` capability; remove the empty `catch { }`;
  surface start failures in the UI.
- Prerequisite plumbing: ensure `EngineWorker`/handlers actually publish trade-closed, bar-progress
  (every Nth bar), and governor events into `BacktestProgressStore` as typed
  `BacktestProgressEvent { Category, Level, SimTimeUtc, Payload }` records — today the store carries
  mostly raw lines. Define the record once in Application; both SSE (legacy Progress page) and Blazor
  consume it.

### U2 — Run Detail (upgrade TradeExplorer or new `/runs/{id}` page)

Implements the layout from `docs/OPEN-ISSUES.md` Part 8: equity curve with trade markers; summary
stats; trades table; **signal audit** (BarEvaluations: bars evaluated / signals / rejections grouped
by reason — REENTRY and GOVERNOR reasons from this iteration appear here); **per-strategy breakdown**
(OBS-05: trades, WR, net R per strategy); **governor timeline** (state changes across the run).
Backed by extensions to `IBacktestQueryService` (`GetSignalAuditAsync`, `GetStrategyBreakdownAsync`,
`GetGovernorTimelineAsync` — the latter reads the PipelineEvents journal).

Plus the **Daily Protection view** (`/protection`, also embedded as a tab in run detail): a
calendar/table of trading days color-coded by final governor state (clean / reduced / soft-stop /
profit-locked / **breach in red**), with day-level columns (start→end equity, min equity, max
daily DD used, signals blocked). Clicking a day opens the full decision timeline from
`ProtectionLedgerEntries` — every block, size reduction, state change, and flatten with reason and
equity at that moment. Backed by the G3 `/api/protection/*` endpoints. This is the owner's
"pinpoint why it failed on a particular day" requirement; it must work for both backtest runs and
the live engine.

### U3 — Experiment Browser (`/experiments`)

List experiments (status, top variant, composite); detail view: score table, per-fold bars,
train-vs-test gap highlighting, link each cell to its run's detail page (U2); "Run spec…" textarea
posting to `POST /api/experiments` for quick agent-less use.

### Verification U (gate)

- WebSmokeTests extended: all new routes 200; progress channel typed-event round-trip test.
- Manual checklist (recorded in HANDOVER): start a 3-month EURUSD H1 replay from the dashboard and
  observe live bars, live equity, ≥1 trade appearing in the feed, log filtering, governor banner;
  open the run detail; open an experiment report.
- OPEN-ISSUES updates: OBS-01, OBS-02, OBS-03 (lifecycle via typed events), OBS-05 marked fixed.

---

# Part 4 — Shipping Strategy

## Four PRs, 2–3 sprints

| PR | Phases | Branch | Sprint | Theme |
|----|--------|--------|--------|-------|
| PR1 | G0–G3 | `iter/19-governor` | 1 | Protect: fix broken loss enforcement, then the machine cannot lose past the threshold |
| PR2 | R1–R5 | `iter/19-research-lab` | 1–2 | Measure: agents run experiment campaigns |
| PR3 | T0–T4 | `iter/19-trade-intelligence` | 2 | Improve: playbook consolidation, ride trends, gated re-entry — with PR2 evidence |
| PR4 | U1–U3 | `iter/19-live-ui` | 2–3 | Observe: live streaming dashboard, run detail, protection ledger, experiment browser |

Order rationale: the governor is the safety net everything else trades under; the research lab must
exist *before* the cleverness PR so every trailing/re-entry idea is measured, not guessed (owner's
stated philosophy: selectivity + quant-verified ideas, not market-edge hunting). PR4 is code-independent
and may run in parallel with PR2/PR3 by a second agent after PR1 merges.

Carry-over from iter-18 folded in: F2 FakeCBot extensions land where first needed (G verification:
fill-every-SL scripting; T3: partial close). F3 live-mode re-verification is PR4's final DoD item.

---

# Part 5 — Definition of Done

### PR1 — Loss Enforcement + Governor
- [ ] G0: replay adapter mark-to-market equity (regression test FAILS on pre-G0 code)
- [ ] G0: breach watchdog flattens on daily AND max-DD cause; `EnterProtectionMode` has callers
- [ ] G0: exposure pre-check fixed (market-order no-op pinned by unit test, sized lots used)
- [ ] G0: budget-aware entry guard + downsize-before-reject; `config/sizing-policy.json` (3 knobs, all fractions — P1/P2 audit)
- [ ] State machine unit tests (bands, precedence, streak-across-days, cooling-off, profit lock)
- [ ] Causal simulation test: governor on vs off on a scripted losing sequence
- [ ] `GovernorSizeModifier` in pipeline; `GOVERNOR:`/`BUDGET:` rejection reasons in BarEvaluations
- [ ] `config/governor.json` + ConfigLoader + DIValidation
- [ ] `GovernorStateChanged` journaled and in progress store
- [ ] `DailyProtectionLedger` + `AddProtectionLedger` migration + `/api/protection/*` endpoints; ledger rows written for a full simulated bad day
- [ ] Full suite green; no `DateTime.UtcNow`; all thresholds fractions of PropFirmRuleSet (P1 audit)

### PR2 — Research Lab
- [ ] `AddExperiments` migration + fresh-DB test
- [ ] Spec validation: missing bars fail fast per symbol; MaxRuns cap; unknown override path = error
- [ ] Deterministic E2E: same spec twice → identical scores
- [ ] CLI + REST verbs; example specs in `config/experiments/`
- [ ] REPORT.md + report.json generated; train folds excluded from composite

### PR3 — Trade Intelligence
- [ ] T0: `config/playbook.json` (asset class × timeframe baselines) + inheritance in config loading; all 9 strategies inherit
- [ ] T0: `PLAYBOOK-AUDIT.md` — per-strategy deviations removed or justified by experiment ID; duplicated exit code consolidated into SlTpResolver/helpers
- [ ] SignalGate wired pre-risk; `REENTRY:` reasons in signal audit; all 9 strategy JSONs updated
- [ ] Structure + SteppedR + RideOptions; monotonic-SL property test
- [ ] `close_partial` end-to-end (replay, FakeCBot, simulated, cBot net6.0/C#10) + dedup regression
- [ ] PROTOCOL-DELTA.md written
- [ ] Three evidence experiments committed under `docs/experiments/`; losing features default-off (P3)

### PR4 — Live UI
- [ ] Typed `BacktestProgressEvent`; trade/bar/governor events published during replay
- [ ] Dashboard: live bars, equity, trade feed, filterable log, governor banner; 250 ms render batching
- [ ] Run detail: signal audit + per-strategy breakdown + governor timeline
- [ ] Daily Protection view: color-coded day calendar + per-day decision-timeline drill-down (backtest + live)
- [ ] Experiment browser
- [ ] WebSmokeTests green; manual checklist in HANDOVER; OBS-01/02/03/05 closed in OPEN-ISSUES
- [ ] F3: live-mode re-verification documented

---

# Part 6 — Files to Create / Modify (summary)

## New files

```
Domain:    Interfaces/ITradingGovernor.cs, Interfaces/ISignalGate.cs,
           RiskAndEquity/GovernorTypes.cs, RiskAndEquity/GovernorOptions.cs,
           RiskAndEquity/SizingPolicyOptions.cs, Trading/ReentryOptions.cs,
           Events/GovernorStateChanged.cs, Events/PositionPartiallyClosed.cs
Risk:      Governor/TradingGovernorService.cs, Sizing/GovernorSizeModifier.cs
Services:  SignalGateService.cs, (TrailingHelpers additions)
Application: Experiments/{ExperimentSpec.cs, ExperimentRunner.cs, WalkForwardSplitter.cs,
           ConfigOverrideApplier.cs, VariantScorer.cs, ExperimentReportWriter.cs,
           IExperimentRunner.cs, BacktestProgressEvent.cs}
Persistence: Entities/{Experiment.cs, ExperimentRun.cs, DailyProtectionLedger.cs,
           ProtectionLedgerEntry.cs}, Migrations/{AddExperiments, AddProtectionLedger},
           ExperimentRepository.cs, ProtectionLedgerRepository.cs
Web:       Api/{GovernorController.cs, ExperimentsController.cs, ProtectionController.cs},
           Components/Pages/{ExperimentBrowser.razor, RunDetail.razor, ProtectionLedger.razor},
           Components/Shared/{LiveTradeFeed.razor, LiveLogPanel.razor, GovernorBanner.razor,
           BarProgress.razor, DayDecisionTimeline.razor}
Config:    governor.json, sizing-policy.json, playbook.json, experiments/*.json
Docs:      iterations/iter-19/{PLAN.md, PROTOCOL-DELTA.md, PLAYBOOK-AUDIT.md, HANDOVER.md},
           experiments/**/REPORT.md
```

## Modified (key)

```
BacktestReplayAdapter (G0: mark-to-market balance/equity, truthful AccountUpdate per bar),
RiskManager (G0: Validate entry-price fix + budget guard; governor check; watchdog wiring),
SizeModifierPipeline registration, EngineWorker (breach watchdog in HandleAccountUpdate,
OnBar/resets, SignalGate, progress events), EngineHostFactory (all new registrations),
risk profile JSONs (RiskPerTradePercent review), PositionManager +
TrailingHelpers (Structure/SteppedR/Ride/PartialTp), PositionTracker (partial close),
NetMQBrokerAdapter + TradingEngineCBot.cs + FakeCBot + BacktestReplayAdapter + SimulatedBrokerAdapter
(close_partial), PositionManagementOptions (extend), IStrategyConfig configs ×9 (reentry block),
IBacktestQueryService (+3 methods), BacktestProgressStore (typed events),
BacktestDashboard.razor (rewrite), OhlcTradeChart.razor (appendBars), Program.cs (Web: controllers)
```
