# iter-viability — TRACKER (resume here)

**Branch:** `iter/viability` | **Plan:** `PLAN.md` (V0–V7) | **Rationale:** `../../QUANT-REVIEW-RESPONSE-2026-07.md`

**Read order for a fresh session:** this file → `PLAN.md` (incl. §6 owner-asks map) →
`../../QUANT-REVIEW-RESPONSE-2026-07.md` → `../iter-structural-edge/LEDGER.md` (tail: S1 close +
owner-gate ruling) → `../iter-structural-edge/RESEARCH.md` (F64–F68) → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL (owner call 2026-07-16).

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **Iteration opened at the iter-structural-edge S1 owner gate (2026-07-16).** G1 accepted
(no D5-surviving exit component; R3's 8/8 + v6a = F70 artifact), D7 park ratified, this plan
adopted. All work through the gate merged to `main`; branch `iter/viability` cut from that merge.
Baseline gates: build 0err/5warn · Unit 770/0/6 · Integration 153/0/0 · Sim-fast 144/0/0.
EMBARGO-2 untouched; 2024 era-holdout (D3) in force from V1 onward. Findings continue at **F73**.
next: **Session 1 = V0 + regime analysis.** (a) V0 challenge-model truth: verify FTMO current
terms (time limits / Swing / daily-loss intraday definition + reset / scaling) vs
`config/prop-firms/ftmo-standard.json`; correct config + `ChallengeSimulator`; add P(bust) +
E[time-to-target] to sv2 outputs; owner signs account type [GV0]. (b) Old-S2(b) regime
conditioning, zero new runs: 2×2 family-class × census-half interaction, external regime vars,
block bootstrap. Pre-register both in LEDGER.md here (MDE lines per D1) BEFORE anything scored.
Session 2 = V1 backfill (importer + overlap-year validation).

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| V0 | Challenge-model truth — FTMO terms verified, sv2 metrics corrected, account type signed; Gate GV0 (OWNER) | TODO | | |
| V1 | Backfill + importer — 2019–2024 bid/ask tape, overlap-validated, era-holdout flagged; Gate GV1 | TODO | | |
| V2 | Frozen-bank pure OOS census — F68 ranking tested on 6 years; Gate GV2 (OWNER) | TODO | | |
| V3 | Exit lab — excursion recorder + offline replayer, paired verdicts all families; Gate GV3 | TODO | | |
| V4 | New material — session/time-of-day, cross-sectional FX, indices, gap family + absorbed S2/S3 analyses; Gate GV4 (OWNER) | TODO | | |
| V5 | Gate upgrade — bootstrap + MDE + EB shrinkage + stitched WF tooling; Gate GV5 | TODO | | |
| V6 | Control layer — intraday equity envelope, portfolio −3% stop, challenge-state risk policy MC; Gate GV6 | TODO | | |
| V7 | Era-holdout → EMBARGO-2 → portfolio → audit; Gates GV7a/b (OWNER) | TODO | | |
