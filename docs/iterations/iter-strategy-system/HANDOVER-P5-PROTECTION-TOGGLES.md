# Handover — P5: Per-Run Protection Toggles + Raw Preset + Gate Rejection UI

**Session date:** 2026-06-29
**Branch:** `iter/strategy-system`
**Commits:** `cc3c00e`, `90d9092`, `??` (handover)
**Agent:** Claude (open-code)

---

## 🔥 SESSION WARM-UP (read this first)

This handover documents a session that:
1. Diagnosed why backtests produce only 7-11 trades even over 3 months
2. Built per-run protection toggles so individual risk checks can be disabled
3. Created "Raw / Unprotected" prop-firm + risk-profile presets
4. Surfaced gate rejection reasons in the run-report UI
5. Added auto-rebuild of Angular front-end when `web-ui/src/**/*.ts` files change

**Key insight:** The PreTradeGate worst-case DD projection checks (section 6) were NOT toggle-gated — they ran unconditionally, so `DailyDdEnabled`/`MaxDdEnabled` toggles only affected the breach watchdog, not the pre-trade gate. Fixed in this session. **But BudgetOk and MaxPortfolioHeatRiskMultiples still run unconditionally** — see §"Remaining Trade Limiters" below.

**To continue investigation**, jump to §"Remaining Trade Limiters" and §"Investigation Plan for Next Session".

---

## 1. What Was Built (Parts A–E)

### Part A — Fix PreTradeGate Toggle Gating
**File:** `src/TradingEngine.Engine/Kernel/PreTradeGate.cs:127-153`

Before: worst-case DD projection always computed and checked, regardless of `DailyDdEnabled`/`MaxDdEnabled`.
After: wrapped in `if (c.DailyDdEnabled || c.MaxDdEnabled)`, with each check individually gated.

### Part B — Per-Run Protection Toggle Overrides
**Files (5):**

| File | Change |
|------|--------|
| `src/.../Dtos/Runs/StartRunRequest.cs:31-33` | Added `DailyDdEnabled`, `MaxDdEnabled`, `ForceCloseOnBreachEnabled` (default `true`) |
| `src/.../Api/RunsController.cs:113-115` | Serializes toggles to `CustomParams` |
| `src/.../Services/BacktestOrchestrator.cs:628-643` | ANDs per-run toggles into ALL prop-firm ruleset `Toggles` |
| `web-ui/.../new-backtest/new-backtest.component.ts` | 3 checkboxes: Daily DD, Max DD, Force close |
| `web-ui/.../models/api.types.ts` | 3 new optional fields on `StartRunRequest` |

### Part C — Raw / Unprotected Presets
- `config/prop-firms/raw.json` — all 9 toggles OFF, DD limits at 100% (1.0), force-close off
- `config/risk-profiles/raw.json` — 5%/trade, 500 SL pips, 20 max positions, 50% exposure, links to `propFirmRuleSetId: "raw"`

Both are auto-discovered by `ConfigLoader.LoadDirectory<T>()` — no seeder or registration changes needed.

### Part D — Gate Rejection Visibility
- `BacktestQueryService.cs` — queries `OrderProposed` StepRecords with non-null `DecisionReason`, parses `StrategyId` from `EventJson`, merges as `GATE:`-prefixed reasons into `topRejections`
- Run-report UI — amber bars for gate rejections, emerald for strategy reasons, legend, expanded to top 10

### Part E — Angular Auto-Rebuild
- `scripts/rebuild-ng-if-stale.ps1` — compares `web-ui/src/**/*.ts` timestamps vs stamp file, runs `npm run build` if stale
- `src/TradingEngine.Web/TradingEngine.Web.csproj` — MSBuild `<Target BeforeTargets="BeforeBuild">` calling the script
- On every `dotnet build` (including VS F5), you'll see `Angular: up to date` or `Angular: rebuilding...`

---

## 2. Full Architecture Deep-Dive: Signal → Trade Pipeline

### 2.1 The Kernel Loop (per-bar processing)

