# Iteration 18 — Risk Overhaul, Strategy Bank & Blazor Dashboard

**Branch**: `iter/18-strategy-bank-blazor`
**Base**: `iter/17-deterministic-pipeline` (after merge to main)
**Written**: 2026-06-11
**Status**: Agreed — owner decisions locked 2026-06-11 (see Decisions Log)

---

## Decisions Log (owner-confirmed, 2026-06-11)

1. **UI**: Blazor Server **hybrid** — new SPA pages mounted alongside existing Razor Pages; old pages removed in a later cleanup PR, not this iteration.
2. **Ensemble**: Regime filters + opt-in performance rotation only. No voting/meta-strategy this iteration (revisit once per-strategy analytics from this iteration are trustworthy).
3. **Walk-forward optimization**: Deferred to iter-19. This iteration ships the analytics (Monte Carlo, comparison, daily-PnL endpoints) it will build on.
4. **Strategies**: All four proposed new strategies **plus SuperTrend** → 9 strategies total. Additionally, SL/TP/trailing/breakeven must be unified into ONE configurable position-management layer applied to ALL strategies (per-strategy JSON block + global overrides) — Phase D3.

### Implementation Q&A (locked 2026-06-11)

5. **cBot platform**: stays `net6.0` (cTrader requirement). `LangVersion 10` is already in the csproj — the old "C# 6" rule is obsolete and removed from this plan. No C# 11+ features; no engine project references; protocol semantics unchanged.
6. **Multi-value indicators**: multiple suffixed keys per indicator in the flat `IndicatorValues` dictionary — see the key convention table in §2.4. The current BB `.Middle`-only gap is fixed as part of C4.
7. **Strategy scenario tests**: comprehensive — every new strategy must pass the full case matrix in Verification C, not just a happy-path sign-off.
8. **Phase F**: included in THIS iteration as PR4. The iteration is not done until PR4 merges.
9. **EngineWorker constructor bloat**: introduce an `EngineWorkerDependencies` parameter-object record (Phase A0) before any new dependencies land.
10. **IStrategyConfig**: add all four fields — `Enabled` (closing the existing interface gap), `RegimeFilter`, `OrderEntry`, `PositionManagement`.

---

## Read First

- `docs/reference/SYSTEM-REFERENCE.md` — full system reference (canonical; start here)
- `docs/iterations/iter-17/PLAN.md` + `HANDOVER.md` — previous iteration; lock-step protocol is the foundation, do not touch it
- `docs/OPEN-ISSUES.md` — canonical issues tracker; mark items fixed as you go

**Rules for the implementing agent:**
- Phases are in dependency order. Do not start Phase C before A is verified, do not start Phase E before the API surface it depends on (B analytics endpoints) exists.
- Every phase ends with its Verification block passing. Paste actual command output in `HANDOVER.md`.
- All money/price arithmetic in `decimal`. `double` only for indicator values, ATR ratios, and regime scores.
- No `Thread.Sleep`. No `DateTime.Now`/`DateTime.UtcNow` — use `IEngineClock`.
- `TradingEngineCBot.cs` targets **net6.0 / C# 10** (cTrader Automate requirement — verified: `<TargetFramework>net6.0</TargetFramework>`, `<LangVersion>10</LangVersion>` in the csproj). C# 10 features are fine; **no C# 11+ syntax** (raw string literals, `required` members, list patterns). Hard rules that DO remain: no references to engine projects, single-file cBot, lock-step protocol semantics unchanged.
- `TradingEngine.Domain` gets interfaces and value objects only. Zero infrastructure dependencies.
- Update `docs/OPEN-ISSUES.md` as items are fixed: mark `✅ Fixed (Iteration 18)`.
- Update `docs/reference/SYSTEM-REFERENCE.md` sections 2, 3, 4, 6, 7, and 8 at each PR boundary.
- Existing Razor Pages must remain functional throughout Phase E (additive, never delete until cleanup PR).

---

# Part 1 — System Analysis

## 1.1 Strengths to Preserve

| Strength | Why it matters |
|----------|----------------|
| Deterministic lock-step protocol (iter-17) | Every backtest is identical given the same data. This is the non-negotiable foundation. Nothing here touches the protocol except the additive extensions in Phase D1. |
| Config-driven everything | `config/` holds strategies, risk profiles, prop firm rules. All new features in this iteration must add JSON files, not hardcode. |
| Clean domain boundary | `TradingEngine.Domain` has zero infra deps. The analyser rule enforces this; do not disable it. |
| 87+ unit tests + simulation harness | All must pass at every PR boundary. Never merge a PR that breaks an existing test. |
| Single composition root (`EngineHostFactory`) | All DI happens here. Register every new service here — do not create parallel DI roots. |
| `IStrategy` contract | All four existing strategies are clean implementations. Every new strategy must satisfy the same contract — no special-casing in the engine. |

## 1.2 Weaknesses to Fix

| # | Weakness | Impact | Phase |
|---|----------|--------|-------|
| W1 | `DrawdownTracker` only tracks daily + max DD | Weekly/monthly FTMO limits never enforced | A1 |
| W2 | Prop-firm compliance logic embedded inside `RiskManager` | Can't swap firms, can't simulate pass criteria, hard to test in isolation | A2 |
| W3 | No ATR-based risk scaling | Lot sizes unchanged in extreme volatility — overly large or small relative to current market | A3 |
| W4 | No currency exposure aggregation | Long EURUSD + Long GBPUSD = silently double-exposed to USD weakness | A4 |
| W5 | `NewsFilter` is a stub (always false) | FTMO news rule is config-present but never enforced | D2 |
| W6 | Schema changes via raw `ALTER TABLE` in `Program.cs` | Not auditable, breaks CI, not reversible | B1 |
| W7 | `TradeResult` commissions/MAE/MFE zeroed | Performance analytics incomplete; equity curve uses synthetic values | B3 |
| W8 | Only 4 strategies, no rotation or switching | Cannot run portfolio-mode backtests; no regime-aware strategy selection | C1–C5 |
| W9 | No market regime detection | TrendBreakout fires in ranging markets; MeanReversion fires in strong trends | C2 |
| W10 | All orders are market orders | Sub-optimal entries; no configurable slippage tolerance | D1 |
| W11 | UI is Razor Pages with no real-time charts | No OHLC trade visualization, no live equity updates in components, no comparison tools | E1–E6 |
| W12 | Weekly/monthly equity snapshots missing | Cannot build long-horizon performance analytics | B2 |
| W13 | `EngineWorker` never calls weekly/monthly resets | W1 is moot without triggers wired in the engine loop | A5 |
| W14 | SL/TP/trailing params duplicated in every strategy's `Parameters` record (`SlAtrMultiple`, `TpRrMultiple`, `TrailingMethod`) | No global overrides, inconsistent tuning, breakeven barely configurable | D3 |

---

# Part 2 — Architecture Changes

## 2.1 New Domain Interfaces

Add to `src/TradingEngine.Domain/Interfaces/`:

### `IPropFirmComplianceService.cs`
```csharp
/// <summary>Pluggable prop-firm rule validator, decoupled from core risk logic.</summary>
public interface IPropFirmComplianceService
{
    ComplianceResult ValidateSignal(TradeIntent intent, ExtendedRiskState state, RiskProfile profile);
    ComplianceResult ValidateAtBarOpen(ExtendedRiskState state, DateTime utcNow);
    PassProbabilityEstimate EstimatePassProbability(PassProbabilityInput input);
    void OnDailyReset(DateTime utcNow, decimal equity);
    void OnWeeklyReset(DateTime utcNow, decimal equity);
    void OnMonthlyReset(DateTime utcNow, decimal equity);
    ComplianceSummary GetSummary();
}
```

### `IStrategyBank.cs`
```csharp
/// <summary>Dynamic strategy registry with regime filtering and performance tracking.</summary>
public interface IStrategyBank
{
    IReadOnlyList<IStrategy> GetActive(Symbol symbol, Timeframe timeframe, MarketRegime regime);
    IReadOnlyList<IStrategy> GetAll();
    void Enable(string strategyId);
    void Disable(string strategyId);
    void NotifyResult(string strategyId, TradeResult result);
    StrategyBankSnapshot GetSnapshot();
}
```

### `IRegimeDetector.cs`
```csharp
/// <summary>Classifies current market conditions for a given symbol.</summary>
public interface IRegimeDetector
{
    MarketRegime Detect(Symbol symbol, IReadOnlyList<Bar> bars,
        IReadOnlyDictionary<string, double> indicators);
}
```

### `ICurrencyExposureTracker.cs`
```csharp
/// <summary>Tracks open risk aggregated by base/quote currency to detect correlated overexposure.</summary>
public interface ICurrencyExposureTracker
{
    void Open(Guid positionId, string baseCurrency, string quoteCurrency,
              TradeDirection direction, decimal riskAmount);
    void Close(Guid positionId);
    CurrencyExposureSnapshot GetSnapshot();
    bool WouldExceedLimit(string baseCurrency, string quoteCurrency,
                          TradeDirection direction, decimal newRisk,
                          double maxPercent, decimal equity);
}
```

### `IPassProbabilityEstimator.cs`
```csharp
/// <summary>Monte Carlo estimator for prop-firm challenge pass probability.</summary>
public interface IPassProbabilityEstimator
{
    PassProbabilityEstimate Estimate(PassProbabilityInput input);
}
```

### `ISizeModifier.cs`
```csharp
/// <summary>Composable lot-size scale factor. Enabled modifiers are multiplied together,
/// then the product is clamped to the profile's configured min/max combined scale.</summary>
public interface ISizeModifier
{
    string Name { get; }
    double ComputeScale(SizeModifierContext context);
}
```

## 2.2 New Domain Value Objects / Records

Add to `src/TradingEngine.Domain/`:

### `RiskAndEquity/ExtendedRiskState.cs`
Replaces `RiskState` (keep `RiskState` as a type alias or keep it for backward compat — the engine currently stores `CurrentState` as `RiskState`; migration is additive):
```csharp
public record ExtendedRiskState
{
    public bool TradingAllowed { get; init; }
    public bool InProtectionMode { get; init; }
    public string? ProtectionReason { get; init; }
    public decimal DailyDrawdownUsed { get; init; }
    public decimal WeeklyDrawdownUsed { get; init; }
    public decimal MonthlyDrawdownUsed { get; init; }
    public decimal MaxDrawdownUsed { get; init; }
    public decimal DrawdownVelocity { get; init; }       // DD change per day, rolling 5-day window
    public bool IsDrawdownAccelerating { get; init; }    // velocity > 0.001 (0.1%/day)
    public CurrencyExposureSnapshot CurrencyExposure { get; init; } = new();
    public DateTime? ProtectionUntilUtc { get; init; }
    public decimal DailyDrawdownLimit { get; init; }
    public decimal MaxDrawdownLimit { get; init; }
}
```

### `MarketData/MarketRegime.cs`
```csharp
public enum MarketRegime { Unknown, Trending, Ranging, HighVolatility, LowVolatility }
```

### `Compliance/ComplianceResult.cs`
```csharp
public record ComplianceResult(bool Passed, IReadOnlyList<string> Violations, ComplianceSeverity Severity);
public enum ComplianceSeverity { None, Warning, Block, HardStop }
```

### `Compliance/PassProbabilityEstimate.cs`
```csharp
public record PassProbabilityEstimate
{
    public double ProbabilityOfPass { get; init; }
    public double ProbabilityOfDailyBreach { get; init; }
    public double ProbabilityOfMaxBreach { get; init; }
    public int ExpectedDaysToTarget { get; init; }
    public decimal ProjectedFinalEquity { get; init; }
    public string Recommendation { get; init; } = "";
}

public record PassProbabilityInput
{
    public decimal CurrentEquity { get; init; }
    public decimal InitialBalance { get; init; }
    public double ProfitTargetPercent { get; init; }
    public double MaxDailyLossPercent { get; init; }
    public double MaxTotalLossPercent { get; init; }
    public int DaysRemaining { get; init; }
    public IReadOnlyList<decimal> HistoricalDailyPnL { get; init; } = [];
    public int MonteCarloRuns { get; init; } = 10_000;
}
```

### `Compliance/ComplianceSummary.cs`
```csharp
public record ComplianceSummary
{
    public bool IsInChallenge { get; init; }
    public decimal CurrentEquity { get; init; }
    public decimal TargetEquity { get; init; }
    public decimal MaxDrawdownFloor { get; init; }
    public double EstimatedPassProbability { get; init; }
    public int TradingDaysMet { get; init; }
    public int TradingDaysRequired { get; init; }
    public string Status { get; init; } = "";   // "OnTrack", "Warning", "AtRisk", "Failed"
}
```

### `Compliance/CurrencyExposureSnapshot.cs`
```csharp
public record CurrencyExposureSnapshot
{
    public static readonly CurrencyExposureSnapshot Empty = new();
    public IReadOnlyDictionary<string, decimal> NetRiskByCurrency { get; init; } =
        new Dictionary<string, decimal>();
    public decimal TotalCorrelatedRisk { get; init; }
}
```

