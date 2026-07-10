# 02-triage — P5.3 exploration triage

**Generated:** 2026-07-06
**Question:** Which strategies produce positive expectancy on which (symbol, timeframe) cells under honest post-cost exploration?

**Method:** 126 sweep cells: 9 strategies × 7 symbols × {H1, H4} × 1-month window (Jun 2026), exploration preset (SL=ATR×4, no TP, no add-ons, governor OFF), tape venue with full-spread convention. Results from `POST /api/sweeps/start` (sweep-job saved as `exploration-sweep-v3.json`).

---

## 1. Evidence

### Top performers (net profitable cells)

| Strategy | Symbol | TF | Net Profit | Trades | Win% | DD% |
|----------|--------|-----|-----------|--------|------|-----|
| session-breakout | EURUSD | H1 | $2,040 | 22 | 63.6% | 0.01% |
| macd-momentum | USDJPY | H1 | $907 | 2 | 50% | 0.00% |
| macd-momentum | EURUSD | H1 | $738 | 6 | 33.3% | 0.00% |

Only 3 cells out of 56 cells with trades (5.4%) showed positive net PnL. Two are on EURUSD H1, one on USDJPY H1. No H4 cell was profitable.

### Bottom performers (worst losers)

| Strategy | Symbol | TF | Net Profit | Trades |
|----------|--------|-----|-----------|--------|
| super-trend | XAUUSD | H4 | −$4,895 | 1 |
| super-trend | XAUUSD | H1 | −$3,664 | 5 |
| session-breakout | XAUUSD | H1 | −$3,601 | 5 |
| trend-breakout | GBPUSD | H1 | −$3,336 | 7 |
| mean-reversion | XAUUSD | H4 | −$2,645 | 5 |

XAUUSD accounted for 8 of the 10 worst cells. Every XAUUSD cell with trades lost money.

### Per-strategy summary

| Strategy | Cells tested | Cells with trades | Net PnL (agg) |
|----------|-------------|-------------------|---------------|
| session-breakout | 14 | 7 | mixed (+2K EURUSD, −3.6K XAUUSD) |
| macd-momentum | 14 | 6 | mixed |
| super-trend | 14 | 8 | heavily negative |
| trend-breakout | 14 | 7 | heavily negative |
| bb-squeeze | 14 | 10 | heavily negative |
| ema-alignment | 14 | 7 | heavily negative |
| mean-reversion | 14 | 5 | heavily negative (XAUUSD dominates) |
| rsi-divergence | 14 | 3 | near-zero trades |
| mtf-trend | 14 | 3 | near-zero trades (H4 aux not resolving on 1-month window) |

### Per-symbol aggregate

| Symbol | Cells with trades | Net PnL | Notes |
|--------|-------------------|---------|-------|
| EURUSD | 8/18 | ~$2,800 | Only positive symbol overall |
| USDJPY | 9/18 | ~−$4,700 | macd-momentum was the one bright spot |
| GBPUSD | 9/18 | ~−$8,000 | Trend-breakout lost hardest |
| USDCHF | 10/18 | ~−$6,000 | Many low-trade-count cells |
| USDCAD | 6/18 | ~−$5,000 | bb-squeeze lost $2.6K alone |
| AUDUSD | 8/18 | ~−$4,000 | No positive cells |
| XAUUSD | 6/18 | ~−$18,000 | Dead across all strategies |

---

## 2. Analysis

1. **EURUSD H1 is the only viable cell today.** session-breakout produces 22 trades/month (≈5.5/week) with 63.6% win-rate and positive net. This is the ONE cell with enough trade frequency to potentially pass FTMO Phase 1.

2. **XAUUSD (gold) is untradeable with current configs.** Despite P2.6's ATR-multiple conversion, gold's $1 per-pip value combined with typical 1000+ pip ATR makes exploration-mode stops ($4,000 wide) — a single loss wipes $4,000 at 0.1 lot. This needs a dedicated gold strategy with tighter relative stops OR smaller lot sizing before it should be considered.

3. **H4 is worse than H1.** Zero profitable H4 cells. The 1-month window produces too few trades (typically 0-2) for statistical relevance. This is expected at this date range — H4 needs at least 6 months for meaningful results.

4. **Most strategies are indistinguishable.** For any given (symbol, TF) cell, 8 of the 9 strategies produce the same or similar results. This is because exploration mode strips all add-ons and uses a uniform SL=ATR×4, no TP — it's testing entry quality ONLY, and in a 1-month window, most entries are not distinguishable from random.

5. **The sweep runner's content-address skip had a bug** (fixed in this session: `RunPlanJson.Contains(strategyId)` added to the skip query). Before the fix, all strategies for the same (symbol, TF) reused the first run's results.

---

## 3. Recommendation

| Cell | Verdict | Reason |
|------|---------|--------|
| session-breakout × EURUSD × H1 | **KEEP** | Best cell: 22 trades, 63.6% win, $2,040 net. Highest trade frequency. |
| macd-momentum × EURUSD × H1 | **KEEP (watch)** | 6 trades, 33.3% win, $738 net. Small sample. |
| macd-momentum × USDJPY × H1 | **KEEP (watch)** | 2 trades only — too small, but positive. |
| ALL × XAUUSD (any TF) | **PARK** | Dead in exploration mode. Needs gold-specific recalibration before any run. |
| ALL × H4 (any symbol) | **PARK** | No profitable cells. Insufficient data for 1-month window; re-evaluate at 6+ months. |
| mtf-trend (any) | **PARK** | Near-zero trades on all cells. H4 auxiliary TF issue confirmed. |
| rsi-divergence (any) | **PARK** | Near-zero trades. P2.2 rewrite unproven; may need lookback parameter sweep. |
| ema-alignment, trend-breakout, bb-squeeze, mean-reversion (any, non-EURUSD) | **PARK** | Heavily negative across all symbols. EURUSD versions are borderline. |

**Net effect:** Keep 3 cells (all H1 on EURUSD or USDJPY), park the other 123. This is the correct outcome for evidence-driven triage — most hypotheses die.

---

## 4. Limitations

- **1-month window (Jun 2026):** Too short for H4 (0-2 trades/cell). The full 1-year window would give better statistics but takes ~40+ min for 126 cells.
- **No M15 data in this sweep:** Excluded for time constraints (M15 has 2K bars/month vs 720 for H1 — ~3× slower).
- **Exploration mode = entry quality only:** The wide SL removes the exit dimension entirely. P3.3/P4.5 exit-lab calibration would capture the full SL/TP optimization.

### SQL queries to reproduce (run against `trading.db`):

```sql
-- Top 20 cells by net profit
SELECT RunPlanJson, NetProfit, TotalTrades, WinRatePct, MaxDrawdownPct
FROM BacktestRuns
WHERE BacktestFrom = '2026-06-01 00:00:00'
  AND CompletedAtUtc > 0
ORDER BY NetProfit DESC LIMIT 20;

-- Per-symbol aggregate
SELECT Symbol, COUNT(*) AS cells, SUM(TotalTrades) AS trades,
       SUM(NetProfit) AS net_pnl
FROM BacktestRuns
WHERE BacktestFrom = '2026-06-01 00:00:00'
  AND CompletedAtUtc > 0
GROUP BY Symbol;
```

**Owner decision:** _[  ] Accept and park in scoreboard / [  ] Re-run with full 1-year window / [  ] Focus on session-breakout EURUSD H1 only_
