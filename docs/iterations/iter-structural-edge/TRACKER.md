# iter-structural-edge — TRACKER (resume here)

**Branch:** `iter/structural-edge` | **Plan:** `PLAN.md` (S0–S7) | **Conductor plan:** `conductor-structural-edge.plan.json` (repo root)

**Read order for a fresh session:** this file → `PLAN.md` → `RESEARCH.md` → `LEDGER.md` (tail) →
`../iter-alpha-loop/HANDOVER.md` → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL for now (owner call 2026-07-16);
the conductor plan stays valid — hand stages back to conductor whenever the owner wants.

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **S0 DONE (2026-07-16, manual session) — Gate G0 PASSED, all three legs pasted in LEDGER.md.**
sv2 scoring live (F63 executed): `ChallengeSimulationService.ComputeSurvivalAsync` + SetupScoreService
version bump sv1→sv2; placeholder deleted; sv1 rows untouched (D4). Research tools committed:
`tools/research/{split_half,quant_research}.py` + `research persistence` verb (+ endpoint + service).
F64 reproduced from the live DB EXACTLY ($0 delta): 38/74, $116,518 → −$880, 9/38 (24%).
gate: build 0err/5warn · Unit 767/0/6 · Integration 153/0/0 · Sim-fast 144/0/0 (new baseline; +1/+5/+0
tests vs PLAN G0 baseline). No BacktestRuns rows created — EMBARGO-2 untouched.
next: **S1 exit-layer factorial** (OWNER GATE after S1, not before it). Start with `trend-breakout`;
pre-register ≤8 variants in LEDGER.md BEFORE running anything scored (D5); one-cell-per-run (D13),
sv2-scored, family-level evaluation. Read PLAN.md §3 S1 + §0 D5 first.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| S0 | Truth infra — sv2 scoring + research tools + LEDGER/TRACKER; Gate G0 | DONE | 27b22b2 | LEDGER.md S0 entry: G0 legs 1–3 pasted (F64 exact reproduction + suite counts); tools/research/; SetupScoreSv2Tests + SplitHalfPersistenceTests + DailyCapBreach_Dominates_TargetHit |
| S1 | Exit-layer factorial — per-family component verdict (D5 legs); Gate G1 | TODO | | |
| S2 | Entry noise floor + regime gating; Gate G2 | TODO | | |
| S3 | Cost-aware knobs — cost-drag table; Gate G3 | TODO | | |
| S4 | Re-census under winning config — pooled expR + WF; Gate G4 | TODO | | |
| S5 | EMBARGO-2 dress rehearsal — candidate cards (≥45 days first); Gate G5 | TODO | | |
| S6 | Portfolio phase (conditional) — aggregate challenge + attribution; Gate G6 | TODO | | |
| S7 | Final audit + close — audit vs PLAN.md + bugfix queue + RESUME | TODO | | |