### `RiskAndEquity/SizeModifierContext.cs`
```csharp
public record SizeModifierContext
{
    public required EquitySnapshot Equity { get; init; }
    public required RiskProfile Profile { get; init; }
    public required TradeIntent Intent { get; init; }
    public double? CurrentAtr { get; init; }
    public IReadOnlyList<double> AtrBaseline { get; init; } = [];
    public TimeSpan UtcTimeOfDay { get; init; }       // from IEngineClock, never DateTime.UtcNow
    public int StrategyWinStreak { get; init; }        // populated by EngineWorker from strategy Stats
    public int StrategyLossStreak { get; init; }
}
```

### `StrategyBank/StrategyBankSnapshot.cs`
```csharp
public record StrategyBankSnapshot
{
    public IReadOnlyList<StrategyStatus> Strategies { get; init; } = [];
}

public record StrategyStatus
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    public required StrategyPerformanceStats Stats { get; init; }
}

public record StrategyPerformanceStats
{
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public decimal TotalPnL { get; init; }
    public decimal ProfitFactor { get; init; }
    public int WinStreak { get; init; }
    public int LossStreak { get; init; }
    public MarketRegime LastRegime { get; init; }
    public double WinRate => TotalTrades > 0 ? (double)WinningTrades / TotalTrades : 0;
}
```

## 2.3 New Config Records

### Extend existing `PropFirmRuleSet.cs` (add fields)
```csharp
// Add to existing PropFirmRuleSet record:
public double MaxWeeklyLossPercent { get; init; }      // e.g. 0.04
public double MaxMonthlyLossPercent { get; init; }     // e.g. 0.08
public double ProfitTargetPercent { get; init; }       // e.g. 0.10
public int MinTradingDays { get; init; }               // e.g. 4
public bool RequireProfitTarget { get; init; }         // true for challenge, false for funded
public GracePeriodOptions GracePeriod { get; init; } = new();
```

### New `GracePeriodOptions.cs` (in Domain/RiskAndEquity/)
```csharp
public record GracePeriodOptions
{
    public bool Enabled { get; init; } = false;
    public int MaxGraceDaysPerMonth { get; init; } = 1;
    public double MaxDDForGrace { get; init; } = 0.02;
}
```

### New `StrategyRotationOptions.cs` (in Domain/StrategyBank/)
```csharp
public record StrategyRotationOptions
{
    public RotationMode Mode { get; init; } = RotationMode.Disabled;
    public int EvaluationWindowDays { get; init; } = 30;
    public double MinWinRateToKeepActive { get; init; } = 0.35;
    public double MinProfitFactorToKeepActive { get; init; } = 0.8;
    public int MinTradesForEvaluation { get; init; } = 10;
}
public enum RotationMode { Disabled, PerformanceBased, RegimeBased, Combined }
```

### New `RegimeFilterOptions.cs` (in Domain/StrategyBank/)
```csharp
public record RegimeFilterOptions
{
    public bool AllowTrending { get; init; } = true;
    public bool AllowRanging { get; init; } = true;
    public bool AllowHighVolatility { get; init; } = true;
    public bool AllowLowVolatility { get; init; } = true;
    public bool AllowUnknown { get; init; } = true;
}
```

### New `OrderEntryOptions.cs` (in Domain/Trading/)
```csharp
public record OrderEntryOptions
{
    public OrderEntryMethod Method { get; init; } = OrderEntryMethod.Market;
    public double LimitOffsetPips { get; init; } = 0;
    public double MaxSlippagePips { get; init; } = 2.0;
    public int LimitOrderExpiryBars { get; init; } = 3;
    public int MaxMarketRetries { get; init; } = 2;
}
public enum OrderEntryMethod { Market, LimitOffset, MarketWithSlippage }
```

### New `SizeModifierOptions.cs` + sub-options (in Domain/RiskAndEquity/)

Umbrella record on `RiskProfile`. Drawdown scaling keeps its existing `DrawdownScaleThreshold` /
`DrawdownScaleFloor` profile fields — the drawdown modifier just wraps the existing `DrawdownScaler`.

```csharp
public record SizeModifierOptions
{
    public double MinCombinedScale { get; init; } = 0.1;
    public double MaxCombinedScale { get; init; } = 1.5;
    public AtrScalingOptions AtrRegime { get; init; } = new();
    public TimeOfDayScalingOptions TimeOfDay { get; init; } = new();
    public ConfidenceScalingOptions Confidence { get; init; } = new();
}

public record AtrScalingOptions
{
    public bool Enabled { get; init; } = false;
    public int AtrPeriod { get; init; } = 14;
    public int AtrBaselinePeriod { get; init; } = 100;
    public double HighAtrMultiple { get; init; } = 1.5;
    public double LowAtrMultiple { get; init; } = 0.5;
    public double HighAtrSizeScale { get; init; } = 0.7;
    public double LowAtrSizeScale { get; init; } = 1.2;
    public double ExtremeAtrSizeScale { get; init; } = 0.3;  // ATR > 3× baseline
}

public record TimeOfDayScalingOptions
{
    public bool Enabled { get; init; } = false;
    public IReadOnlyList<TimeOfDayScaleWindow> Windows { get; init; } = [];
}
public record TimeOfDayScaleWindow(TimeSpan StartUtc, TimeSpan EndUtc, double Scale);

public record ConfidenceScalingOptions
{
    public bool Enabled { get; init; } = false;
    public int LossStreakThreshold { get; init; } = 3;
    public double LossStreakScale { get; init; } = 0.5;
    public int WinStreakThreshold { get; init; } = 5;
    public double WinStreakScale { get; init; } = 1.2;   // capped by MaxCombinedScale
}
```

## 2.4 Modifications to Existing Types

### `TradeIntent.cs` — add order entry options
```csharp
// Add one optional field (null = default Market):
public OrderEntryOptions? Entry { get; init; }
```

### `IStrategyConfig` (existing interface) — add four fields
```csharp
bool Enabled { get; }                                    // closes an existing gap: present on every
                                                         // config record, missing from the interface
RegimeFilterOptions RegimeFilter { get; }
OrderEntryOptions OrderEntry { get; }
PositionManagementOptions PositionManagement { get; }   // resolved at load time, never null (D3)
```

All nine `*Config.cs` records (4 existing + 5 new) must satisfy these (existing records already have
`Enabled` — only the interface declaration is new). `StrategyBankService` uses config `Enabled` as
each strategy's **initial** state; runtime `Enable()`/`Disable()` calls override it for the session.

### `RiskProfile.cs` — add two fields
```csharp
public double MaxExposurePerCurrencyPercent { get; init; } = 0.05;
public SizeModifierOptions SizeModifiers { get; init; } = new();
```

### `IndicatorRequest.cs` — add timeframe discriminator
```csharp
// Extend the record to include Timeframe so multi-tf strategies can request H4 indicators:
public record IndicatorRequest(string Key, IndicatorType Type, int Period,
                               Timeframe Timeframe = Timeframe.H1);
```

All existing `RequiredIndicators` arrays in existing strategies omit the `Timeframe` param — defaults to H1, backward compatible.

### `IndicatorType` enum — add ADX and SuperTrend
```csharp
public enum IndicatorType { Ema, Sma, Rsi, Atr, BollingerBands, Macd, Adx /* new */, SuperTrend /* new */ }
```

### Multi-value indicator key convention (SkenderIndicatorService)

`MarketContext.IndicatorValues` stays a flat `IReadOnlyDictionary<string, double>`. Indicators that
produce multiple series emit **one entry per component**, suffixed off the request key. The
unsuffixed key is the primary value.

| Indicator | Keys emitted |
|-----------|--------------|
| BollingerBands | `BB_{p}_{σ}` (= Middle, the primary), `BB_{p}_{σ}_Upper`, `BB_{p}_{σ}_Lower` |
| MACD | `MACD_{f}_{s}_{sig}` (= MACD line), `MACD_{f}_{s}_{sig}_Signal`, `MACD_{f}_{s}_{sig}_Histogram` |
| SuperTrend | `ST_{p}_{m}` (= line price), `ST_{p}_{m}_Direction` (+1.0 bullish / −1.0 bearish) |

Single-value indicators (EMA, SMA, RSI, ATR, ADX) are unchanged. **The current implementation only
stores BB `.Middle`** — emitting `_Upper`/`_Lower` is a required fix in C4 (BollingerSqueeze depends
on it). Cache entries remain keyed per `(symbol, timeframe, indicator, period, barCount)` per the
existing Skender caching rule.

---

# Part 3 — Phased Implementation Plan

---

## Phase A — Risk & Compliance Foundation

**Goal**: Extend risk layer with weekly/monthly DD, pluggable compliance, ATR-based sizing, and currency exposure limits. Zero breaking changes — only additive.

---

### A0 — EngineWorker Dependency Grouping (do this FIRST)

`EngineWorker`'s constructor already takes ~20 dependencies, and this iteration adds more (size-modifier
context inputs in PR1; `IStrategyBank` and `IRegimeDetector` in PR2). Introduce a parameter-object
**before** any new dependency lands so subsequent diffs stay readable.

**New file**: `src/TradingEngine.Host/EngineWorkerDependencies.cs`

```csharp
public sealed record EngineWorkerDependencies
{
    public required MarketServices Market { get; init; }
    public required RiskServices Risk { get; init; }
    public required StrategyServices Strategies { get; init; }
    public required PersistenceServices Persistence { get; init; }
}
// Nested groups (sealed records in the same file is acceptable here as a documented exception
// to one-type-per-file, OR split into four files — implementing agent's choice):
//   MarketServices      — IBrokerAdapter factory, ISymbolInfoRegistry, IIndicatorService, CrossRateStore, IEngineClock
//   RiskServices        — IRiskManager, IPropFirmComplianceService, ICurrencyExposureTracker, SizeModifierPipeline, IRiskProfileResolver
//   StrategyServices    — StrategyRegistry (→ IStrategyBank in PR2), IRegimeDetector (PR2), IPositionManager, ITrailingStopService
//   PersistenceServices — IEventBus, IPipelineJournal, persistence handlers
```

- Built in **one** place: `EngineHostFactory` (stays the sole composition root), registered as a singleton.
- `EngineWorker` constructor becomes `(EngineWorkerDependencies deps, EngineRunContext ctx, ILogger<EngineWorker> logger)`.
- Group membership above is indicative — map the actual ~20 existing parameters into whichever group fits; do not change any behavior.

**Verification A0**: `dotnet build --no-incremental` clean; all 87+ existing tests pass unchanged. This is a pure mechanical refactor — if any test needs modification beyond construction plumbing, something went wrong.

---

### A1 — Extended DrawdownTracker

**File**: `src/TradingEngine.Risk/DrawdownTracker.cs` (modify existing)

Changes:
- Add `WeeklyStartEquity`, `MonthlyStartEquity` fields (set during `Initialize()`)
- Add `CurrentWeeklyDrawdown` and `CurrentMonthlyDrawdown` computed properties (same formula as daily, but using weekly/monthly start)
- Add `DrawdownVelocity` property: rolling 5-day window of daily max-DD deltas stored as `Queue<(DateTime, decimal)>`. Velocity = average daily increase in `CurrentMaxDrawdown` over window.
- Add `IsAccelerating` property: `DrawdownVelocity > 0.001m`
- Add `OnWeeklyReset(decimal currentEquity)` — resets `WeeklyStartEquity`
- Add `OnMonthlyReset(decimal currentEquity)` — resets `MonthlyStartEquity`
- Modify `OnDailyReset()` to push a velocity data point into the queue and prune entries older than 5 days

New file: `src/TradingEngine.Domain/RiskAndEquity/ExtendedRiskState.cs` (defined in §2.2 above)

Modify `src/TradingEngine.Risk/RiskManager.cs`:
- Change `CurrentState` type from `RiskState` to `ExtendedRiskState`
- Update all `with { ... }` mutations to populate new fields
- Add `OnWeeklyReset()` and `OnMonthlyReset()` pass-through methods

**Verification A1**:
New test file `tests/TradingEngine.Tests.Unit/Risk/DrawdownTrackerExtendedTests.cs`:
- `WeeklyDrawdown_AfterReset_StartsFromCurrentEquity`
- `MonthlyDrawdown_AccumulatesAcrossWeekResets`
- `DrawdownVelocity_IncreasesWhenDDGrowsEachDay`
- `IsAccelerating_FalseWhenDDStable`
- All 14 existing `DrawdownTracker` tests still pass

---

### A2 — PropFirmComplianceService

**New files**:

`src/TradingEngine.Domain/Compliance/` — add all compliance value objects from §2.2.

`src/TradingEngine.Risk/Compliance/PropFirmComplianceService.cs`:
- `sealed` class implementing `IPropFirmComplianceService`
- Constructor: `(PropFirmRuleSet ruleSet, DrawdownTracker drawdownTracker, IEngineClock clock)`
- `ValidateSignal()`:
  1. Check daily DD vs `ruleSet.MaxDailyLossPercent` → `Block` if exceeded
  2. Check weekly DD vs `ruleSet.MaxWeeklyLossPercent` → `Block` if exceeded
  3. Check monthly DD vs `ruleSet.MaxMonthlyLossPercent` → `Block` if exceeded
  4. Check min trading days: if `DaysRemaining < ruleSet.MinTradingDays - tradingDaysMet`, emit `Warning` (cannot meet minimum by challenge end)
  5. If DD is accelerating (`drawdownTracker.IsAccelerating`), emit `Warning` severity (not a block, just advisory)
