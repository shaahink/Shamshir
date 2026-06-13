# Iter-19 Remediation Plan (FIX-PLAN.md)

**Audience**: the next implementing agent. This document is self-contained — do not assume access to the review conversation that produced it.
**Branch**: create `iter/19-fixes` off `dev`. One commit per phase, message prefix `fix(iter19-fN):`.
**Status of iter-19**: a post-merge code review found that the three flagship features (TradingGovernor, SignalGate re-entry, Experiment harness) are largely **non-functional as shipped**, and one regression (F1) likely stops the engine from taking any trades at all. The 133 passing tests do not catch this because the new services are tested in isolation but were never wired into the engine's event flow.

---

## Hard rules (unchanged from iter-17/18 — violations block merge)

- `decimal` for ALL money/price arithmetic. Never `double` for money.
- `IEngineClock` everywhere; no `DateTime.UtcNow` in engine code (Web controllers/UI excepted).
- `TradingEngine.Domain` has zero infrastructure dependencies.
- cBot project (`TradingEngine.Adapters.CTrader`) targets **net6.0 / C# 10** — no C# 11+ constructs there.
- Single composition root: `EngineHostFactory`.
- `CancellationToken` on every async method.
- EF migrations only — no raw SQL schema changes.
- **Test-first**: every phase below begins with a failing test. Run the test, confirm it FAILS for the stated reason, then fix, then confirm green. Do not skip the failing-first step — it is the proof the fix addresses the real defect.

Test commands:
```
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test --filter "FullyQualifiedName~<TestClassName>"
```

---

## Phase 0 — Triage: "the backtest didn't run" (DO THIS FIRST)

The owner reports that launching a backtest from the dashboard appeared to do nothing. There are three confirmed candidate causes, all found in review. Establish which apply before fixing:

1. Start the web app, POST a run (or use the dashboard form), then check:
   - Does `/api/backtest/{runId}/status` return `status: completed`? If yes, the run executed — the **dashboard merely cannot display it** (see F8: the page polls for `barCount`/`trades`/`logs`/`governor` fields that the endpoint does not return, so all panels stay empty).
   - Check the run's journal/logs for `REJECTED` entries containing `MAX_EXPOSURE`. If every `SIGNAL` is followed by a rejection, F1 is the cause of "no trades".
   - If the process crashes at startup, capture the DI/config exception (e.g., `governor.json` / `sizing-policy.json` deserialization, `ITradingGovernor` resolution) and fix that first.
2. Record what you find in this file under a `## Phase 0 findings` heading before proceeding. The phases below assume causes (F1) and (F8); if you find a different root cause, fix it first and document it.

---

## Phase F1 — CRITICAL: MAX_EXPOSURE rejects virtually all market orders

**Defect**: `src/TradingEngine.Risk/RiskManager.cs:119`
```csharp
var newPositionRisk = (decimal)slPips.Value * pipValue * (decimal)symbolInfo.MaxLots;
```
`symbolInfo.MaxLots` is **100** for all symbols (`config/symbols.json`). A 20-pip SL on EURUSD ⇒ 20 × $10 × 100 = $20,000 = 20% of a $100k account, vs `maxExposurePercent: 0.05`. **Any SL ≥ ~5 pips is rejected.** This was the G0.3 "fix" for BUG-08 (the old code used the SL as entry price, making the check a 0-pip no-op); it overshot from never-fires to always-fires.

**Failing test** (add to `tests/TradingEngine.Tests.Unit/RiskTests/RiskManagerTests.cs`):
```csharp
[Fact]
public void Validate_TypicalMarketOrder_DoesNotTriggerMaxExposure()
{
    // EURUSD market order, 20-pip SL, $100k equity, standard profile (maxExposurePercent 0.05),
    // no open positions. Must NOT contain a MAX_EXPOSURE violation.
    // Build via the same harness/fixtures the existing Validate tests use.
    var violations = riskManager.Validate(intent20PipSl, equity100k, standardProfile, currentMid: 1.1000m);
    Assert.DoesNotContain(violations, v => v.Code == "MAX_EXPOSURE");
}
```
Confirm it fails (MAX_EXPOSURE present) before fixing.

