# Baseline Sweep Scoreboard — s1

**Experiment:** baseline-sv1 (`23C20ED8-83F7-43BF-B247-0EAB73CE19C8`)  
**Sweep:** 9 strategies x 14 symbols x {H1, H4} = 252 cells  
**Window:** 2025-07-04 -> 2026-05-05 (embargoed window untouched)  
**Scorer:** SetupScore v1 (sv1-partial — no OOS robustness)  
**Validity floor:** >= 20 trades, tape venue, clean (no warnings), completed  
**Wall time:** ~31 minutes (28 batched runs + scoring pass)

## Summary

| Metric | Value |
|--------|-------|
| Total cells | 252 |
| Scored (>= floor) | 4 |
| Below floor | 248 |
| Failed | 0 |
| Scored-or-null % | 100% |

## Top 4 (all cells above floor)

| # | Variant | Score | Strategy | Symbol | TF |
|---|---------|-------|----------|--------|----|
| 1 | trend-breakout/XAUUSD/H4 | 100.0 | trend-breakout | XAUUSD | H4 |
| 2 | trend-breakout/USDCAD/H4 | 74.7 | trend-breakout | USDCAD | H4 |
| 3 | bb-squeeze/USDCAD/H4 | 73.2 | bb-squeeze | USDCAD | H4 |
| 4 | trend-breakout/NZDUSD/H1 | 47.1 | trend-breakout | NZDUSD | H1 |

## Observations

- **20-trade floor is restrictive** for 10-month windows: only 4/252 cells (1.6%) qualified. This is by design (D3) — 0 is information; null is "insufficient data."
- **trend-breakout dominates**: 3 of 4 qualifying cells use this strategy, suggesting it produces more frequent entries.
- **H4 outperforms H1**: 3 of 4 qualifying cells are H4 (fewer bars but apparently clearer signals).
- **Metals head the board**: XAUUSD at score 100.0 (max) means mean R >= 0.5 and drawdown < 3%.
- **248 below-floor cells**: All have valid reasons recorded (trade count < 20). These are NOT failures — they are null scores per D3.

Full data: [scoreboard-s1.csv](scoreboard-s1.csv)