- `ValidateAtBarOpen()`: same checks but called once per bar, not per signal — used to gate the entire bar evaluation
- `EstimatePassProbability()`: delegates to `PassProbabilityEstimator`
- Internal `_tradingDaysMet` counter: increment on `OnDailyReset()` if any trade was taken that day

`src/TradingEngine.Risk/Compliance/PassProbabilityEstimator.cs`:
- Implements `IPassProbabilityEstimator`
- Monte Carlo algorithm:
  1. Seed with `input.HistoricalDailyPnL` (if empty, return `new() { ProbabilityOfPass = 0, Recommendation = "Insufficient history" }`)
  2. For each of `MonteCarloRuns` iterations:
     - Sample `DaysRemaining` daily PnL values from the historical distribution (uniform random resample with replacement)
     - Walk equity forward from `CurrentEquity`; stop early on DD breach
     - Record: did it reach profit target? did it breach daily limit? did it breach max limit?
  3. Return fraction of runs that reached target, fractions that breached limits, median projected equity

Modify `src/TradingEngine.Risk/RiskManager.cs`:
- Add `IPropFirmComplianceService? _complianceService` (nullable — compliance is optional, not required for non-challenge runs)
- Add `SetComplianceService(IPropFirmComplianceService svc)` method (called from EngineHostFactory after construction)
- In `Validate()`: after existing violation checks, call `_complianceService?.ValidateSignal()` and merge any `Block`-severity violations

Modify `src/TradingEngine.Host/EngineHostFactory.cs`:
- After constructing `RiskManager`, construct `PropFirmComplianceService` and call `riskManager.SetComplianceService()`
- This keeps `RiskManager`'s constructor unchanged (non-breaking)

Modify `config/prop-firms/ftmo-standard.json`:
```json
{
  "maxWeeklyLossPercent": 0.04,
  "maxMonthlyLossPercent": 0.08,
  "profitTargetPercent": 0.10,
  "minTradingDays": 4,
  "requireProfitTarget": true,
  "gracePeriod": { "enabled": false }
}
```

Modify `config/prop-firms/ftmo-aggressive.json` — same structure with aggressive values.

**Verification A2**:
New test file `tests/TradingEngine.Tests.Unit/Risk/ComplianceServiceTests.cs`:
- `WeeklyDDLimit_Blocks_WhenWeeklyLossExceeds4Pct`
- `MonthlyDDLimit_Blocks_WhenMonthlyLossExceeds8Pct`
- `PassProbability_ReturnsHighProbability_WhenEquityOnTrack`
- `PassProbability_ReturnsLowProbability_WhenAtMaxDDLimit`
- `PassProbability_ReturnsZero_WhenHistoryEmpty`
- `MinTradingDays_EmitsWarning_WhenInsufficientDaysRemain`

---

### A3 — Composable Size-Modifier Pipeline

Replaces the single hardcoded drawdown scale factor with a composable pipeline. Each modifier
implements `ISizeModifier` (§2.1), returns a multiplicative scale, and is individually
config-toggled per risk profile. The combined product is clamped to
`[SizeModifiers.MinCombinedScale, SizeModifiers.MaxCombinedScale]`.

**New files** in `src/TradingEngine.Risk/Sizing/`:

