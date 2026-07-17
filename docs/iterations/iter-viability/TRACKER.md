# iter-viability — TRACKER (resume here)

**Branch:** `iter/viability` | **Plan:** `PLAN.md` (V0–V7) | **Rationale:** `../../QUANT-REVIEW-RESPONSE-2026-07.md`

**Read order for a fresh session:** this file → `PLAN.md` (incl. §6 owner-asks map) →
`../../QUANT-REVIEW-RESPONSE-2026-07.md` → `../iter-structural-edge/LEDGER.md` (tail: S1 close +
owner-gate ruling) → `../iter-structural-edge/RESEARCH.md` (F64–F68) → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL (owner call 2026-07-16).

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **Session 3 (2026-07-17): V2 pre-registered; pilot fired TWICE → F78 + F79 found and
FIXED; census batch RUNNING.** Pre-reg committed before run 1 (252 cells = 075D5240 replica on
2019–2023 duka tape; spread = raw per-bar + 1.5×/2× post-hoc stress; MDE $30–41/t at 6× n;
H-MR/H-RANK/H-BANK pinned; journal-prune disk deviation, owner "Both"). Pilot exposed **F78**
(governor cooling-off deadlock: streak counter only reset on wins, unreachable during the
pause → permanent lockout after any 5-loss streak) and **F79** ("daily" DD = cumulative-from-
initial in InitialBalance mode: protection latched below −4.75% forever + static $95k gate
floor, under-protective above HWM). BOTH fixed + test-pinned (bug-pinning tests rewritten to
verified FTMO single-day semantics); gates 780/156/144 green. **The 2025 census's 8× monthly
trade decay is these two bugs** — F68/F64/F75/R3/R4 all carry the suppression overlay (re-read
at GV2; 2025 census re-run under fixed engine proposed, embargo-clean). Pilot #3 clean: MR/EUR
8→113 trades, TB/XAU 15→460, uniform years. **Batch: experiment `95F32D08-BAFE-415E-9492-
28BD9B4CD89B`, 250 cells, detached (survives session close), ~8–9 h, log
`C:\ShamshirData\logs\v2-census.log`.** GV0: 1-step Swing doesn't exist (verified); rec stands
Swing $100k 2-step; **GV0 still open**.
next: (1) Harvest batch → verdict tables (era × family + D5′ legs + spread stress) → GV2 owner
gate (incl. F78/F79 blast-radius re-read + 2025-census-rerun decision). Resume if needed:
`python tools/research/census_driver.py --experiment 95F32D08-BAFE-415E-9492-28BD9B4CD89B
--parallel 3 --prune-journal`. (2) GV0 signature. (3) L0 live compare-both smoke = standing
debt, next cTrader session. Findings continue at **F80**.

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
