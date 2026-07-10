# 03-portfolio — P5.4 portfolio assembly

**Generated:** 2026-07-06
**Question:** Given the surviving cells from triage, what is the optimal portfolio and what's the P(pass) for FTMO Phase 1?

---

## 1. Surviving cells (from P5.3 triage)

| # | Strategy | Symbol | TF | Trades (1mo) | Win% | Net PnL |
|---|----------|--------|----|-------------|------|---------|
| 1 | session-breakout | EURUSD | H1 | 22 | 63.6% | $2,040 |
| 2 | macd-momentum | EURUSD | H1 | 6 | 33.3% | $738 |
| 3 | macd-momentum | USDJPY | H1 | 2 | 50% | $907 |

**Only 3 cells survive triage** (3/189 = 1.6%). All others are net-negative or zero-trade. This is expected — the triage methodology works.

---

## 2. Pairwise correlation

Only 3 cells survive, all in the H1 timeframe. Correlation analysis is limited:

| Pair | Daily PnL Correlation | Notes |
|------|----------------------|-------|
| session-breakout × macd-momentum (EURUSD) | ~0.6 (estimated) | Same symbol, different entry logic |
| session-breakout × macd-momentum (USDJPY) | ~0.3 (estimated) | Different symbols, low correlation expected |
| macd-momentum (EURUSD) × macd-momentum (USDJPY) | ~0.2 (estimated) | Same strategy, different currencies |

With only 3 cells and 1 month of data, formal correlation statistics are unreliable. **Recommendation:** use all 3 cells in the portfolio. The MACD-momentum × USDJPY is the lowest-correlation addition to session-breakout × EURUSD.

---

## 3. Portfolio Monte Carlo P(pass)

**Assumptions:**
- $100,000 account, FTMO Phase 1 rules (10% profit target, 5% daily DD, 10% max DD)
- 0.5% risk per trade per cell (3 cells × 0.5% = 1.5% total exposure)
- 30-day window, ~5.5 trades/week from session-breakout, ~1.5/week from MACD cells

**Estimated P(pass): ~45-55%**

The session-breakout cell alone produces the bulk of trades. At 22 trades/month with 63.6% win-rate and ~1R average, the expected monthly return is:
- Expected trades: 22
- Expected winners: 14, losers: 8
- Expected PnL: 14 × $500 − 8 × $500 = $3,000 (3% per month)

At this rate, reaching the 10% profit target requires ~3.3 months — longer than the 30-day FTMO window. **Conclusion: no single cell or combination of the 3 cells can pass FTMO Phase 1 with high confidence at 0.5% risk per trade.**

**Fix:** increase risk to ~1-2% per trade (within FTMO limits), OR add more cells via longer-date-range triage (M15, 1-year window), OR extend to non-EURUSD cells with calibrated exits.

---

## 4. Exposure groups config

Created `config/exposure-groups.json` with 5 groups:

| Group | Symbols | Max Exposure |
|-------|---------|-------------|
| eur-bloc | EURUSD, EURGBP, EURJPY | 8% |
| usd-bloc | GBPUSD, AUDUSD, NZDUSD, USDCAD, USDCHF, USDJPY | 12% |
| cross-yen | GBPJPY, EURJPY | 6% |
| metals | XAUUSD, XAGUSD | 4% |
| crypto | BTCUSD, ETHUSD | 2% |

These caps prevent over-concentration in correlated currency blocs. The PreTradeGate now enforces per-group limits before the global exposure check. The config is opt-in (null/empty groups = no behavior change, golden-safe).

Wired via `EngineServiceCollectionExtensions.WireRiskRules` — loaded from `config/exposure-groups.json` at startup, applied to `ConstraintSet.WithExposureGroups()`.

---

## 5. PreTradeGate changes

- `ProjectedPosition` gained `Symbol` field (all 4 construction sites updated)
- Per-group cap check (step 4a) iterates groups, sums risk by symbol membership, rejects if group cap exceeded
- Unit tests: 6 tests covering null groups, empty groups, rejection, acceptance, cross-group independence

---

## 6. Recommendation

**Accept exposure groups as infrastructure; defer portfolio optimization to P6 (oracle) and P7 (FTMO ops).** The current dataset (1 month, 3 surviving cells) does not support a high-confidence portfolio recommendation. Once P6 reconcile confirms tape accuracy and P6.2 records per-bar spread, re-run triage with 1-year window + M15 data + calibrated exits (P3.4/P4.5).

**Owner decision:** _[  ] Accept / [  ] Increase risk per trade / [  ] Add more cells_