| File | Behavior |
|------|----------|
| `SizeModifierPipeline.cs` | Multiplies all enabled modifiers, clamps the product, logs each modifier's contribution at `Debug` (`SIZE_MOD\|{Name}\|{Scale}`) |
| `DrawdownSizeModifier.cs` | Wraps the **existing** `DrawdownScaler` — that class and its 4 unit tests stay untouched. Always enabled (it's the current behavior). |
| `AtrRegimeSizeModifier.cs` | `currentAtr / avg(baseline)` ratio → scale per `AtrScalingOptions` thresholds. Returns 1.0 if disabled, baseline empty, or baseline avg ≤ 0. |
| `TimeOfDaySizeModifier.cs` | Matches `context.UtcTimeOfDay` against configured windows (handles windows crossing midnight, e.g. 22:00–06:00). First matching window's scale wins. |
| `ConfidenceSizeModifier.cs` | `LossStreak >= LossStreakThreshold` → `LossStreakScale`; `WinStreak >= WinStreakThreshold` → `WinStreakScale`; else 1.0 |

Modify `src/TradingEngine.Risk/RiskManager.cs`:
- `CalculateLotSize()` gains an optional `SizeModifierContext? sizeContext = null` parameter
- Replace the inline `DrawdownScaler.ComputeScaleFactor` call with `SizeModifierPipeline.ComputeCombinedScale(context)` — when `sizeContext` is null (older call sites, replay path), build a minimal context with only equity/profile/intent so behavior degrades gracefully to drawdown-only scaling

Modify `src/TradingEngine.Host/EngineWorker.cs`:
- Build the `SizeModifierContext` per signal: current ATR from `context.IndicatorValues`, ATR baseline from `_atrBaselines[symbol]` (a `Queue<double>` capped at `AtrBaselinePeriod`, pushed once per closed bar), `UtcTimeOfDay` from `IEngineClock`, win/loss streaks from the signaling strategy's `Stats`

Register `SizeModifierPipeline` + all four modifiers in `EngineHostFactory.cs`.

**Verification A3**:
New test file `tests/TradingEngine.Tests.Unit/Risk/SizeModifierPipelineTests.cs`:
- `Pipeline_MultipliesEnabledModifiers_AndClamps`
- `Pipeline_DrawdownOnly_MatchesLegacyDrawdownScalerOutput` (regression: same numbers as before)
- `AtrModifier_HighAtr_ScalesDown` / `AtrModifier_ExtremeAtr_ReturnsMinimumScale` / `AtrModifier_Disabled_ReturnsOne`
- `TimeOfDayModifier_InsideWindow_AppliesScale` / `TimeOfDayModifier_WindowCrossingMidnight_Matches`
- `ConfidenceModifier_LossStreak_ReducesSize` / `ConfidenceModifier_WinStreak_IncreasesSize_CappedByMaxCombined`

---

### A4 — CurrencyExposureTracker

**New file**: `src/TradingEngine.Risk/CurrencyExposureTracker.cs`

```csharp
public sealed class CurrencyExposureTracker : ICurrencyExposureTracker
{
    private readonly Dictionary<Guid, (string Base, string Quote, TradeDirection Dir, decimal Risk)> _positions = new();

    public void Open(Guid positionId, string baseCurrency, string quoteCurrency,
                     TradeDirection direction, decimal riskAmount) { ... }
    public void Close(Guid positionId) { ... }

    public CurrencyExposureSnapshot GetSnapshot()
    {
        // For each position, add risk to base currency (direction-signed) and subtract from quote currency
        // Long EURUSD: +risk to EUR, -risk to USD (we're "buying" EUR, "selling" USD)
        // This gives net signed exposure per currency across all open positions
    }

    public bool WouldExceedLimit(string baseCurrency, string quoteCurrency,
                                  TradeDirection direction, decimal newRisk,
                                  double maxPercent, decimal equity)
    {
        // Simulate adding the new position and check if any single currency's
        // abs(netRisk) / equity > maxPercent
    }
}
```

Modify `src/TradingEngine.Risk/RiskManager.cs`:
- Inject `CurrencyExposureTracker` (add to constructor)
- In `Validate()`: call `currencyTracker.WouldExceedLimit()` → if true, add `CURRENCY_EXPOSURE_LIMIT` violation
- In `RegisterPosition()`: also call `currencyTracker.Open()` — need to pass symbol's currencies (look up from `symbolRegistry`)
- In `DeregisterPosition()`: also call `currencyTracker.Close()`

Modify `src/TradingEngine.Host/EngineHostFactory.cs`:
- Register `CurrencyExposureTracker` as `ICurrencyExposureTracker` and inject into `RiskManager`

Modify `config/risk-profiles/standard.json`, `conservative.json`, `aggressive.json`:
- Add `"maxExposurePerCurrencyPercent": 0.05` (standard), `0.03` (conservative), `0.08` (aggressive)
- Add `"sizeModifiers"` section — `atrRegime`, `timeOfDay`, `confidence` all `"enabled": false` (opt-in; drawdown scaling continues via the existing threshold/floor fields)

**Verification A4**:
New test file `tests/TradingEngine.Tests.Unit/Risk/CurrencyExposureTrackerTests.cs`:
- `EurUsd_Plus_GbpUsd_Long_IncreasesUsdExposure`
- `EurUsd_Long_Plus_UsdJpy_Long_DoesNotDoubleExpose_Eur`
- `Limit_Blocks_ThirdUsdPair_WhenAlreadyAtLimit`
- `Close_Decreases_CurrencyExposure`
- `EmptyTracker_ReturnsZeroExposure`

---

### A5 — EngineWorker Reset Scheduling

**File**: `src/TradingEngine.Host/EngineWorker.cs` (modify existing)

Add private fields:
```csharp
private int _lastResetIsoWeek = -1;
private int _lastResetMonth = -1;
```

In the bar evaluation loop, after `riskManager.UpdateEquityLevels()`:
```csharp
var now = clock.UtcNow;
var isoWeek = ISOWeek.GetWeekOfYear(now);
var month = now.Month;

if (isoWeek != _lastResetIsoWeek)
{
    _lastResetIsoWeek = isoWeek;
    riskManager.OnWeeklyReset(equity);
}
if (month != _lastResetMonth)
{
    _lastResetMonth = month;
    riskManager.OnMonthlyReset(equity);
}
```

Add `OnWeeklyReset()` and `OnMonthlyReset()` to `IRiskManager` interface and `RiskManager`:
- Pass-through to `drawdownTracker.OnWeeklyReset()` and `drawdownTracker.OnMonthlyReset()`
- Also notify `_complianceService?.OnWeeklyReset()` / `OnMonthlyReset()`

**PR1 boundary check**: All of Phase A passes; all 87+ existing tests pass; all A-phase unit tests pass.

---

## Phase B — EF Migrations & Data Completeness

**Goal**: Replace raw SQL schema management, add weekly/monthly equity snapshots, fix zeroed `TradeResult` fields.

---

### B1 — EF Core Migrations

Current state: `Program.cs` contains raw `ALTER TABLE` SQL and the `PipelineEvents` table was created by `EnsureCreated`. This blocks CI reproducibility.

Steps (in order — each is a discrete git commit):

1. **Audit** `src/TradingEngine.Web/Program.cs` for all raw SQL schema operations. Copy them to a migration comment for reference, then remove.

2. **Ensure all columns/tables are in `TradingDbContext.OnModelCreating`**:
   - Check `PipelineEvents` entity has correct EF configuration in `TradingDbContext`
   - Verify all columns added via `ALTER TABLE` have corresponding EF property configurations
   - The `TradingDbContext` already has 9 DbSets; the new column additions just need property config

3. **Generate migration**:
   ```powershell
   dotnet ef migrations add InitialFullSchema `
     --project src/TradingEngine.Infrastructure `
     --startup-project src/TradingEngine.Web `
     --output-dir Persistence/Migrations
   ```
   If the DB already exists with the raw-SQL-created schema, the migration may be a no-op for existing tables. Verify the generated migration SQL matches the existing schema.

4. **Replace `EnsureCreated()` with `MigrateAsync()`** in `Program.cs`:
   ```csharp
   using var scope = app.Services.CreateScope();
   var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
   await db.Database.MigrateAsync();
   ```

5. **Verify on fresh DB**: delete `data/trading.db` and restart — all tables must appear without errors.

**Verification B1**:
```powershell
Remove-Item data/trading.db -ErrorAction SilentlyContinue
dotnet run --project src/TradingEngine.Web --no-launch-profile -- --stop-after-migrate
# Or: start normally and verify tables in SQLite browser
dotnet test tests/TradingEngine.Tests.Unit --no-build  # must still pass
```
- OPEN-ISSUES.md: mark `STD-07` (raw ALTER TABLE) as `✅ Fixed (Iteration 18)`

---

### B2 — Weekly + Monthly Equity Snapshots

Extend `EquitySnapshot` entity:
```csharp
// Add to existing EquitySnapshot (EF entity, not the domain record):
public EquitySnapshotType Type { get; set; } = EquitySnapshotType.Tick;
```

New enum (in Domain): `EquitySnapshotType { Tick, Daily, Weekly, Monthly }`

Add EF migration: `dotnet ef migrations add AddEquitySnapshotType ...`

Modify `src/TradingEngine.Host/EquityPersistenceHandler.cs`:
- Existing `EquityUpdated` events persist as `EquitySnapshotType.Tick`
- Add handler for a new `WeeklyEquitySnapshot` domain event (fired from `RiskManager.OnWeeklyReset()`)
- Add handler for a new `MonthlyEquitySnapshot` domain event

Add to `src/TradingEngine.Domain/Events/`:
- `WeeklyEquitySnapshotTaken.cs`
- `MonthlyEquitySnapshotTaken.cs`

Modify `RiskManager.OnWeeklyReset()` and `OnMonthlyReset()` to publish these events via `IEventBus`.

---

### B3 — TradeResult Fields from cBot

The cBot (iter-17 Phase C1) already sends `grossProfit` and `netProfit` in exec payloads. The engine ignores them.

**File**: `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs`

In `ParseBarResult()` / exec processing, extract additional fields from the JSON exec object:
```json
{
  "clientOrderId": "...",
  "kind": "sl|tp|manual",
  "state": "filled",
  "fillPrice": 1.2345,
  "grossProfit": 123.45,
  "netProfit": 121.00,
  "commission": -2.45,
  "swap": 0.0
}
```

These must flow into the `ExecutionEvent` type. Currently `ExecutionEvent` may not carry these fields — add them:
```csharp
// Extend ExecutionEvent (or whatever DTO carries cBot exec data):
public decimal? GrossProfit { get; init; }
public decimal? NetProfit { get; init; }
public decimal? Commission { get; init; }
public decimal? Swap { get; init; }
```

**File**: Wherever `ClosePositionAsync` is called (likely `src/TradingEngine.Host/` — check `PositionTracker.cs` or `EngineWorker.cs`):
- Use `execEvent.NetProfit` for `TradeResult.NetPnL` when available
- Use `execEvent.GrossProfit` for `TradeResult.GrossPnL` when available
- Use `execEvent.Commission` for `TradeResult.CommissionPaid`
- Fallback to current synthetic calculation only when the field is null (e.g. replay path)

MAE/MFE: The `ExcursionTracker` in `src/TradingEngine.Services/Helpers/ExcursionTracker.cs` already tracks price extremes during position lifetime. Wire its output into `TradeResult.MaePrice` / `MfePrice` when closing a position.

R-multiple: `NetPnL / riskAmount` — calculable once real NetPnL is available. Populate `TradeResult.RMultiple`.

**Verification B3**:
- After a cTrader backtest (`EurUsd_H1_3Days`), query the trades table — `NetPnL`, `CommissionPaid`, `RMultiple` must be non-zero
- Unit test: `TradeResult_RMultiple_CorrectlyCalculated` — use `SimulatedBroker` path

**PR1 boundary**: Phase A + B complete. All existing tests pass. New unit tests pass. EF migrations applied cleanly.

---

## Phase C — Strategy Bank & Regime Detection

**Goal**: Replace the hardcoded strategy list with a dynamic, regime-aware bank. Add 4 new strategies. Add performance-based rotation config.

---

### C1 — IStrategyBank + StrategyBankService

Add `src/TradingEngine.Domain/Interfaces/IStrategyBank.cs` (defined in §2.1 above)

Create `src/TradingEngine.Host/StrategyBankService.cs`:
```csharp
public sealed class StrategyBankService : IStrategyBank
{
    private readonly StrategyRegistry _registry;
    private readonly StrategyRotationOptions? _rotation;
    private readonly Dictionary<string, bool> _enabled;
    private readonly Dictionary<string, StrategyPerformanceStats> _stats;

    public IReadOnlyList<IStrategy> GetActive(Symbol symbol, Timeframe timeframe, MarketRegime regime)
    {
        return _registry.GetAll()
            .Where(s => _enabled.GetValueOrDefault(s.Id, true))
            .Where(s => s.Config.Symbols.Contains(symbol.Value))
            .Where(s => s.RequiredTimeframes.Contains(timeframe))
            .Where(s => s.Config.RegimeFilter.Allows(regime))
            .ToList();
    }
    // ... Enable, Disable, NotifyResult, GetSnapshot
}
```

Add `Allows(MarketRegime)` extension method on `RegimeFilterOptions`:
```csharp
public static bool Allows(this RegimeFilterOptions filter, MarketRegime regime) => regime switch
{
    MarketRegime.Trending     => filter.AllowTrending,
    MarketRegime.Ranging      => filter.AllowRanging,
    MarketRegime.HighVolatility => filter.AllowHighVolatility,
    MarketRegime.LowVolatility => filter.AllowLowVolatility,
    _                         => filter.AllowUnknown
};
```

**Performance-based rotation** (if `_rotation.Mode == PerformanceBased`):
- In `NotifyResult()`: update `_stats[strategyId]`
- Check: if `stats.TotalTrades >= rotation.MinTradesForEvaluation` AND `stats.WinRate < rotation.MinWinRateToKeepActive`: call `Disable(strategyId)` and log at `Warning`

Modify `src/TradingEngine.Host/EngineHostFactory.cs`:
- Load `config/rotation.json` via `ConfigLoader` into `LoadedConfig.StrategyRotation` (nullable)
- Register `StrategyBankService` as `IStrategyBank` singleton
- Keep `StrategyRegistry` registration; `StrategyBankService` takes it as a dependency

Modify `src/TradingEngine.Host/EngineWorker.cs`:
- Replace any direct `strategyRegistry.GetForSymbol()` calls with `strategyBank.GetActive(symbol, timeframe, regime)` (regime comes from C2 below; use `MarketRegime.Unknown` until C2 is implemented)
- After each trade closes, call `strategyBank.NotifyResult(trade.StrategyId, tradeResult)`

---

### C2 — AtrBasedRegimeDetector

Create `src/TradingEngine.Infrastructure/Indicators/AtrBasedRegimeDetector.cs`:
- Implements `IRegimeDetector`
- Dependencies: none beyond the bars and indicator values passed in
- Algorithm:
  ```
  baseline = average of ATR values for last AtrBaselinePeriod bars
  atrRatio = currentAtr / baseline

  adx = indicators["ADX_14"] (if available)

  if atrRatio >= 2.5:        return HighVolatility
  if atrRatio <= 0.4:        return LowVolatility
  if adx >= 25.0:            return Trending
  if adx <= 18.0:            return Ranging
  return Unknown
  ```
- Requires `bars.Count >= AtrBaselinePeriod` to return non-Unknown
- Returns `Unknown` when indicator values not yet available (warm-up bars)

Add `Adx` to `IndicatorType` enum in `src/TradingEngine.Domain/IndicatorRequest.cs`

Modify `src/TradingEngine.Infrastructure/Indicators/SkenderIndicatorService.cs`:
- Add case for `IndicatorType.Adx`: call `Skender.Stock.Indicators.Indicator.GetAdx(bars, period)`

Modify `src/TradingEngine.Host/EngineWorker.cs`:
- Add `private readonly Dictionary<(Symbol, Timeframe), MarketRegime> _currentRegimes`
- After computing indicators for each (symbol, timeframe), call `_regimeDetector.Detect()` and store result
- Pass result to `strategyBank.GetActive()`

Register `AtrBasedRegimeDetector` in `EngineHostFactory.cs` as `IRegimeDetector`.

**Verification C2**:
New test file `tests/TradingEngine.Tests.Unit/Indicators/RegimeDetectorTests.cs`:
- `HighAtrRatio_Returns_HighVolatility`
- `HighAdx_Returns_Trending`
- `LowAdx_Returns_Ranging`
- `InsufficientBars_Returns_Unknown`
- `NormalConditions_Returns_Unknown` (no clear signal = Unknown, not Ranging)

---

### C3 — RegimeFilter in Strategy Configs

Modify all four existing `*Config.cs` records:
```csharp
public RegimeFilterOptions RegimeFilter { get; init; } = new();
public OrderEntryOptions OrderEntry { get; init; } = new();
```

Update all four JSON strategy config files to include:
```json
"regimeFilter": {
    "allowTrending": true,
    "allowRanging": true,
    "allowHighVolatility": true,
    "allowLowVolatility": true,
    "allowUnknown": true
},
"orderEntry": {
    "method": "Market",
    "maxSlippagePips": 2.0
}
```

**Tailored defaults per strategy** (override the above defaults in JSON):
- `mean-reversion.json`: `"allowTrending": false` — mean reversion shouldn't fire in strong trends
- `macd-momentum.json` (new): `"allowRanging": false` — momentum strategy only in trending/normal
- `rsi-divergence.json` (new): `"allowTrending": false, "allowHighVolatility": false`
- `mtf-trend.json` (new): `"allowRanging": false`

---

### C4 — New Strategies

Each strategy is two C# files + one JSON config. Follow the exact pattern of `TrendBreakoutStrategy.cs` / `TrendBreakoutConfig.cs`.

#### Strategy 1: RSI Divergence

**Files**:
- `src/TradingEngine.Strategies/RsiDivergence/RsiDivergenceConfig.cs`
- `src/TradingEngine.Strategies/RsiDivergence/RsiDivergenceStrategy.cs`
- `config/strategies/rsi-divergence.json`

**Config record**:
```csharp
public sealed record RsiDivergenceConfig : IStrategyConfig
{
    public string Id { get; init; } = "rsi-divergence";
    public string DisplayName { get; init; } = "RSI Divergence";
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Symbols { get; init; } = ["EURUSD", "GBPUSD", "USDJPY"];
    public string RiskProfileId { get; init; } = "standard";
    public Timeframe Timeframe { get; init; } = Timeframe.H1;
    public RegimeFilterOptions RegimeFilter { get; init; } = new() { AllowTrending = false, AllowHighVolatility = false };
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public RsiDivergenceParameters Parameters { get; init; } = new();
}

public sealed record RsiDivergenceParameters
{
    public int RsiPeriod { get; init; } = 14;
    public int DivergenceLookback { get; init; } = 10;
    public int AtrPeriod { get; init; } = 14;
    public double SlAtrMultiple { get; init; } = 1.5;
    public double TpRrMultiple { get; init; } = 2.0;
}
```

**Algorithm** (in `Evaluate()`):
```
priorBars = bars.TakeLast(DivergenceLookback + 1).SkipLast(1)
currentBar = bars[^1]

// Bullish: price makes lower low but RSI makes higher low
bullish = currentBar.Low < priorBars.Min(b => b.Low)
       && currentRsi > priorRsi_at_lowest_low_bar
       && currentRsi < 50

// Bearish: price makes higher high but RSI makes lower high
bearish = currentBar.High > priorBars.Max(b => b.High)
       && currentRsi < priorRsi_at_highest_high_bar
       && currentRsi > 50

if bullish → TradeDirection.Long
if bearish → TradeDirection.Short
SL = SlTpHelpers.AtrBased(entry, direction, atr, SlAtrMultiple, symbolInfo)
TP = SlTpHelpers.RRMultiple(entry, sl, direction, TpRrMultiple, symbolInfo)
```

**Required indicators**: `RSI_{RsiPeriod}`, `ATR_{AtrPeriod}`
**RequiredBarCount**: `DivergenceLookback + RsiPeriod + 5`

**JSON**:
```json
{
  "id": "rsi-divergence",
  "displayName": "RSI Divergence",
  "enabled": true,
  "symbols": ["EURUSD", "GBPUSD", "USDJPY"],
  "timeframe": "H1",
  "riskProfileId": "standard",
  "regimeFilter": { "allowTrending": false, "allowHighVolatility": false },
  "orderEntry": { "method": "Market", "maxSlippagePips": 2.0 },
  "parameters": {
    "rsiPeriod": 14,
    "divergenceLookback": 10,
    "atrPeriod": 14,
    "slAtrMultiple": 1.5,
    "tpRrMultiple": 2.0
  }
}
```

---

#### Strategy 2: Bollinger Squeeze

**Files**:
- `src/TradingEngine.Strategies/BollingerSqueeze/BollingerSqueezeConfig.cs`
- `src/TradingEngine.Strategies/BollingerSqueeze/BollingerSqueezeStrategy.cs`
- `config/strategies/bb-squeeze.json`

**Config**:
```csharp
public sealed record BollingerSqueezeParameters
{
    public int BbPeriod { get; init; } = 20;
    public double BbStdDev { get; init; } = 2.0;
    public int AtrPeriod { get; init; } = 14;
    public double SqueezeThreshold { get; init; } = 0.8;   // current width < 80% of 20-bar min
    public int CooldownBars { get; init; } = 3;
    public double SlBandBuffer { get; init; } = 0.5;        // ATR buffer beyond opposite band
    public double TpRrMultiple { get; init; } = 2.5;
}
```

**Algorithm**:
```
bbWidth = (upperBand - lowerBand) / middleBand
minBbWidth = min(bbWidth over last BbPeriod bars)
squeeze = bbWidth < SqueezeThreshold × minBbWidth

if squeeze AND barsInCooldown > CooldownBars:
    if close > upperBand → Long
    if close < lowerBand → Short
    SL = opposite band ± (SlBandBuffer × atr)
    TP = SlTpHelpers.RRMultiple(entry, sl, direction, TpRrMultiple, symbolInfo)
    barsInCooldown = 0
else:
    barsInCooldown++
    return null
```

**Required indicators**: `BB_20_2.0` (upper, middle, lower bands), `ATR_14`
Note: `BollingerBands` indicator returns three values — the `IndicatorService` must return them via a naming convention, e.g. `"BB_20_2.0_Upper"`, `"BB_20_2.0_Middle"`, `"BB_20_2.0_Lower"`. Verify this is already supported in `SkenderIndicatorService`; if not, extend the indicator key scheme.

**JSON**: `config/strategies/bb-squeeze.json` — follow the standard schema with `"regimeFilter": { "allowHighVolatility": false }`

---

#### Strategy 3: MACD Momentum

**Files**:
- `src/TradingEngine.Strategies/MacdMomentum/MacdMomentumConfig.cs`
- `src/TradingEngine.Strategies/MacdMomentum/MacdMomentumStrategy.cs`
- `config/strategies/macd-momentum.json`

**Config**:
```csharp
public sealed record MacdMomentumParameters
{
    public int MacdFast { get; init; } = 12;
    public int MacdSlow { get; init; } = 26;
    public int MacdSignal { get; init; } = 9;
    public int SmaPeriod { get; init; } = 200;
    public int AdxPeriod { get; init; } = 14;
    public double AdxMinThreshold { get; init; } = 20.0;
    public int AtrPeriod { get; init; } = 14;
    public double SlAtrMultiple { get; init; } = 2.0;
    public double TpRrMultiple { get; init; } = 3.0;
}
```

**Algorithm**:
```
// Trend filter: price vs SMA200
priceAboveSma = close > sma200

// MACD histogram zero-cross (fresh signal only)
histNow = macdHistogram[^1]
histPrev = macdHistogram[^2]

longSignal  = histPrev < 0 && histNow >= 0 && priceAboveSma && adx >= AdxMinThreshold
shortSignal = histPrev > 0 && histNow <= 0 && !priceAboveSma && adx >= AdxMinThreshold

if longSignal → Long
if shortSignal → Short
// Only one signal per histogram cycle — use _lastHistogramSign to track
```

**Required indicators**: `MACD_12_26_9_Histogram`, `SMA_200`, `ADX_14`, `ATR_14`
Note: MACD returns Macd line, Signal line, and Histogram — use `"MACD_12_26_9_Histogram"` key. Verify `SkenderIndicatorService` supports this; extend if not.

**Supported timeframes**: H1 and H4 (configurable via `Timeframe` in config)

---

#### Strategy 4: Multi-Timeframe Trend

**Files**:
- `src/TradingEngine.Strategies/MtfTrend/MtfTrendConfig.cs`
- `src/TradingEngine.Strategies/MtfTrend/MtfTrendStrategy.cs`
- `config/strategies/mtf-trend.json`

**Config**:
```csharp
public sealed record MtfTrendConfig : IStrategyConfig
{
    public string Id { get; init; } = "mtf-trend";
    public string DisplayName { get; init; } = "Multi-Timeframe Trend";
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Symbols { get; init; } = ["EURUSD", "GBPUSD"];
    public string RiskProfileId { get; init; } = "standard";
    public Timeframe Timeframe { get; init; } = Timeframe.H1;           // entry timeframe
    public Timeframe HigherTimeframe { get; init; } = Timeframe.H4;     // trend timeframe
    public RegimeFilterOptions RegimeFilter { get; init; } = new() { AllowRanging = false };
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public MtfTrendParameters Parameters { get; init; } = new();
}

public sealed record MtfTrendParameters
{
    public int EmaPeriod { get; init; } = 200;
    public int RsiPeriod { get; init; } = 14;
    public double RsiBullishPullback { get; init; } = 45.0;  // RSI dips below this on H1 = pullback
    public double RsiBearishPullback { get; init; } = 55.0;  // RSI rises above this on H1 = pullback
    public int SwingLookback { get; init; } = 10;
    public int AtrPeriod { get; init; } = 14;
    public double SlAtrMinMultiple { get; init; } = 1.5;      // minimum SL = 1.5× ATR
    public double TpRrMultiple { get; init; } = 2.0;
}
```

**Algorithm**:
```
// H4 trend direction
h4Ema200 = context.IndicatorValues["H4_EMA_200"]
h4Close = context.Bars[HigherTimeframe][^1].Close
h4Bullish = h4Close > h4Ema200
h4Bearish = h4Close < h4Ema200

// H1 pullback detection
h1Rsi = context.IndicatorValues["RSI_14"]

// Long entry: H4 bullish AND H1 RSI was below 45 last bar AND crosses back above 45 now
// Short entry: H4 bearish AND H1 RSI was above 55 last bar AND crosses back below 55 now
longEntry  = h4Bullish && prevRsi < RsiBullishPullback && h1Rsi >= RsiBullishPullback
shortEntry = h4Bearish && prevRsi > RsiBearishPullback && h1Rsi <= RsiBearishPullback

SL = max(
    swing_low / swing_high over SwingLookback bars,
    SlTpHelpers.AtrBased(entry, direction, atr, SlAtrMinMultiple, symbolInfo)
)
TP = SlTpHelpers.RRMultiple(entry, sl, direction, TpRrMultiple, symbolInfo)
```

**RequiredTimeframes**: `[Timeframe.H1, Timeframe.H4]`
**Required indicators**: `RSI_14` (H1), `ATR_14` (H1), `EMA_200` (H4 — key: `"H4_EMA_200"`)

The H4 EMA indicator uses `IndicatorRequest("H4_EMA_200", IndicatorType.Ema, 200, Timeframe.H4)` (new `Timeframe` discriminator from §2.4). `SkenderIndicatorService` must compute indicators per-timeframe using the bars for that timeframe.

---

#### Strategy 5: SuperTrend

**Files**:
- `src/TradingEngine.Strategies/SuperTrend/SuperTrendConfig.cs`
- `src/TradingEngine.Strategies/SuperTrend/SuperTrendStrategy.cs`
- `config/strategies/super-trend.json`

**Config**:
```csharp
public sealed record SuperTrendParameters
{
    public int AtrPeriod { get; init; } = 10;
    public double AtrMultiplier { get; init; } = 3.0;
    public int AdxPeriod { get; init; } = 14;
    public double AdxMinThreshold { get; init; } = 20.0;
}
```

**Algorithm**:
```
stValue = indicators["ST_10_3.0"]            // SuperTrend line price
stDir   = indicators["ST_10_3.0_Direction"]  // +1 bullish, -1 bearish (encoded as double)

// Signal only on a direction flip, gated by ADX (avoid whipsaw in flat markets)
flipToBull = _prevDirection == -1 && stDir == +1 && adx >= AdxMinThreshold  → Long
flipToBear = _prevDirection == +1 && stDir == -1 && adx >= AdxMinThreshold  → Short
_prevDirection = stDir   // track across bars; exactly one signal per flip

// SL: the SuperTrend line itself is the natural stop. Pass stValue as the
// strategy-supplied SL via the unified SlTpResolver (SwingPoint method, D3) —
// the resolver enforces the ATR-minimum floor from positionManagement config.
// TP: from unified positionManagement config (default RrMultiple 2.0).
```

**Required indicators**: `ST_10_3.0` (+ `_Direction` key), `ADX_14`, `ATR_14`
**RequiredBarCount**: `max(AtrPeriod, AdxPeriod) × 2 + 5` (SuperTrend needs warm-up beyond ATR period)
**RegimeFilter default**: `"allowRanging": false`
**Trailing default**: `AtrMultiple` in the `positionManagement` block — natural fit for a trend-follower

`SkenderIndicatorService`: add case for `IndicatorType.SuperTrend` → `Indicator.GetSuperTrend(bars, AtrPeriod, AtrMultiplier)`. Emit two keys per request: `"{key}"` = SuperTrend line value, `"{key}_Direction"` = ±1.0 derived from which band is active (UpperBand set = bearish −1, LowerBand set = bullish +1).

---

### C5 — Strategy Rotation Config

New file: `config/rotation.json`
```json
{
  "mode": "Disabled",
  "evaluationWindowDays": 30,
  "minWinRateToKeepActive": 0.35,
  "minProfitFactorToKeepActive": 0.8,
  "minTradesForEvaluation": 10
}
```

Default `mode: "Disabled"` — rotation is opt-in. Change to `"PerformanceBased"` to activate.

Modify `src/TradingEngine.Host/ConfigLoader.cs`:
- Load `config/rotation.json` as `StrategyRotationOptions?` (null if file missing = disabled)
- Store as `LoadedConfig.StrategyRotation`

**Verification C** (full):

New simulation test files in `tests/TradingEngine.Tests.Simulation/Strategies/` — one per strategy:
`RsiDivergenceScenarios.cs`, `BollingerSqueezeScenarios.cs`, `MacdMomentumScenarios.cs`,
`MtfTrendScenarios.cs`, `SuperTrendScenarios.cs`.

These are **comprehensive** suites, not happy-path sign-offs. Every file must cover this case matrix
(deterministic CSV fixtures, credential-free, same harness pattern as `TrendBreakoutScenarios`):

| # | Case | Assertion |
|---|------|-----------|
| 1 | Long setup (happy path) | Signal fires with correct direction; SL/TP present and on the correct side of entry |
| 2 | Short setup | Mirror of case 1 |
| 3 | Warm-up | No signal while `bars.Count < RequiredBarCount` |
| 4 | Filter rejection | Setup present but gating condition fails (ADX below threshold, wrong side of EMA/SMA, RSI mid-range, …) → no signal |
| 5 | No duplicate signals | Setup persists across consecutive bars → exactly one signal per setup/flip/cross |
| 6 | Regime filter block | Same setup, regime set to a disallowed value → strategy not evaluated / no signal |
| 7 | Unified SL/TP config | SL/TP values match the strategy's resolved `positionManagement` block (after D3) — change the config, assert the numbers move |
| 8 | Strategy-specific edge | At least one: MTF = H4 missing from context; BB Squeeze = cooldown respected; SuperTrend = flip without ADX confirmation; RSI Div = lower low with *lower* RSI (no divergence); MACD = zero-cross against SMA200 side |

CSV fixtures live next to existing scenario data; name them `{strategy}-{case}.csv`.

Unit tests for `StrategyBankService`:
- `GetActive_FiltersOut_WhenRegimeMismatch`
- `GetActive_Includes_WhenRegimeMatches`
- `PerformanceRotation_Disables_BelowMinWinRate` (requires `MinTradesForEvaluation` trades)

**PR2 boundary**: Phase C + D complete. All strategy tests pass. All existing simulation tests pass (document trade count changes if regime filter alters existing strategy behavior).

---

## Phase D — Intelligent Order Placement & Unified Position Management

**Goal**: Add limit-offset order entry support, harden news/gap protection, add slippage tolerance, and unify SL/TP/trailing/breakeven configuration across all strategies.

---

### D1 — LimitOffset Order Entry

Modify `src/TradingEngine.Domain/Trading/TradeIntent.cs` (add optional field):
```csharp
public OrderEntryOptions? Entry { get; init; }  // null = default Market
```

**Protocol addition** (document in `docs/iterations/iter-18/PROTOCOL-DELTA.md`):
The `bar_done.commands[].submit_order` object gains two new optional fields:
```json
{
  "type": "submit_order",
  "clientOrderId": "...",
  "symbol": "EURUSD",
  "direction": "buy",
  "lots": 0.1,
  "orderType": "Limit",          // NEW: "Market" (default) or "Limit"
  "limitPrice": 1.08500,         // NEW: present when orderType=Limit
  "expiryBars": 3,               // NEW: cancel if not filled after N bars
  "maxSlippagePips": 2.0,        // NEW: for Market type, tolerance
  "sl": 1.08000,
  "tp": 1.09000
}
```

Modify `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs`:
- In `BuildSubmitOrderCommand()`: if `intent.Entry?.Method == LimitOffset`, populate `orderType: "Limit"`, `limitPrice`, and `expiryBars`

Modify `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` (net6.0 / C# 10 — see Rules):
- Add `_pendingLimitOrders` dictionary: `clientOrderId → (limitPrice, barsWithoutFill, expiryBars)`
- When processing a `submit_order` command with `orderType == "Limit"`: call `PlaceLimitOrder()` instead of `PlaceMarketOrder()`
- On each bar: tick `barsWithoutFill` counter for all pending limit orders; if `>= expiryBars`, cancel via `CancelOrder()` and add exec with `"state": "cancelled"` to next `bar_result`
- Add `state: "cancelled"` to existing exec states handled by the engine

Modify `PositionTracker.cs` (or wherever exec events are processed):
- On `state == "cancelled"`: log at `Information`, call `DeregisterPendingOrder()`, do not create a position

**Slippage tolerance**: If `maxSlippagePips > 0` for a Market order:
- cBot checks `abs(fillPrice - requestedPrice) > maxSlippagePips × pipSize`
- If exceeded: add exec with `"state": "slippage_exceeded"`
- Engine on `slippage_exceeded`: log `Warning`, deregister order, no position created

---

### D2 — ConfigurableNewsFilter

Current `src/TradingEngine.Risk/Filters/NewsFilter.cs` is a stub.

Create `src/TradingEngine.Risk/Filters/ConfigurableNewsFilter.cs`:
```csharp
public sealed class ConfigurableNewsFilter : INewsFilter
{
    private readonly IReadOnlyList<NewsBlockWindow> _windows;

    public bool IsNewsWindowActive(string symbol, DateTime utcNow)
    {
        foreach (var w in _windows)
        {
            if (w.Symbol != "*" && w.Symbol != symbol) continue;
            if (w.DayOfWeek.HasValue && w.DayOfWeek.Value != utcNow.DayOfWeek) continue;
            if (utcNow.TimeOfDay >= w.StartUtc && utcNow.TimeOfDay <= w.EndUtc) return true;
        }
        return false;
    }
}
```

New domain record: `NewsBlockWindow { string Symbol, DayOfWeek? DayOfWeek, TimeSpan StartUtc, TimeSpan EndUtc, string Reason }`

New config file: `config/news/blocked-windows.json`:
```json
{
  "windows": [
    { "symbol": "*", "dayOfWeek": "Friday", "startUtc": "20:00", "endUtc": "23:59", "reason": "Weekend pre-close" },
    { "symbol": "*", "dayOfWeek": "Sunday", "startUtc": "00:00", "endUtc": "21:00", "reason": "Weekend open gap" }
  ]
}
```

Modify `src/TradingEngine.Host/ConfigLoader.cs`:
- Load `config/news/blocked-windows.json` into `LoadedConfig.NewsWindows` (if file missing, empty list = no blocking)

Modify `src/TradingEngine.Host/EngineHostFactory.cs`:
- Replace `new NewsFilter()` with `new ConfigurableNewsFilter(loadedConfig.NewsWindows)`

Note: This is a pre-configured blocklist, not a live news feed. Real-time news feed integration is post-scope for this iteration — add a note to OPEN-ISSUES.md flagging it as future work.

---

### D3 — Unified Position Management (SL/TP/Trailing/Breakeven)

Today every strategy carries its own `SlAtrMultiple` / `TpRrMultiple` / `TrailingMethod` fields in
its `Parameters` record — duplicated per strategy, no global overrides, breakeven barely
configurable. Target: one `positionManagement` block with the same schema for every strategy,
resolved **strategy JSON → global `config/position-management.json` → coded defaults**.

**Reconcile, don't duplicate**: `src/TradingEngine.Domain/PositionManagement/` already contains
`PositionManagementConfig`, `TrailingConfig`, `TrailingMethod`, `SlParameters`, `TpParameters`,
`PartialClose`, and `PositionLifecycleState`. The implementing agent MUST audit these first and
extend the existing types — do not create parallel ones. The schema below is the target shape,
mapped onto whatever already exists.

**Target unified schema** (per-strategy `positionManagement` section and global file share it):
```json
{
  "stopLoss":   { "method": "AtrMultiple", "atrMultiple": 1.5, "fixedPips": 0, "maxPips": 100 },
  "takeProfit": { "method": "RrMultiple", "rrMultiple": 2.0, "fixedPips": 0, "atrMultiple": 0 },
  "breakeven":  { "enabled": false, "triggerRMultiple": 1.0, "offsetPips": 1.0 },
  "trailing":   { "method": "None", "stepPips": 10, "atrMultiple": 1.0, "activateAfterBreakeven": true }
}
```
- `stopLoss.method`: `AtrMultiple | FixedPips | SwingPoint` — SwingPoint means the strategy supplies a
  price (swing low/high, SuperTrend line); the resolver enforces the ATR-multiple as a *minimum floor*
  and `maxPips` as a hard cap (existing `MAX_SL` risk-profile rule still applies downstream)
- `takeProfit.method`: `RrMultiple | FixedPips | AtrMultiple | None`
- `trailing.method`: the existing `TrailingMethod` enum (`None | StepPips | AtrMultiple | BreakevenThenTrail`)
- `breakeven`: when unrealized PnL ≥ `triggerRMultiple × riskAmount`, move SL to entry ± `offsetPips`.
  Generalizes the existing `BreakevenThenTrail` behavior so breakeven composes with ANY trailing method.

**New service**: `src/TradingEngine.Services/SLTPCalculation/SlTpResolver.cs`
- `Resolve(entry, direction, atr, symbolInfo, PositionManagementOptions opts, Price? strategySuppliedSl, CancellationToken)` → `(Price sl, Price? tp)`
- Strategies call this instead of picking `SlTpHelpers` methods themselves; `SlTpHelpers` remains
  the math layer underneath (unchanged, keeps its 8 tests)

**Resolution** happens once in `ConfigLoader` at load time (not per-bar): each strategy config ends
up holding a single fully-resolved `PositionManagementOptions` instance.

**Migration (same PR, all 9 strategies)**:
- Every strategy JSON gains a `positionManagement` block carrying its current effective values
  (e.g. trend-breakout: AtrMultiple 1.5 / RrMultiple 2.0 / trailing AtrMultiple 1.0) — **behavior
  must not change**, only where the numbers live
- Remove `SlAtrMultiple`, `TpRrMultiple`, `TrailingMethod`, `TrailingAtrMultiple` from every
  `*Parameters` record and JSON — single source of truth
- All strategy `Evaluate()` methods switch to `SlTpResolver`
- `PositionManager` / `TrailingStopService` read trailing + breakeven settings from the resolved
  options keyed by `strategyId` (wired in `EngineHostFactory`)
- New global file `config/position-management.json` ships with the coded defaults (so overriding
  globally is discoverable)

**Verification D3**:
New test file `tests/TradingEngine.Tests.Unit/Services/SlTpResolverTests.cs`:
- `AtrMultipleSl_MatchesLegacySlTpHelpersOutput` (regression — same numbers as before migration)
- `SwingPointSl_UsesStrategyPrice_WhenBeyondAtrFloor`
- `SwingPointSl_FallsBackToAtrFloor_WhenStrategyPriceTooTight`
- `MaxPips_Caps_OversizedStop`
- `TpNone_ReturnsNullTakeProfit`
- `Resolution_StrategyBlock_Overrides_GlobalFile`
- `Breakeven_MovesSl_ToEntryPlusOffset_AtTriggerR`
- Existing trailing-stop tests (2) and SlTpCalculator tests (8) still pass

**Verification D**:
Unit tests `tests/TradingEngine.Tests.Unit/Risk/ConfigurableNewsFilterTests.cs`:
- `FridayEvening_Blocked_WhenWeekendWindowConfigured`
- `SundayMorning_Blocked`
- `WednesdayMorning_NotBlocked`
- `SymbolSpecific_Window_DoesNotBlock_OtherSymbols`

Simulation tests with `FakeCBot`:
- `LimitOrder_Fills_WhenPriceReachesLimit` — FakeCBot scripted to fill at the limit price
- `LimitOrder_Cancelled_AfterExpiryBars` — FakeCBot price never reaches limit → verify no position created
- `SlippageExceeded_NoPosition_Created`

OPEN-ISSUES.md: mark `NewsFilter stub` item as `✅ Fixed (Iteration 18)`.

---

## Phase E — Blazor Server Dashboard

**Goal**: Introduce Blazor Server into `TradingEngine.Web` alongside existing Razor Pages. Build a rich interactive dashboard with OHLC trade visualization, live equity curves, strategy management, and FTMO pass-probability.

---

### E1 — Blazor Server Setup (Hybrid Mode)

Modify `src/TradingEngine.Web/TradingEngine.Web.csproj`:
```xml
<PackageReference Include="Blazor.Bootstrap" Version="3.x" />
<!-- OR Radzen.Blazor — choose one, evaluate both before committing -->
<PackageReference Include="ApexCharts.Blazor" Version="1.x" />
<!-- lightweight-charts via CDN in _Host.cshtml — no NuGet package needed -->
```

Modify `src/TradingEngine.Web/Program.cs`:
```csharp
// After existing service registrations:
builder.Services.AddServerSideBlazor();

// In pipeline, after MapRazorPages():
app.MapBlazorHub();
app.MapFallbackToPage("/blazor/_Host");   // separate path to avoid conflicting with Razor fallback
```

Create `src/TradingEngine.Web/Pages/blazor/_Host.cshtml` — standard Blazor Server host page. Loads `<component type="typeof(App)" render-mode="ServerPrerendered" />`.

Create `src/TradingEngine.Web/Components/App.razor` — Blazor router pointing to `Components/Pages/`.

Create `src/TradingEngine.Web/Components/_Imports.razor` — standard global using directives.

Add `wwwroot/js/trading-charts.js` — JS interop module for lightweight-charts (details in E4).

Add lightweight-charts via CDN reference in `_Host.cshtml`:
```html
<script src="https://unpkg.com/lightweight-charts/dist/lightweight-charts.standalone.production.js"></script>
```

**Verify E1**: `dotnet run --project src/TradingEngine.Web` starts without errors. Navigate to `/blazor/_Host` → Blazor loads. Existing `/`, `/trades`, `/backtests` routes still work (Razor Pages unaffected).

---

### E2 — Live Backtest Dashboard

Create `src/TradingEngine.Web/Components/Pages/BacktestDashboard.razor`:

**Component hierarchy**:
```
BacktestDashboard (route: /dashboard)
├── BacktestControlPanel
│   ├── SymbolSelector (12 checkboxes, mirrors existing Run.cshtml)
│   ├── TimeframeSelector (6 checkboxes)
│   ├── DateRangePicker
│   ├── BalanceInput
│   └── RunButton (calls POST /api/backtest/start)
├── LiveEquityCurve (ApexCharts line chart, updates via SignalR)
│   └── subscribes to BacktestProgressStore[runId] channel
├── StrategyStatusTable
│   ├── Columns: Strategy | Regime | Trades | Win Rate | PnL | Status
│   └── updates on each BarEvaluated event
├── BacktestProgressLog
│   ├── Color-coded log lines (replaces Razor SSE Progress.cshtml)
│   └── Counters: Bars | Signals | Trades
└── BacktestResultPanel (visible on completion)
    ├── Net Profit | Max DD% | Win Rate | Profit Factor
    ├── Link to TradeExplorer for this run
    └── FtmoPassProbabilityMini (compact gauge)
```

**Real-time updates** (Blazor Server pattern):
```csharp
// In BacktestDashboard.razor code-behind:
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (!firstRender) return;
    await foreach (var evt in _progressStore.GetChannel(runId).Reader.ReadAllAsync(CancellationToken))
    {
        _events.Add(evt);
        await InvokeAsync(StateHasChanged);
    }
}
```

This replaces the SSE stream — same data, same `BacktestProgressStore`, but via Blazor's SignalR channel instead of HTTP SSE.

---

### E3 — Strategy Manager

Create `src/TradingEngine.Web/Components/Pages/StrategyManager.razor` (route: `/strategies`):

```
StrategyManager
├── For each strategy in IStrategyBank.GetAll():
│   ├── StrategyCard
│   │   ├── Name + ID badge
│   │   ├── Enable/Disable toggle (calls IStrategyBank.Enable/Disable — requires a web-facing wrapper)
│   │   ├── Regime filter display (shows which regimes are allowed)
│   │   ├── Stats: Trades | Win Rate | P&L | Profit Factor | Streaks
│   │   └── [Expand] → StrategyParamsEditor
│   └── StrategyParamsEditor (expanded inline)
│       ├── Risk profile selector (dropdown of loaded profiles)
│       ├── Symbol checkboxes
│       ├── Timeframe selector
│       └── Parameter sliders/inputs (generated from config JSON)
└── StrategyRotationPanel
    ├── Rotation mode dropdown
    └── Threshold sliders (min win rate, min profit factor)
