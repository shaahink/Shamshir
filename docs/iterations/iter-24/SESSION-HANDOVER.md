# Iter-24/21 Session Handover — 2026-06-15

Continues `docs/iterations/iter-24/HANDOVER.md`. Branch `iter/24-build`. 10 commits this session
(`ab3635c`..`313b17e`). All deterministic suites green: **175 Unit + 12 Simulation (10 FTMO +
2 exit-reason) + 3 Integration contract**. Real cTrader e2e verified working.

## Working rules (unchanged + new)
- Failing-test-first; build + fast suites green at every commit.
- Fast suites: `dotnet test tests/TradingEngine.Tests.Unit` (175), `dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~Ftmo"` (12).
- **Real cTrader e2e works now.** Creds in `src/TradingEngine.Web/appsettings.Development.json`, cli at
  `%LOCALAPPDATA%\Spotware\cTrader\...\ctrader-cli.exe`. Run with `--filter "FullyQualifiedName~CtraderScenarioTests"` (skips without creds, ~30s/test, network). Kill stray `ctrader-cli` before.
- **cBot edits require `dotnet build src/TradingEngine.Adapters.CTrader/...` to repackage `src.algo`** before a real run picks them up. Engine (`TradingEngine.Host`) edits do NOT (engine runs in-process in the test host).
- **IDE0011 (braces) is an error** in `Infrastructure`, `Services`, `Host`, `Tests.Simulation`. Brace every `if`/`else`, even one-liners with the body on the next line.
- Commit message files: use `mktemp` OUTSIDE the repo — `git add -A` will otherwise stage an in-repo `.gitmsg.tmp` into the commit.

## What shipped this session

