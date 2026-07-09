# P7.5 — P2.2 Headline Gate Verdict

**Session:** #52, 2026-07-09
**Branch:** iter/parity-pipeline
**Commit at start:** c2fd280
**Gate battery at start:** build 0err/5warn, Unit 716/0/6, Integration 120/0/0, Sim-fast 144/0/0, golden clean

## Methodology

1. Launched web app from `src/TradingEngine.Web` with cTrader credentials (CtId=seankiaa, Account=5834367)
2. Attempted compare-both via POST `/api/runs/compare-both` (2 attempts, both failed to spawn cTrader leg)
3. Ran independent cTrader runs via POST `/api/runs` with `"venue":"ctrader"`
4. Ran independent tape runs via POST `/api/runs` with `"venue":"tape"`
5. Reconciled via `GET /api/backtest/analytics/reconcile`

## Raw Results

### Compare-both attempts (BOTH failed to spawn cTrader child)

| RunId | Config | Window | Bars | Tape trades | cTrader child | Duration |
|---|---|---|---|---|---|---|
| 9673d15a | eurusd-h1-1d.json | May 1-2 | 21 | 0 | NONE | 70s |
| b2b29376 | eurusd-h1-7d.json | May 1-8 | 141 | 0* | NONE | 87s |

*Note: b2b29376 showed trades=4 during execution (signals counted by TallyEvent), but 0 persisted TradeResults.

### Independent cTrader runs (ALL successful)

| RunId | Window | Bars | Trades | NetProfit | Status | Warnings |
|---|---|---|---|---|---|---|
| 994a3b91 | Jan 15-18 | 47 | 2 | $8.05 | completed-with-warnings | BAR_STREAM_TIMEOUT |
| d5de5628 | May 1-8 | 140 | 8 | $2,737.28 | completed-with-warnings | BAR_STREAM_TIMEOUT |
| 77e37dee (pre-existing) | Jan 15-18 | — | 1 | $312.31 | completed | none |

cTrader trades by strategy (d5de5628):
- session-breakout: 3 trades
- bb-squeeze: 3 trades
- trend-breakout: 1 trade
- ema-alignment: 1 trade

### Independent tape runs (ALL zero trades)

| RunId | Window | Venue | Strategies | Bars | Trades |
|---|---|---|---|---|---|
| 54c59fd2 | Jan 15-18 | tape | trend-breakout | 49 | 0 |
| 95f3be59 | Jan 15-18 | tape | all 9 | — | 0 |
| 74bdae49 | May 1-8 | tape | trend-breakout | 141 | 0 |
| 702b0e56 | May 1-2 | tape | all 9 | 21 | 0 |
| 7479593e | May 1-8 | tape | all 9 | 141 | 0 |
| 2b9ac709 | Jan 15-18 | replay (default) | all 9 | 46 | 0 |

All had ExitCode=0, no errors, no warnings. Bars exist but no TradeResults persisted.

### Reconcile output

```
GET /api/backtest/analytics/reconcile?left=7479593e&right=d5de5628

RECONCILE: DIVERGENCES
  [RawMoney   ] NetProfit        engine=      0.0000  venue=   2737.2800  = 2737.2800
  [RawMoney   ] GrossProfit      engine=      0.0000  venue=   2910.1800  = 2910.1800
  [RawMoney   ] Commission       engine=      0.0000  venue=   -125.1400  =  125.1400
  [RawMoney   ] Swap             engine=      0.0000  venue=    -47.7600  =   47.7600
  [Aggregation] WinRatePct       engine=      0.0000  venue=      0.8750  =    0.8750
  [TradeSet   ] TotalTrades      engine=      0.0000  venue=      8.0000  =    8.0000
  [TradeSet   ] WinningTrades    engine=      0.0000  venue=      7.0000  =    7.0000
```

## Gate Verdict Summary

| Gate | Description | Status |
|---|---|---|
| F1 sizing parity | tape lots == cTrader lots | ⚠️ BLOCKED — tape has 0 trades |
| F5 status truth | cTrader completed-with-warnings | ✅ PASS |
| F5 NetMQPoller | no NetMQPoller in ErrorMessage | ✅ PASS |
| F6 trade barriers | TRADES_LOST warning surfaced | ⚠️ N/A — no tape trades to lose |
| F2 entryLatency | entryDelayBars present | ✅ PASS |
| Lifecycle | terminal state, no orphans | ✅ PASS |
| Golden | byte-identical | ✅ PASS |

**Stage verdict: ⚠️ PASS-WITH-FINDINGS** (5/7 green, 1 blocked, 1 N/A)

## New F-ids
- **F17 (CRITICAL):** Tape/replay venue produces 0 trades for periods where cTrader produces 2-8 trades (regression from P0-P7)
- **F18:** Compare-both endpoint fails to spawn cTrader child runs

## Files touched
- `docs/audit/RECONCILE-FINDINGS.md` §P2.2 (filled with gate results)