```

**Strategy enable/disable** requires a web-facing API since the `IStrategyBank` singleton lives in the engine process, not the web process. In the current architecture, engine and web run in the same process (`BacktestOrchestrator` creates the inner host inline). So `IStrategyBank` can be injected into the Blazor component directly once registered in the outer `Program.cs` service collection as well.

Note: This only affects the *next* backtest run — in-progress runs are not affected by enable/disable changes.

**Parameter editing**: Expose a `PUT /api/strategies/{id}/config` endpoint that writes a modified JSON config to the `config/strategies/` directory. The next backtest run reloads configs from disk via `ConfigLoader`, picking up the changes. Do not hot-reload in-process — disk write + next-run reload is simpler and safer.

Create `src/TradingEngine.Web/Api/StrategiesController.cs`:
- `GET /api/strategies` → returns all strategy configs + current stats from `IStrategyBank`
- `PUT /api/strategies/{id}/enable` → calls `IStrategyBank.Enable(id)`
- `PUT /api/strategies/{id}/disable` → calls `IStrategyBank.Disable(id)`
- `PUT /api/strategies/{id}/config` → writes modified JSON to `config/strategies/{id}.json`

---

### E4 — Trade Explorer with OHLC Chart

Create `src/TradingEngine.Web/Components/Pages/TradeExplorer.razor` (route: `/explorer`):

```
TradeExplorer
├── TradeFilterBar
│   ├── Symbol dropdown
│   ├── Strategy dropdown
│   ├── Date range picker
│   └── Win/Loss/All toggle
├── TradeList (left panel, virtualized)
│   └── TradeRow: Date | Symbol | Direction | Lots | Entry | Exit | PnL | R-multiple
│       └── click → sets SelectedTrade → updates chart panel
└── TradeChartPanel (right panel)
    ├── OhlcChart (lightweight-charts via JS interop)
    │   ├── Candlestick series (bars from /api/bars)
    │   ├── EntryMarker (arrow shape, colored by direction)
    │   ├── ExitMarker (arrow shape)
    │   ├── SL line (horizontal, dashed red)
    │   └── TP line (horizontal, dashed green)
    └── EquityCurveOverlay (secondary panel, synced x-axis)
