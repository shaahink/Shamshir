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

---

## cTrader live-verification follow-ups (accumulating; resolved in K7)
- None new from K1 (backtest-path only). The sim-time verdict change also benefits live (weekend/news no
  longer wall-clock-dependent) but live e2e is out of sandbox scope — owner to confirm in K7.
- K2 `ClientOrderId`: only `BacktestReplayAdapter` (+ test `FakeVenue`) honor it. The cTrader adapter must
  use it as the venue Label/clientOrderId for the kernel path to join venue fills to kernel positions
  live — owner live-verification follow-up (no cTrader in sandbox).
