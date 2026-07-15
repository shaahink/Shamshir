# Iter-31 Plan — Honest Costs + an Inspectable Journal

Context: deep read-throughs (requested by the owner) found that backtests **overstate net results**
(no trading costs applied), that **every entry is a market order** (no limit/entry-price control even
though the config for it exists and is dead), and that a finished/previous run has **no way to see its
full journal** even though the data is in the DB. All three are "trust and inspect the backtest"
problems, so they ship in one iteration as **three streams** (A and C are engine/venue, B is
Web/persistence — they are independent and can be worked in parallel or any order):

- **Stream A — Money model.** Commission and swap/financing are **not** applied in backtest
  (`net = gross`), and live captures only a lump-sum net (not itemized). The data model already has
  the fields (`TradeResult.Commission/Swap/GrossPnL/NetPnL`, `ExecutionEvent.*`, `PublishTradeClosed.*`)
  and the live patch path is wired — what's missing is **cost data** and the **simulated-venue
  computation**. This distorts PnL, win-rate, **and the equity/drawdown the FTMO breach watchdog runs
  on** (you can "pass" in backtest and breach live).
- **Stream B — Observability (journal & stats).** The comprehensive per-run journal is persisted to
  `PipelineEvents` and projected by `RunProjection.GetRunAsync`, but **no page renders it**; the live
  Monitor journal is a lossy 30-item in-memory rolling buffer; the two systems use **different event
  names**; the equity sparkline freezes after 500 frames; and stats are split across pages with
  inconsistent sources (Trades vs PipelineEvents) and a trade-rebuilt equity curve.
- **Stream C — Order entry (limit orders + entry pricing).** Every strategy hardcodes `OrderType.Market`
  with a null limit price, so all entries fill at market (bar close + slippage). The config to do better
  already exists and is **dead**: `OrderEntryOptions { Method=Market|LimitOffset|MarketWithSlippage,
  LimitOffsetPips, LimitOrderExpiryBars, MaxSlippagePips }` is parsed from each strategy's `orderEntry`
  JSON, the live cTrader adapter even branches on `Method==LimitOffset`, and `TradeIntent`/`SubmitOrder`/
  `OrderDispatcher` all carry `LimitPrice` — but nothing populates it, and the simulated venue fills every
  pending order at market on the next tick (no resting-limit semantics, no expiry). The goal: honor the
  entry config — place limit orders at a configurable, computed price, fill them only when price reaches
  the limit, and expire unfilled ones.

Predecessors (executed directly, no docs folder — see memory + OPEN-ISSUES): iter-28 trade metrics,
iter-29 indicator/regime correctness, iter-30 breakeven/trailing wiring. This iteration depends on
none of them but assumes them landed.

Working style (unchanged): each phase ships independently, leaves the solution **building + fast
suites green**, small commits, the machine-checkable **Gate** met before moving on. Branch off the
current branch. Prefer **failing-test-first**. Fast feedback = `dotnet test tests/TradingEngine.Tests.Unit`
+ `tests/TradingEngine.Tests.Simulation` (avoid the ~60s IHost `ReplayTestHarness`; trust
Unit/Arch/Golden — see project-test-harness-gotchas). The two streams are independent — A is
engine/venue, B is Web/persistence — so they can be worked in either order or in parallel.

**Acceptance for the whole iteration:**
1. A backtest over a known fixture produces a trade whose `NetPnL = Gross − Commission − Swap`, with
   commission and swap **non-zero and itemized**, and the **equity/drawdown reflect the net** (not
   gross) — all asserted by simulation tests.
2. A finished run's full journal is viewable in the UI (every persisted event, filterable), and the
   stats shown reconcile against the trades (NetPnL(stats) == Σ trade net == equity end).