```

**JS interop module** `wwwroot/js/trading-charts.js`:
```javascript
import { createChart, CandlestickSeries } from 'lightweight-charts';

const _charts = {};

export function createOhlcChart(elementId, theme) { ... }
export function setOhlcData(elementId, bars) { /* bars: [{time, open, high, low, close}] */ }
export function addTradeMarkers(elementId, entry, exit, sl, tp) {
    // entry/exit: { time, price, direction }
    // sl: { price }  tp: { price }
}
export function setVisibleRange(elementId, from, to) { ... }
export function destroyChart(elementId) { ... }
```

Create `src/TradingEngine.Web/Components/Shared/OhlcTradeChart.razor`:
```csharp
// Blazor component wrapping the JS interop module
// Parameters: Trade SelectedTrade, IReadOnlyList<Bar> Bars
// On SelectedTrade changed: calls JS interop to update markers
// On Bars changed: calls JS interop to set OHLC data
```

New API endpoint: `GET /api/bars?symbol=EURUSD&from=2024-01-01&to=2024-01-31&timeframe=H1`

Create `src/TradingEngine.Web/Api/BarsController.cs`:
- Queries `TradingDbContext.Bars` filtered by symbol, timeframe, date range
- Returns `[{time, open, high, low, close, volume}]` as JSON array (lightweight-charts format)

**Lazily load bars**: only fetch bars for ±50 bars around the trade's entry/exit time. Update the range as the user scrolls. Use `setVisibleRange()` JS interop to auto-fit the chart to the trade period on selection.

---

### E5 — Backtest Comparison + FTMO Pass Probability

Create `src/TradingEngine.Web/Components/Pages/BacktestComparison.razor` (route: `/compare`):

```
BacktestComparison
├── BacktestMultiSelector (up to 4 runs, date/symbol/status filter)
├── OverlaidEquityCurves (ApexCharts, multiple series, indexed to % return)
├── ComparisonTable
│   ├── Row per backtest: Net PnL | Max DD% | Win Rate | Profit Factor | Sharpe | Trades
│   └── Expandable per-strategy rows
└── FtmoPassProbabilityPanel (shown for selected run)
    ├── PassProbabilityGauge (large circular gauge, 0–100%)
    ├── DDTrajectoryChart (line chart: daily DD over time + limit line)
    ├── MonteCarloFanChart (current equity + fan of projected paths)
    └── RecommendationText
        ├── "On Track — current trajectory passes with {P}% probability"
        ├── "Warning — reduce risk. At current pace, max DD reached in {N} days"
        └── "At Risk — P(pass) < 20%. Consider stopping."
