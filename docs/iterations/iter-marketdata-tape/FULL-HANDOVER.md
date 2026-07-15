# Shamshir — Full System Handover & Audit

**Date:** 2026-07-02
**Branch:** `iter/integration-cache-tape` (pushed to origin)
**Author:** AI agent (OpenCode / DeepSeek)
**Status:** P0-P4 iteration delivered. Build green, 314 unit / 3 golden determinism / 90 integration (1 pre-existing).

---

## 1. System Overview — What Is Shamshir?

Shamshir is a **prop-firm algorithmic trading engine** for cTrader. The system runs strategy backtests, evaluates risk, manages money, and surfaces results through an Angular SPA. The core architecture is venue-agnostic: strategies, risk rules, and position management are decoupled from the broker/venue that provides market data and executes fills.

**The primary venue is cTrader** — its model of commission, swap, spread, partial fills, limit orders, and intrabar exit detection is the source of truth. A second venue path (`BacktestReplayAdapter`) provides fast, credential-free deterministic replay. A third path (`TapeReplayAdapter`, new) provides the fastest in-process backtests against downloaded market data with dual-resolution exits.

### Architecture at a glance

```
┌─ FRONTEND ──────────────────────────────────────────────────────┐
│  Angular 19 SPA (web-ui/) → served single-origin by .NET Host   │
│  SignalR for live monitor, REST for all data                    │
└────────────────────────────┬────────────────────────────────────┘
                             │
┌─ ORCHESTRATION ─────────────────────────────────────────────────┐
│  BacktestOrchestrator  →  run lifecycle, DI wiring, port mgmt   │
│  RunQueryService       →  cache-first reads + memory run detail │
│  RunProgressBroadcaster →  SignalR live envelope                │
└────────────────────────────┬────────────────────────────────────┘
                             │
┌─ ENGINE HOST ───────────────────────────────────────────────────┐
│  EngineHostFactory  →  creates inner IHost per run              │
│  KernelBacktestLoop →  one bar: evaluate → pump → equity → trail│
│  BarEvaluator       →  indicators + strategy eval + verdicts    │
│  KernelTrailingEvaluator → per-bar trailing/breakeven/partial   │
└────────────────────────────┬────────────────────────────────────┘
                             │
┌─ VENUES (IBrokerAdapter) ───────────────────────────────────────┐
│  TapeReplayAdapter      → in-process, IMarketDataStore (FAST)   │
│  BacktestReplayAdapter  → in-process, IBarRepository (FAST)     │
│  CTraderBrokerAdapter   → 3-process: CLI→cBot→NetMQ→engine(SLOW)│
└────────────────────────────┬────────────────────────────────────┘
                             │
┌─ RISK & MONEY ──────────────────────────────────────────────────┐
│  PreTradeGate        →  deterministic kernel gate (pure, replay) │
│  KernelSizing        →  position sizing math (PercentRisk, etc.)│
│  RiskManager (legacy)→  validation, budget, violation detection │
│  GovernorMachine     →  loss-based size reduction, cooling-off  │
│  PropFirmCompliance  →  daily/weekly/monthly DD, profit target  │
│  ProtectionState     →  in-protection mode, reset policy        │
└─────────────────────────────────────────────────────────────────┘
```

---

## 2. The Backtest Model — How It Works & How We're Making It Faster

### 2.1 The cTrader Path (Current, Slow)

```
ctrader-cli.exe         [Process 1 — replays M1 data]
  └─ cBot (.NET 6)      [Process 2 — inside cTrader]
      ├─ PUB :15555  → bars/ticks/account → engine SUB
      └─ DEALER ↔ ROUTER :15556 → round-trip per bar
           └─ CTraderBrokerAdapter (.NET 8)   [Process 3 — engine]
```

**Why it's slow** (`docs/audit/PERF-DEEP-AUDIT.md:82-98`):
1. **ctrader-cli M1 replay** — ~63,000 M1 bars replayed for a 3-month H1 run (uncontrollable external process)
2. **Per-bar cross-process round-trips** — ~1,500 H1 bars × 2 process hops each
3. **cBot serialization + send** on cTrader thread per bar
4. **cBot blocking wait** for engine reply (lock-step protocol)
5. **Tick PUB flood** — `OnTick` publishes millions of ticks, all discarded in backtest
6. **Fixed ~23-second CLI startup overhead** regardless of run size

Result: **IPC + external-process floor dominates wall-clock**, not engine compute. A 3-month H1 run takes several minutes; engine CPU is <5% of wall time.

### 2.2 The Tape Replay Path (New, Fast) — `Venue=tape`

