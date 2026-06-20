# Iter-36 — Kernel Cutover HANDOVER (per-phase deltas)

Branch: `iter/36-kernel-cutover`. Tracks each phase's deltas, golden re-baselines (none so far), and
cTrader live-verification follow-ups. See `PLAN.md` for the full plan + inline per-phase STATUS blocks.

---

## K0 — Real gates (DELIVERED by owner, pre-handoff)
See the `> STATUS — K0 DELIVERED` block in `PLAN.md`. Purity body-scan hardened; determinism compares
the full StepRecord (effect payloads + risk); equivalence de-magic'd against `golden-snapshot.json`; the
date-dependent golden oracle fixed by anchoring the harness clock to fixture sim-time. No re-baseline.

## K1 — Evaluator stage (DELIVERED 2026-06-20)

**What landed**
- **`src/TradingEngine.Host/BarEvaluator.cs`** — per-`BarClosed` event producer porting
  `TradingLoop.ProcessBarAsync`'s evaluation: indicator recompute → regime → strategy bank → strategy
  eval → entry planner → signal gate. Emits one `OrderProposed` per firing strategy (with `SlPips` +
  cross-rate-aware `PipValuePerLot`) + a per-strategy `StrategyVerdict`. Output `BarEvaluation(Regime,
  Proposals, Verdicts)`; `Latest` cached for the driver `evaluatorView` seam (K3/K5). Deterministic
  order-id counter (no `Guid.NewGuid`).
- **External verdicts ported + replay-safe.** `KernelOrderGate.ComputeVerdicts`
  (news/weekend/compliance/governor) ported into the evaluator, evaluated at the **bar's sim-time**.
  Verdict frozen onto the event.

**Contract changes**
- `OrderProposed` (Domain) gained trailing `ExternalVerdicts External = default` (backward-compatible —
  existing positional constructions unaffected).
- `Kernel.DecideProposed` now passes `p.External` to `PreTradeGate.Evaluate` (was hard-coded `default`,
  which silently dropped ALL external protection in the kernel path — a real latent bug, now fixed).
- `ExternalVerdicts` moved `TradingEngine.Engine` (nested in `PreTradeGate`) →
  `TradingEngine.Domain/Kernel/ExternalVerdicts.cs` (a Domain event must be able to carry it).
  `PreTradeGate` + `KernelOrderGate` updated to the moved type; behaviour identical.

**Tests** — `tests/.../GoldenReplay/KernelEvaluatorEquivalenceTests.cs` (3, `[Speed=Fast]`):
evaluator→kernel reproduces golden's first order (0.20/Long, one `SubmitOrder`); kernel applies a
weekend verdict carried on a proposal (reject `WEEKEND_RESTRICTION`) vs default-verdict accept; evaluator
freezes the weekend verdict from a Saturday bar's sim-time.

**Golden re-baseline:** none. `golden-snapshot.json` preserved.

**Verified green:** K1 3/3 · K0 GoldenReplay+Determinism+KernelAcceptance 9/1-skip · Engine purity 4/4 ·
Architecture 4/4 · fast Simulation 21/1-skip · Unit 209/4-skip.

**Carried forward (K2/K3):** the K1 test hand-drives the event queue — the `BarEvaluator` + the
venue-feedback bridge (K2) get assembled into the real kernel backtest loop in K3, where the `[Skip]`
`KernelAcceptanceTests.KernelFullRun_MatchesGolden_TradesAndRisk` gets un-skipped. The driver
`evaluatorView` (folding `Verdicts`/`Regime` onto the BarClosed StepRecord) is wired in K5.

## K2 — Venue-feedback bridge (DELIVERED 2026-06-20)

**What landed**
- **`src/TradingEngine.Host/KernelFeedback.cs`** — pure venue→kernel translation: `ExecutionEvent` →
  `OrderFilled`/`OrderPartiallyFilled`/`OrderCancelled`/`OrderRejected`; `AccountUpdate` → `EquityObserved`.

**Contract changes**
- `OrderRequest` gained `Guid? ClientOrderId = null`; `BacktestReplayAdapter.SubmitOrderAsync` + test
  `FakeVenue.SubmitOrderAsync` honor it; `EffectExecutor` submits under the kernel order id. ⇒ venue id ==
  kernel id (= PositionId); the feedback bridge needs no id translation.
- `CloseOpenPosition` gained `Price? ExitPrice = null`; `EngineReducer.HandleBarClosed` sets the position
  `CloseReason` + the stop/target price on the effect; `EffectExecutor` routes a priced close to
  `ClosePositionAtAsync`. ⇒ a kernel SL/TP closes at the stop price with reason "SL"/"TP" (not "FORCE"/bar
  close). `FakeVenue` gained a `ClosePositionAtAsync` override.

**Tests** — `KernelFeedbackTests` (3): flat-book `Equity==Balance` no false breach (C5); 6% drop → protection
exactly once; SL close fill closes via the reducer (`PublishTradeClosed` reason=SL, price=stop).

**Carried forward:** Day/Week/Month roll events (NEW-1) not yet emitted by the loop (golden is single-day);
threading venue commission/swap through `OrderFilled`→`PublishTradeClosed` (net==gross matches zero-cost
golden) — both pre-K4/K7 items.