```

New API endpoints (create `src/TradingEngine.Web/Api/BacktestAnalyticsController.cs`):
- `GET /api/backtest/{runId}/pass-probability` → calls `IPassProbabilityEstimator.Estimate()` with equity history from DB
- `GET /api/backtest/compare?runIds=a,b,c,d` → returns normalized equity curves and comparison stats for up to 4 runs
- `GET /api/backtest/{runId}/daily-pnl` → returns daily P&L array (for Monte Carlo input)

Compute Sharpe ratio: `(meanDailyPnL / stdDailyPnL) × sqrt(252)` — computed from stored equity snapshots. Add this to `BacktestQueryService`.

---

### E6 — Symbol Correlation + Regime Heatmap

Create `src/TradingEngine.Web/Components/Pages/SymbolAnalysis.razor` (route: `/analysis`):

```
SymbolAnalysis
├── CorrelationMatrix
│   ├── Symbol pair checkboxes (select subset to display)
│   ├── Lookback period selector (30 / 90 / 180 days)
│   └── Colored table (Pearson r: red=negative, grey=zero, green=positive)
└── RegimeHeatmap
    ├── Symbol selector (multi)
    ├── Date range slider (last 30/90 days)
    └── Calendar-style grid
        ├── X axis: dates
        ├── Y axis: symbols
        └── Cell color: Trending=blue, Ranging=amber, HighVol=red, LowVol=green, Unknown=grey
```

New API endpoints:
- `GET /api/analytics/correlation?symbols=EURUSD,GBPUSD,USDJPY&days=90` — compute Pearson r matrix from daily close prices in `TradingDbContext.Bars`
- `GET /api/analytics/regime-history?symbol=EURUSD&days=30` — return per-day regime classification (compute on-the-fly from stored bars using `IRegimeDetector`)

These endpoints are compute-on-demand from SQLite — no separate data store needed.

**Verification E** (manual checklist):
- [ ] Navigate to `/dashboard` → Blazor page loads, no console errors
- [ ] Start a backtest → equity curve updates in real-time
- [ ] Per-strategy table shows correct regime label and trade count
- [ ] Click a trade in `/explorer` → OHLC chart appears with entry/exit markers and SL/TP lines
- [ ] Navigate to `/compare` → select 2 runs → overlaid equity curves render
- [ ] FTMO pass-probability gauge shows non-zero value for a completed run
- [ ] Navigate to `/strategies` → all 9 strategies listed (4 existing + 5 new), enable/disable toggles work
- [ ] Navigate to `/analysis` → correlation matrix shows colored cells (EURUSD/GBPUSD should be high positive)
- [ ] All existing Razor Pages still work: `/`, `/trades`, `/backtests`, `/events`

**PR3 boundary**: Phase E complete. Manual checklist all green.

---

## Phase F — Testing & Live Mode Hardening

**Goal**: Expand simulation test coverage for new features, extend FakeCBot, re-verify live mode. **In-iteration — ships as PR4** (may start in parallel once PR2 merges).

---

### F1 — Simulation Tests for New Risk Features

New test file `tests/TradingEngine.Tests.Simulation/Risk/`:
- `WeeklyDDProtectionTests.cs`:
  - `WeeklyDDBreach_Blocks_AllSignals_UntilWeeklyReset`
  - Uses `FakeCBot` + `DrawdownScenarios` harness pattern
- `CurrencyExposureTests.cs`:
  - `LongEurUsd_Plus_LongGbpUsd_Blocks_ThirdUsdLong`
  - `LongEurUsd_Plus_ShortUsdJpy_DoesNotExceed_UsdLimit` (opposite directions partially offset)
- `AtrRegimeScalingTests.cs`:
  - `HighAtrRegime_ReducesLotSize_InBacktest` — simulate a period of high ATR, verify lots are scaled down

---

### F2 — FakeCBot Extensions

Modify `tests/TradingEngine.Tests.Simulation/Harness/FakeCBot.cs`:
- Add `LimitOrderBehavior`:
  - `FillAtLimitPrice` (default) — fill when bar's High/Low reaches the limit price
  - `NeverFill` — for testing expiry behavior
  - `FillWithSlippage(double pips)` — fill at limit price + random slippage
- Add `ExecutionBehavior.RejectOrder(string reason)` — returns `state: "rejected"` for scripted testing
- Add `FaultInjection.DisconnectAfterNBars(int n)` — simulates cBot disconnecting mid-run (close NetMQ dealer socket after n bars, verify engine handles `OperationCanceledException` gracefully)

New simulation test: `FakeCBotFaultTests.cs`:
- `Engine_HandlesGracefully_WhenCBotDisconnectsMidRun`
- `LimitOrder_Cancelled_WhenFakeCBotNeverFills`

---

### F3 — Live Mode Re-verification

After all phases complete:
1. Set `CTrader:UseForBacktest = true` in `appsettings.Development.json`
2. Start `dotnet run --project src/TradingEngine.Web`
3. Run a 3-day EURUSD H1 backtest via `/dashboard` (new Blazor UI)
4. Verify: `DEALER_RECV` in logs, trades execute, equity curve updates on the Blazor dashboard
5. Verify weekly/monthly DD displays correctly in the `BacktestDashboard`
6. Document actual trade count in `HANDOVER.md`

---

# Part 4 — Shipping Strategy

## Four PRs

### PR1 — Risk Foundation + DB Migrations (Phase A + B)
**Contents**: ExtendedDrawdownTracker, PropFirmComplianceService, composable size-modifier pipeline (drawdown / ATR regime / time-of-day / confidence), CurrencyExposureTracker, EngineWorker reset scheduling, EF migrations, weekly/monthly equity snapshots, TradeResult field fixes.

**Blast radius**: Internal only — no UI, no strategy, no protocol changes.

**Gate**: 
- All 87+ existing unit tests pass
- All A-phase and B-phase unit tests pass  
- `ReplayBacktest_FullPipeline` passes
- `dotnet ef database update` applies cleanly on a fresh DB
- No `ALTER TABLE` raw SQL remaining in `Program.cs`

**Merge when**: Compliance service validates correctly, EF migration clean, all gates green.

---

### PR2 — Strategy Bank + Order Intelligence (Phase C + D)
**Contents**: IStrategyBank + StrategyBankService, AtrBasedRegimeDetector, 5 new strategies + JSON configs (RSI Divergence, Bollinger Squeeze, MACD Momentum, MTF Trend, SuperTrend), strategy rotation config, LimitOffset order entry, ConfigurableNewsFilter, slippage tolerance, unified position management (D3 — SlTpResolver + `positionManagement` blocks for all 9 strategies).

**Blast radius**: Changes engine evaluation loop (strategy selection path changed to go via IStrategyBank), strategy SL/TP computation path (all strategies migrate to SlTpResolver — gate on regression tests proving identical numbers), + cBot protocol (limit order commands are additive, cBot ignores unknown fields).

**Gate**:
- All 5 new strategy scenario tests pass
- `SlTpResolverTests` pass, including the legacy-output regression test (same SL/TP numbers as before migration)
- `StrategyBankService` regime filter unit tests pass
- `ConfigurableNewsFilter` unit tests pass
- `LimitOrder_Fills_WhenPriceReachesLimit` simulation test passes
- `EurUsd_H1_3Days` passes (document trade count if regime filter changes it)
- cBot rebuilt (`dotnet build src/TradingEngine.Adapters.CTrader`)

**Merge when**: All gates green. If trade count changes on existing test due to regime filters on existing strategies, confirm the change is expected and document it.

---

### PR3 — Blazor Dashboard (Phase E)
**Contents**: Blazor Server setup in TradingEngine.Web, all new Blazor pages and components, JS interop for lightweight-charts, new API endpoints (bars, analytics, pass-probability, comparison, strategies).

**Blast radius**: Isolated to `TradingEngine.Web`. No engine, risk, or strategy changes.

**Gate**: All 6 manual verification checklist items from Phase E green.

**Note**: Existing Razor Pages remain in place throughout Phase E. A cleanup PR to remove them is post-scope for this iteration.

---

### PR4 — Testing & Live Hardening (Phase F) — part of THIS iteration
**Contents**: simulation tests for weekly DD protection, currency exposure, and ATR-regime scaling (F1); FakeCBot extensions — limit-fill behaviors, order rejection, disconnect fault injection (F2); live-mode re-verification (F3).

**Blast radius**: tests + FakeCBot harness only. Production code changes only where these tests expose real bugs (each such fix documented in HANDOVER.md).

**Gate**:
- All F1 simulation tests pass
- `Engine_HandlesGracefully_WhenCBotDisconnectsMidRun` passes
- `LimitOrder_Cancelled_WhenFakeCBotNeverFills` passes
- F3 live-mode checklist completed, actual trade count pasted in HANDOVER.md

**The iteration is not done until PR4 merges.** PR4 may start in parallel once PR2 merges (it depends on Phase C/D features, not on the Blazor UI).

---

# Part 5 — UI Component Hierarchy

```
src/TradingEngine.Web/
├── Components/
│   ├── App.razor
│   ├── _Imports.razor
│   ├── Pages/
│   │   ├── BacktestDashboard.razor     (route: /dashboard)
│   │   ├── TradeExplorer.razor         (route: /explorer)
│   │   ├── StrategyManager.razor       (route: /strategies)
│   │   ├── BacktestComparison.razor    (route: /compare)
│   │   └── SymbolAnalysis.razor        (route: /analysis)
│   └── Shared/
│       ├── EquityCurveChart.razor      — ApexCharts line/area chart
│       ├── OhlcTradeChart.razor        — lightweight-charts JS interop wrapper
│       ├── StrategyStatusCard.razor    — card: name + toggle + stats
│       ├── PassProbabilityGauge.razor  — circular gauge + Monte Carlo fan
│       ├── RegimeHeatmap.razor         — calendar grid with color cells
│       └── CorrelationMatrix.razor     — colored table, Pearson r values
├── Pages/
│   ├── blazor/_Host.cshtml             — Blazor Server entry point
│   └── [all existing Razor Pages]      — kept, not modified
└── wwwroot/
    └── js/
        └── trading-charts.js           — lightweight-charts JS interop module