```
TapeReplayAdapter (in-process, .NET 8 only)
  ├─ Reads bars from IMarketDataStore (canonical, deduped data)
  ├─ Dual-resolution exits (decide on H1, detect SL/TP on M1)
  └─ No NetMQ, no ctrader-cli, no serialization — just method calls
```

**Why it's fast** (`TapeReplayAdapter.cs:167-194`):
1. **Zero process hops** — engine, venue, data store all in one process
2. **No serialization/deserialization** per bar
3. **No NetMQ sockets** — channels are in-memory `System.Threading.Channels`
4. **No external ctrader-cli M1 replay** — data pre-downloaded into `marketdata.db`
5. **Dual-resolution exits** recover intrabar fidelity without paying IPC cost
6. **Expected 10-100× speedup** over the cTrader path (`PERF-DEEP-AUDIT.md:169`)

### 2.3 The Market Data Store

**Files:** `src/TradingEngine.Infrastructure/MarketData/SqliteMarketDataStore.cs`, `IMarketDataStore.cs`
**Database:** separate `marketdata.db` from `trading.db` — market data is long-lived and shared; run data is per-run and churny.

**How data gets in:**
1. **Recorder cBot** (`TradingEngineCBot.cs:747-815`) — cTrader parameter `--Record=true` runs a plain backtest, subscribes to `MarketData.GetBars`, writes NDJSON shards to `--ReportPath`. No engine, no NetMQ.
2. **Ingester** (`MarketDataIngester.cs:29-46`) — bulk-loads NDJSON shards into `IMarketDataStore`. Dedupes by (symbol, timeframe, openTime). Idempotent — re-ingesting same shard inserts 0 new rows.
3. **FileDrop provider** (`FileDropProvider.cs`) — portable NDJSON files as a vendor-agnostic interchange format.

### 2.4 Enabling Parallel Experiments

With the tape path, multiple backtests can run simultaneously:
- Each run gets its own inner `IHost` with its own `TapeReplayAdapter`
- All share the same read-only `IMarketDataStore` (read-heavy, no contention)
- Writes go to separate run-scoped `trading.db` connections
- The Web app serves multiple runs from a single process

**This opens the door to:** parameter sweeps, walk-forward optimization, grid searches, ensemble backtests — all without spawning multiple cTrader CLI instances.

---

## 3. The Strategy Ecosystem

### 3.1 Nine Strategies (All Test-Focused)

All strategies operate on H1 timeframe, return `TradeIntent`, and are built for **testing the engine's risk/money/position-management** infrastructure — not production trading. They live in `src/TradingEngine.Strategies/`:

| Strategy | Entry Signal | SL Method | TP Method | `src/TradingEngine.Strategies/...` |
|----------|-------------|-----------|-----------|------|
| Trend Breakout | Price break of 20-bar range + EMA filter | ATR×1.5 | R:R×2.0 | `TrendBreakout/TrendBreakoutStrategy.cs:48-118` |
| SuperTrend | SuperTrend direction flip + ADX≥20 | SwingPoint (ST line) | R:R×2.0 | `SuperTrend/SuperTrendStrategy.cs:42-114` |
| Mean Reversion | RSI extreme + bar proximity 0.33 | ATR×1.5 | R:R×2.0 | `MeanReversion/MeanReversionStrategy.cs:36-85` |
| MACD Momentum | MACD histogram zero-cross + SMA200 + ADX≥20 | ATR×2.0 | R:R×3.0 | `MacdMomentum/MacdMomentumStrategy.cs:43-125` |
| Session Breakout | Break of session range (05-07 UTC) | ATR×1.5 | R:R×2.0 | `SessionBreakout/SessionBreakoutStrategy.cs:37-104` |
| RSI Divergence | Bullish/bearish RSI divergence | ATR×1.5 | R:R×2.0 | `RsiDivergence/RsiDivergenceStrategy.cs:34-78` |
| EMA Alignment | Fast > Slow EMA + price > fast EMA | ATR×1.5 | R:R×2.0 | `EmaAlignment/EmaAlignmentStrategy.cs:37-78` |
| MTF Trend | H4 trend + H1 RSI pullback cross | Swing-based | R:R×2.0 | `MtfTrend/MtfTrendStrategy.cs:42-122` |
| Bollinger Squeeze | BB squeeze then band break | Band-based + ATR buffer | R:R×2.5 | `BollingerSqueeze/BollingerSqueezeStrategy.cs:43-155` |

### 3.2 How Strategies Are Configured