## K3 — Kernel-driven backtest loop (DELIVERED 2026-06-20)

**What landed**
- **`src/TradingEngine.Host/KernelBacktestLoop.cs`** — `RunAsync` driving `BarTape → BarEvaluator → Kernel
  → IEffectExecutor → venue + feedback bridge`, `EngineState` as the single authority, one `StepRecord`
  per event. A "pump" drains the kernel queue + interleaves venue feedback to quiescence (determinism).

**Tests** — `KernelBacktestLoopGoldenTests` (2) + the **un-skipped**
`KernelAcceptanceTests.KernelFullRun_MatchesGolden_TradesAndRisk` (1): the full golden run through the new
loop reproduces golden's closed trade + final risk **exactly** and is **bit-identical across two runs**.
Shared wiring in `KernelLoopHarness` (FakeVenue + real `EffectExecutor` + realized equity).

**Equity model:** realized (`initial + closed net PnL`) to match the harness-generated golden; the loop also
supports mark-to-market (`realizedEquity: null` → venue `AccountStream`). **No golden re-baseline.**

**Old loop untouched** — `EngineRunner.RunBacktestLoopAsync` + imperative twins remain default. The flip +
twin deletions (with a golden re-baseline for mark-to-market DD) are **K4**.

**Verified green:** K2+K3 6/6 · kernel category 15/0 · Engine purity 4/4 · Architecture 4/4 · fast
Simulation 27/0 · Unit 209/4-skip.

## K4 — Full flip (IN PROGRESS, 2026-06-20)

Owner directive: **full flip — production runs only the kernel; the imperative loop must not continue.**
Validation model: deliver with fast tests, then verify via the existing cTrader e2e.

**Landed (foundation, committed, green):**
- **`KernelBacktestLoop.RunFromBrokerAsync`** — the mode-agnostic production entry point: drives the kernel
  loop off `IBrokerAdapter.BarStream`. Both `BacktestReplayAdapter` and the cTrader adapter publish bars
  there, so one kernel engine serves live + backtest (refactored the per-bar body into a shared
  `ProcessBarAsync`). Test `KernelLoop_DrivenFromBrokerStream_ReproducesGolden` proves it reproduces golden.

**Production-readiness gaps found during recon (must be closed for a *correct* flip — a blind flip is
subtly wrong, not just unfinished):**
1. **Per-strategy risk profile.** `KernelConfig.Profile` is a single run-constant profile; `KernelOrderGate`
   resolves per proposal (`intent.RiskProfileId`). Multi-profile runs would mis-size. Fix: carry the
   resolved `RiskProfile` (or its id) on `OrderProposed` and have `Kernel.DecideProposed` use it.
2. **News/session filters** are not in `EngineWorkerDependencies` — wire `INewsFilter` + `SessionFilter`
   into the deps/DI so the production `BarEvaluator` can compute external verdicts.
3. **Trailing/breakeven** lives in `TradingLoop.UpdateTrailingStopsAsync` (imperative, via `PositionTracker`)
   — must run in the kernel loop (emit `ModifyStopLoss` effects on `BarClosed`) or trailing strategies
   regress.
4. **Monitor equity** comes from `AccountProcessor → IEquitySink/AccountSnapshotStore` (polled by the
   orchestrator). The kernel loop must write equity/DD snapshots from `EngineState` or the Monitor goes
   blank.
5. **Test-oracle preservation.** 24 test files build on the imperative `EngineHarnessBuilder`
   (`TradingLoop`/`OrderDispatcher`/`KernelOrderGate`/`AccountProcessor`). Keep those classes as the
   regression oracle behind `golden-snapshot.json`; remove them from the **production** path only. ⇒ literal
   `grep→0`-in-`src` is intentionally NOT the gate; "no imperative engine in the production wiring" is.

**Remaining flip steps (next pass; each compile-safe):**
- A. Close gaps 1–2 (per-profile on the proposal; news/session in deps) as correct, tested kernel changes.
- B. Build the production kernel engine in `EngineRunner`: construct `KernelConfig` + `BarEvaluator` from
  `EngineWorkerDependencies`, drive `RunFromBrokerAsync`, write equity snapshots (gap 4) + progress/funnel
  events, journal to `SqliteStepRecordSink`.
- C. Point both pacers (`BarSteppedPacer`, `AsyncStreamPacer`) at the kernel engine; remove the imperative
  composition (`TradingLoop`/`AccountProcessor`/`MarketEventSource`) from `EngineRunner`; delete
  `RunBacktestLoopAsync` + `SimulateBarExitsAsync`; drop `OrderGate`/`AccountProcessor` from the production
  deps (classes stay for the test oracle).
- D. Wire trailing into the kernel loop (gap 3).
- E. Verify via cTrader e2e; reconcile.

---

## cTrader live-verification follow-ups (accumulating; resolved in K7)
- None new from K1 (backtest-path only). The sim-time verdict change also benefits live (weekend/news no
  longer wall-clock-dependent) but live e2e is out of sandbox scope — owner to confirm in K7.
- K2 `ClientOrderId`: only `BacktestReplayAdapter` (+ test `FakeVenue`) honor it. The cTrader adapter must
  use it as the venue Label/clientOrderId for the kernel path to join venue fills to kernel positions
  live — owner live-verification follow-up (no cTrader in sandbox).
