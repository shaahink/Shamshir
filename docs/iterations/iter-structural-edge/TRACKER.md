# iter-structural-edge — TRACKER (resume here)

**Branch:** `iter/structural-edge` | **Plan:** `PLAN.md` (S0–S7) | **Conductor plan:** `conductor-structural-edge.plan.json` (repo root)

**Read order for a fresh session:** this file → `PLAN.md` → `RESEARCH.md` → `LEDGER.md` (tail) →
`../iter-alpha-loop/HANDOVER.md` → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL for now (owner call 2026-07-16);
the conductor plan stays valid — hand stages back to conductor whenever the owner wants.

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **S1.1 DONE (2026-07-16) — trend-breakout exit factorial, 96/96 runs, experiment `862C5D04`.
VERDICT: no exit variant survives D5** (best dollar sign-consistency 6/12; every variant fails
split-half; WF moot). **F69** census baseline had BE+2.5×ATR trail ON for 7/9 families (RESEARCH.md
corrected). **F70** PartialTp row-splitting inflates expR — R3's 8/8 was that artifact + H1 regime;
family evaluation is position-level DOLLARS from now on. **F71** TakeProfit.Method is a dead knob in
trend-breakout/rsi-divergence/macd-momentum (strategy reads RrMultiple directly) — no-TP arm ran as
trail-only duplicate; fix before re-testing. **F72** control == census exactly (0/12 drift, refactor
behavior-preserving). Full tables + execution record in LEDGER.md S1.1.
gate: no code changed; S0 baseline stands (0/5 · 767 · 153 · 144). EMBARGO-2 untouched (all runs end
2026-05-05).
next: **S1.2** — owner call: fix F71 first (small change + tests, makes no-TP testable everywhere),
or proceed to `ema-alignment` factorial with dollar-based discipline. Then super-trend,
mean-reversion (contrast), mtf-trend + D7 park decision. OWNER GATE after all of S1.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| S0 | Truth infra — sv2 scoring + research tools + LEDGER/TRACKER; Gate G0 | DONE | 27b22b2 | LEDGER.md S0 entry: G0 legs 1–3 pasted (F64 exact reproduction + suite counts); tools/research/; SetupScoreSv2Tests + SplitHalfPersistenceTests + DailyCapBreach_Dominates_TargetHit |
| S1 | Exit-layer factorial — per-family component verdict (D5 legs); Gate G1 | IN PROGRESS | | S1.1 trend-breakout DONE (LEDGER.md: no D5 survivor; F69–F72); ema-alignment / super-trend / mean-reversion / mtf-trend+D7 remain |
| S2 | Entry noise floor + regime gating; Gate G2 | TODO | | |
| S3 | Cost-aware knobs — cost-drag table; Gate G3 | TODO | | |
| S4 | Re-census under winning config — pooled expR + WF; Gate G4 | TODO | | |
| S5 | EMBARGO-2 dress rehearsal — candidate cards (≥45 days first); Gate G5 | TODO | | |
| S6 | Portfolio phase (conditional) — aggregate challenge + attribution; Gate G6 | TODO | | |
| S7 | Final audit + close — audit vs PLAN.md + bugfix queue + RESUME | TODO | | |