Each strategy has a `config/strategies/{id}.json` file with:
```json
{
  "id": "trend-breakout",
  "enabled": true,
  "riskProfileId": "standard",
  "regimeFilter": { "detectionEnabled": true, "allowTrending": true, "allowRanging": false },
  "orderEntry": { "method": "Market" },
  "positionManagement": {
    "stopLoss":    { "method": "AtrMultiple", "atrMultiple": 1.5 },
    "takeProfit":  { "method": "RrMultiple", "rrMultiple": 2.0 },
    "breakeven":   { "enabled": true, "triggerRMultiple": 1.0 },
    "trailing":    { "enabled": true, "mode": "Auto", "method": "AtrMultiple", "atrMultiple": 2.5 },
    "dynamicSlTp": { "enabled": false, "mode": "Auto" }
  },
  "parameters": { "LookbackBars": 20, "MaPeriod": 50, "AtrPeriod": 14 }
}
```

JSON is seeded to DB at startup via `StrategyConfigSeeder`. The DB is the canonical config source. The `EffectiveConfigResolver` deep-merges per-run overrides.

---

## 4. Risk & Money Management — The Heart of the System

This is where Shamshir goes beyond a simple backtester. The risk system is designed to enforce **prop-firm rules** (FTMO, etc.) with precision:

### 4.1 Risk Profile (`config/risk-profiles/*.json`, `RiskProfile.cs`)

| Parameter | Standard | Conservative | Aggressive | Raw |
|-----------|----------|-------------|-----------|-----|
| Risk per trade | **0.5%** | 0.25% | 2% | 5% |
| Max daily DD | **5%** | 3% | 5% | 100% |
| Max total DD | **10%** | 6% | 10% | 100% |
| Max SL pips | **100** | 50 | 150 | 500 |
| Max positions | **3** | 2 | 5 | 20 |
| Drawdown scale threshold | **50%** | 50% | 75% | 100% |
| Lot sizing | **PercentRisk** | PercentRisk | PercentRisk | PercentRisk |

### 4.2 Position Sizing (`KernelSizing.cs:47-68`)

The canonical sizing formula (PercentRisk):
```
lots = (equity × riskPerTradePercent × drawdownScale) / (slPips × pipValuePerLot)
```
- Uses `Math.Floor`, never `Math.Round` (safety: never over-size)
- Drawdown scale: linear from 1.0 at threshold% to floor at 100% DD
- Size modifiers (ATR regime, time-of-day, confidence streaks) multiply on top
- Clamped to symbol `[minLots, maxLots]`, stepped to `lotStep`

### 4.3 Pre-Trade Gate (`PreTradeGate.cs:33-199`)

The deterministic kernel gate that every order passes through. Checks, in order:
1. **Protection mode** — reject if in protection
2. **Governor** — reject if hard-stop/soft-stop/cooling-off/profit-locked
3. **Position count** — reject if at max (per-strategy and overall)
4. **Exposure** — reject if `(openRisk + newRisk) / equity > MaxExposure`
5. **SL distance** — reject if stop-loss > `MaxSlPips`
6. **Worst-case projection** — simulate ALL positions hitting SL + commission; reject if projected equity breaches daily or max DD floor
7. **Budget with downsizing** — halve lots iteratively until remaining daily DD budget can absorb the risk; reject if can't fit at minLots

### 4.4 Governor (`GovernorMachine.cs`, `GovernorOptions`)

Loss-based adaptive risk reduction:

| Governor Band | Trigger | Action |
|--------------|---------|--------|
| Band 1 (40% daily DD) | Daily PnL fraction >= 40% | Size multiplier → 0.5× |
| Band 2 (60% daily DD) | Daily PnL fraction >= 60% | Soft stop (size → 0) |
| Streak reduce | 3+ consecutive losses | Size → 0.5× |
| Streak pause | 5+ consecutive losses | 24-bar cooling-off |
| Profit lock | 60% of daily target met | Lock profits, stop trading |

### 4.5 Drawdown Types

- **Fixed DD** (FTMO-style): drawdown measured from `InitialBalance` or `DailyStart`
- **Trailing DD**: drawdown measured from `PeakEquity` — floor rises with profits
- Daily reset at 22:00 UTC; protection clears on `NextTradingDay` (configurable)

---

## 5. Regime Filter — How It Works

**File:** `src/TradingEngine.Infrastructure/Indicators/AtrBasedRegimeDetector.cs:21-54`
**Config:** `src/TradingEngine.Domain/MarketData/RegimeOptions.cs:8-33`

Every bar is classified into one of five regimes using ATR + ADX:

```
1. Compute ATR(14) and ADX(14) for the bar series
2. Compute rolling ATR baseline (100-bar average of true-range)
3. Volatility first:
   - currentAtr / baseline >= 2.5 → HighVolatility
   - currentAtr / baseline <= 0.4 → LowVolatility
4. ADX-based classification:
   - ADX >= 25 → Trending
   - ADX <= 18 → Ranging
5. Fallback → Unknown
```