**Fix**: estimate the new position's risk with the lots this trade will actually request, not the symbol maximum. The intended size is risk-based, so the estimate simplifies to per-trade risk:
```csharp
// Estimated risk of the new position if sized normally: equity × per-trade risk fraction.
// (lots = equity×risk% / (slPips×pipValue), so slPips×pipValue×lots = equity×risk%.)
var newPositionRisk = equity.Equity * (decimal)profile.RiskPerTradePercent;
```
Keep the `entryPrice = intent.LimitPrice ?? new Price(currentMid)` change from iter-19 — that part was correct. If `slPips`/`pipValue` become unused in this branch, remove them. Update any RiskManagerTests that asserted the old MaxLots-based arithmetic.

**Also verify after fixing**: run a real backtest (1 month EURUSD H1) and confirm trades occur. If the trade count is still 0, check the governor SoftStop and `ValidateBudgetEntry` paths next (see F2/F9) and document.

---

## Phase F2 — Governor state machine: state never persists; profit-lock unreachable; streak dead; size reduction inert

All in `src/TradingEngine.Risk/Governor/TradingGovernorService.cs` plus wiring. Four distinct defects:

### F2a — `Evaluate` never assigns `_state`
`Evaluate` (line ~28) computes a candidate state via `DetermineState` and returns it, but never sets `_state`. Consequences (verified):
- `if (_state == GovernorTradingState.SoftStop || _profitLockedToday) return (_state, 0m, _reason);` — after profit lock triggers once, this returns `_state` = **Normal** ⇒ `AllowNewTrades` = true ⇒ trading resumes immediately after "locking" profit.
- `_state == HardStop` check is dead code (nothing ever sets HardStop).
- `GetSnapshot()` always reports Normal.

**Failing test** (new file `tests/TradingEngine.Tests.Unit/RiskTests/TradingGovernorServiceTests.cs` — the handover lists this entire suite as missing):
```csharp
[Fact]
public void ProfitLock_BlocksTrades_OnSubsequentEvaluations()
{
    var gov = MakeGovernor(); // ProfitLockEnabled, ProfitLockFraction 0.6
    var winningCtx = ContextWithDayPnl(+0.04m); // +4% day vs 5% daily limit => gain ≥ 0.6×limit
    var first = gov.Evaluate(winningCtx);
    Assert.False(first.AllowNewTrades);                       // passes today
    var second = gov.Evaluate(winningCtx);
    Assert.False(second.AllowNewTrades);                      // FAILS today — returns true
    Assert.Equal(GovernorTradingState.ProfitLocked, second.State); // FAILS today — returns Normal
}
```

**Fix**: at the end of `Evaluate`, persist: `_state = candidateState; _reason = candidateReason;` and have the sticky branch return `(GovernorTradingState.ProfitLocked, 0m, _reason)` explicitly when `_profitLockedToday` (do not echo `_state` blindly). Also: when `DetermineState` returns SoftStop, it must stay SoftStop until `OnDailyReset` even if the DD fraction later recedes (floating recovery) — that is the documented "sticky" semantics.

### F2b — Profit lock mathematically unreachable
`RiskManager.Validate` (line ~76) builds `GovernorContext` with `equity.CurrentDailyDrawdown` as `DayRealizedPnLPercent`. `DrawdownTracker.OnEquityUpdate` clamps `CurrentDailyDrawdown = Math.Max(0m, …)` — it is **never negative on a profitable day**, so `dailyDdFraction <= -ProfitLockFraction` can never be true.

**Failing test**: the test in F2a already fails for this reason too (with a real `GovernorContext` built the way RiskManager builds it). Add one integration-flavored assertion: build the context exactly as `RiskManager.Validate` does from a DrawdownTracker showing +4% day, and assert `Evaluate(...).State == ProfitLocked`.

**Fix**: give the governor a **signed** day PnL. Change `GovernorContext` construction in `RiskManager.Validate` to:
```csharp
var dayStart = drawdownTracker.DailyStartEquity;
var dayPnLFraction = dayStart > 0 ? (equity.Equity - dayStart) / dayStart : 0m; // signed: + = profit
var governorCtx = new GovernorContext(dayPnLFraction, dayStart, equity.Equity, 0, ...);
```
In `TradingGovernorService.Evaluate`, derive `dailyDdFraction = Math.Max(0m, -context.DayRealizedPnLPercent) / maxDailyLoss` and the profit-lock condition `context.DayRealizedPnLPercent >= (decimal)_options.ProfitLockFraction * maxDailyLoss`. Rename the record field to `DayNetPnLFraction` while you're there (it was never "realized" — it includes floating). Note `GovernorContext.ConsecutiveLosses`, `DayStartEquity`, `CurrentEquity` are currently never read by `Evaluate` — either use them or delete them; do not leave dead fields.