### Money/correctness (engine)
- **p7 (`ab3635c`)** — `ValidateOrder` worst-case daily floor now honors `DailyDdBase` (was always `DailyStartEquity`, diverging from the breach watchdog). Regression test in `RiskManagerTests`.
- **p7 (`12c5d24`)** — portfolio worst-case projection uses cross-rate-aware `PipCalculator.PipValuePerLot` for open positions (was naive `PipSize*ContractSize`; wrong for non-USD-quote pairs). `TradingLoop` now takes the cross-rate provider.
- **p9 (`b7c95d0`) — CRITICAL** — `EffectExecutor` needs `IReadOnlyList<IStrategy>` but both DI paths registered only `IEnumerable<IStrategy>`, so `EngineHostFactory.Create` threw at startup. **This broke the production dashboard backtest path AND every cTrader pipeline test.** Fixed both `AddStrategies`/`AddStrategiesFromOptions` in `EngineServiceCollectionExtensions`.
- **p10 (`3ce14c4`)** — cBot `MakeExecResult` hard-coded `grossProfit/netProfit=0`, so engine-requested closes booked **$0 PnL** in the ledger (a 96-pip move read $0). Now captures realized PnL before `ClosePosition`. **Verified live** (real PnL on every trade).
- **p11 (`6a78f12`)** — accurate exit reasons for engine-detected exits: `PositionTracker.SetCloseReason` stamps SL/TP/breach before the venue close so the close fill journals the real reason, not "FORCE". `RequestForceCloseAllAsync` stamps DailyDD/MaxDD. Verified by `ExitReasonTests`.
- **p12 (`313b17e`)** — venue-initiated closes (cTrader server-side SL/TP fire intrabar, before the engine's bar-level detection) now propagate cTrader's `PositionCloseReason` → `ExecutionEvent.CloseReason` → `PositionTracker` stamps it. **Needs a real-cTrader run to confirm** the live ledger reads SL/TP (deterministic paths verified; the prior real run still showed all-FORCE because this fix landed after).

### Venue ↔ engine (p8, `2f5f99f`)
V1 (startup/reconnect reconciliation via cBot hello snapshot), V2 (durable Guid↔venue-id via position `Comment`), V5 (buffered-command durability across disconnect), V3 (SL/TP modify writeback). 8 tests (5 `PositionTrackerReconciliationTests`, 3 `FakeTransportTests`). **Wire logic verified via FakeTransport; not yet exercised against real cTrader reconnect** (cTrader backtester can't easily trigger reconnect).

### UI (iter-21)
- **U0 partial (`e3c711e`)** — fixed all `@expr:Fn` Razor bugs (rendered literal `:F2`/`:yyyy-MM-dd`) across Trades/Performance/RunDetail.
- **U1 (`d26447a`)** — `RunHub` SignalR + throttled `RunProgress` envelope (RunProjection-shaped) + `RunProgressBroadcaster` wired into `BacktestOrchestrator` + `run-client.js` + 3 contract tests.

### Real cTrader e2e tests (`5cf5d4e`)
`CtraderScenarioTests` (happy-path ledger integrity, weekend/sparse edge case, no-orphan cleanup) + `CtraderTestHarness.Result.TradeRows`. Skips without creds.

---

## What's LEFT — ordered for a new agent

### A. Finish the cTrader-faithful simulated venue (the headline ask — highest value)
**Goal:** a deterministic, network-free in-process venue that mimics cTrader well enough to pressure money guards / prop rules / multi-symbol+timeframe — so most edge cases verify in <1s with no cTrader install.

Two existing vehicles: `FakeCBot` (real NetMQ wire, in `Tests.Simulation/Harness`, currently **unused**) and `EngineHarness`/`EngineHarnessBuilder` (in-process, no sockets, used by FTMO tests). **Recommend extending the in-process `EngineHarness`** (deterministic, no socket flake) rather than FakeCBot, since money-guard/prop-rule edge cases are engine concerns, not wire concerns. Reserve FakeCBot for protocol/transport tests.

Concrete steps:
1. **Real PnL in the harness equity model.** `EngineHarness.DriveBarsAsync` uses `ApproximateClosedPnL` (fixed −50 pips/close) — too crude to pressure drawdown/breach accurately. Replace with realized PnL from the now-captured `ClosedTrades` (direction-aware `(exit−entry)*lots*contractSize`, cross-rate via the registry). **Risk:** the FTMO tests assert drawdown magnitudes; re-validate all 12 and adjust fixtures. Gate: a known down-leg produces the arithmetically-correct equity curve.
2. **Multi-symbol / multi-timeframe.** `EngineHarnessBuilder` is single-symbol (EURUSD). Parameterize symbol set + per-symbol bars; drive interleaved bars by timestamp. Gate: a 2-symbol run opens/exits positions per symbol with correct per-symbol pip values.
3. **Account snapshot + drawdown fidelity.** Drive `AccountProcessor.HandleAsync` from the harness (today the harness mirrors breach logic in `CheckBreachAsync` instead of using `AccountProcessor`). Feed real balance/equity/floating-PnL so daily/weekly resets + the breach watchdog run exactly as production. Gate: a position spanning a day-roll updates the daily-start baseline (also closes out the deferred **A2 daily-reset baseline** item).
4. **Position snapshot / reconciliation in-harness.** Exercise `SeedOpenPositions` (V1) deterministically: seed venue positions, restart the loop, assert they're managed/force-closed. Gate: engine restarted with an open position can force-close it.
5. **OnStart/OnStop + meaningful bars.** A small bar generator (trend / range / gap / spike regimes) so tests read like scenarios. Gate: a "trend up then reverse" run trips a trailing stop.

Then add **edge-case scenario tests** on top: max-daily-loss breach mid-day halts trading; max-total-loss; exposure cap across N symbols; weekend hold restriction; news window; per-strategy concurrent-position cap.

### B. Rich, accurate decision journal ("run a backtest, see every decision with values")
**Goal:** after a backtest, read the journal and see every decision (bar eval → signal → gate pass/block → risk validation w/ violation codes → order submit w/ lots/risk → fill → exit w/ reason → drawdown updates), each with the values and the reason, in order.
- The plumbing exists: `IDecisionJournal`/`DecisionRecord` (persisted), `RunProjection.GetRunAsync` already projects a timeline, and `OrderDispatcher` records `OrderRejected`/`OrderSubmitted` with violation codes + detail JSON. **Gaps:** (a) the FTMO/`EngineHarness` passes `journal: null` — wire an in-memory `IDecisionJournal` so deterministic tests can assert the decision trail; (b) exit/close decisions and drawdown/breach transitions aren't all journaled as `DecisionRecord`s; (c) `RunProjection.DecisionRecordView.StrategyId` is hardcoded `null`. 
- Deliver: an in-memory journal in the harness + a test that runs a multi-bar backtest and asserts the ordered decision sequence with values; then ensure the real backtest journal (queried via `RunProjection`) carries the same richness for the UI Report.
- Confirm **p12** end-to-end with a real run: `CtraderScenarioTests` should assert SL/TP appears in `TradeRows[].ExitReason` (tighten the happy-path test once verified).

### C. Carry-forward from the original iter-24 queue (still open)
- **Phase 0f `IEnginePacer`** — remove the last `if (_engineMode == Backtest)` fork in `EngineRunner` (async-stream vs bar-stepped) behind a pacer abstraction. No fast-test coverage of `EngineRunner` exists — add an in-process harness first (depends on A).
- **A4** (unify FloatingPnL definition), **MonthRolled** path / remove dead `ApplyMonthlyReset`, retire `TradingGovernorService`.

---

## UI action plan (iter-21) — for the new agent, after/with the engine work

iter-21 `PLAN.md` is the spec (U0–U7). U1 (hub + envelope) is done. **These phases need the app running for visual verification** (`/run` or `/verify` skill): `dotnet run --project src/TradingEngine.Web`.

1. **U0 (finish the foundation)** — *do first.* Currently only the Razor format bugs are fixed. Remaining: consolidate on **one** chart stack (LightweightCharts ES module in `wwwroot/js/charts/`; delete `charts.js`/`trading-charts.js` + Chart.js CDN); new nav/IA (LIVE / RESEARCH / LIBRARY) in `_Layout` + `_RunNav` partial; design tokens in `site.css` (see PLAN Appendix A); retire the unused Blazor `_Host.cshtml` or document it. Gate: dashboard equity chart renders via LightweightCharts, no console errors, no `:F5` literals.
2. **U2 — Live Run Monitor** (`/runs/{runId}/monitor`) consuming the shipped `RunProgress` envelope via `wwwroot/js/run-client.js`: sim-clock, progress+ETA+speed, live equity sparkline, KPI tiles, funnel counters, streaming journal feed, breach banner. Note: equity/governor/DD fields in the envelope are honest zero/null until the engine sources them (see B + `BacktestOrchestrator.BuildProgress`) — wire those once B lands.
3. **U3 — Trade detail** real candle chart + markers + correct fields (`/api/trades/{id}/chart`).
4. **U4 — Backtest Report** KPIs, duration/speed, equity+drawdown, **strategy funnel + compliance breach verdict** (now meaningful with accurate PnL/reasons from p10/p11/p12 + the rich journal from B), trade links, CSV export.
5. **U5** analyzer/strategies/compliance pages; **U6** new-backtest wizard + run history/compare; **U7** states/responsive/a11y/buffers.

**Coordination:** every page reads an endpoint/hub message shaped like the eventual kernel `RunProjection`/`DecisionRecord`/`AccountSnapshot` (already partly present). The richer journal (B) is what makes the Report and Monitor journal feed truthful — sequence B before U4.

## Known minor findings (not fixed)
- `ctrader-cli` returns exit code 1 even on successful runs that produce correct trades; `CBOT|` stdout lines aren't captured. Data/exec path is correct; harden the harness's exit-code interpretation.
- End-of-run open positions are flattened by a synthetic zero-price close (M2) and booked as one `$0.00 FORCE` trade — the Report should label/exclude these.
- `.gitmsg.tmp` was committed into two intermediate commits (`b7c95d0`, `5cf5d4e`) before the mktemp fix; harmless, not at HEAD.