```
Bar arrives (venue BarStream)
  │
  ▼
KernelBacktestLoop.ProcessBarAsync        [Host/KernelBacktestLoop.cs:137]
  ├─ Advance venue (match limits)          [line 140]
  ├─ Drain venue feedback (fills)          [line 144]
  ├─ Emit roll events (day/week/month)     [line 151-161]
  │
  ├─ BarEvaluator.EvaluateAsync(bar, state)[Host/BarEvaluator.cs:51]
  │    ├─ Compute indicators               [line 67]
  │    ├─ Detect regime                    [line 90]
  │    ├─ StrategyBank.GetActive(sym,tf,regime) [line 91]
  │    ├─ For each active strategy:        [line 96]
  │    │    ├─ strategy.Evaluate(context) → TradeIntent?    [line 110]
  │    │    ├─ EntryPlanner.Plan(intent) → order type/limit [line 119]
  │    │    ├─ SignalGate.Check() → re-entry cooldown       [line 123]
  │    │    ├─ ComputeVerdicts(news,weekend,compliance,gov) [line 211-234]
  │    │    └─ Emit OrderProposed
  │    └─ Return BarEvaluation(proposals, verdicts)
  │
  ├─ PumpAsync(state, ct)                  [Host/KernelBacktestLoop.cs:216]
  │    └─ For each OrderProposed:
  │         └─ Kernel.Decide(state, proposal)     [Engine/Kernel/Kernel.cs:22]
  │              └─ Kernel.DecideProposed         [line 32]
  │                   └─ PreTradeGate.Evaluate()  [Engine/Kernel/PreTradeGate.cs:33]
  │                        ├─ [1] PROTECTION_MODE_ACTIVE?
  │                        ├─ [2] GOVERNOR block? (HardStop/SoftStop/CoolingOff/ProfitLocked)
  │                        ├─ [3] NO_EQUITY?
  │                        ├─ [4] SL_TOO_WIDE?
  │                        ├─ [5] MAX_POSITIONS?
  │                        ├─ [6] STRATEGY_MAX_POSITIONS?
  │                        ├─ [7] MAX_EXPOSURE?
  │                        ├─ [8] NEWS_WINDOW?
  │                        ├─ [9] WEEKEND_RESTRICTION?
  │                        ├─ [10] COMPLIANCE_BLOCK?
  │                        ├─ [11] ZERO_LOTS? (sizing)
  │                        ├─ [12] WorstCaseDDWouldBreachDaily?  ← NOW TOGGLE-GATED ✅
  │                        ├─ [13] WorstCaseDDWouldBreachOverall? ← NOW TOGGLE-GATED ✅
  │                        ├─ [14] WEEKLY_DD_LIMIT?
  │                        ├─ [15] MONTHLY_DD_LIMIT?
  │                        └─ [16] BudgetBlocked? ← NOT TOGGLE-GATED ❌
  │                        
  │                        ├─ REJECTED → RecordDecisionEvent (journaled as SignalRejected)
  │                        └─ ACCEPTED → EngineReducer.Apply (OrderSubmitted)
  │                                       → Effects: SubmitOrder + RegisterRisk
  │
  ├─ Equity observation                    [line 175-179]
  │    └─ Kernel.DecideEquity → DrawdownReducer → EvaluateDrawdownBreach
  ├─ Trailing/breakeven evaluation         [line 185-199]
  └─ venue.CompleteBarAsync()              [line 202]
```

### 2.2 Strategy Evaluation → Signal Generation

Each of 9 strategies in `src/TradingEngine.Strategies/` evaluates on every H1 bar. Key files:

| Strategy | File | Lines | Signal Condition |
|----------|------|-------|------------------|
| TrendBreakout | `TrendBreakout/TrendBreakoutStrategy.cs` | 70-89 | High > highest(lookback) AND price > EMA50 |
| EMAAlignment | `EmaAlignment/EmaAlignmentStrategy.cs` | 62-64 | EMA20 > EMA50 AND price > EMA20 |
| MeanReversion | `MeanReversion/MeanReversionStrategy.cs` | 64-66 | RSI < 30 AND close in lower 33% of bar range |
| SuperTrend | `SuperTrend/SuperTrendStrategy.cs` | 69-82 | ADX >= 20 AND SuperTrend direction FLIPS |
| MACD Momentum | `MacdMomentum/MacdMomentumStrategy.cs` | 89-101 | MACD histogram zero-cross AND price above SMA200 |
| BollingerSqueeze | `BollingerSqueeze/BollingerSqueezeStrategy.cs` | 79-126 | Bandwidth contraction → break above/below bands |
| RSI Divergence | `RsiDivergence/RsiDivergenceStrategy.cs` | 54-67 | Price HH/LL with opposite RSI divergence |
| SessionBreakout | `SessionBreakout/SessionBreakoutStrategy.cs` | 48-96 | Range 05-09 UTC, breakout within that window |
| Multi-TF Trend | `MtfTrend/MtfTrendStrategy.cs` | 83-95 | H4 EMA200 trend + H1 RSI pullback cross 45/55 |

**Strategy configs:** `config/strategies/*.json` — each has `parameters`, `regimeFilter`, `orderEntry`, `positionManagement`, `reentry`.

### 2.3 Strategy Selection (StrategyBankService)
**File:** `src/TradingEngine.Host/StrategyBankService.cs:38-41`

Strategies are excluded from evaluation when:
- `Config.Enabled == false` (unless enabled override exists)
- Not in RunPlan for this symbol/timeframe
- `EntryTimeframe != timeframe`
- `RegimeFilter.Allows(regime)` returns false (e.g., `allowRanging: false` blocks in ranging market)

### 2.4 SignalGateService (Re-Entry Cooldowns)
**File:** `src/TradingEngine.Services/SignalGateService.cs:17-53`
**Defaults:** `src/TradingEngine.Domain/Trading/ReentryOptions.cs:6-8`

| Cooldown | Bars | Triggered By |
|----------|------|-------------|
| `CooldownBarsAfterEntry` | 3 | Position opened |
| `CooldownBarsAfterSl` | 5 | Position stopped out |
| `CooldownBarsAfterTp` | 2 | Position hit TP |

The check is per **strategy × symbol × direction** key. A long from Strategy A does NOT block a short from Strategy B.

### 2.5 The Full PreTradeGate (All 16 Checks)
**File:** `src/TradingEngine.Engine/Kernel/PreTradeGate.cs:33-182`

| # | Line(s) | Check | Gated By | Status |
|---|---------|-------|----------|--------|
| 1 | 46-49 | Protection mode active | N/A | Always active |
| 2 | 54-67 | Governor block | `c.GovernorEnabled` | Gated |
| 3 | 69-71 | Equity ≤ 0 | N/A | Always |
| 4 | 75-78 | SL > MaxSlPips | Profile | Always |
| 5 | 82-85 | Max open positions (global) | Profile | Always |
| 6 | 88-91 | Max open positions (per-strategy) | Profile | Always |
| 7 | 96-99 | Max exposure | `c.MaxExposure` | Always calc'd |
| 8 | 104-106 | News window | `c.NewsFilterEnabled` | Gated |
| 9 | 108-110 | Weekend restriction | `c.WeekendFilterEnabled` | Gated |
| 10 | 112-114 | Compliance block | N/A | Always |
| 11 | 118-125 | Zero lots after sizing | N/A | Always |
| **12** | **129-137** | **Worst case would breach daily DD** | **`c.DailyDdEnabled`** | **✅ NOW GATED** |
| **13** | **140-153** | **Worst case would breach overall DD** | **`c.MaxDdEnabled`** | **✅ NOW GATED** |
| 14 | 147-150 | Weekly DD limit | `c.WeeklyDdEnabled` | Gated |
| 15 | 151-154 | Monthly DD limit | `c.MonthlyDdEnabled` | Gated |
| **16** | **169-178** | **Budget blocked** | **NONE** | **❌ NOT GATED** |

---

## 3. Remaining Trade Limiters (Why Still Only 7-11 Trades)

Even with the "Raw" preset and all 4 UI toggles off, trade count stays low. Here's why:

### 3.1 🔴 CRITICAL: BudgetOk Runs Unconditionally (NOT toggle-gated)

**File:** `src/TradingEngine.Engine/Kernel/PreTradeGate.cs:169`