```

### New API Endpoints Summary

| Method | Route | Purpose | Phase |
|--------|-------|---------|-------|
| `GET` | `/api/bars` | OHLC bars for chart | E4 |
| `GET` | `/api/backtest/{id}/pass-probability` | FTMO pass probability | E5 |
| `GET` | `/api/backtest/compare` | Multi-run comparison stats | E5 |
| `GET` | `/api/backtest/{id}/daily-pnl` | Daily PnL for Monte Carlo | E5 |
| `GET` | `/api/strategies` | Strategy list + stats | E3 |
| `PUT` | `/api/strategies/{id}/enable` | Enable strategy | E3 |
| `PUT` | `/api/strategies/{id}/disable` | Disable strategy | E3 |
| `PUT` | `/api/strategies/{id}/config` | Write config to disk | E3 |
| `GET` | `/api/analytics/correlation` | Pearson correlation matrix | E6 |
| `GET` | `/api/analytics/regime-history` | Per-day regime labels | E6 |

---

# Part 6 — Definition of Done

### PR1 (Risk + DB)
- [ ] `EngineWorkerDependencies` parameter-object in place; `EngineWorker` constructor takes the record; `EngineHostFactory` remains the only place it is built (A0)
- [ ] `DrawdownTracker` tracks weekly + monthly drawdown
- [ ] `PropFirmComplianceService.ValidateSignal()` blocks signals when weekly/monthly limit exceeded
- [ ] `PassProbabilityEstimator.Estimate()` returns a non-trivial result when given 10+ daily PnL samples
- [ ] `SizeModifierPipeline` multiplies enabled modifiers and clamps to min/max combined scale (unit test passes)
- [ ] Drawdown-only pipeline output matches legacy `DrawdownScaler` numbers exactly (regression test passes)
- [ ] ATR-regime modifier returns < 1.0 in high-ATR scenario; time-of-day modifier applies inside a configured window; confidence modifier reduces size after the configured loss streak (unit tests pass)
- [ ] `CurrencyExposureTracker` blocks third correlated position (unit test passes)
- [ ] `EngineWorker` calls `OnWeeklyReset()` on ISO week boundary and `OnMonthlyReset()` on month boundary
- [ ] No raw SQL in `Program.cs` (zero `ALTER TABLE` or `CREATE TABLE IF NOT EXISTS`)
- [ ] `dotnet ef migrations add InitialFullSchema` runs without errors
- [ ] Fresh DB created via `MigrateAsync()` contains all expected tables and columns
- [ ] `EquitySnapshot` rows have a `Type` column distinguishing Tick / Weekly / Monthly
- [ ] `TradeResult.NetPnL` uses cBot-provided value (not synthetic `Price(1m)` fallback)
- [ ] `TradeResult.RMultiple` is non-zero for cBot trades
- [ ] All 87+ existing tests pass
- [ ] `ReplayBacktest_FullPipeline` passes
- [ ] OPEN-ISSUES.md: `STD-07` marked fixed

### PR2 (Strategy Bank + Order Intelligence)
- [ ] `IStrategyBank.GetActive()` correctly filters by regime (unit test: disabled regime → strategy not returned)
- [ ] `AtrBasedRegimeDetector` classifies High ADX as Trending (unit test)
- [ ] All 5 new strategies have at least one passing scenario test
- [ ] All 9 strategy JSONs contain a `positionManagement` block; no `SlAtrMultiple`/`TpRrMultiple`/`TrailingMethod` fields remain in any `*Parameters` record
- [ ] `SlTpResolver` regression test proves identical SL/TP numbers for existing strategies before/after migration
- [ ] Breakeven moves SL to entry ± offset at the configured trigger R (unit test)
- [ ] Global `config/position-management.json` defaults are overridden by a strategy-level block (unit test)
- [ ] `mean-reversion` strategy does not fire signals when regime = Trending (regime filter test)
- [ ] `ConfigurableNewsFilter` blocks Friday 20:00 UTC (unit test passes)
- [ ] Limit order fills when FakeCBot price reaches the limit (simulation test passes)
- [ ] Limit order cancels after expiry bars (simulation test passes)
- [ ] Slippage-exceeded exec causes no position to be created (simulation test passes)
- [ ] `EurUsd_H1_3Days` passes (trade count documented if changed)
- [ ] `config/rotation.json` loads without exception; missing file = rotation disabled (no crash)
- [ ] All new JSON strategy configs load cleanly via `ConfigLoader`
- [ ] OPEN-ISSUES.md: NewsFilter stub marked fixed

### PR3 (Blazor Dashboard)
- [ ] `dotnet run --project src/TradingEngine.Web` starts without errors
- [ ] `/dashboard` Blazor page loads with no console errors
- [ ] Real-time equity curve updates during a running backtest
- [ ] Per-strategy table shows regime label and live trade count
- [ ] `/explorer` OHLC chart renders with entry/exit markers after clicking a trade
- [ ] SL and TP lines visible on the OHLC chart as horizontal lines
- [ ] FTMO pass-probability gauge shows a non-zero percentage for a completed backtest
- [ ] Strategy enable/disable toggle in `/strategies` persists across page refresh (disk-written config)
- [ ] `/compare` with 2 selected runs renders overlaid equity curves
- [ ] `/analysis` correlation matrix shows colored cells; EURUSD/GBPUSD shows high positive correlation
- [ ] All existing Razor Pages (`/`, `/trades`, `/backtests`, `/events`) still work
- [ ] Blazor pages visible in navbar alongside existing pages

### PR4 (Testing & Live Hardening)
- [ ] `WeeklyDDProtectionTests`, `CurrencyExposureTests`, `AtrRegimeScalingTests` simulation suites pass
- [ ] FakeCBot supports limit-fill behaviors (`FillAtLimitPrice`, `NeverFill`, `FillWithSlippage`), order rejection, and `DisconnectAfterNBars` fault injection
- [ ] `Engine_HandlesGracefully_WhenCBotDisconnectsMidRun` passes
- [ ] F3 live-mode re-verification checklist completed; actual trade count documented in HANDOVER.md
- [ ] Any production bug fixes uncovered by PR4 tests are listed in HANDOVER.md with their fix commits

---

# Part 7 — Files to Create / Modify (Summary)

## New files

```
src/TradingEngine.Domain/
  Interfaces/IStrategyBank.cs
  Interfaces/IPropFirmComplianceService.cs
  Interfaces/IRegimeDetector.cs
  Interfaces/ICurrencyExposureTracker.cs
  Interfaces/IPassProbabilityEstimator.cs
  Interfaces/ISizeModifier.cs
  MarketData/MarketRegime.cs
  RiskAndEquity/ExtendedRiskState.cs
  RiskAndEquity/SizeModifierOptions.cs   (incl. AtrScalingOptions, TimeOfDayScalingOptions, ConfidenceScalingOptions)
  RiskAndEquity/SizeModifierContext.cs
  RiskAndEquity/GracePeriodOptions.cs
  Trading/OrderEntryOptions.cs
  StrategyBank/StrategyRotationOptions.cs
  StrategyBank/RegimeFilterOptions.cs
  StrategyBank/StrategyBankSnapshot.cs
  Compliance/ComplianceResult.cs
  Compliance/PassProbabilityEstimate.cs
  Compliance/ComplianceSummary.cs
  Compliance/CurrencyExposureSnapshot.cs
  Events/WeeklyEquitySnapshotTaken.cs
  Events/MonthlyEquitySnapshotTaken.cs

src/TradingEngine.Risk/
  CurrencyExposureTracker.cs
  Sizing/SizeModifierPipeline.cs
  Sizing/DrawdownSizeModifier.cs
  Sizing/AtrRegimeSizeModifier.cs
  Sizing/TimeOfDaySizeModifier.cs
  Sizing/ConfidenceSizeModifier.cs
  Compliance/PropFirmComplianceService.cs
  Compliance/PassProbabilityEstimator.cs
  Filters/ConfigurableNewsFilter.cs

src/TradingEngine.Services/
  SLTPCalculation/SlTpResolver.cs

src/TradingEngine.Host/
  EngineWorkerDependencies.cs
  StrategyBankService.cs

src/TradingEngine.Infrastructure/
  Indicators/AtrBasedRegimeDetector.cs

src/TradingEngine.Strategies/
  RsiDivergence/RsiDivergenceConfig.cs
  RsiDivergence/RsiDivergenceStrategy.cs
  BollingerSqueeze/BollingerSqueezeConfig.cs
  BollingerSqueeze/BollingerSqueezeStrategy.cs
  MacdMomentum/MacdMomentumConfig.cs
  MacdMomentum/MacdMomentumStrategy.cs
  MtfTrend/MtfTrendConfig.cs
  MtfTrend/MtfTrendStrategy.cs
  SuperTrend/SuperTrendConfig.cs
  SuperTrend/SuperTrendStrategy.cs

config/
  rotation.json
  position-management.json
  strategies/rsi-divergence.json
  strategies/bb-squeeze.json
  strategies/macd-momentum.json
  strategies/mtf-trend.json
  strategies/super-trend.json
  news/blocked-windows.json

src/TradingEngine.Web/
  Components/App.razor
  Components/_Imports.razor
  Components/Pages/BacktestDashboard.razor
  Components/Pages/TradeExplorer.razor
  Components/Pages/StrategyManager.razor
  Components/Pages/BacktestComparison.razor
  Components/Pages/SymbolAnalysis.razor
  Components/Shared/EquityCurveChart.razor
  Components/Shared/OhlcTradeChart.razor
  Components/Shared/StrategyStatusCard.razor
  Components/Shared/PassProbabilityGauge.razor
  Components/Shared/RegimeHeatmap.razor
  Components/Shared/CorrelationMatrix.razor
  Pages/blazor/_Host.cshtml
  Api/BarsController.cs
  Api/BacktestAnalyticsController.cs
  Api/StrategiesController.cs
  wwwroot/js/trading-charts.js

docs/iterations/iter-18/
  PLAN.md (this file)
  PROTOCOL-DELTA.md  (documents limit order protocol additions)
  HANDOVER.md        (created by implementing agent on completion)

tests/TradingEngine.Tests.Unit/Risk/
  DrawdownTrackerExtendedTests.cs
  ComplianceServiceTests.cs
  SizeModifierPipelineTests.cs
  CurrencyExposureTrackerTests.cs
  ConfigurableNewsFilterTests.cs

tests/TradingEngine.Tests.Unit/Services/
  SlTpResolverTests.cs

tests/TradingEngine.Tests.Unit/Indicators/
  RegimeDetectorTests.cs

tests/TradingEngine.Tests.Simulation/Strategies/
  RsiDivergenceScenarios.cs
  BollingerSqueezeScenarios.cs
  MacdMomentumScenarios.cs
  MtfTrendScenarios.cs
  SuperTrendScenarios.cs

tests/TradingEngine.Tests.Simulation/Risk/
  WeeklyDDProtectionTests.cs
  CurrencyExposureTests.cs
  AtrRegimeScalingTests.cs
```

## Modified files (key changes)

```
src/TradingEngine.Risk/DrawdownTracker.cs       — add weekly/monthly/velocity tracking
src/TradingEngine.Risk/RiskManager.cs           — inject CurrencyExposureTracker, compliance svc, SizeModifierPipeline
src/TradingEngine.Risk/PositionSizer.cs         — no change (combined scale applied at RiskManager level)
src/TradingEngine.Services/TrailingStop/TrailingStopService.cs — read trailing+breakeven from resolved PositionManagementOptions per strategyId
src/TradingEngine.Domain/Trading/TradeIntent.cs — add OrderEntryOptions? Entry
src/TradingEngine.Domain/IndicatorRequest.cs    — add Timeframe parameter + Adx to IndicatorType
src/TradingEngine.Domain/RiskAndEquity/PropFirmRuleSet.cs  — add weekly/monthly/target fields
src/TradingEngine.Domain/Interfaces/IRiskManager.cs        — add OnWeeklyReset, OnMonthlyReset
src/TradingEngine.Host/EngineHostFactory.cs     — register all new services
src/TradingEngine.Host/EngineWorker.cs          — weekly/monthly reset triggers, regime detection, IStrategyBank
src/TradingEngine.Infrastructure/Indicators/SkenderIndicatorService.cs — add ADX, MACD histogram, per-timeframe support
src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs        — limit order commands, exec field parsing
src/TradingEngine.Infrastructure/Persistence/TradingDbContext.cs       — EF config for all entities
src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs   — limit order handling (net6.0 / C# 10)
src/TradingEngine.Web/Program.cs               — add Blazor Server, remove raw SQL, MigrateAsync
src/TradingEngine.Web/TradingEngine.Web.csproj — add Blazor + chart library NuGet packages
config/prop-firms/ftmo-standard.json           — add weekly/monthly/target fields
config/prop-firms/ftmo-aggressive.json         — same
config/risk-profiles/standard.json             — add maxExposurePerCurrencyPercent, sizeModifiers
config/risk-profiles/conservative.json         — same
config/risk-profiles/aggressive.json           — same
config/strategies/trend-breakout.json          — add regimeFilter + orderEntry + positionManagement; remove SL/TP/trailing params
config/strategies/mean-reversion.json          — same (regimeFilter: allowTrending false)
config/strategies/session-breakout.json        — same
config/strategies/ema-alignment.json           — same
docs/OPEN-ISSUES.md                            — mark STD-07 (raw SQL) and NewsFilter stub as fixed
docs/reference/SYSTEM-REFERENCE.md            — update sections 2, 3, 4, 6, 7, 8, 9 on completion
```
