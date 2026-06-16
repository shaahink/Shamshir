# Iter-26 Plan — Make Backtest Results & the Breach Safety-Net Honest

Context: a deep read of the **backbones** (engine reducer / position lifecycle, RiskManager +
sizing, the live TradingLoop, AccountProcessor breach watchdog, PositionTracker / EffectExecutor,
and both venues — `SimulatedBrokerAdapter` and `BacktestReplayAdapter`) found that the *pure
kernel is sound, but the wiring around it corrupts backtest numbers and disarms the breach
flatten path.* Three themes:

1. **The replay venue's equity/PnL feed is decoupled from execution** — drawdown is fabricated
   (≈flat at initial balance), so the breach watchdog never fires in backtest and the equity
   curve is meaningless. (This is why iter-25 had to compute report KPIs from trades, not equity.)
2. **Two id namespaces** (`PositionState.PositionId` vs `OrderId`) are used inconsistently across
   the effect path and the venue path → force-close-all is a silent no-op against sim/replay.
3. **Configured features are silently dead** — `LotSizingMethod` (FixedLots/Kelly/FixedDollar) is
   bypassed; `PositionManager` trailing/breakeven is never invoked and leaks its dictionaries.

This is an **engine/risk/venue correctness** iteration (iter-24's lane), **not** UI. No Web changes.

Working style (unchanged): each phase ships independently, leaves the solution **building + fast
suites green**, small commits, the machine-checkable **Gate** met before moving on. Branch off the
current branch. Prefer **failing-test-first**: write the test that encodes the correct behavior,
watch it fail, then fix. Fast feedback = `dotnet test tests/TradingEngine.Tests.Unit` +
`tests/TradingEngine.Tests.Simulation` (avoid the ~60s IHost `ReplayTestHarness` for tight loops;
trust Unit/Arch/Golden — see project-test-harness-gotchas).

**Acceptance for the whole iteration:** a replay backtest over a known fixture produces (a) an
equity curve that actually moves with realized PnL, (b) drawdown that matches the trades, (c) a
breach scenario that *enters protection mode AND flattens* positions, and (d) SL/TP exits priced
at the stop/target — all asserted by simulation tests, not by eyeballing the UI.

---

## Root-cause map (finding → diagnosis → location)

| # | Symptom | Root cause (read-verified) | Location |
|---|---------|----------------------------|----------|
| F1 | Backtest drawdown ≈ 0, equity curve flat, breach watchdog never trips | `BacktestReplayAdapter` feeds bars over **unbounded** channels with **no lock-step** (`CompleteBarAsync` default no-op), so `FeedBarsAsync` races to completion. `ComputeFloatingPnL` runs on the feed thread before trades exist → every `AccountUpdate` is `equity=balance=initial`. `ClosePositionAsync` mutates `_balance` but **never emits a new `AccountUpdate`**, so realized PnL never reaches the account stream. Also `_openTrades` (plain Dictionary) is read on the feed thread while mutated on the engine thread → "collection modified" race. | `BacktestReplayAdapter.cs:78-121, 163-192, 229-250`; lock-step absent (`IBrokerAdapter.CompleteBarAsync` default) |
| F2 | Backtest PnL systematically wrong (stops don't cost what they should) | `SimulateBarExitsAsync` detects SL/TP off `bar.Low/High`, but the close fills at `_lastClose`, not the stop/target. `BacktestReplayAdapter.ClosePositionAsync` uses `_lastClose` (`:165`); the ledger inherits it via `effect.ExitPrice`. `SimulatedBrokerAdapter` does this right (`:222`). | `EngineRunner.cs:237-267`; `BacktestReplayAdapter.cs:163-192` |
| F3 | Breach "flatten all" silently does nothing in sim/replay | Effect-path closes carry `PositionState.PositionId` (`HandleForceCloseAll`/`HandleCloseRequested` → `CloseOpenPosition(PositionId)`), but venues key open trades by the **OrderId** returned from `SubmitOrderAsync`. The SL/TP path happens to pass `OrderId` (`EngineRunner.cs:262`), so the two close paths disagree. | `EngineReducer.cs:273-281`; `PositionLifecycle.cs:126-136`; `EffectExecutor.cs:80-84`; venues key by orderId |
| F4 | Open risk overstated after a partial close → over-blocks new entries | `HandlePartialCloseAsync` computes `proportionalRisk = riskAmount * remainingLots / position.Lots`, but by then `position.Lots == remainingLots` (reducer already shrank it) → ratio is always 1 → risk never decreases. | `PositionTracker.cs:347-361` |
| F5 | `LotSizingMethod` (FixedLots/Kelly/FixedDollar) has no effect | `CalculateLotSize` calls the **simple** `PositionSizer.Calculate` overload (always percent-risk); the rich overload that switches on `profile.LotSizingMethod` is never called. | `RiskManager.cs:225-247`; `PositionSizer.cs:23-41` |
| F6 | `PositionManager` dictionaries grow unbounded; trailing/breakeven never runs | `OnOpened` registers `PositionManager` under a **fresh** `Guid.NewGuid()`, but deregister uses `PositionState.PositionId` → ids never match → entries leak. Separately, `PositionManager.Evaluate`/`PositionTracker.EvaluatePosition` are **never called** in the live or backtest loop. | `PositionTracker.cs:311-335, 44-45`; `EffectExecutor.cs:98-101`; `PositionManager.cs:30-41` |
| F7 | First update of a new day can spuriously enter protection mode | In `AccountProcessor.HandleAsync` the breach watchdog (`:54-89`) runs **before** the daily/weekly/monthly roll (`:96-124`), so it measures `DailyDrawdownUsed` against *yesterday's* `DailyStartEquity`. | `AccountProcessor.cs:54-124` |
| F8 | Duplicate venue fill can close a live position | The dedup guard only skips when the order id was seen **and** no position exists for it; a duplicate **fill** on an Open position passes through to `(Open, OrderFilled)` → `HandleOpenFilled` → unintended close/reduce. | `PositionTracker.cs:210-217` |
| F9 | Wrong pip value for non-USD-quote cross pairs | `CrossRateStore.Convert` only maps `JPY→USD`/`GBP→USD`, returns `1m` otherwise; `EngineRunner.UpdateCrossRates` only tracks GBPUSD/USDJPY. | `CrossRateStore.cs:8-13`; `EngineRunner.cs:281-285` |
| F10 | Misc: sim close emits no exec event; dead monthly baseline; dead stub | `SimulatedBrokerAdapter.ClosePositionAsync` writes no `ExecutionEvent` (asymmetric with partial); `HandleMonthRolled` passes `DailyStartEquity` as the monthly baseline; `PositionTracker.TryBuildPosition` always returns false. | `SimulatedBrokerAdapter.cs:130-137`; `EngineReducer.cs:262-266`; `PositionTracker.cs:397-401` |

---

## Decisions (authoritative — do not re-litigate)

- **D1 — One id at the venue boundary: `OrderId`.** The venues (sim/replay/cTrader) own positions
  by the order/client id. The engine's internal `PositionId` must **not** cross the
  `IBrokerAdapter` boundary. Carry `OrderId` on `CloseOpenPosition`/`ModifyStopLoss`/
  `ModifyTakeProfit` effects (or resolve PositionId→OrderId in `EffectExecutor` before calling the
  broker). Make the SL/TP path and the effect path use the **same** id. (This is a re-emergence of
  the iter-17 "Close position ID mismatch" class — keep it from coming back with an Arch test.)
- **D2 — The replay venue is the source of truth for equity, and it must publish realized PnL.**
  Every balance change in `BacktestReplayAdapter` (open float + each close) emits an `AccountUpdate`
  on the account stream so `AccountProcessor` sees real equity/drawdown. The feed must be
  **lock-stepped** (or at minimum serialized with execution) so floating PnL is computed against the
  actual open book, never a racing one.
- **D3 — SL/TP exits fill at the stop/target price, not the bar close.** The engine already knows
  the exit reason (it stamps SL/TP via `SetCloseReason`); pass the exit *price* too, or have the
  venue price the close at the position's SL/TP when the engine signals an SL/TP close.
- **D4 — Trailing/breakeven (F6) is "verify intent before wiring".** Do **not** blindly wire
  `PositionManager.Evaluate` into the loop — confirm with the owner whether trailing is meant to be
  live this iteration. The **id-leak half of F6 is an unconditional bug** and gets fixed regardless.
- **D5 — Don't touch `aspire/AppHost`** (unrelated `NU1903`); build the affected projects directly.

---

## Phases

> Each phase: failing test first → fix → Gate. Keep commits small and named `feat(iter26-pN): …`.

### P0 — Replay venue tells the truth about equity (F1)  ⟵ foundational
**Why first:** every drawdown/breach/report number downstream is wrong until this is fixed.
- Emit an `AccountUpdate` from `BacktestReplayAdapter.ClosePositionAsync` /
  `ClosePartialPositionAsync` after `_balance` changes (realized PnL reaches the stream).
- Make floating-PnL/equity updates come from the **same serialized step** as bar processing so they
  reflect the real open book (either lock-step `CompleteBarAsync`, or compute float in
  `OnBarObserved`/`SyncToBar` on the engine thread instead of the feed thread).
- Guard `_openTrades` access (lock or move all access to one thread) — kill the cross-thread
  Dictionary race.
- **Gate:** a simulation test that opens a known trade and closes it in profit/loss asserts the
  `AccountStream` shows `equity` moving by the realized PnL, and `RiskManager.Drawdown.CurrentMaxDrawdown`
  > 0 after a losing trade. (Today it stays 0.)

### P1 — SL/TP exits fill at the stop/target (F2, D3)
- Thread the exit *price* through: when `SimulateBarExitsAsync` detects SL/TP, the resulting close
  fill must be priced at `pos.CurrentStopLoss`/`pos.TakeProfit`, not `_lastClose`. Either pass the
  price on the close call, or have the adapter look up the position's SL/TP for an SL/TP-reason close.
- **Gate:** a sim test where price gaps through the stop within a bar asserts the trade's exit price
  equals the SL (within tick), and ledger gross PnL equals `(entry−SL)*lots*contract` — not the
  close-based value.

### P2 — Unify the close/modify id at the venue boundary (F3, D1)
- Carry `OrderId` (not `PositionId`) on `CloseOpenPosition`/`ModifyStopLoss`/`ModifyTakeProfit`, or
  resolve in `EffectExecutor`. Make `RequestForceCloseAllAsync` close through the **same** id the
  SL/TP path uses.
- Add an **Architecture test** asserting no `EngineEffect` carrying a venue-bound id is constructed
  from `PositionState.PositionId` (or a focused unit test that force-close actually removes the
  position from a fake venue).
- **Gate:** a breach scenario (drive equity below the daily flatten fraction) asserts every open
  position is closed at the venue (`OpenPositions` empties) and a `TradeClosed`/`PublishTradeClosed`
  is emitted per position. (Today the venue keeps them.)

### P3 — Partial-close risk proportionality + duplicate-fill guard (F4, F8)
- Capture the **original** lots before the reducer shrinks them (or derive remaining/closed from the
  pre-reduction value) so `proportionalRisk` actually decreases.
- Tighten the dedup: a fill that does not advance the lifecycle phase (same-phase duplicate) must be
  ignored rather than re-applied as a close/reduce. Key dedup on `(OrderId, state, filledLots)` or
  track an idempotency token per execution.
- **Gate:** unit test — after a 50% partial close, `RiskManager` open risk for that position is ~half;
  and a duplicated Open-phase fill event leaves the position Open (no spurious close).

### P4 — Honor `LotSizingMethod` (F5)
- Route `CalculateLotSize` through the rich `PositionSizer.Calculate(profile, …)` overload so
  FixedLots/FixedDollarRisk/KellyFraction take effect; keep PercentRisk as default.
- **Gate:** unit test per method — a profile set to `FixedLots=0.5` returns 0.5 (clamped to broker
  min/max/step); Kelly scales by `KellyFraction`. (Today all collapse to percent-risk.)

### P5 — PositionManager id leak (+ trailing decision) (F6, D4)
- Register `PositionManager` under the **same** id used to deregister (`PositionState.PositionId`),
  so closed positions are removed. Verify `_tracked`/`_highWaterBid`/`_lowWaterAsk`/
  `_initialSlDistance` all drain on close.
- Surface the trailing question to the owner (is `PositionManager.Evaluate` meant to run live?). If
  yes → separate, scoped wiring task; if no → leave unwired but documented.
- **Gate:** unit/sim test — after N open→close cycles, `PositionManager`'s internal dictionaries are
  empty (no leak). (Add a test-only count accessor if needed.)

### P6 — Breach watchdog ordering (F7)
- Roll daily/weekly/monthly baselines **before** evaluating the breach for that update (or evaluate
  breach against the post-roll baseline on a day boundary).
- **Gate:** test — a day that opens fresh after the prior day closed near the daily limit does **not**
  enter protection mode on the first update of the new day.

### P7 — Coverage & cleanup (F9, F10)
- `CrossRateStore.Convert`: add the missing legs (CHF/CAD/AUD/NZD and their inverses) or make an
  unmapped pair **throw/log loudly** instead of silently returning `1m`; extend
  `EngineRunner.UpdateCrossRates` accordingly.
- `SimulatedBrokerAdapter.ClosePositionAsync`: emit an `ExecutionEvent` symmetric with the partial
  close so effect-driven closes propagate.
- Fix `HandleMonthRolled` baseline (use month-start/current equity, not `DailyStartEquity`) even
  though the path is UNWIRED, and delete the dead `PositionTracker.TryBuildPosition` stub.
- **Gate:** solution builds; Unit + Simulation + Architecture suites green; no new warnings.

---

## Out of scope / guardrails
- No Web/UI changes (iter-25's lane is closed for this iteration).
- Do not "wire the reducer's UNWIRED branches" (BarClosed/TickReceived/EquityObserved/Day/Week/Month)
  — RiskManager remains authoritative per SYSTEM-MODEL §3.2; only fix the live imperative paths.
- Keep the 28/28 simulation FTMO tests green throughout; treat any red there as a stop-the-line.

## Definition of Done
All eight gates met; the iteration acceptance scenario passes as a simulation test; OPEN-ISSUES.md
updated with F1–F10 marked `✅ Fixed (Iteration 26)` (and any deferred — e.g. trailing — left open
with a note).