```csharp
if (!BudgetOk(state, c, sizing, totalOpenRisk, riskAmount, perTradeRiskAmount))
```

The `BudgetOk` function (lines 213-245) has no toggle gate. It ALWAYS checks:
1. **BudgetUseFraction** (default 0.25 in `SizingPolicyOptions.cs:6`) — caps total open risk to 25% of remaining daily budget
2. **MaxPortfolioHeatRiskMultiples** (default 3.0 in `SizingPolicyOptions.cs:7`) — caps concurrent positions to `3.0 / riskPerTradePercent`

With raw risk profile (5%/trade):
- `heatCap = perTradeRiskAmount * 3.0 = (equity * 5%) * 3.0 = 15% of equity`
- `maxConcurrentAtFullRisk = 15% / 5% = 3 positions`

With standard risk profile (0.5%/trade):
- `heatCap = perTradeRiskAmount * 3.0 = 1.5% of equity`
- `maxConcurrentAtFullRisk = 1.5% / 0.5% = 3 positions`

**Both profiles cap to 3 concurrent positions via the heat cap.** This is the #1 remaining limiter.

### 3.2 Budget BudgetUseFraction = 0.25

**File:** `src/TradingEngine.Domain/RiskAndEquity/SizingPolicyOptions.cs:6`
**Config:** `config/sizing-policy.json`

Even after the heat cap lets 3 positions through, the budget cap further restricts:
```
budgetCap = remainingDailyBudget * 0.25
```

With zero DD and MaxDailyLoss=1.0 (raw): budgetCap = 25% of initial balance (allows 5 positions at 5% each). But the heat cap catches first at 3 positions. So both limits effectively cap the same way.

### 3.3 Budget Halving Downsizing Loop

**File:** `src/TradingEngine.Engine/Kernel/PreTradeGate.cs:170-189`

If BudgetOk fails, the gate tries halving lot sizes until the budget fits or lots fall below MinLots:
```csharp
while (lots > symbol.MinLots) {
    lots = Math.Max(lots * 0.5m, symbol.MinLots);
    // ...
}
if (lots < symbol.MinLots || !BudgetOk(...))
    return GateResult.Reject($"BudgetBlocked:lots={lots:F4}");
```

For EURUSD (MinLots=0.01, LotStep=0.01): halving from 0.03 to 0.02 to 0.01 is fine. But if the half step truncates to 0, it rejects.

### 3.4 MaxConcurrentPositions (Risk Profile)

**Standard profile:** `maxConcurrentPositions: 3` — `config/risk-profiles/standard.json:12`
**Raw profile:** `maxConcurrentPositions: 20` — `config/risk-profiles/raw.json:12`

All 9 strategy configs use `riskProfileId: "standard"` by default (e.g., `config/strategies/trend-breakout.json:5`). Switching the Run Profile dropdown to "Raw" gives 20 positions, but the heat cap (3.1) still limits to 3.

### 3.5 Re-Entry Cooldowns (SignalGateService)

**File:** `src/TradingEngine.Services/SignalGateService.cs:30-53`

After each position opens, the same strategy+symbol+direction is blocked for 3-5 bars. This adds ~25-50% dead time per cycle. With 3 concurrent positions cycling every ~15 bars, cooldowns extend the effective cycle to ~18-22 bars.

### 3.6 Strategy-Level Signal Sparsity

All 9 strategies are H1-only and highly selective:
- **TrendBreakout:** ~1 signal per 20-50 bars (needs new high/low)
- **EMAAlignment:** ~1 signal per 30-80 bars (needs crossover)
- **SuperTrend:** ~1 signal per 50-150 bars (needs direction flip)
- **BollingerSqueeze:** ~1 signal per 100-500 bars (needs squeeze + break)
- **SessionBreakout:** max 2 bars/day eligible (05-09 UTC window)

Collective signal rate: ~0.1-0.3 signals per bar. In 480 bars (1 month H1), that's ~48-144 signals. After all gates, 7-11 fills is at the low end but not unexpected.

### 3.7 Regime Filter

**File:** `src/TradingEngine.Domain/StrategyBank/RegimeFilterOptions.cs:24-33`

