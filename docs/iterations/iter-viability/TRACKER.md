# iter-viability — TRACKER (resume here)

**Branch:** `iter/viability` | **Plan:** `PLAN.md` (V0–V7) | **Rationale:** `../../QUANT-REVIEW-RESPONSE-2026-07.md`

**Read order for a fresh session:** this file → `PLAN.md` (incl. §6 owner-asks map) →
`../../QUANT-REVIEW-RESPONSE-2026-07.md` → `../iter-structural-edge/LEDGER.md` (tail: S1 close +
owner-gate ruling) → `../iter-structural-edge/RESEARCH.md` (F64–F68) → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL (owner call 2026-07-16).

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **Session 6 (2026-07-18): V2 frozen-bank OOS census COMPLETE (252/252) + HARVESTED — CLEAN
ROBUST NEGATIVE.** `--rescore-nulls` found **0 infra-nulls** (the 14 nulls are legitimate 0-trade
D3 cells, not F80 casualties); `v2_harvest.py` → **`evidence/v2-harvest.md`** (all 5 GV2
deliverables). **§0 integrity gate PASSED**: only 1/238 cells stopped early
(`mean-reversion/XAGUSD/H4` at 2.30% DD = signal exhaustion, NOT the floor), **0 pinned at the ≥9%
floor** (vs 29% truncation gate-ON) ⇒ F82 genuinely off, **batch VALID** not void. Guards:
era-holdout 0, EMBARGO-2 0. F70 split factor 1.0000 (no PartialTp ⇒ fold is a no-op).
**H-BANK REFUTED** — bank −$20.06/position (n=101,572), 95% CI [−23.23, −16.97].
**H-MR REFUTED** — mean-reversion (frozen census's ONLY winner, +$19.6/t) → −$29.0/pos OOS, CI <0.
**H-RANK not detectable** — Spearman ρ=+0.10, CI [−0.35, +0.60] ⇒ frozen ranking has NO OOS
predictive power (MR rank 1→7). All 9 families negative ⇒ 8/9 CI-excludes-0 = **PARK**;
session-breakout indistinguishable-from-0 (−6.4 [−13,+0], firms to −25.1 at 1.5× spread). Spread
stress: **nothing cost-fragile** (all already negative: bank −20→−35→−51 at raw/1.5×/2×). Leg-4
jackknife: all 9 sign-stable across 60 months. The crypto per-family positives (ema-align +39.7,
session-breakout +41.5) are **F77 1-pip-cost artifacts** inside already-negative families. Live
experiment `4F56B1AE`; `95F32D08` (gate-ON) + `CCA30637` (F83-tainted) RETIRED park-never-delete.
**The 9-strategy bank has no structural edge — structural-edge G1 confirmed at the whole-bank
level.** Full F73–F84 detail (F78/F79 engine bugs, F80 race, F82 gate, F83 idempotency, F84 disk,
S5 parallelism) in LEDGER Sessions 3–5.

next (owner-gated — NO agent work queued; census + harvest fully done):
(1) **GV2 owner decision** — what a dead bank means for the program: retire families / form a new
hypothesis for V3+ / move to V6 account-policy. The harvest's per-family PARK recommendations are
mechanical INPUT, not the decision. (2) **GV0 STILL OPEN** — owner leans **1-step / standard,
options open**; Swing≡Standard for backtests (news gate is dead code); NO `ftmo-1step` ruleset
exists — authoring it (3% daily) is clean independent V0 work. (3) L0 live compare-both smoke =
standing debt, next cTrader session. Findings continue at **F85**.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| V0 | Challenge-model truth — FTMO terms verified, sv2 metrics corrected, account type signed; Gate GV0 (OWNER) | IN PROGRESS — evidence complete, awaiting GV0 owner signature on account type | 131b4d8 | LEDGER.md Session 1: rule-diff table, F73–F75, gates paste |
| V1 | Backfill + importer — 2019–2024 bid/ask tape, overlap-validated, era-holdout flagged; Gate GV1 | DONE — evidence complete (M1 count appended on completion) | Session 2 | LEDGER.md Session 2: reconciliation table, coverage log, guard pastes, F76/F77 |
| V2 | Frozen-bank pure OOS census — F68 ranking tested on 6 years; Gate GV2 (OWNER) | IN PROGRESS — census 252/252 COMPLETE + harvested (clean robust negative, §0 gate passed), awaiting GV2 owner decision | Session 6 | evidence/v2-harvest.md (5 GV2 tables); LEDGER.md Sessions 3–6 |
| V3 | Exit lab — excursion recorder + offline replayer, paired verdicts all families; Gate GV3 | TODO | | |
| V4 | New material — session/time-of-day, cross-sectional FX, indices, gap family + absorbed S2/S3 analyses; Gate GV4 (OWNER) | TODO | | |
| V5 | Gate upgrade — bootstrap + MDE + EB shrinkage + stitched WF tooling; Gate GV5 | TODO | | |
| V6 | Control layer — intraday equity envelope, portfolio −3% stop, challenge-state risk policy MC; Gate GV6 | TODO | | |
| V7 | Era-holdout → EMBARGO-2 → portfolio → audit; Gates GV7a/b (OWNER) | TODO | | |
