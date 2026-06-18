# Shamshir — Next Steps / Roadmap Backlog

**Updated**: 2026-06-18 (owner brain dump organized)
This is a *roadmap*, not the bug log — correctness bugs live in `docs/OPEN-ISSUES.md`.
Items grouped by theme with rough priority (P1 = do soon, P3 = later).

---

## A. Reporting & journal (highest near-term value)

### A1 — One-page, LLM-readable backtest report (P1)
A single page (and/or exportable markdown/JSON) explaining, for one run:
- What the backtest did: symbols, timeframes, period, strategies + effective params, risk profile, prop-firm ruleset, initial balance.
- Per-bar narrative: signal fired (and *why*), signal rejected (and *why*), order, fill, close (reason + costs). Foundation is in place — journal taxonomy + reasons + cost detail from iter-31. Not yet stitched into one view.
- Final stats: net/gross, commission/swap totals, win rate, profit factor, max DD, R distribution, per-strategy breakdown, breach timeline.
- Why signals were rejected: aggregate `BarEvaluated` reasons + `OrderRejected` violation codes into a funnel.

### A2 — Report is missing columns/fields (P1)
Trades table + run KPIs are incomplete. Add: Commission, Swap, Gross, Net columns (data exists now), R-multiple, MAE/MFE, pips, hold time, entry type, strategy, exit reason. Run-level: cost-inclusive KPIs, commission/swap totals, per-strategy breakdown.

### A3 — Per-bar "why rejected / why no signal" surfaced in UI (P1)
At every bar the system either has criteria to enter from strategy point of view, or not. If yes, system should pass criteria (time, DD, exposure, governor). Currently we don't know what exactly happened at each bar — whether system rejected or passed and for what reason. This is critical for diagnosing strategies.
`BarEvaluations` holds per-strategy per-bar data but isn't rendered. Build: paginated bar table + rejection-reason histogram.

### A4 — Journal completeness (P1)
**Violations not rendering**: REJECTED journal entries show `violations=[object Object],[object Object]` instead of readable violation names. Need proper serialization of violation list.

**Commission/swap null in closes**: CLOSE journal entries show `commission=null, swap=null`. Need to verify cost data flows through the close path — data exists in `TradeCostCalculator` but not reaching journal detail.

**Orders + fills not joined**: FILL and ORDER are separate journal tabs. Hard to see when what order got filled or expired. Should be in one unified view — order + fill events for the same order side by side.

**Signal flooding**: Monitor journal feed shows multiple SIGNAL entries per bar (e.g., same second timestamps for rsi-divergence signals with different SL/TP). Need to verify this is expected (multiple strategies? multiple symbols?) or a bug.

### A5 — Trade page fixes (P1)
**Chart missing**: The most important feature — price chart showing the trade with bars before/after, entry/exit markers, and strategy-relevant indicators (e.g., RSI for rsi-divergence, EMA for trend-breakout). Currently shows "No price data for this window."

**Formatting**: Commission and Swap show 0.00 as placeholders (data is null/wrong). Duration shows 00:00:00. Numbers need proper font, size, and coloring (green for profit, red for loss).

### A6 — Progress/UX glitches (P2)
- Equity curve flickers — re-render thrash; throttle or diff updates
- No cancellation button on backtest runs
- Backtest overlap: running a second backtest after one completes can throw OperationCancelledException — previous run didn't fully finish/cleanup
- Progress % and elapsed time inconsistent (`BarsTotal` ignores weekend/market gaps)
- **DD timeline display**: Show DD per day in a timeline, Max DD per month in the backtest period

### A7 — Monitor page improvements (P2)
Current monitor info isn't very helpful:
- Counter row (Signals:98, Orders:17, Fills:34, Closes:17, Rejections:0, Breaches:0) is confusing — for example, Fills count is double Closes (each trade has open+close fills) but this isn't explained
- Journal feed is sparse — Orders/Fills/Trades tabs often empty even when trades occurred
- Journal feed is incomplete — need per-bar "why no signal" for bars where no trade happened
- 30-item in-memory rolling buffer (31-B2 carry-forward) — needs lossless journal API polling
- Equity sparkline freezes after 500 frames (31-B2 carry-forward)

---

## B. cTrader CLI integration

### B1 — cTrader CLI JSON/HTML report capture (P1)
cTrader CLI can output JSON and HTML reports. Need to:
- Capture both report formats to our desired location
- Wire this into the backtest orchestration path (`BacktestOrchestrator` → `BacktestRunner`)

### B2 — E2E schema comparison test (P1)
Create E2E tests that compare our database schema/results with the cTrader CLI's final report and flag discrepancies. This will surface issues in PnL calculation, trade counts, drawdown, cost values, etc. Run as part of local CI until the system is mature — then diagnosis-only when divergences appear.

### B3 — cTrader venue parameter tracking (P2)
cTrader got quite a lot of updates when calling it. Need to surface these in the system/UI and allow overriding for a specific call. Track: CLI args, version, symbol string, timeframes, account config used.