Strategy configs selectively disable regimes:
- SuperTrend, MACD, MtfTrend: `allowRanging: false`
- MeanReversion, RsiDivergence: `allowTrending: false`
- BollingerSqueeze: `allowHighVolatility: false`

In mixed markets, 2-4 strategies are silenced at any time. Set `DisableRegime: true` in backtest or `detectionEnabled: false` to bypass.

### 3.8 Limit Order Expiry (MeanReversion)

**Config:** `config/strategies/mean-reversion.json` → `orderEntry.method: "LimitOffset"` with 3-bar expiry.

When a limit order doesn't fill within 3 bars, it's cancelled. The opportunity is "wasted" on an unfilled order.

---

## 4. Risk & Governor Architecture Reference

### 4.1 ProtectionToggles (9 Boolean Flags)

**File:** `src/TradingEngine.Domain/RiskAndEquity/ProtectionToggles.cs:8-17`

| Flag | Default | Effect When OFF |
|------|---------|----------------|
| `DailyDdEnabled` | true | Skips daily DD pre-trade check + breach watchdog |
| `MaxDdEnabled` | true | Skips overall DD pre-trade check + breach watchdog |
| `WeeklyDdEnabled` | false | (already off) |
| `MonthlyDdEnabled` | false | (already off) |
| `ProfitTargetEnabled` | true | Skips profit target tracking |
| `ForceCloseOnBreachEnabled` | true | Prevents forced closes on breach |
| `NewsFilterEnabled` | false | (already off) |
| `WeekendFilterEnabled` | false | (already off) |
| `GovernorEnabled` | true | Skips ALL governor checks |

Toggle flow: `PropFirmRuleSet.Toggles` → `ConstraintSet.Resolve()` (ConstraintSet.cs:55-85) → `PreTradeGate.Evaluate()` and `Kernel.EvaluateDrawdownBreach()`

### 4.2 Governor (Loss Bands + Cooldown)

**Files:**
- `src/TradingEngine.Domain/RiskAndEquity/GovernorTypes.cs` — `GovernorTradingState` enum
- `src/TradingEngine.Domain/RiskAndEquity/GovernorState.cs` — Current governor snapshot
- `src/TradingEngine.Domain/RiskAndEquity/GovernorOptions.cs` — Configurable options
- `src/TradingEngine.Engine/GovernorMachine.cs` — The authoritative governor implementation

**Default thresholds** (`config/governor.json`):
- Loss bands: 40% DD → Reduced (×0.5 size), 60% → SoftStop (×0.0 = no new trades)
- Streak: 3 consecutive losses → reduce size, 5 → CoolingOff (24 bars)
- Profit lock: 60% of daily DD limit → lock trading for the day

**Two ANDed enable switches** (`EngineServiceCollectionExtensions.cs:316-318`):
1. `GovernorOptions.Enabled` (from Governor page / per-run override)
2. `ProtectionToggles.GovernorEnabled` (from prop-firm ruleset)

### 4.3 ConstraintSet (Resolved Risk Limits)

**File:** `src/TradingEngine.Domain/RiskAndEquity/ConstraintSet.cs:11-87`

Single resolved view combining `RiskProfile` + `PropFirmRuleSet` into decimal values consumed by the kernel gate and breach watchdog. `Resolve()` copies toggle values from `ruleSet.Toggles` into the constraint set fields.

### 4.4 FTMO Rule Set Structure

**File:** `src/TradingEngine.Domain/RiskAndEquity/PropFirmRuleSet.cs:3-30`

Positional record with constructor params for core FTMO fields + init properties for weekly DD, monthly DD, profit target requirement, grace period, and protection toggles.

### 4.5 Drawdown Tracking

**File:** `src/TradingEngine.Engine/DrawdownReducer.cs` — `Apply()` called on every equity update. Tracks daily/weekly/monthly/max DD against their respective start equities. Daily DD re-bases at UTC 22:00 (cTrader reset time).

### 4.6 Breach Watchdog

**File:** `src/TradingEngine.Engine/Kernel/Kernel.cs:115-127` — `EvaluateDrawdownBreach()`