**How it gates strategies** (`BarEvaluator.cs:91`):
- Each strategy has `RegimeFilterOptions` with booleans: `AllowTrending`, `AllowRanging`, `AllowHighVolatility`, `AllowLowVolatility`, `AllowUnknown`
- `strategyBank.GetActive(symbol, tf, regime)` filters strategies whose filter allows the current regime
- Example: SuperTrend only trades in Trending (`AllowRanging: false`); RSI Divergence avoids Trending (`AllowTrending: false`)
- Master switch: `DetectionEnabled = false` → all regimes pass; used per-strategy, per-pack, or per-run (`DisableRegime`)

Fixed in iter-38: previously used a dead-twin regime register that always returned `Unknown`. Now self-computes ATR/ADX independently per bar.

---

## 6. Trailing Stops — How They Work

**File:** `src/TradingEngine.Host/KernelTrailingEvaluator.cs:42-141`
**Auto-tuner:** `src/TradingEngine.Services/AddOns/AddOnAutoTuner.cs:41-101`

### At Entry (`KernelTrailingEvaluator.cs:62-76`):
1. On first encounter of a position, `AddOnResolver.ResolveAtEntry()` runs once
2. For each add-on where `Enabled && Mode == Auto`, `AddOnAutoTuner.Tune()` computes values from volatility context
3. Results are frozen for the position's life (deterministic, replayable)
4. An `ADDON_RESOLVED` journal entry records what was activated

### Per Bar (`PositionManager.Evaluate`):
1. **Trailing**: stop moves behind price by `ATR × trailingAtrMultiple` when price moves favorably. Step size = `trailingStepPips` (prevents micro-adjustments).
2. **Breakeven**: when price reaches `entry ± SL_distance × triggerRMultiple`, SL moves to entry + offset.
3. **Ride**: when ADX > 25 (strong trend), relaxes trailing by 1.4× to let winners run.
4. **Partial TP**: at `triggerRMultiple × SL_distance`, close `closeFraction` of position. Remainder stays open with trailing.

### Auto-Tune Values (timeframe-dependent) (`AddOnAutoTuner.cs:68-75`):

| Timeframe | Trailing ATR Base | Breakeven Trigger R | Dynamic SL Multiple |
|-----------|-------------------|---------------------|---------------------|
| M1/M5/M15 | 2.0 | Volatility-scaled | 0.8 × TF base |
| M30/H1 | 2.5 | Volatility-scaled | 0.8 × TF base |
| H4 | 3.0 | Volatility-scaled | 0.8 × TF base |
| D1/W1 | 3.5 | Volatility-scaled | 0.8 × TF base |

Final values clamped to safe ranges (e.g., trailing ATR 1.5–4.0, dynamic SL 1.0–2.5).

---

## 7. Add-On System

**Files:** `src/TradingEngine.Domain/AddOns/*.cs`, `src/TradingEngine.Services/AddOns/AddOnResolver.cs:14-54`

Add-ons are **position management enrichments** that layer on top of the strategy's baseline SL/TP:

| Add-on | What it does |
|--------|-------------|
| **Breakeven** | Moves SL to entry when profit reaches trigger R |
| **Trailing** | Moves SL behind price as trade becomes more profitable |
| **Ride** | Relaxes trailing when ADX indicates strong trend |
| **Partial TP** | Closes fraction of position at trigger R; remainder trails |
| **Dynamic SL/TP** | Replaces baseline SL/TP with ATR-based values computed at entry |

Each add-on has:
- `Enabled` toggle (per-strategy, per-pack, per-run overridable)
- `Mode`: `Auto` (auto-tuner computes from volatility) or `Custom` (stored numbers)
- Pack system: named bundles (`AddOnPack`) that can be attached to multiple strategies

**Wiring flow:**
```
Strategy config → AddOnResolver.ResolveAtEntry()
  → AddOnAutoTuner.Tune(timeframe, volatility)
    → PositionManager.RegisterPosition(frozen config)
      → PositionManager.Evaluate() per bar → stop-move / partial-close instructions
```

Pack application in orchestrator (`BacktestOrchestrator.cs:631-662`):
```csharp
c = c with {
    PositionManagement = _configResolver.ApplyPack(c.PositionManagement, pack),
    RegimeFilter = ... with { DetectionEnabled = pack.RegimeDetectionEnabled }
};
```

---

## 8. SL/TP & Entry — Current Defaults and Auto-Calculation

### 8.1 Baseline SL/TP (per strategy config, `SlTpResolver.cs:5-52`)

| Method | Description |
|--------|-------------|
| `AtrMultiple` (SL default) | SL = entry ± ATR(14) × multiplier (default 1.5), capped at `MaxSlPips` from RiskProfile |
| `RrMultiple` (TP default) | TP = entry ± SL_distance × R:R ratio (default 2.0) |
| `SwingPoint` | SL at the most recent swing high/low within `SwingLookback` bars |
| `FixedPips` | SL at entry ± fixed pip value |

