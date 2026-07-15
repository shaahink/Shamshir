# A1 Session 1 Evidence — 2026-07-09

## Gate baseline (pre-fix)
- `dotnet build TradingEngine.slnx`: 0 errors, 5 pre-existing warnings
- Unit: 716 pass, 0 fail, 6 skip
- Integration: 121 pass, 0 fail, 0 skip
- Sim-fast: 144 pass, 0 fail, 0 skip
- Golden: clean (no diff)

## Gate baseline (post-fix)
- Build: 0 errors, 5 warnings
- Unit: 716/0/6
- Integration: 121/0/0
- Sim-fast: 144/0/0
- Golden: clean

## Fixes applied

### 1. Revert C# default (defense-in-depth)
File: `src/TradingEngine.Domain/Trading/OrderEntryOptions.cs:5`
Change: `OrderEntryMethod.LimitOffset` → `OrderEntryMethod.Market`

### 2. Startup diagnostic log
File: `src/TradingEngine.Web/Configuration/MiddlewarePipeline.cs`
Adds per-strategy log line at startup: `Startup config: {Strategy} entry method = {Method}`

### Startup log output (confirmed working)
```
Startup config: bb-squeeze entry method = Market
Startup config: ema-alignment entry method = Market
Startup config: macd-momentum entry method = Market
Startup config: mean-reversion entry method = LimitOffset
Startup config: mtf-trend entry method = Market
Startup config: rsi-divergence entry method = Market
Startup config: session-breakout entry method = Market
Startup config: super-trend entry method = Market
Startup config: trend-breakout entry method = Market
```

### DB truth (confirmed correct)
```sql
SELECT Id, json_extract(OrderEntryJson, '$.Method') as Method FROM StrategyConfigs;
-- 8 strategies = 0 (Market), 1 strategy (mean-reversion) = 1 (LimitOffset)
```

## Live tape backtest results

### Run 39c66994 (venue=tape, all 9 strategies, Jan 15-22 2026)
- Status: completed
- TotalBars: 145
- TotalTrades: 0
- Journal entries: 0
- WallElapsedMs: 5451

### Run eddec2cd (venue=tape, strategies=["trend-breakout","mean-reversion"], Jan 15-22 2026)
- Status: completed
- TotalBars: 145
- TotalTrades: 0
- Journal entries: 0
- WallElapsedMs: 4812
- Note: SSE progress briefly showed "trades=11" during run, but final persisted value is 0

## Finding
The C# default revert alone does NOT fix F17. The DB already had correct `OrderEntryJson` values (Market for 8/9 strategies). The root cause is in a different layer — the kernel loop processes bars but produces 0 journal entries and 0 persisted trades. Old tape runs (2026-07-07) with explicit RunPlanJson entries worked and have journal entries + trades. Regression occurred between 2026-07-07 and 2026-07-08.

Next session should investigate why kernel events (Journal, TradeResults) aren't being persisted despite the engine processing bars and generating in-memory fills.