Static helper that checks DD levels against `ConstraintSet` (toggle-gated):
- Daily DD >= MaxDailyLoss * FlattenAtFraction → `ProtectionCause.DailyDrawdown`
- Max DD >= MaxTotalLoss * FlattenAtFraction → `ProtectionCause.MaxDrawdown`
- Weekly / Monthly (gated by their respective toggles)

Breach triggers: `ProtectionState.Enter()` + if `ForceCloseOnBreachEnabled && ForceCloseOnBreach` → force-close all positions.

---

## 5. How Backtests Actually Work

### 5.1 BacktestReplayAdapter (Default, Credential-Free)
**File:** `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs`

- Loads bars from SQLite → feeds to BarStream chronologically
- Market orders: fill instantly at bar close
- Limit orders: rest until price reaches limit or expiry (BarsRemaining reaching 0)
- All closes compute costs via `TradeCostCalculator` (gross, commission, swap, net)

### 5.2 SimulatedBrokerAdapter (Synthetic Tick-Driven)
**File:** `src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs`

- 4 ticks per bar (0%, 25%, 50%, 75% of bar duration)
- Fills on tick-at-limit, SL/TP monitored per tick
- Limit expiry decremented PER BAR (not per tick)

### 5.3 CTraderBrokerAdapter (Live / CLI)
**File:** `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs`

- NetMQ bridge: PUB (ticks) + DEALER (lock-step bar protocol)
- Engine sends `bar_done` with buffered commands; cBot executes and returns `bar_result`
- Only runs first row; use replay for multi-row plans

### 5.4 BacktestOrchestrator
**File:** `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`

- `Start()`: generates RunId, launches `RunAsync()`
- Resolves venue: "ctrader" → NetMQ path; anything else → replay path
- `RunEngineReplayAsync()`: builds per-pass IHost with `BacktestReplayAdapter`
- `BuildLoadedConfigFromDbAsync()`: loads configs + applies per-run overrides (governor, protection toggles)

### 5.5 EffectiveConfigResolver
**File:** `src/TradingEngine.Services/EffectiveConfigResolver.cs`

Merges stored config ← per-run overrides ← run plan. Per-row packs replace breakeven/trailing/partial/ride/dynamic-sl-tp but keep the strategy's baseline SL/TP.

---

## 6. Investigation Plan for Next Session

### Priority 1: BudgetOk should honor toggles
- Gate the `BudgetOk` call in `PreTradeGate.cs:169` behind `c.DailyDdEnabled` (the budget is fundamentally a daily DD budget)
- OR add a separate `BudgetValidationEnabled` toggle
- **Expected impact:** with daily DD disabled, budget check is skipped entirely → no cap on concurrent risk

### Priority 2: SizingPolicyOptions are too restrictive
- Raise `MaxPortfolioHeatRiskMultiples` from 3.0 → 10.0 (or make it configurable per-run)
- Raise `BudgetUseFraction` from 0.25 → 1.0
- Create a `config/sizing-policy-raw.json` that the raw preset can reference
- **Expected impact:** heat cap goes from 3 positions to 10 positions at full risk

### Priority 3: Debug live signal flow
- Run a backtest with "Raw" profile + all toggles off
- Use the new rejection histogram in the run report to see exactly which reasons are blocking
- Check if `GATE:BudgetBlocked` or `GATE:MAX_EXPOSURE` dominates
- Count `BarClosed` StepRecords to verify strategies are firing
- Check regime filter — try `DisableRegime: true`

### Priority 4: Cooldown reduction
- Set all `reentry` cooldowns to 0 in a custom strategy config override
- Test if trade count increases meaningfully

### Priority 5: Per-run SizingPolicyOptions override
- Add a `SizingPolicyId` or per-run overrides to `StartRunRequest` 
- Allow selecting a "raw" sizing policy with generous heat/caps budget limits

### Priority 6: Write tests for the P5 changes
- Test: `PreTradeGate` with `DailyDdEnabled=false` skips worst-case daily DD check
- Test: `PreTradeGate` with `MaxDdEnabled=false` skips worst-case overall DD check
- Test: `BacktestOrchestrator` AND-style toggle propagation
- Test: `BacktestQueryService` gate rejection query + GATE: prefix

---

## 7. Key File Index