### 8.2 Automatic SL/TP via DynamicSlTp (`BarEvaluator.cs:143-172`)

When `DynamicSlTp.Enabled = true`:
- **Auto mode**: tuner computes `AtrMultipleSl` and `RrMultipleTp` from current volatility
  - `DynamicSlAtrMultiple` = 0.8 × timeframe base, clamped 1.0–2.5
  - `DynamicTpRrMultiple` = 1.5 + 0.25 × TF tier, clamped 1.5–3.0
- **Custom mode**: uses stored `AtrMultipleSl` and `RrMultipleTp` values
- Replaces the strategy's baseline SL/TP entirely for this position

### 8.3 Entry Planning (`EntryPlanner.cs:9-64`)

| Method | Behavior |
|--------|----------|
| `Market` | Entry at current bar price immediately |
| `MarketWithSlippage` | Entry at worst price within `maxSlippagePips` |
| `LimitOffset` | Limit order `limitOffsetPips` pips better than signal price; preserves SL distance from signal (shifts SL with limit so risk stays constant); expires after `limitOrderExpiryBars` bars |

### 8.4 Cost Calculation (`TradeCostCalculator.cs:27-55`)

Single source of truth for all venues:
```
GrossProfit = (exitPrice - entryPrice) × lots × pipValuePerLot
Commission  = lots × CommissionPerLotPerSide × 2 (round-turn)
Swap        = CountNightsHeld × per-lot swap rate (triple Wednesday)
NetProfit   = GrossProfit - Commission - Swap
```
- `CountNightsHeld` counts rollover boundaries at 22:00 UTC
- Triple-swap on the configured day (default Wednesday, configurable)

---

## 9. Auto-Tuning Across Timeframes & Symbols — Ideas & Architecture

The system already has the plumbing for auto-tuning via `AddOnAutoTuner`. Here's how to extend it:

### 9.1 Current Auto-Tune Mechanism

`AddOnAutoTuner.Tune(timeframe, volatilityContext)` at `AddOnAutoTuner.cs:41-65`:
- Takes `Timeframe` and `VolatilityContext` (ATR pips, spread pips, reference ATR)
- Returns tuned values for trailing ATR, breakeven trigger, dynamic SL/TP
- `TrailingBaseFor(Timeframe)` maps TF to a base multiplier (2.0→3.5)
- `ReferenceAtrPips(Timeframe, spreadPips)` computes a reference ATR for the TF (e.g., H1 = 20× spread, H4 = 35× spread)

### 9.2 Extending to 15m / 1h / 4h / Daily

The auto-tuner can be calibrated per timeframe by:
1. **Adding TF entries to `TrailingBaseFor`**: 15m → 1.5, 1h → 2.5, 4h → 3.0, D1 → 3.5 (linear scaling)
2. **Adding TF entries to `ReferenceAtrPips`**: 15m → 10× spread, 1h → 20×, 4h → 35×, D1 → 60×
3. **Volatility factor**: `currentAtr / referenceAtr` → determines if conditions are calm (arm breakeven sooner) or volatile (widen stops)

### 9.3 Using MAE/MFE for Tuning (Proven, Non-ML Methods)

**MAE (Maximum Adverse Excursion) and MFE (Maximum Favorable Excursion)** are per-trade metrics already collected in the engine:
- `TradeResult.MaxAdverseExcursion` (MAE) — in pips, how far price went against the trade
- `TradeResult.MaxFavorableExcursion` (MFE) — in pips, how far price went in favor

**Tuning approach — no ML, just statistics:**

1. **Optimal SL from MAE distribution:**
   - Run a batch of backtests with wide stops (e.g., ATR×3.0)
   - Collect MAE for all trades (both winners and losers)
   - Compute the MAE percentile where winners stop becoming losers:
     - `optimalSl = MAE.percentile(75)` — stop-loss at the 75th percentile of MAE captures 75% of winning trades while cutting losers earlier
   - Compare `optimalSl` against the current `atrMultiple × ATR`:
     - If `optimalSl < currentSl`, tighten stops → fewer losing trades at same win rate
     - If `optimalSl > currentSl`, loosen stops → capture more setups

2. **Optimal TP from MFE distribution:**
   - Collect MFE for winning trades
   - `optimalTp = MFE.median` — TP at the median MFE captures the central tendency of price movement
   - Alternative: `optimalTp = MFE.percentile(60)` — slightly more aggressive, captures 60% of the move