### F2c — `OnTradeClosed` has zero callers ⇒ streak features dead
Nothing in `src/` calls `governor.OnTradeClosed`, so `_consecutiveLosses` is always 0: streak-reduce, streak-pause, and cooling-off can never fire.

**Failing test** (integration — `tests/TradingEngine.Tests.Integration/`): drive 3 losing trades through `PositionTracker` (submit order fill, then close at a loss via execution events — copy the pattern from existing PositionTracker tests), then assert `governor.GetSnapshot().ConsecutiveLosses == 3`. Fails today (stays 0).

**Fix**: `PositionTracker.ClosePositionAsync` already builds a `TradeResult` and publishes `TradeClosed` (`src/TradingEngine.Services/PositionTracker.cs:122`). Inject optional `ITradingGovernor? governor` and `ISignalGate? signalGate` into `PositionTracker`'s constructor (both are DI singletons in `EngineHostFactory`; pass null in tests that don't care) and:
- in the close path, after building `tradeResult`: `governor?.OnTradeClosed(tradeResult);` and `signalGate?.OnPositionClosed(pos.StrategyId, pos.Symbol.Value, pos.Direction, exitReason, clock.UtcNow);` (the `exitReason` from `DetermineExitReason` is already in scope — it produces the "SL"/"TP" strings `SignalGateService` switches on; verify the exact strings match and align them if not),
- in the fill path (pending order → open position): `signalGate?.OnPositionOpened(...)`.
This single wiring point fixes F2c and half of F4.

### F2d — Governor never reduces size
`GovernorSizeModifier.ComputeScale` (`src/TradingEngine.Risk/Sizing/GovernorSizeModifier.cs:16`) reads `GetSnapshot().SizeMultiplier`, which is computed from the streak only — the band multiplier that `Evaluate` returns (e.g. 0.5 in the Reduced band) is discarded (RiskManager only reads `AllowNewTrades`).

**Failing test** (unit): set up governor in the Reduced band (day PnL −2.4% of a 5% limit with `LossBandFractions [0.4,0.6]`, `LossBandMultipliers [0.5, …]` — note: see F2e below first), call `Evaluate`, then assert `GetSnapshot().SizeMultiplier == 0.5m`. Fails today (returns 1.0).

**Fix**: store the last decision: in `Evaluate`, set `_lastDecision = decision` before returning; `GetSnapshot()` returns `_lastDecision.SizeMultiplier` (and the persisted `_state`). Ordering is safe: `OrderDispatcher` calls `riskManager.Validate(...)` (which runs `Evaluate`) **before** `CalculateLotSize` (which runs the size pipeline), so the snapshot is fresh per order. Also fix `GetSnapshot`'s 5th argument: it currently passes `_dayRealizedPnLPercent` for **both** `DayRealizedPnLPercent` and `DistanceToDailyLimitFraction` (copy-paste); compute the real distance `1 - dailyDdFraction`.

### F2e — Config sanity (no test needed, just fix)
Default `LossBandMultipliers` is `[1.0, 0.5]`: the first band (40% of daily budget) multiplies size by **1.0** — a no-op "Reduced" state — and 0.5 is assigned to the SoftStop band where trading is blocked anyway, so it never applies. Change `config/governor.json` and the `GovernorOptions` defaults to `[0.5, 0.0]` (or another deliberate choice) and document the intent in the JSON. Also note `OnTradeClosed` treats PnL == 0 as a loss; make break-even trades reset-neutral (neither increment nor reset) or document the choice.

---

## Phase F3 — Bar-time bookkeeping wrong in every mode

**Defects** (`src/TradingEngine.Host/EngineWorker.cs`):
- Line ~183 (live tick loop): `_governor?.OnBar(tick.TimestampUtc)` fires **per tick** — a 24-bar cooling-off expires in ~24 ticks (seconds), not 24 bars.
- The live bar loop never calls `_signalGate?.OnBar(...)` — cooldowns never expire in live mode (permanent block once armed).
- Backtest loop (line ~398): `OnBar` fires per **bar event**, i.e. per symbol × timeframe — with 2 symbols × 2 TFs, cooldowns expire 4× too fast.