| File | Purpose |
|------|---------|
| `src/TradingEngine.Engine/Kernel/PreTradeGate.cs` | The single pre-trade risk gate (16 checks) |
| `src/TradingEngine.Engine/Kernel/Kernel.cs` | Kernel decision core (routes events) |
| `src/TradingEngine.Engine/Kernel/KernelSizing.cs` | Lot sizing logic (Calculate + Clamp) |
| `src/TradingEngine.Engine/GovernorMachine.cs` | Governor implementation |
| `src/TradingEngine.Engine/DrawdownReducer.cs` | Drawdown tracking |
| `src/TradingEngine.Host/KernelBacktestLoop.cs` | The production kernel loop |
| `src/TradingEngine.Host/BarEvaluator.cs` | Strategy evaluation per bar |
| `src/TradingEngine.Host/StrategyBankService.cs` | Strategy selection + regime filtering |
| `src/TradingEngine.Services/SignalGateService.cs` | Re-entry cooldown enforcement |
| `src/TradingEngine.Domain/RiskAndEquity/ProtectionToggles.cs` | 9 boolean toggles |
| `src/TradingEngine.Domain/RiskAndEquity/ConstraintSet.cs` | Resolved risk limits |
| `src/TradingEngine.Domain/RiskAndEquity/SizingPolicyOptions.cs` | BudgetUseFraction + HeatRiskMultiples |
| `src/TradingEngine.Domain/Trading/ReentryOptions.cs` | Cooldown defaults |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Run orchestration + config loading |
| `src/TradingEngine.Web/Services/BacktestQueryService.cs` | Query service (strategy breakdown) |
| `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` | Credential-free replay adapter |
| `config/prop-firms/raw.json` | Raw / Unprotected prop-firm preset |
| `config/risk-profiles/raw.json` | Raw / Unprotected risk profile preset |
| `config/sizing-policy.json` | Default sizing policy (BudgetUseFraction=0.25, Heat=3.0) |
| `config/strategies/*.json` | Per-strategy config (riskProfileId, reentry, regime, etc.) |

---

## 8. Running the App

```powershell
# Build (auto-rebuilds Angular if stale)
dotnet build

# Run (starts at http://localhost:5134)
dotnet run --project src/TradingEngine.Web

# Delete DB to start fresh
Remove-Item src/TradingEngine.Web/data/trading.db -Force

# Run unit tests
dotnet test tests/TradingEngine.Tests.Unit
```

### To run a "wild" backtest:
1. Open http://localhost:5134 → New Backtest
2. Select strategy(ies), EURUSD, H1, desired date range
3. Risk Profile: **"Raw / Unprotected"**
4. Uncheck: Governor, Daily DD protection, Max DD protection, Force close on breach
5. Optionally uncheck Regime detection
6. Start → Monitor

---

## 9. Session Trail (for agent context)

```
User asked: "are we on latest changes"
→ Detected on iter/strategy-system, 8 commits ahead of origin

User asked: "push these. then understand from docs skills, memory whatever, how our kernel, risk, ctrader backtests, governor, ftmo protection work"

→ Researched entire kernel pipeline, risk gate, governor, backtest architecture
→ Discovered: PreTradeGate worst-case DD checks are NOT toggle-gated
→ Discovered: BudgetOk runs unconditionally even with all toggles off
→ Discovered: MaxPortfolioHeatRiskMultiples=3.0 caps to 3 concurrent positions

User selected: "Both presets + per-run overrides" + "Show per-bar rejection reasons" + "Create raw risk profile"

→ Implemented Parts A-E (2 commits: cc3c00e + 90d9092)
→ Unit tests: 272 passed, 0 failed
→ Simulation tests (non-NetMQ): 97 passed, 0 failed

User asked: "ready?"
→ Launched app at http://localhost:5134 with fresh DB

User asked: "add automatic mechanism to make sure i don't run old angular build when running through vs"
→ Added MSBuild target + PowerShell script for auto-rebuild

User asked: "write a handover and cover what we discussed... still when I run a 3 months backtest i get 7 trades"
→ Wrote this handover, identified BudgetOk + HeatRiskMultiples as remaining limiters
```