3. **Optimal R:R from MAE/MFE ratio:**
   - For each trade: `rr_achieved = MFE.pips / MAE.pips` (this is what the market actually offered)
   - The **attainable** R:R = median of winning trades' `rr_achieved`
   - If the strategy targets R:R=2.0 but actual median is 1.2, tighten TP or the R:R target is unrealistic

4. **Optimal trailing from MFE decay:**
   - For winning trades, compute how far price retraced from peak MFE before hitting TP
   - `trailingDistance = MFE.percentile(90) - exitPrice` — stop should be this far behind peak
   - Feed this into `trailingAtrMultiple` calibration

5. **Symbol-specific calibration:**
   - Run the MAE/MFE analysis per symbol (EURUSD, GBPUSD, etc.)
   - Store per-symbol calibration tables: `{symbol: {atrMultipleSl: X, rrMultipleTp: Y, trailingAtr: Z}}`
   - At entry, `AddOnAutoTuner` reads the per-symbol calibration instead of generic TF base

### 9.4 Implementation Path for Auto-Tuning

```
Phase 1 — Data collection:
  Run 50-100 tape backtests per symbol/TF with wide stops (ATR×3) and no TP
  → Collect per-trade MAE/MFE/result into a SQLite analysis table

Phase 2 — Statistical analysis:
  Compute optimal SL/TP/trailing per symbol per TF per regime
  → Store as JSON calibration tables

Phase 3 — Hot-reload calibration:
  AddOnAutoTuner reads calibration from DB instead of hardcoded lookups
  → Each backtest uses symbol+TF-specific tuned values

Phase 4 — Walk-forward validation:
  Split data into in-sample (train calibration) and out-of-sample (validate)
  → Confirm calibration doesn't overfit
```

This approach uses **descriptive statistics** (percentiles, medians) rather than ML — it's explainable, auditable, and doesn't require training infrastructure.

---

## 10. Fast Backtests Enabling Parallel Experiments

With the tape path (`Venue=tape`) functional:

1. **Parameter sweeps**: Run the same strategy across N parameter combinations simultaneously
   - Each run has its own inner host + adapter
   - All share the read-only `IMarketDataStore`
   - Example: sweep `AtrMultiplier` from 1.0 to 4.0 in steps of 0.25 = 13 runs × 0.5s each = 6.5s total

2. **Walk-forward optimization**:
   - Split market data into N windows
   - For each window: optimize parameters on in-sample, validate on out-of-sample
   - Trades from each window stitched together for continuous equity curve

3. **Ensemble backtests**:
   - Run multiple strategies simultaneously on the same symbol/TF
   - Combine signals: vote-based, weighted, or independent positions
   - Risk manager handles position correlation automatically

4. **Monte Carlo robustness**:
   - Randomize entry timing (±1 bar), shuffle trade order
   - Run 100+ simulations in seconds instead of hours
   - Compute confidence intervals for net PnL, max DD, win rate

---

## 11. Current Gaps & Issues — Prioritized

> ⚠ **UPDATED 2026-07-02 (iter-tape-trust):** The gap list below was written before the independent
> handover review. An updated bug/fidelity-gap audit with file:line evidence is at
> `docs/iterations/iter-marketdata-tape/HANDOVER-REVIEW.md` (B1–B11, F1–F8). The fix plan is at
> `docs/iterations/iter-tape-trust/PLAN.md`. T0–T5 have been implemented; see
> `docs/iterations/iter-tape-trust/HANDOVER.md` for the current state. The issues below (T1, T6, T7,
> Perf-*) may be stale — cross-reference with the review before treating them as active.

### P0 — Critical (blocking or correctness)

| ID | Issue | Location |
|----|-------|----------|
| **T1** | cBot entry timestamp is wall-clock, not sim-time → inverted chart, negative duration | `TradingEngineCBot.cs` |
| **T7** | Live journal shows only CLOSE; no SIGNAL/ORDER/FILL. Every position force-closed | `BarEvaluator.cs` → cTrader path |
| **T6** | cTrader trades show zero commission/swap | cBot close exec frames |
| **Perf-0** | No measurement baseline — unknown where wall-clock goes | All venues |
| **T11** | Live equity chart: no DD line + wrong axes | `RunMonitorComponent` |

### P1 — High (significant impact)

| ID | Issue |
|----|-------|
| **T4** | Per-bar "why" table shows warmup rows only (needs server-side endpoint) |
| **T10** | "Duplicate" re-runs through engine, not deterministic replay |
| **H17** | Bar-range SL/TP vs tick-based — different fill probability across venues |
| **CT-1** | cTrader E2E silently skips when credentials absent |
| **A1-A5** | Report missing: commission/swap, per-bar rejection reasons, trade chart |
| **D3** | Backtest is slow on cTrader path (perf action plan below) |
| **32-P4/P5** | Strategy browse/edit UI, New-Backtest override UI |
| **Reconcile** | LedgerReconciler built but not run with real data |

