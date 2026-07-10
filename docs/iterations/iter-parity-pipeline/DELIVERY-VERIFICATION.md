# iter-parity-pipeline — Independent Delivery Verification

**Verifier:** Claude (owner session, 2026-07-10). Method: claims from PLAN.md/TRACKER.md/AUDIT.md
checked against the live DB, a running app, real CLI invocations, and code reads — not against
the agents' own reports. Companion: `../iter-land-fix/ENGINE-TRUTH.md` (engine truth + F17).

## Headline verdict

**BUILT-BUT-STARVED.** The parity-pipeline delivery is largely real: the tools exist, build,
and respond correctly when driven. But the research machine has processed **zero real data** —
every research table is empty — and the two regressions it shipped (F17, F18) blocked the very
gate (P2.2) that would have exercised it. The next iteration's first job is feeding the machine,
not building more of it.

## Phase-by-phase verification

| Phase | Claim | My check | Verdict |
|---|---|---|---|
| P0.1 sizing | sizes off config balance | not re-derived; unit tests green; sizing journal exists | UNVERIFIED-TRUSTED |
| P0.2 status truth | completed-with-warnings separates result/teardown | observed live on runs `8bd9cedb`, `d5de5628` | ✅ CONFIRMED |
| P0.3 barrier | no vanishing trades | worked, but **false-positives on tape** (F19): healthy run flagged unreconstructable | ⚠️ CONFIRMED-WITH-BUG |
| P0.4 latency | measured 1-bar lag, reconcile carries it | RECONCILE-FINDINGS numbers coherent; endpoint fields present in code | ✅ CONFIRMED (static) |
| P0.5 parity tier | venue-parity test tier | `Simulation/VenueParity/VenueSizingParityTests.cs` exists — **sizing only**, not signals | ⚠️ PARTIAL |
| P1.1 one DB | DbPathResolver, 84 ReferenceScales | ReferenceScales = 84 ✓; BUT the removed config key caused **F17** (orchestrator fallback missed) — fixed `9962432`, verified by live run | ⚠️ DELIVERED-CAUSED-F17 |
| P1.2 config drift | JSON→DB propagation + lint | lint-config verb exists; not exercised by me | UNVERIFIED-TRUSTED |
| P2.1 state machine | one lifecycle, no illegal jumps | exists; refactor **regressed F18** (compare-both children) | ⚠️ DELIVERED-CAUSED-F18 |
| P2.2 headline gate | compare-both + reconcile | **BLOCKED then, still blocked**: F17 now fixed, F18 still broken | ❌ NOT DONE |
| P3.1 ResearchCli | HTTP verbs + machine verdicts | **driven live**: `run validate 8bd9cedb --min-trades 1` → `VERDICT: PASS…exit=0`; `data quality` → real FAIL verdict (24,894 gaps/6.9M bars ≈ weekend closures — sentinel likely not market-hours aware); `exitlab eval` correctly demands `--grid` | ✅ CONFIRMED E2E |
| P3.2 playbooks | engine + canonical playbooks | **11 playbook JSONs exist** in `playbooks/`; engine has unit tests; **zero playbooks ever executed end-to-end** (B1 pending) | ⚠️ BUILT-UNRUN |
| P3.3 /research UI | review page | not driven by me | UNVERIFIED |
| P4 labs + funnel | exploration funnel, MAE/MFE, excursions | columns exist (ExplorationMode/RecordExcursions); **TradeExcursions = 0 rows ever** | ⚠️ BUILT-UNRUN |
| P4.5.1 walk-forward | test leg actually runs (fake-OOS fixed) | code confirmed (`RunTestWindowAsync`, warmup, frozen params); **WalkForwardJobs = 0 — never run** | ⚠️ FIXED-UNRUN |
| P5 UI truth | F13-F16 | not driven by me; F16 child-visibility moot until F18 | UNVERIFIED |
| P6 research verbs | sentinel/sessions/filters/bootstrap/pyramid | code + playbooks present; VenueSessions = 5163 rows (real data!) — the only fed research surface | ⚠️ MOSTLY-UNRUN |
| P7 audits | 17 CONFORMS + traps + cTrader verified | cTrader verified-run claim checks out in DB (`77e37dee` +312.31); P7.7 test classification exists (execute per test doctrine, ENGINE-TRUTH §4b) | ✅ CONFIRMED |

## The empty-tables fact (the single most important finding)

```
Experiments = 0            ExperimentRuns = 0        ExitCalibrations = 0
WalkForwardJobs = 0        WalkForwardWindowResults = 0
ResearchPipelines = 0      ResearchPipelineSteps = 0
TradeExcursions = 0        StrategyCellParks = 0     Datasets = 0   ConfigSets = 0
(fed: ReferenceScales = 84, VenueSessions = 5163, StrategyConfigs = 9, RiskProfiles = 4, AddOnPacks = 3)
```

Three iterations built a measurement machine that has never measured anything. AUDIT F11
("research surfaces got no food") is still true today, post-delivery.

## Research-readiness inventory (for the next mega plan)

**READY:** tape venue (verified truthful + deterministic), 14 symbols × 6 TFs × 1 year
(2025-07-04→2026-07-05) of tape data, ResearchCli with machine verdicts, 11 playbooks,
walk-forward with honest test leg, Experiment/ExperimentRun tables shaped exactly for a scoring
loop (`ScoreJson`, `FoldRole`, `VariantLabel`), governor + 4 risk profiles + prop-rule sets,
EquitySnapshots for FTMO simulation.

**NOT READY (must fix before research):**
1. F18 — compare-both spawns no cTrader child → no parity guard possible.
2. F19 — false warning poisons every tape run's status (contaminates any status-based gate).
3. No scoring verb — `ScoreJson` has no writer; SetupScore must be specified + implemented.
4. Data-quality sentinel not market-hours aware (24.9k "gaps" ≈ weekends) → can't gate windows.
5. `/api/health` referenced by docs does not exist (SPA fallback answers); port drift (5134).
6. Playbooks never executed — B1's "run all 11" validation still pending.

**Verdict: one readiness stage (R0) closes items 1–5; then the machine can finally eat.**
Next mega plan: `docs/iterations/iter-alpha-loop/PLAN.md`.
