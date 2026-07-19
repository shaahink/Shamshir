# iter-viability — TRACKER — **ITERATION CLOSED at GV4 (2026-07-19, program stop)**

**Branch:** `iter/viability` | **Plan:** `PLAN.md` (V0–V7) | **Rationale:** `../../QUANT-REVIEW-RESPONSE-2026-07.md`

**Read order for a fresh session:** this file → `PLAN.md` (incl. §6 owner-asks map) →
`../../QUANT-REVIEW-RESPONSE-2026-07.md` → `../iter-structural-edge/LEDGER.md` (tail: S1 close +
owner-gate ruling) → `../iter-structural-edge/RESEARCH.md` (F64–F68) → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL (owner call 2026-07-16).

## Handoff  (final — iteration closed, no further sessions)

last: **Session 8 (2026-07-18/19): V4 census COMPLETE → GV4 CLOSED → PROGRAM STOP.** The 80-cell
session/time-of-day census ran clean (80/80 scored, 0 nulls, F82 absent; completeness re-verified
post power-loss, harvest regeneration byte-identical). **H-SESSION REFUTED**: family-pooled
**−$20.01/position** (n=119,670), 95% CI [−22.40, −17.78], MDE@n $3.3 — all four strategies
individually refuted, every era negative, leg-2/leg-4 robust, spread stress only deepens, M15 worse
than H1 everywhere (H-TF answered). Owner **ratified the pre-registered stop rule**: PARK all 4
(park-never-delete, experiment `5D06CE0B` retained), **program clean stop** — V2's whole-bank
negative (F85) + V4's refutation exhaust the honest search on this data/market class. **F86** (ops):
run-finalize metadata unreliable — trust ScoreJson + UpdatedAtUtc, never Experiments.Status /
CompletedAtUtc. Evidence: `evidence/v4-harvest.md`. LEDGER Session 8 is the close-out record.

next: **nothing inside this iteration** — any new plan is drafted from scratch by the owner. Standing
items that outlive the close: off-machine DB backup (owner; the power loss is the argument), GV0
dormant (only matters if something earns a live seat), L0 live compare-both smoke at the next
cTrader session. Findings end at **F86**.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| V0 | Challenge-model truth — FTMO terms verified, sv2 metrics corrected, account type signed; Gate GV0 (OWNER) | IN PROGRESS — evidence complete, awaiting GV0 owner signature on account type | 131b4d8 | LEDGER.md Session 1: rule-diff table, F73–F75, gates paste |
| V1 | Backfill + importer — 2019–2024 bid/ask tape, overlap-validated, era-holdout flagged; Gate GV1 | DONE — evidence complete (M1 count appended on completion) | Session 2 | LEDGER.md Session 2: reconciliation table, coverage log, guard pastes, F76/F77 |
| V2 | Frozen-bank pure OOS census — F68 ranking tested on 6 years; Gate GV2 (OWNER) | **DONE — GV2 CLOSED (owner accepted the whole-bank negative → bank PARKS, F85)** | Session 6 (S7 ruling) | evidence/v2-harvest.md (5 GV2 tables); LEDGER.md Sessions 3–7 |
| V3 | Exit lab — excursion recorder + offline replayer, paired verdicts all families; Gate GV3 | CLOSED UNEXECUTED — program stop at GV4 | | |
| V4 | New material — session/time-of-day (THE decisive shot); Gate GV4 (OWNER) | **DONE — GV4 CLOSED (H-SESSION REFUTED −$20.01/pos CI [−22.40, −17.78] → PARK ×4, PROGRAM STOP per pre-registered stop rule)** | Session 8 | evidence/v4-harvest.md; LEDGER.md Sessions 7 (pre-reg) + 8 (results, ruling, F86) |
| V5 | Gate upgrade — bootstrap + MDE + EB shrinkage + stitched WF tooling; Gate GV5 | CLOSED UNEXECUTED — program stop at GV4 (bootstrap+MDE tooling was delivered early inside V2/V4 harvests) | | |
| V6 | Control layer — intraday equity envelope, portfolio −3% stop, challenge-state risk policy MC; Gate GV6 | CLOSED UNEXECUTED — program stop at GV4 | | |
| V7 | Era-holdout → EMBARGO-2 → portfolio → audit; Gates GV7a/b (OWNER) | CLOSED UNEXECUTED — program stop at GV4 (no survivor to carry to the 2024 era-holdout; holdout remains untouched/clean for any future program) | | |