**Failing test** (unit, `SignalGateServiceTests` / `TradingGovernorServiceTests`):
```csharp
[Fact]
public void OnBar_SameTimestampTwice_DecrementsOnce()
{
    var gate = new SignalGateService();
    gate.OnPositionClosed("s1", "EURUSD", TradeDirection.Long, "SL", T0); // arms 5-bar cooldown (defaults)
    var t1 = T0.AddHours(1);
    gate.OnBar(t1); gate.OnBar(t1); gate.OnBar(t1); gate.OnBar(t1); gate.OnBar(t1);
    // 5 calls, 1 unique timestamp => 4 bars must remain
    Assert.False(gate.Check("s1", "EURUSD", TradeDirection.Long, t1).Allowed); // FAILS today (cooldown gone)
}
```
Mirror test for `TradingGovernorService.OnBar` cooling-off decrement.

**Fix** (defense in depth — do both):
1. Make `OnBar` idempotent per timestamp in **both** `SignalGateService` and `TradingGovernorService`: `if (barTimeUtc <= _lastBarTimeUtc) return; _lastBarTimeUtc = barTimeUtc;` before decrementing. This makes multi-symbol/multi-TF call patterns harmless.
2. In `EngineWorker`: move the live-path `_governor?.OnBar` from the tick loop into the live bar loop, and add `_signalGate?.OnBar(bar.OpenTimeUtc)` beside it (matching what the backtest loop already does).

Note the timeframe-mixing caveat: with H1+H4 in one run, idempotency-by-timestamp means cooldowns tick at the fastest timeframe's rate. That is acceptable and must be stated in a comment on `_lastBarTimeUtc`; per-(symbol,timeframe) cooldown clocks are an iter-20 refinement if needed.

---

## Phase F4 — SignalGate fully inert (zero wiring)

**Defects** (verified by grep — zero callers in `src/`):
- `SignalGateService.RegisterStrategy` never called ⇒ `_configs` empty ⇒ per-strategy reentry config never applies.
- `OnPositionOpened`/`OnPositionClosed` never called ⇒ no cooldown is ever armed ⇒ `Check` always allows. The whole T1 feature is dead.
- `ComposedStrategy.Reentry => new();` (`src/TradingEngine.Services/Strategy/ComposedStrategy.cs:101`) discards configured values.
- `ConfigLoader` never parses the `reentry` blocks added to the 9 strategy JSONs (only `governor.json`/`sizing-policy.json` parsing was added in iter-19).
- Bonus defect in `SignalGateService.OnPositionOpened`: the entry cooldown only arms when `BlockWhileSameDirectionOpen && CooldownBarsAfterEntry > 0` — two unrelated options conflated. Arm the entry cooldown on `CooldownBarsAfterEntry > 0` alone. (`BlockWhileSameDirectionOpen` as designed means "block while a same-direction position is open" — implementing that requires open-position awareness; either implement it via `OnPositionOpened`/`OnPositionClosed` tracking an open flag per key, or remove the option. Do not leave it half-wired.)

**Failing test** (integration): end-to-end through `PositionTracker` + `SignalGateService`: open a position, close it with exit reason "SL", then assert `gate.Check(strategyId, symbol, direction, t).Allowed == false`. Fails today because nothing calls the gate callbacks.

**Fixes**:
1. PositionTracker wiring — done in F2c (same injection).
2. `RegisterStrategy`: in `EngineWorker` startup (where strategies are resolved from the bank) or `EngineHostFactory`, call `signalGate.RegisterStrategy(strategy.Config)` for every loaded strategy. `ISignalGate` doesn't declare `RegisterStrategy` — add it to the interface (`src/TradingEngine.Domain/Interfaces/ISignalGate.cs`).
3. `ComposedStrategy.Reentry`: return the underlying config entry's reentry options (pass through from whatever config source ComposedStrategy wraps), not `new()`.
4. `ConfigLoader`: parse the `reentry` JSON block into each strategy config's `ReentryOptions` (follow the existing pattern used for `positionManagement`). Add a `ConfigLoaderTests` case asserting a strategy JSON's `reentry.cooldownBarsAfterSl` round-trips.

---

## Phase F5 — SteppedRTrail freezes at breakeven forever

**Defect**: `src/TradingEngine.Services/TrailingStop/TrailingHelpers.cs:150`
```csharp
var slDistance = Math.Abs(position.EntryPrice.Value - position.CurrentStopLoss.Value);
```
R is measured from the **current** SL. After the first ratchet moves SL to entry, `slDistance == 0`; every later evaluation computes `newSl = entry + 0 = entry`, which is never `> CurrentStopLoss`, so the trail returns null forever. The 1R/2R/3R ratchet never advances past breakeven.

