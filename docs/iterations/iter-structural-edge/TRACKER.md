# iter-structural-edge — TRACKER (resume here)

**Branch:** `iter/structural-edge` | **Plan:** `PLAN.md` (S0–S7) | **Conductor plan:** `conductor-structural-edge.plan.json` (repo root)

**Read order for a fresh session:** this file → `PLAN.md` → `RESEARCH.md` → `LEDGER.md` (tail) →
`../iter-alpha-loop/HANDOVER.md` → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL for now (owner call 2026-07-16);
the conductor plan stays valid — hand stages back to conductor whenever the owner wants.

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **OWNER GATE RULED (2026-07-16, LEDGER.md final entry): G1 accepted, early stop accepted,
D7 park ratified, `iter-viability` ADOPTED as successor** (`docs/iterations/iter-viability/PLAN.md`
— drafted by the external quant review, `docs/QUANT-REVIEW-RESPONSE-2026-07.md`). S1's verdict
stands: no D5-surviving exit component (trend-breakout `862C5D04` + ema-alignment `23DA6546`
refuted in dollars; R3's 8/8 and v6a = F70 artifact; no-TP value-destroying; remaining families
unpowered). F71 fixed; speed kit shipped. gate: build 0/5 · Unit 770 · Int 153 · Sim 144.
EMBARGO-2 untouched.
next: **this iteration is CLOSED to new stages** — S2(b) survives as a zero-run analysis inside
iter-viability Session 1; S2(a)/S3 absorbed into V4/V3; S4–S7 superseded by V2/V5/V7 (same
discipline, backfilled data). Resume from `docs/iterations/iter-viability/TRACKER.md` on branch
`iter/viability`. This tracker is historical from here.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| S0 | Truth infra — sv2 scoring + research tools + LEDGER/TRACKER; Gate G0 | DONE | 27b22b2 | LEDGER.md S0 entry: G0 legs 1–3 pasted (F64 exact reproduction + suite counts); tools/research/; SetupScoreSv2Tests + SplitHalfPersistenceTests + DailyCapBreach_Dominates_TargetHit |
| S1 | Exit-layer factorial — per-family component verdict (D5 legs); Gate G1 | DONE (early close w/ reason) | | LEDGER.md S1.1 + S1.2 + "S1 CLOSE": trend-breakout & ema-alignment refuted in dollars (F70 artifact), no-TP value-destroying, remaining families unpowered (quant review), D7 park executed; OWNER GATE pending |
| S2 | Entry noise floor + regime gating; Gate G2 | TODO | | |
| S3 | Cost-aware knobs — cost-drag table; Gate G3 | TODO | | |
| S4 | Re-census under winning config — pooled expR + WF; Gate G4 | TODO | | |
| S5 | EMBARGO-2 dress rehearsal — candidate cards (≥45 days first); Gate G5 | TODO | | |
| S6 | Portfolio phase (conditional) — aggregate challenge + attribution; Gate G6 | TODO | | |
| S7 | Final audit + close — audit vs PLAN.md + bugfix queue + RESUME | TODO | | |
