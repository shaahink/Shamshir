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

---

## cTrader live-verification follow-ups (accumulating; resolved in K7)
- None new from K1 (backtest-path only). The sim-time verdict change also benefits live (weekend/news no
  longer wall-clock-dependent) but live e2e is out of sandbox scope — owner to confirm in K7.