**Failing test** (unit, `TrailingStopTests` or a new `SteppedRTrailTests`):
```csharp
[Fact]
public void SteppedRTrail_AfterBreakeven_RatchetsToNextLevel()
{
    // long: entry 1.1000, INITIAL sl 1.0950 (50 pips = 1R)
    var posAtBreakeven = MakePosition(entry: 1.1000m, currentSl: 1.1000m, TradeDirection.Long);
    var sl = TrailingHelpers.SteppedRTrail(posAtBreakeven, initialSlDistance: 0.0050m,
        currentBid: 1.1100m, currentAsk: 1.1101m, new[] {1.0, 2.0, 3.0}, EurusdInfo);
    Assert.NotNull(sl);                       // FAILS today (returns null)
    Assert.Equal(1.1050m, sl!.Value.Value);   // +2R profit => lock 1R
}
```

**Fix**: `Position` has no initial-SL field (verified: `src/TradingEngine.Domain/Trading/Position.cs`). Add a `decimal initialSlDistance` parameter to `SteppedRTrail` and have `PositionManager` supply it from a new `Dictionary<Guid, decimal> _initialSlDistance` cache: on first sight of a position id (same place `_highWaterBid` seeds), store `Math.Abs(EntryPrice - CurrentStopLoss)`; remove the entry in `DeregisterPosition`. Do **not** add the field to the `Position` record unless you also update every construction site.

Also add the **monotonic-SL property test** the handover lists as missing: for all four trail methods (AtrMultiple, Structure, SteppedR, BreakevenThenTrail), assert a returned SL is never lower (long) / higher (short) than `CurrentStopLoss`. Structure/SteppedR already guard this; the test locks it in.

---

## Phase F6 — Experiment harness cannot run, and would compare noise if it did

Three independent fatal defects in `src/TradingEngine.Host/Experiments/ExperimentRunner.cs`:

### F6a — Guid format string throws on every run
Lines 89 and 97: `$"{experimentId:N[..8]}-{runIndex++}"` passes the invalid format `N[..8]` to `Guid.ToString` ⇒ `FormatException` on the first fold of every experiment; the catch-all marks it Failed. Fix: `$"{experimentId.ToString("N")[..8]}-{runIndex++}"`.

### F6b — Variant overrides never reach the engine
Line 78 builds `variantConfig` via `ConfigOverrideApplier.Apply`, it is passed to `RunSingleAsync` (line 186) — which **never uses its `config` parameter**. `EngineHostFactory.Create` reloads baseline config from `options.SolutionRoot` internally. Every variant runs the identical baseline; results would be comparisons of noise.