### Performance Action Plan (`docs/audit/BACKTEST-PERFORMANCE-ACTION-PLAN.md`)

| Phase | What | Impact |
|-------|------|--------|
| 0 | Measure: add stage timers in cBot + KernelBacktestLoop | Baseline |
| 1 | SQLite PRAGMAs (cache_size, synchronous, mmap) | Medium |
| 2 | Defer journal JSON serialization off pump thread | Low |
| 3 | cBot diet: suppress tick/account PUB, gate Print/Diag | High for cTrader |
| 4 | Indicator allocation diet: one bar→quote conversion, reuse buffers | Medium |
| 5a | Incremental indicators (streaming) | High CPU win, high risk |
| 5b | TCP_NODELAY, command-less-bar fast path | Medium for cTrader |

### Market Data / Tape Carry-Forward

| Phase | Task |
|-------|------|
| V2 | Download owner's EURUSD H1+M1, 1-6 months |
| V3 | Run tape backtest, measure speedup vs cTrader |
| V4 | Reconcile tape vs cTrader (LedgerReconciler.Compare) |
| V5 | Reconcile engine-DB vs cTrader report.json |
| P5 | Fidelity hardening: close MaxDD/swap/TradeSet divergences |
| P6 | Ticks support (optional, columnar binary tape) |

---

## 12. Key File Map (Code Reference Index)

### Engine & Kernel
| File | Purpose | Key Lines |
|------|---------|-----------|
| `TradingEngine.Host/KernelBacktestLoop.cs` | Unified per-bar driver for all venues | `RunFromBrokerAsync:128`, `ProcessBarAsync:163` |
| `TradingEngine.Host/BarEvaluator.cs` | Indicator compute + strategy eval + verdicts | `EvaluateAsync:51`, `DynamicSlTp:143-172` |
| `TradingEngine.Host/KernelTrailingEvaluator.cs` | Per-bar trailing/breakeven/partial | `Evaluate:42`, `BuildVolatility:98` |
| `TradingEngine.Engine/Kernel/PreTradeGate.cs` | Deterministic pre-trade validation | `Evaluate:33-199` |
| `TradingEngine.Engine/Kernel/KernelSizing.cs` | Position sizing math | `Calculate:47`, `Clamp:75` |

### Venues
| File | Purpose | Key Lines |
|------|---------|-----------|
| `TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs` | Fast in-process replay with dual-resolution exits | `OnBarObserved:167`, `FeedBarsAsync:134` |
| `TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` | In-process replay (single resolution) | `OnBarObserved:260` |
| `TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` | cTrader NetMQ bridge | `ReadRouterLoop:127`, `ReadSubLoop:110` |

### Market Data
| File | Purpose |
|------|---------|
| `TradingEngine.Infrastructure/MarketData/SqliteMarketDataStore.cs` | Canonical bar store (marketdata.db) |
| `TradingEngine.Infrastructure/MarketData/MarketDataIngester.cs` | Bulk-load NDJSON shards |
| `TradingEngine.Infrastructure/MarketData/MarketDataShardIo.cs` | NDJSON wire format |
| `TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:747-815` | Recorder cBot mode |

### Risk & Money
| File | Purpose | Key Lines |
|------|---------|-----------|
| `TradingEngine.Risk/RiskManager.cs` | Central risk orchestrator | `Validate:95`, `ValidateOrder:168` |
| `TradingEngine.Risk/Filters/SessionFilter.cs` | Trading session + weekend filter |
| `TradingEngine.Risk/Compliance/PropFirmComplianceService.cs` | Daily/weekly/monthly DD validation |
| `TradingEngine.Risk/Compliance/PassProbabilityEstimator.cs` | Monte Carlo pass probability |
| `TradingEngine.Engine/Kernel/PreTradeGate.cs` | Kernel-side gate (deterministic) |

### Add-Ons
| File | Purpose | Key Lines |
|------|---------|-----------|
| `TradingEngine.Domain/AddOns/AddOnPack.cs` | Named add-on bundle |
| `TradingEngine.Domain/PositionManagement/PositionManagementOptions.cs` | All enrichment options |
| `TradingEngine.Services/AddOns/AddOnResolver.cs` | Resolve add-ons at entry | `ResolveAtEntry:16` |
| `TradingEngine.Services/AddOns/AddOnAutoTuner.cs` | Timeframe+volatility-based tuning | `Tune:41`, `TrailingBaseFor:68` |

