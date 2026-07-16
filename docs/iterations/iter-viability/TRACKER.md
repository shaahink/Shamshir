# iter-viability — TRACKER (resume here)

**Branch:** `iter/viability` | **Plan:** `PLAN.md` (V0–V7) | **Rationale:** `../../QUANT-REVIEW-RESPONSE-2026-07.md`

**Read order for a fresh session:** this file → `PLAN.md` (incl. §6 owner-asks map) →
`../../QUANT-REVIEW-RESPONSE-2026-07.md` → `../iter-structural-edge/LEDGER.md` (tail: S1 close +
owner-gate ruling) → `../iter-structural-edge/RESEARCH.md` (F64–F68) → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL (owner call 2026-07-16).

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **Session 1 (V0 + regime analysis) evidence-COMPLETE (2026-07-16).** FTMO terms verified
against the published contract (LEDGER.md rule-diff, 14 rows cited). **F73**: daily reset was 2 h
early (22:00 Prague) — configs fixed to 00:00 Prague, live-DB rulesets upserted (seeder is
one-shot), display bucketing aligned. Simulator corrected: balance-referenced daily floor,
min(start,close)-equity breach checks, OPENED-day trading-day counting. sv2 extended:
P(bust-before-target) + E[time-to-target] first-class (30d PassRate kept as velocity index;
composite unchanged). **F74**: untimed rules invert R4 — 2/4 candidates viable-but-slow (0 busts
in ANY anchored window; Phase 1 ≈ 4 mo median, Ph1+2 ≈ 6–7 mo at 1×), 2/4 never resolve. **F75**:
regime conditioning null-with-reason at MDE ($123–165/t ≈ 0.3R); ER-regime mix shift H1→H2 real;
RV20-Low × contrarian +0.17R split = V4e hypothesis for backfilled data. Zero new runs; embargo +
era-holdout untouched. Gates: build 0 err · Unit 773/0/6 · Int 155/0/0 · Sim 144/0/0.
next: **GV0 OWNER SIGNATURE — account type (recommendation: FTMO Swing, $100k, 2-step)**, then
Session 2 = V1 backfill (Dukascopy 2019–24 bid/ask M1 importer, overlap-2025 validation, 2024
era-holdout DB flag). Lane D may start the V1 importer concurrently (D9/PLAN §8). **L0 live
compare-both smoke = standing debt, next cTrader session.** Findings continue at **F76**.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| V0 | Challenge-model truth — FTMO terms verified, sv2 metrics corrected, account type signed; Gate GV0 (OWNER) | IN PROGRESS — evidence complete, awaiting GV0 owner signature on account type | Session 1 | LEDGER.md Session 1: rule-diff table, F73–F75, gates paste |
| V1 | Backfill + importer — 2019–2024 bid/ask tape, overlap-validated, era-holdout flagged; Gate GV1 | TODO | | |
| V2 | Frozen-bank pure OOS census — F68 ranking tested on 6 years; Gate GV2 (OWNER) | TODO | | |
| V3 | Exit lab — excursion recorder + offline replayer, paired verdicts all families; Gate GV3 | TODO | | |
| V4 | New material — session/time-of-day, cross-sectional FX, indices, gap family + absorbed S2/S3 analyses; Gate GV4 (OWNER) | TODO | | |
| V5 | Gate upgrade — bootstrap + MDE + EB shrinkage + stitched WF tooling; Gate GV5 | TODO | | |
| V6 | Control layer — intraday equity envelope, portfolio −3% stop, challenge-state risk policy MC; Gate GV6 | TODO | | |
| V7 | Era-holdout → EMBARGO-2 → portfolio → audit; Gates GV7a/b (OWNER) | TODO | | |
