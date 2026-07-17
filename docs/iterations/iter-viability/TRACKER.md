# iter-viability — TRACKER (resume here)

**Branch:** `iter/viability` | **Plan:** `PLAN.md` (V0–V7) | **Rationale:** `../../QUANT-REVIEW-RESPONSE-2026-07.md`

**Read order for a fresh session:** this file → `PLAN.md` (incl. §6 owner-asks map) →
`../../QUANT-REVIEW-RESPONSE-2026-07.md` → `../iter-structural-edge/LEDGER.md` (tail: S1 close +
owner-gate ruling) → `../iter-structural-edge/RESEARCH.md` (F64–F68) → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL (owner call 2026-07-16).

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **Session 3 (2026-07-17): V2 PRE-REGISTERED + census launched.** Pre-reg in LEDGER.md
Session 3 (committed BEFORE run 1): 252 cells (9 strat × 14 sym × {H1,H4}, parked cells
included — parks bind candidacy not research), window 2019-01-01→2023-12-31T00:00 (era-holdout
clean by construction), config = 075D5240 exact replica on dukascopy tape (M1 fine bars,
99.99% per-bar spread). Spread policy: raw per-bar duka primary + 1.5×/2× post-hoc analytic
stress (F77 floor values degenerate; FTMO published spreads not citable/static). MDE (D1,
block_bootstrap, blinded): per-family $30–41/t ≈ 0.06–0.09R at 6× n — powered for the 0.10R
question; bank-pooled $13/t caveat stated. H-MR / H-RANK / H-BANK verdict rules pinned; frozen
census vectors pasted ($/t AND expR — they disagree on rsi-div, F70). **Disk finding:** V2
journal would be 10–12 GB vs 5.1 GB free → pre-registered deviation: `census_driver.py
--prune-journal` (delete Journal per run after completed+scored; ALL result records kept);
owner decision "Both" (also frees disk). GV0: owner queried 1-step Swing — doesn't exist
(Swing = 2-step only, re-verified); rec stands Swing $100k 2-step; **GV0 still open**.
next: (1) Census batch to completion (resume: `python tools/research/census_driver.py
--experiment <id in LEDGER> --parallel 3 --prune-journal`; H4 tranche first; disk guard at
1.5 GB). (2) Verdict tables + spread stress + GV2 owner gate. (3) GV0 signature. (4) L0 live
compare-both smoke = standing debt, next cTrader session. Findings continue at **F78**.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| V0 | Challenge-model truth — FTMO terms verified, sv2 metrics corrected, account type signed; Gate GV0 (OWNER) | IN PROGRESS — evidence complete, awaiting GV0 owner signature on account type | 131b4d8 | LEDGER.md Session 1: rule-diff table, F73–F75, gates paste |
| V1 | Backfill + importer — 2019–2024 bid/ask tape, overlap-validated, era-holdout flagged; Gate GV1 | DONE — evidence complete (M1 count appended on completion) | Session 2 | LEDGER.md Session 2: reconciliation table, coverage log, guard pastes, F76/F77 |
| V2 | Frozen-bank pure OOS census — F68 ranking tested on 6 years; Gate GV2 (OWNER) | IN PROGRESS — pre-registered + launched 2026-07-17 | | LEDGER.md Session 3: pre-reg, MDE table, spread policy, disk deviation |
| V3 | Exit lab — excursion recorder + offline replayer, paired verdicts all families; Gate GV3 | TODO | | |
| V4 | New material — session/time-of-day, cross-sectional FX, indices, gap family + absorbed S2/S3 analyses; Gate GV4 (OWNER) | TODO | | |
| V5 | Gate upgrade — bootstrap + MDE + EB shrinkage + stitched WF tooling; Gate GV5 | TODO | | |
| V6 | Control layer — intraday equity envelope, portfolio −3% stop, challenge-state risk policy MC; Gate GV6 | TODO | | |
| V7 | Era-holdout → EMBARGO-2 → portfolio → audit; Gates GV7a/b (OWNER) | TODO | | |