Fix: add `LoadedConfig? PreloadedConfig { get; init; }` to `EngineHostOptions`; in `EngineHostFactory.Create`, use `options.PreloadedConfig ?? new ConfigLoader(options.SolutionRoot).Load()`. Pass `variantConfig` from `RunSingleAsync`. Also verify `ConfigOverrideApplier`'s serialize-round-trip actually survives `LoadedConfig`'s shape (it holds interface-typed strategy configs — if `JsonSerializer.Serialize(config)` drops or fails on `IStrategyConfig` members, overrides must instead be applied to the raw JSON files' parsed DTOs before construction; check this explicitly and document which path you took).

### F6c — Scoring reads a database no engine run writes to
Each `RunSingleAsync` gives the engine host its own temp `dbPath`, but fold scoring (lines ~104–114) queries `_tradeRepo`/`_equityRepo` — repositories bound to the CLI/Web DB. They are always empty ⇒ every fold scores 0 trades. Worse, the queries are **date-range** based, not run-scoped, so even a shared DB would mix trades across variants and folds.

Fix: use **one shared DB per experiment** (single dbPath passed to all engine hosts) and query by run id: add `GetByRunIdAsync(string runId, ...)` to `ITradeRepository`/`IEquityRepository` if absent (the per-run query pattern already exists — see `IBacktestQueryService` and `SqlitePipelineEventRepository.GetByRunIdAsync`). Score each fold from its test-run's runId, never by date.

### F6d — Fold role bookkeeping
In the fold loop, `foldRole` is `"Train"` for multi-fold runs but the score is computed from the **test** range, and only one FoldScore per fold is recorded — `ExperimentReportWriter`'s train/test gap math then never has both roles. Score the train run and the test run separately, two `FoldScore`s per fold, with correct roles.

**Failing test** (integration, new `ExperimentRunnerTests`): seed ~60 days of synthetic H1 bars into a temp bar repo, run a 2-variant 1-fold spec where variant B overrides a config value with an observable effect (e.g. `riskProfiles` per-trade risk), assert: (1) `result.Success` is true — **fails today with FormatException**; (2) both variants have `TotalTrades > 0` — fails today (empty DB); (3) the two variants' scores differ — fails today (identical configs). Then add the handover's **determinism test**: same spec twice ⇒ identical scores.

**Cleanup in the same phase** (no tests needed): `ExperimentCli.ShowReport` and `ExperimentsController.GetReport` ignore the requested id and return the first `REPORT.md` found — match the directory suffix against `id.ToString("N")[..8]`. `ExperimentsController.Create` runs the whole experiment inside the HTTP request — acceptable for now, but add a comment and a `[RequestTimeout]`/known-issue note. Replace the `await Task.Delay(2_000, ct)` post-run sleep with awaiting the actual persistence flush if a handle exists; otherwise document why the delay is needed.

---

## Phase F7 — Partial close protocol: no trigger, and the one handler corrupts risk tracking

**Defects**:
- `ClosePartialPositionAsync` has **zero engine-side callers**; `PartialTpOptions` is declared but never read. Six adapters of plumbing with no faucet (T3 was wired bottom-up only).
- `src/TradingEngine.Services/PositionTracker.cs:140`: `HandlePartialCloseAsync` calls `riskManager.RegisterPosition(pos.Id, pos.StrategyId, 0)` — re-registering the remaining position with **zero risk**, corrupting `_openPositionRisk` used by exposure/budget checks. The `(_, riskProfileId)` tuple fetched on line ~139 is dead code.
- `SimulatedBrokerAdapter.ClosePartialPositionAsync` mutates internal state but emits **no execution event**, so PositionTracker never learns of the partial in simulated mode (BacktestReplayAdapter does emit one — the two adapters disagree).

**Decide scope first**: full PartialTp triggering (PositionManager evaluating `PartialTpOptions` and issuing partial closes) is a feature, not a fix. **Recommended**: implement only the correctness fixes now, file the trigger as an OPEN-ISSUES entry for iter-20.

**Failing test** (the handover's "partial close dedup regression test"): feed PositionTracker a fill for 1.0 lots with registered risk $500, then a partial exec for 0.5 lots; assert the position remains open with 0.5 lots **and** the re-registered risk is $250 (proportional), not 0. Fails today (risk becomes 0).

**Fixes**: proportional risk: look up the position's current registered risk (expose a getter on `IRiskManager` or track open risk beside the position) and re-register `risk * remainingLots / pos.Lots`. Delete the dead `_pendingRisk` tuple fetch. Add the missing execution-event emission to `SimulatedBrokerAdapter.ClosePartialPositionAsync` (mirror its full-close path).

---

## Phase F8 — PR4 UI is dead-on-arrival

**Defects** (all verified):
1. `BacktestDashboard.razor` polls `/api/backtest/{runId}/status` and deserializes `barCount`, `simTime`, `trades`, `logs`, `governor` — `BacktestController.Status` (`src/TradingEngine.Web/Api/BacktestController.cs:84`) returns none of these. Trade feed, log tail, bar progress, and governor banner can never populate. **This is almost certainly what the owner experienced as "the backtest didn't run".**
2. Web `Program.cs:56` registers its own `ITradingGovernor` singleton — a different instance from the per-run engine host's governor. `/api/governor/state` always reports "Normal/Initial".
3. `GovernorStateChanged` is **never published** anywhere ⇒ `ProtectionLedgerWriter` never fires (and it only logs anyway — the ledger tables stay empty; persistence is a known iter-20 gap, leave it, but the event must be published for anything downstream to ever work).
4. Razor format specifiers don't exist: `@t.Lots:F2`, `@_run.NetProfit:N2`, `@b.WinRatePct:F1%` etc. in `BacktestDashboard.razor` / `RunDetail.razor` render the literal text `:F2`. Use `@t.Lots.ToString("F2")` or `@($"{t.PnL:F2}")`.

**Failing test** (WebSmokeTests — handover lists these as missing): start the test host, GET `/api/backtest/{knownRunId}/status`, deserialize into the dashboard's `StatusResponse` shape, assert `barCount > 0` and `governor != null` after a completed seeded run. Fails today.

**Fixes**:
1. Extend `BacktestOrchestrator`'s per-run state to capture, while the run is active: bar count and sim time (it already receives progress events — `BacktestProgressEvent` carries them), recent log lines (it has `GetLogs()`), closed trades (query `IBacktestQueryService` incrementally or capture TradeClosed progress events), and a governor snapshot. For the governor: the orchestrator owns the engine host per run — resolve `ITradingGovernor` from that host's services and call `GetSnapshot()` when building the status response. Then return all fields from `Status()` matching the dashboard's `StatusResponse` property names (camelCase).
2. `GovernorController` should report the **active run's** governor via the orchestrator (latest run), falling back to "no active run". Remove the Web-local `ITradingGovernor`/`DrawdownTracker` registrations unless something else needs them.
3. Publish `GovernorStateChanged(from, to, reason, at)` from `TradingGovernorService` whenever the persisted `_state` changes (F2a gives you the transition point). The governor lives in `TradingEngine.Risk` and must not depend on infrastructure — inject the existing event-bus abstraction used by other domain publishers, or raise a plain C# event that `EngineWorker` forwards to the bus. Follow whatever pattern `RiskManager`/`DrawdownTracker` use for events today.
4. Fix every Razor format-specifier occurrence (grep the two pages for `:F`, `:N`, `:P`, `:yyyy`).

---

## Phase F9 — Secondary correctness fixes (small, do together)

1. **`PositionManager.ComputeAtr` fallback** (`src/TradingEngine.Services/PositionManager.cs`, ~line 152): hardcodes `0.0001` where the pre-iter-19 code used `symbolInfo.PipSize`. On gold/indices an unavailable ATR yields a near-zero trail offset ⇒ SL slammed onto price ⇒ instant stop-out. Pass `SymbolInfo` into `ComputeAtr` and use `(double)symbolInfo.PipSize`. Unit test: `recentBars` empty + XAUUSD info ⇒ fallback offset reflects 0.01 pip size, not 0.0001.
2. **`GetEffectiveAtrMultiple` ride gate is nonsense**: `if (!RideEnabled) return AtrMultiple; return AtrMultiple > 0 ? RideRelaxedAtrMultiple : AtrMultiple;` — when ride is enabled the relaxed multiple applies **unconditionally**; `RideOptions`' trigger fields are never read. Either implement the actual gate (relaxed multiple only after the configured profit threshold is reached — read `RideOptions` and the position's R progress) or hard-disable: return `AtrMultiple` always and log a warning that ride is unimplemented. Do not ship the current always-relaxed behavior — it widens stops from entry, increasing initial risk.
3. **`OrderDispatcher` downsize loop vs heat cap** (`src/TradingEngine.Services/OrderDispatcher.cs`, budget block): `ValidateBudgetEntry(riskAmount, equity, riskAmount)` passes this trade's own (shrinking) risk as `perTradeRiskAmount`, so the heat cap `perTradeRisk × MaxPortfolioHeatRiskMultiples` shrinks as fast as the trade is downsized — downsizing can never satisfy it. Pass the **standard** per-trade risk (`equity.Equity × profile.RiskPerTradePercent`) as `perTradeRiskAmount`, computed once before the loop. Unit test: open risk = 2× standard risk, new trade at standard risk ⇒ downsizing to half must pass the budget check.
4. **`EngineWorker` daily reset key** (`HandleAccountUpdate`): day change is detected via `DayOfYear` only — fine within a year, but also fold in the year (`now.Year * 1000 + now.DayOfYear`) so multi-year backtests can't skip a reset on coincidental wrap.
5. **Governor reason-string cosmetics**: breach-watchdog messages say "hard limit" but fire at 0.9× (flatten fraction); include the multiplier in the message.

---

## Phase F10 — The causal proof test (handover's top missing test — write LAST, after F1–F3)

Simulation test "governor ON vs OFF" (`tests/TradingEngine.Tests.Simulation/`): scripted losing sequence (e.g. AlwaysSignalStrategy with forced SL hits) against FTMO-standard rules. Assert:
- OFF: equity breaches the daily band (proves the scenario bites).
- ON: trading is blocked at SoftStop (≥0.6× daily budget), size halves in the Reduced band, breach watchdog flattens at 0.9× of the hard limit, and the day never exceeds `MaxDailyLossPercent`.
This is the regression net for the entire governor feature. If this test cannot be made to pass after F1–F3, the iteration is not done.

---

## Execution order & Definition of Done

Order: **0 → F1 → F2 → F3 → F4 → F5 → F6 → F7 → F8 → F9 → F10.** F1 unblocks real backtests; F2/F3/F4 share wiring; F10 validates the whole.

DoD checklist (each item commit-gated):
- [x] Phase 0 findings documented in this file
- [x] Every phase's failing test was observed failing before the fix (state this in each commit message)
- [x] `dotnet test` green on Unit + Integration (126 unit + 17 integration = 143 passing)
- [ ] A real 1-month EURUSD H1 backtest from the dashboard: produces trades, dashboard panels populate live, `/api/governor/state` reflects the run (requires running app interactively)
- [x] No `DateTime.UtcNow` introduced in engine code; all money math `decimal`
- [ ] `docs/OPEN-ISSUES.md` updated: close BUG-06/07/08 follow-ups where now truly fixed; file new entries for anything descoped (PartialTp trigger, per-(symbol,timeframe) cooldown clocks, ProtectionLedger persistence, playbook inheritance)
- [x] Update `docs/iterations/iter-19/HANDOVER.md` status from "Complete" to reflect reality, and append a remediation summary

---

## Phase 0 findings

**Date**: 2026-06-13 | **Branch**: `iter/19-fixes` | **Verified by code inspection and test execution**

### Root cause of "the backtest didn't run"

Two causes combine to produce the owner's experience:

1. **F8 (Dashboard blind)**: `BacktestDashboard.razor`'s `StatusResponse` (line 150) polls `GET /api/backtest/{runId}/status` and expects 7 fields: `status`, `simTime`, `barCount`, `trades[]`, `logs[]`, `governor`. `BacktestController.Status()` (line 84–99) returns only `runId`, `status`, `startedAt`, `result`, `error`. Missing: `simTime`, `barCount`, `trades`, `logs`, `governor`. **Bar progress, trade feed, log tail, and governor banner panels stay empty.** The user sees a spinner forever and concludes "didn't run."

2. **F1 (MAX_EXPOSURE overblocks)**: `RiskManager.Validate:119` computes `newPositionRisk = slPips × pipValue × MaxLots` where `MaxLots = 100` for all symbols. A 20-pip EURUSD SL ⇒ 20 × $10 × 100 = $20,000 = 20% of $100k vs `maxExposurePercent: 0.05`. **Any SL ≥ ~5 pips is rejected.** The backtest loop runs but every signal → REJECTED → zero trades → dashboard shows nothing anyway (F8). Even if F8 were fixed first, the user would see bars progress but zero trades.

### Ancillary findings

3. **F2c/F4 (Governor/SignalGate unwired)**: `PositionTracker.ClosePositionAsync` (line 107–123) calls neither `governor.OnTradeClosed` nor `signalGate.OnPositionClosed`. The governor never learns about wins/losses. Cooldowns never arm. Streak stays 0. `SignalGateService.RegisterStrategy` has zero callers — the 9 strategy JSON `reentry` blocks are loaded but never registered.

4. **F8b (Duplicate ITradingGovernor)**: `Web/Program.cs:56` registers its own `ITradingGovernor` singleton separate from the engine host's. `Host/Program.cs:121` also registers one. The web's `/api/governor/state` always returns the web's idle governor ("Normal/Initial"), never the active backtest run's governor.

5. **No config crash observed**: The project builds clean, all existing 21+ tests pass. No DI/config deserialization exceptions at startup.

### Verdict

**The backtest DOES run** — `BacktestOrchestrator.RunEngineReplayAsync` creates an `EngineHost`, replays bars, and stores results via `GetTradeStatsAsync`. But:
- F8 makes the dashboard appear dead (spinner forever, empty panels)
- F1 makes the run produce zero trades (every signal rejected by MAX_EXPOSURE)
- The user sees a dead dashboard AND gets empty results — confirming "didn't run"

**Priority unchanged from the fix plan**: F1 first (unblock trades), then F2/F4 (wire governor/signal gate), then F8 (fix dashboard). This order lets us validate each fix: after F1, a backtest produces trades; after F2/F4, the governor tracks them; after F8, the dashboard displays everything.