### Services
| File | Purpose | Key Lines |
|------|---------|-----------|
| `TradingEngine.Services/EntryPlanner.cs` | Market/Limit order planning | `Plan:9`, `PlanLimitOffset:23` |
| `TradingEngine.Services/Helpers/TradeCostCalculator.cs` | Commission/swap/net profit | `Compute:27` |
| `TradingEngine.Services/SLTPCalculation/SlTpHelpers.cs` | ATR-based / R:R SL/TP | `AtrBased:13`, `RRMultiple:38` |
| `TradingEngine.Services/SLTPCalculation/SlTpResolver.cs` | Dispatches to SL/TP methods | `Resolve:5-52` |

### Infrastructure
| File | Purpose |
|------|---------|
| `TradingEngine.Infrastructure/Indicators/AtrBasedRegimeDetector.cs` | ATR+ADX regime classifier |
| `TradingEngine.Infrastructure/Caching/RunDataCache.cs` | Write-through in-memory read cache |
| `TradingEngine.Web/Services/CacheEvictionSweeper.cs` | Background cache eviction (60s grace, N=8 cap) |

### Orchestration & Web
| File | Purpose | Key Lines |
|------|---------|-----------|
| `TradingEngine.Web/Services/BacktestOrchestrator.cs` | Run lifecycle, venue routing | `RunEngineReplayAsync:739`, tape wiring: `769-895` |
| `TradingEngine.Web/Services/RunQueryService.cs` | Cache-first reads + memory run detail | `GetRunAsync:77` |
| `TradingEngine.Web/Api/DataManagerController.cs` | Market data inventory + download | `GetInventory`, `StartDownload` |

---

## 13. Detailed Phased Plan for Next Iteration

> ⚠ **UPDATED 2026-07-02:** This plan was written pre-review and described P0-P6 phases as planned.
> The actual implementation happened in `iter-tape-trust` (T0-T5). See
> `docs/iterations/iter-tape-trust/PLAN.md` for the executed plan and
> `docs/iterations/iter-tape-trust/HANDOVER.md` for completion status.
> Cross-reference `docs/iterations/iter-marketdata-tape/HANDOVER-REVIEW.md` for the bug/gap
> catalogue (B1-B11, F1-F8) that drove the current plan.

### Phase A — Fix Critical Bugs (P0, ~2-3 sessions)

1. **T1**: Fix cBot sim-time → rebuild `.algo` → verify with 3-day cTrader run
2. **T6**: Fix cBot commission/swap reporting in close exec frames
3. **T7/T11**: Restore live journal SIGNAL/ORDER/FILL → verify monitor + equity chart
4. **Perf-0**: Add stage timers in cBot + engine → capture H1 3-month baseline in `PROGRESS.md`

**Gate after Phase A:** cTrader E2E suite green, monitor shows real events, equity chart correct.

### Phase B — Download Data + Verify Tape (V2-V4, ~1 session)

1. **V2**: Record EURUSD H1+M1, 1-6 months via recorder cBot
2. **V3**: Run tape backtest (`Venue=tape`), record wall-clock speedup
3. **V4**: Reconcile tape vs cTrader (`LedgerReconciler.Compare`) → document divergences
4. **V5**: Reconcile engine-DB vs cTrader report.json → explain the "DB ≠ cTrader" pain

### Phase C — Performance + Fidelity (P0-P1, ~2 sessions)

1. **Phase 1**: SQLite PRAGMAs via `IDbConnectionInterceptor`
2. **Phase 3**: cBot diet — suppress tick PUB, gate Print/Diag
3. **P5**: Close named MaxDD/swap divergences from reconcile findings
4. **Cache-P6**: Incremental/cursor reads for journal/equity polling

### Phase D — UI Completion + Auto-Tuning (P1-P2, ~3 sessions)

1. **A1-A5**: Complete report: per-bar why, trade chart, cost columns
2. **32-P4/P5**: Strategy browse/edit, New-Backtest override UI
3. **P4c/P4d**: Compare mode (side-by-side ledger) + Data Manager download form
4. **Auto-Tuning Phase 1**: Collect MAE/MFE from 50-100 tape runs → compute optimal per-symbol SL/TP
5. **Auto-Tuning Phase 2**: Add symbol-specific calibration to `AddOnAutoTuner`

### Phase E — Experiments & Scale (P3+, ~2-3 sessions)

1. **Parallel experiments**: Parameter sweeps, walk-forward, Monte Carlo
2. **Tape fidelity**: Tick-resolution exits (P6), limit order edge cases
3. **Angular SPA**: Standard CSS framework, final polish
4. **Documentation**: Finalize CODE-MAP, TEST-ARCHITECTURE, VERIFICATION

---

*Generated from 9 source documents + live code audit. All file paths and line numbers verified against the current codebase. Build: 0 errors, Unit: 314/0/6, Golden: 3/3 byte-identical.*
