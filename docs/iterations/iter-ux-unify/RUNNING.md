# iter-ux-unify — RUNNING PLAN

**Branch:** `iter/ux-unify` (cut from `iter/redesign-ctrader` @ 5ec72fe)
**Status:** P0-P5 complete, P6-P9 remaining
**Started:** 2026-06-30
**Last commit:** `ddbc142 feat(ux): P3-P5 unified charts`

## Completed

| Commit | Phase | What |
|--------|-------|------|
| `fa8d443` | P0 | Failing repro tests: SL/TP null, trade-count mismatch, EstimateBarCount overestimate, data-table no-sort/no-number-format |
| `1d2237b` | P1.1 | Map `StopLoss/TakeProfit` in EF projection, rename DTO fields, thread `OrderEntryMethod` through kernel chain, add `'number'` case to `formatValue` |
| `e9ae277` | P1.2-P1.4 | Self-heal `ReconcileAsync` for nonzero-but-wrong TotalTrades, numeric money columns migration (TEXT→REAL), DB backup at `trading.db.ux-unify.bak` |
| `dab8fa4` | P2 | Pre-query actual bar counts for progress, backtest timeline component on live monitor |
| `ddbc142` | P3-P5 | Unified charts (legend + auto-fit + curved lines + same-bar markers), sortable/searchable data-table (sort + filter + search), TradeChartCard component + lazy gallery route `/runs/:id/gallery` |

## Test results

| Suite | Count | Status |
|-------|-------|--------|
| Unit | 277 pass, 6 skip | Green |
| Integration | 79 pass | Green |
| Frontend (Jest) | 6 pass | Green |

## Remaining phases (P6-P9)

See `docs/iterations/iter-ux-unify/PLAN.md` for full details:

- **P6** — Strategy entry/exit formula at backtest launch (static metadata map)
- **P7** — cTrader venue session history page (persisted table)
- **P8** — Full-width layout (remove max-w-7xl)
- **P9** — Backtest run profiling (capture + display)

## Key new/changed files (P3-P5)

| What | Path |
|------|------|
| Base chart (legend, auto-fit, curves) | `web-ui/src/app/shared/base-chart.component.ts` |
| Equity chart (legend, curves, fitContent) | `web-ui/src/app/shared/equity-chart.component.ts` |
| Candle chart (legend, fitContent, same-bar nudge) | `web-ui/src/app/shared/candle-chart.component.ts` |
| Scatter chart (legend, curves, fitContent) | `web-ui/src/app/shared/scatter-chart.component.ts` |
| Histogram chart (fitContent) | `web-ui/src/app/shared/histogram-chart.component.ts` |
| Data table (sort + search + filter) | `web-ui/src/app/shared/data-table.component.ts` |
| Trade chart card (reusable) | `web-ui/src/app/shared/trade-chart-card.component.ts` |
| Trade gallery route | `web-ui/src/app/features/runs/trade-gallery/trade-gallery.component.ts` |
| Trade detail (refactored to use TradeChartCard) | `web-ui/src/app/features/trades/trade-detail/trade-detail.component.ts` |
| Runs routes (added gallery) | `web-ui/src/app/features/runs/runs.routes.ts` |

## Resuming for P6-P9

1. `git checkout iter/ux-unify`
2. Read `docs/iterations/iter-ux-unify/PLAN.md` — full plan
3. Run `dotnet build` + `npm run build` to verify green
4. Start with P6 (strategy entry/exit formula)