3. A strategy configured with `orderEntry.method = "LimitOffset"` and a non-zero offset enters via a
   resting limit order that fills only when price reaches the limit (asserted by a sim test where the bar
   that doesn't reach the limit leaves the order pending, and a later bar fills it at the limit price),
   and an order that never reaches its limit within `LimitOrderExpiryBars` is cancelled (no phantom fill).

---

## Root-cause map (finding → diagnosis → location) — read-verify before trusting line numbers

### Stream A — Money model

| # | Symptom | Root cause | Location |
|---|---------|-----------|----------|
| C1 | Backtest `net == gross`; no commission, no swap | `SimulatedBrokerAdapter.ClosePositionAsync` emits `new ExecutionEvent(..., null, ...)` with no Gross/Net/Commission/Swap; so `evt.NetProfit is null` and `PositionTracker` skips the cost patch; `EffectExecutor.HandlePublishTradeClosed` then does `net = effect.NetProfit ?? gross`, `commission/swap = effect.X ?? 0`. | `SimulatedBrokerAdapter.cs:130-157`; `PositionTracker.cs:271-282`; `EffectExecutor.cs:106-118` |
| C2 | No cost data to apply even if we wanted to | `SymbolInfo` has only `MarginRate`, `TypicalSpread` — **no commission, no swap fields**; `config/symbols.json` likewise. | `SymbolInfo.cs`; `config/symbols.json` |
| C3 | Backtest equity/drawdown are gross → FTMO breach checks optimistic | `SimulatedBrokerAdapter` does `_currentBalance += pnlUsd` where `pnlUsd` is gross; that drives the `AccountUpdate` → `AccountProcessor` → drawdown/breach. | `SimulatedBrokerAdapter.cs:143-154` |
| C4 | Live net is cost-inclusive but **not itemized** (Commission/Swap columns always 0) | The cBot sends only `grossProfit` + `netProfit` (`pos.NetProfit` is already cost-inclusive), not `commission`/`swap`; the adapter parses commission/swap as null. | `TradingEngineCBot.cs:561-562`; `CTraderBrokerAdapter.cs:371-374` |
| C5 | `equityDefinition: "BalancePlusFloatingMinusFeesAndSwaps"` is decorative | No code reads `PropFirmRuleSet.EquityDefinition`; equity = balance + floating, and `PipCalculator.FloatingPnL` is gross (no swap accrual). | `PropFirmRuleSet.cs:11`; `PipCalculator.cs:51-67` |
| C6 | (minor) risk budget is commission-blind | `RiskManager.CalculateLotSize` sizes off SL distance only; true loss at SL exceeds the budget by round-turn cost. | `RiskManager.cs:225-250` |

### Stream B — Observability (journal & stats)

| # | Symptom | Root cause | Location |
|---|---------|-----------|----------|
| J1 | No comprehensive journal for a finished/previous run | `Report` fetches the full timeline (`RunProjection.GetRunAsync`) but uses it **only** for the funnel + breach; `ReportModel` exposes no timeline. `Analyzer` is Trades-charts. `/events` shows the **global last-100** `EngineEventEntity` (different table, not per-run, not `PipelineEvents`). | `Report.cshtml.cs:45,73-87`; `Analyzer.cshtml`; `Events.cshtml.cs:9` |
| J2 | Live journal is lossy / "comes and goes" | `BacktestOrchestrator.RecentJournal` is an in-memory queue capped at **30** (`Dequeue` when >30), carried on **throttled** SignalR frames, never persisted. | `BacktestOrchestrator.cs:152-172` |
| J3 | Filter tabs won't match persisted events | Two vocabularies: live = `SIGNAL/ORDER/EXEC/CLOSE/REJECTED/BREACH` (`BacktestProgressEvent`); persisted = `OrderSubmitted/OrderFilled/OrderRejected/BreachDetected` (`DecisionRecord`). | `BacktestOrchestrator.cs:142-152`; `PipelineEventWriter.cs:31-52`; `Report.cshtml.cs:147-167` |
| J4 | Equity sparkline freezes mid-run | `if (equityPoints.length <= 500)` stops updating the chart after 500 frames. | `Monitor.cshtml:170` |
| J5 | Equity curve is a trade-rebuild, not the real curve | Intra-bar `AccountSnapshot`s live only in the inner host's in-memory store (disposed at run end); the Report rebuilds equity by walking trade closes. `RunProjection` asks `IAccountSnapshotStore` but the Report ignores `view.EquityCurve`. | `Report.cshtml.cs:106-136`; `RunProjection.cs:48-55` |
| J6 | Stats split + inconsistently sourced | Top-line (NetPnL/Return/MaxDd/WinRate/PF) from **Trades**, funnel from **PipelineEvents**, distributions from **Trades** — no single panel, no declared source of truth. | `Report.cshtml.cs:89-137`; `BacktestAnalyticsController.cs:92-107` |

### Stream C — Order entry

| # | Symptom | Root cause | Location |
|---|---------|-----------|----------|
| E1 | Every entry is a market order regardless of config | All 9 strategies construct `new TradeIntent(..., OrderType.Market, /*limitPrice*/ null, ...)`; nothing reads `strategy.Config.OrderEntry.Method`. | `*Strategy.cs` (e.g. `TrendBreakoutStrategy.cs:106-116`, `MeanReversionStrategy.cs:78`) |
| E2 | `LimitOffset`/`LimitOffsetPips`/`LimitOrderExpiryBars` are dead config | `OrderEntryOptions` is parsed from `orderEntry` JSON and carried on the config, but no code converts it into an order type + limit price; `OrderDispatcher` just does `entryPrice = intent.LimitPrice ?? currentMid`. | `OrderEntryOptions.cs`; `OrderDispatcher.cs:23,59` |
| E3 | Sim venue can't rest a limit order | `SimulatedBrokerAdapter.OnTickReceived` fills **every** pending order at `tick.Ask/Bid ± slippage` on the next tick — no check that price reached the limit, no expiry. | `SimulatedBrokerAdapter.cs:188-214` |
| E4 | Live limit path is half-wired | cTrader adapter branches on `Method==LimitOffset` and sends `LimitPrice`, but the intent's `LimitPrice` is always null (strategies never set it), so the branch can't actually place a limit. | `CTraderBrokerAdapter.cs:404-413` |

---

## Decisions (authoritative — do not re-litigate)

- **D1 — Cost data shape.** Add optional fields to `SymbolInfo` + every entry in `config/symbols.json`
  (default 0 ⇒ back-compat, costs off until set): `commissionPerLotPerSide` (account ccy),
  `swapLongPerLotPerNight`, `swapShortPerLotPerNight` (account ccy), `tripleSwapWeekday`
  (default `Wednesday`). Parse in `SymbolCatalog`.
- **D2 — The simulated venue is the ONE place that computes costs** and stamps them on the close
  `ExecutionEvent` (`GrossProfit`, `Commission`, `Swap`, `NetProfit`). The existing
  `PositionTracker`→`EffectExecutor` patch path (`PositionTracker.cs:273-281`) then carries them with
  **no other engine changes**, and backtest equity/drawdown become net automatically because
  `_currentBalance` is incremented by **net**, not gross.
- **D3 — Cost formulas.** `commission = lots × commissionPerLotPerSide × 2` (round turn).
  `swap = nightsHeld × swapRate(direction) × lots`, where `nightsHeld` = number of daily-rollover
  boundaries (the rule set's `dailyResetTimeUtc`) crossed between `OpenedAtUtc` and `ClosedAtUtc`, with
  the `tripleSwapWeekday` rollover counting ×3. Keep it deterministic and unit-testable.
- **D4 — Live itemization, net stays venue-authoritative.** The cBot also sends `commission = pos.Commissions`
  and `swap = pos.Swap`; the adapter already parses them. Do **not** recompute net on the engine side
  for live — `pos.NetProfit` remains the source of truth.
- **D5 — Journal source of truth = `PipelineEvents`.** Introduce one normalized event taxonomy
  `{ SIGNAL, ORDER, FILL, CLOSE, REJECTED, BREACH, GOVERNOR }` and a mapper from BOTH vocabularies onto
  it. The post-run journal reads `PipelineEvents`; the live journal must be **backed by the same store**
  (poll by `afterSeq`) so it is never lossy. The 30-item in-memory queue is a fallback only.
- **D6 — Stats source of truth.** Realized-PnL stats (NetPnL/WinRate/PF/Return/MaxDd) from **Trades**;
  funnel from **PipelineEvents**; equity curve from **persisted `AccountSnapshot`s** once J5 lands, else
  the trade-walk. Document the choice on the page/model so it can't drift again.
- **D8 — One place computes the entry plan, not 9 strategies.** Add an `EntryPlanner` step between
  `strategy.Evaluate` and dispatch (in `TradingLoop`) that reads `strategy.Config.OrderEntry` and rewrites
  the intent's `OrderType` + `LimitPrice` (and re-derives SL/TP off the actual planned entry). Strategies
  stop deciding order type. This keeps the policy in one tested place and lets the JSON drive it.
- **D9 — Limit price = signal reference offset toward a better fill.** For `LimitOffset`, place the limit
  `LimitOffsetPips` **more favorable** than the signal price (below for a long, above for a short) — i.e.
  wait for a small pullback. `LimitOffsetPips = 0` ⇒ limit at the signal price (≈ market, but no positive
  slippage). Re-derive SL/TP off the planned limit so R stays consistent.
- **D10 — Resting + expiry semantics.** A buy-limit fills when a bar's **low ≤ limit**, a sell-limit when
  **high ≥ limit**, filled **at the limit price** (no slippage on limits). Count down `LimitOrderExpiryBars`
  from submission; on expiry, cancel and emit an `ENTRY_EXPIRED`/`OrderCancelled` journal event (no fill).
  `Market`/`MarketWithSlippage` keep today's immediate fill (+ `MaxSlippagePips`).
- **D11 — Default stays Market.** Do not silently switch strategies to limit entries. Ship the capability,
  keep every strategy's JSON `method` = `Market` by default, and set `LimitOffset` on **one** strategy
  (recommend `mean-reversion`, which benefits from a better fill) as a worked example. Owner decides the
  rollout (see "Decisions to confirm").

### Decisions to confirm with the owner (resolve before coding the affected phase — defaults proposed)
> These are product choices, not mechanics. The agent should proceed with the **recommended default** if
> no answer is given, and call it out in the HANDOVER so it can be revisited.
- **Q1 (Stream C, affects C1/C4):** Which strategies should default to limit entry vs market? *Recommended
  default:* all `Market`, with `mean-reversion` on `LimitOffset` as the example. Breakout/trend strategies
  are risky on limits (a "better" price = waiting for a retest that may never come → missed trends), so
  leave them `Market` unless the owner wants retest entries.
- **Q2 (Stream C):** On limit non-fill at expiry — cancel silently, or fall back to a market order?
  *Recommended default:* cancel + journal `ENTRY_EXPIRED` (no fallback); a fallback market order
  re-introduces the slippage the owner wanted to avoid.
- **Q3 (Stream A, affects A3/C5):** Wire `equityDefinition` to actually deduct fees/swap from the equity
  definition, or delete the decorative string (net already includes costs)? *Recommended default:* delete
  the string + document, since A1 already makes equity net-of-costs.
- **Q4 (Stream A):** Best-guess cost values for the bundled symbols — confirm or supply real broker
  numbers. *Recommended default:* FX majors `commissionPerLotPerSide ≈ 3.5` + small ± swap; mark as
  estimates for RW-01 to refine.

- **D7 — Guardrails.** Don't touch `aspire/AppHost` (`NU1903`); build affected projects directly. The
  pre-existing `BacktestReplayTests` NRE at `AccountProcessor.cs:125` is **out of scope** (fails on
  baseline). Keep Unit + Simulation green throughout.

---

## Phases

> Each phase: failing test first → fix → Gate. Commits named `feat(iter31-pN): …`.

### Stream A — Money model

#### A0 — Cost data on `SymbolInfo` + `symbols.json` (C2)  ⟵ foundational for A
- Add the D1 fields (optional, default 0/`Wednesday`) to `SymbolInfo` and parse them in `SymbolCatalog`
  / `symbols.json`. Seed realistic best-guess values for the bundled symbols (e.g. FX majors
  `commissionPerLotPerSide ≈ 3.5`, small ± swap; XAU/indices proportionate). Leave a comment that these
  are estimates to be tuned (RW-01 settings page).
- **Gate:** `SymbolCatalog` loads the new fields; a symbol with no cost fields parses with 0s; all
  existing symbol/registry tests stay green.

#### A1 — Simulated venue computes & stamps costs (C1, C3, D2, D3)  ⟵ foundational for A
- In `SimulatedBrokerAdapter` close paths (`ClosePositionAsync` **and** `ClosePositionAtAsync`), compute
  `commission` and `swap` per D3, set `GrossProfit/Commission/Swap/NetProfit` on the emitted
  `ExecutionEvent`, and increment `_currentBalance` by **net** (not gross). Needs the position's
  `OpenedAtUtc` and the rule set's daily-reset time available at the venue (thread it in, or pass nights
  on the close call).
- **Gate (failing-test-first):** a simulation test opens a trade, holds it across `N` rollovers, closes
  it; the resulting `TradeResult` has `Commission == lots×perSide×2`, `Swap == N×rate×lots` (×3 if a
  triple-swap day was crossed), `NetPnL == Gross − Commission − Swap`, and the `AccountStream` equity /
  `RiskManager.Drawdown` reflect the **net** balance. With costs 0 the numbers match today's behavior
  (regression guard).

#### A2 — Live itemization (C4, D4)
- Have the cBot also emit `commission = pos.Commissions`, `swap = pos.Swap` in the close EXEC frame;
  confirm `CTraderBrokerAdapter` maps them onto `ExecutionEvent.Commission/Swap`. Net stays `pos.NetProfit`.
- **Gate:** a unit test on the adapter's EXEC parse asserts commission/swap are populated when present
  and net is unchanged; an Arch/contract test pins the EXEC JSON field names the cBot and adapter share.

#### A3 — Costs reach the UI + the breach math (C3, C5)
- Verify the Report/Trades pages surface `Commission`/`Swap`/`Gross`/`Net` columns (the entities already
  have them); show per-trade and run totals. Decide `equityDefinition`: either honor it (deduct
  fees/swap in the equity definition path) or delete the dead string and document that net already
  includes costs.
- **Gate:** a sim/integration test asserts the run-level `NetProfit` equals `Σ trade net`
  (cost-inclusive), and the Report shows non-zero commission/swap totals for a cost-configured fixture.

#### A4 — (optional) commission-aware risk budget (C6)
- Subtract expected round-turn commission from the per-trade risk budget so sizing is honest near FTMO
  limits. Gate behind a config flag; default off.
- **Gate:** unit test — with commission set, the sized lots produce a worst-case loss (SL + round-turn)
  within the risk budget; with commission 0, sizing is unchanged.

### Stream B — Observability (journal & stats)

#### B0 — Normalized journal projection + API (J1, J3, D5)  ⟵ foundational for B
- Add a normalized taxonomy `{SIGNAL, ORDER, FILL, CLOSE, REJECTED, BREACH, GOVERNOR}` + a mapper from
  both vocabularies. Expose `GET /api/backtest/{runId}/journal?filter=&afterSeq=&limit=` backed by
  `RunProjection`/`IPipelineEventRepository`, returning paged normalized records (seq, simTime, symbol,
  strategy, kind, reason, detail).
- **Gate (failing-test-first):** a test seeds `PipelineEvents` for a run and asserts the API returns them
  in seq order, that `filter=CLOSE` maps `OrderFilled(reason∈{SL,TP,FORCE,…})`, and that `afterSeq`
  pages without gaps or dupes.

#### B1 — Persisted journal viewer (J1)
- Render the journal on the Report (new "Journal" run-nav tab, or a section) using the B0 API with the
  filter tabs; page/virtualize for large runs. Works for **any** run, live or historical.
- **Gate:** a finished run displays its full persisted journal; clicking each filter shows only matching
  rows; a run with > 1000 events still loads (paged). A Razor/page-model test or a thin integration test
  covers the model wiring.

#### B2 — Lossless live journal + chart-freeze fix (J2, J4)
- Back the Monitor journal with the B0 API (poll by `afterSeq`) instead of the 30-item in-memory queue,
  so nothing is dropped; keep the in-memory frame as a low-latency hint only. Remove the
  `equityPoints.length <= 500` freeze (stream/trim a rolling window, or switch to whole-curve setData).
- **Gate:** a run that emits > 30 journal events shows **all** of them in the Monitor (no gaps), and the
  equity sparkline keeps updating past 500 frames. (Contract test on the frame + a JS/manual note for the
  chart.)

#### B3 — Persist the equity curve (J5, D6)
- Persist per-run `AccountSnapshot`s (the real intra-bar curve) to a store readable post-run; have the
  Report/Monitor read the persisted curve and fall back to the trade-walk only when absent.
- **Gate:** for a finished run the equity curve comes from persisted snapshots, is monotone in time, and
  ends at `initialBalance + Σ trade net`; a test asserts curve-end == reported equity.

#### B4 — Unified, reconciled stats (J6, D6)
- One stats surface with a declared source per metric (D6); reconcile the funnel (PipelineEvents) and
  win-rate (Trades) so they can't disagree silently.
- **Gate:** a test asserts `NetPnL(stats) == Σ trade net == equityCurve.end`, and the funnel's
  fills/closes are consistent with the trade count for a known fixture.

### Stream C — Order entry (limit orders)

#### C0 — `EntryPlanner`: config → order type + limit price (E1, E2, D8, D9)  ⟵ foundational for C
- Add an `EntryPlanner` (Services) that, given the signal (direction, reference price, ATR, symbol) and
  `OrderEntryOptions`, returns `(OrderType, LimitPrice?)` and the re-derived SL/TP. Call it in
  `TradingLoop` right after `strategy.Evaluate`; the strategies keep emitting a Market intent and the
  planner rewrites it per config (so no per-strategy edits beyond, optionally, passing a reference price).
- **Gate (failing-test-first):** unit tests — `Market` → `(Market, null)`; `LimitOffset` with offset N
  on a long → `(Limit, signal − N·pip)` and SL/TP re-derived off that limit so R is unchanged; offset 0
  → limit at signal price.

#### C1 — Simulated venue rests + expires limit orders (E3, D10)  ⟵ foundational for C
- Teach `SimulatedBrokerAdapter` to hold a limit `PendingOrder` until a bar/tick reaches it (long fills on
  `low ≤ limit`, short on `high ≥ limit`), fill **at the limit price**, and cancel after
  `LimitOrderExpiryBars`, emitting a cancellation `ExecutionEvent`/journal line. Market orders unchanged.
  Drive the check each bar from the backtest loop (the pending-order scan already exists; gate it on the
  limit condition + an expiry counter).
- **Gate:** a sim test where bar N stays above a buy-limit leaves the order pending; bar N+1 dips to the
  limit and fills **at the limit** (not bar close); a third scenario never reaches the limit within the
  expiry window and ends with **no position** + a cancellation event.

#### C2 — Live limit path end-to-end (E4)
- Ensure the populated `LimitPrice` reaches the cTrader adapter's `Method==LimitOffset` branch and that
  expiry/cancel is honored live (or documented if the venue handles expiry itself).
- **Gate:** an adapter/contract test asserts a `LimitOffset` intent serializes a non-zero `limitPrice` in
  the order frame and a `Market` intent serializes none; pin the field names with the cBot side.

#### C3 — Config + worked example + journal (D11, Q1)
- Confirm the `orderEntry` JSON round-trips all `OrderEntryOptions` fields; set `mean-reversion` to
  `LimitOffset` with a sensible offset as the demonstration (per Q1 default); ensure pending/expired
  entries show in the journal (ties into Stream B's taxonomy — add `ENTRY`/`CANCELLED` kinds).
- **Gate:** a backtest of the limit-configured strategy produces at least one limit fill and one expiry in
  its journal; all other strategies behave exactly as before (Market). Suites green.

---

## Out of scope / guardrails
- **Part 10 RW backlog stays deferred** (RW-01 settings page, RW-02 layered config, RW-03 batch runner,
  RW-04 auto-mode, RW-05 global symbol selection) — this iteration is costs + journal/stats only, though
  it is a prerequisite for RW-03's "compare runs" to be meaningful. Note the RW-01 link from A0's cost
  estimates.
- No auto-strategy mode, no multi-TF/multi-symbol data import, no `mtf-trend` H4 work.
- Don't "fix" the pre-existing `AccountProcessor.cs:125` NRE (out of scope; baseline failure).
- Don't touch `aspire/AppHost`.
- Keep the simulation FTMO suite green throughout; treat red there as stop-the-line.

## Definition of Done
All non-optional gates met; the three acceptance scenarios pass as automated tests; `docs/OPEN-ISSUES.md`
updated — log C1–C6 (costs), J1–J6 (observability), E1–E4 (order entry) (mark fixed ones
`✅ Fixed (Iteration 31)`, leave deferred with a note); Unit + Simulation + Architecture suites green; no
new warnings; a short `HANDOVER.md` in this folder records what landed, what deferred, the **answers to
Q1–Q4** (or the defaults taken), and the cost estimates used (so RW-01 can refine them).

> Note (cross-iteration): Stream C adds `orderEntry`/`limit` knobs to the strategy JSON; **iter-32**
> migrates strategy config to a DB-backed, per-run-editable store. Keep the `OrderEntryOptions` shape
> stable so iter-32's JSON→DB seed picks it up unchanged.