---

## C. Live venue & status

### C1 — Venue status bar + page (P2)
A dedicated page (or status bar in the UI) showing whether the live venue is running or not. Key events displayed: started handshake, stop requested, error occurred, stop finalized, current state (connected/disconnected). Today this info is buried in logs. Also show: bars received, last tick time, NetMQ port state.

---

## D. Data & infrastructure

### D1 — Database fragmentation (P1)
Multiple database locations exist:
- `Shamshir\src\TradingEngine.Web\data\trading.db`
- `Shamshir\data\trading.db`
- Tests create databases in random folders that never get deleted
Need to unify to one location and clean up test artifacts. Run from a single configurable path.

### D2 — Hardcoded values audit (P1)
Can't run a backtest with another timeframe like 15m — some values appear hardcoded. Audit for:
- Timeframe defaults that bypass config
- Symbol defaults that override user selection
- Balance/account size defaults
- Any other magic numbers in the path from UI selection → engine config

### D3 — Backtest is slow (P1)
Profile and speed up. Suspects: per-bar indicator recompute (`IndicatorSnapshotService`), per-bar DB writes (journal/bar-eval flush), 5s settle delays in `RunEngineReplayAsync`, EF change-tracking on bulk inserts. Measure bars/sec first on the replay path (easiest to profile).

---

## E. Strategy / config UX

### E1 — View strategies & risk setup (P1)
Read-only first: a page to inspect every strategy's effective params (SL/TP, breakeven/trailing, regime filter, order entry, symbols, timeframe, risk profile). DB now holds seeded configs (iter-32 32-P0).

### E2 — Choose + save custom profiles per run (P1)
Across New-Backtest, Run-Report, and Live-Run, let users pick strategies + params + sizing and save as named custom profile. Uses `EffectiveConfigResolver` already built. (32-P4/P5 carry-forward.)

### E3 — Edit + validate write-back (P2)
Editing must reuse `ConfigLoader` cross-reference validation before upsert.

---

## F. Architecture / framework

### F1 — SPA + standard CSS framework (P2)
Adopt a standard CSS framework (Bootstrap or Tailwind) and drop custom styling. Move interactive surfaces to SPA (Angular or equivalent) with SignalR push. Keep mixed approach — not a full rewrite.

### F2 — Vertical slicing with clean wiring (P3)
Reorganize around feature slices (Backtest, LiveRun, Strategy, Risk, Reporting). Reduce DI/wiring sprawl. Incremental, large work.

---

## G. Testing — pressure the rules

### G1 — Test inventory (P1)
Produce a detailed test case list mapping each test to the system rule it pins, and identify gaps.

### G2 — Rule pressure / edge-case suite (P1)
Adversarial scenarios: strategy races, daily-loss limit, max-DD, max-exposure, force-close-on-breach, protection-mode entry/exit, governor states, news/session filters, weekend/rollover (triple-swap), partial fills, limit expiry. Assert the engine **enforces** and the journal **explains** each enforcement.

---

## H. cTrader E2E verification (iter-31/32 carry-forward)

| Phase | What | Priority |
|-------|------|----------|
| 31-A2 | cBot emits `commission`/`swap` in close EXEC frame; adapter maps to `ExecutionEvent` | Medium |
| 31-A3 | Report shows Commission/Swap/Gross/Net columns. Delete dead `equityDefinition` string | Medium |
| 31-C2 | Live limit path — verify `CTraderBrokerAdapter` limit branch with populated `LimitPrice` | Medium |
| 32-P4 | Strategy browse/edit UI | High |
| 32-P5 | New-Backtest per-run override UI | High |
| 31-B2 | Monitor: lossless journal API polling, remove 500-frame sparkline freeze | Low |
| 31-C3 | Set `mean-reversion.json` → `LimitOffset` as worked example | Low |
| 32-P6 | `JsonExportService` endpoint + regenerate `InitialCreate` migration | Low |
| 31-A4 | (Optional) Commission-aware risk budget | Optional |

---

## Suggested sequencing

1. **A1+A2+A3+A4** (one-page report + missing fields + per-bar "why" + journal completeness) — highest signal, mostly rendering on data that already exists
2. **A5** (trade page chart + formatting) and **BUG-09** (governor cooling-off) — critical correctness + UX
3. **D1** (DB fragmentation) + **D2** (hardcoded values) — infrastructure hygiene
4. **B1+B2** (cTrader reports + E2E comparison) — verification layer
5. **A6+A7** (Monitor + Progress UX) — makes the tool usable
6. **G1+G2** (test inventory + pressure suite) — confidence
7. **E1→E2** (config view → custom profiles) — product UX
8. **D3** (performance) — unblocks faster iteration
9. **C1** (venue status) and **B3** (cTrader parameter tracking)
10. **F1+F2** (CSS framework + vertical slices) — larger refactors after above stabilizes
