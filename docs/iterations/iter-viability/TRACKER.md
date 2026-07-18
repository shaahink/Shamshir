# iter-viability — TRACKER (resume here)

**Branch:** `iter/viability` | **Plan:** `PLAN.md` (V0–V7) | **Rationale:** `../../QUANT-REVIEW-RESPONSE-2026-07.md`

**Read order for a fresh session:** this file → `PLAN.md` (incl. §6 owner-asks map) →
`../../QUANT-REVIEW-RESPONSE-2026-07.md` → `../iter-structural-edge/LEDGER.md` (tail: S1 close +
owner-gate ruling) → `../iter-structural-edge/RESEARCH.md` (F64–F68) → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL (owner call 2026-07-16).

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **Session 7 (2026-07-18): GV2 CLOSED.** Owner accepted the whole-bank OOS negative → the
9-strategy bank **PARKS** (park-never-delete; configs + experiment `4F56B1AE` retained untouched, no
family earns a live seat). **F85** written (whole-bank pure-OOS negative = structural-edge G1 at
scale). Program pivots to **V4 as one decisive, well-powered shot: the session/time-of-day family**
(owner delegated the pick → agent chose it: fewest knobs ⇒ highest power, clock-keyed ⇒ maximally
different from the dead indicator bank, M15 execution now honest). **V4 pre-registered** (LEDGER
Session 7): 4 net-new strategies `london-orb`/`ny-open-drive`/`asia-range`/`day-of-week` × 10 FX
symbols (7 majors + 3 JPY, no metals/crypto) × {M15,H1} = **80 cells**, 2019–2023 IS window, raw
per-bar dukascopy spread, `maxDdEnabled` off, MDE ≈ $5–6/pos family-pooled (≈0.01R). **M15 data
CONFIRMED present** (dukascopy 2.17M M15 bars, all 10 FX symbols 2019–2023, per-bar spread ~100%).
Implementation delivered as **`V4-SESSION-TOD-PLAN.md`** (5 phases, clone `session-breakout`; windows
FROZEN by the pre-reg). **Stop rule BINDING:** family refuted under D5′ ⇒ clean program stop. Docs
uncommitted at session end (offer-to-commit pending owner).

next: (1) **Lane D — OpenCode agent** builds the 4 strategies per `V4-SESSION-TOD-PLAN.md` (Phases
0–4), merge at gate (golden 63/63 byte-identical + Unit/Integration/Sim green + determinism probe).
(2) **Lane R** (after merge): clone `census_driver.py` → run the 80-cell V4 census + harvest →
**GV4 owner call**. (3) **GV0 still open** — 1-step vs standard; no `ftmo-1step` ruleset authored.
(4) L0 live compare-both = standing debt, next cTrader session. Findings continue at **F86**.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| V0 | Challenge-model truth — FTMO terms verified, sv2 metrics corrected, account type signed; Gate GV0 (OWNER) | IN PROGRESS — evidence complete, awaiting GV0 owner signature on account type | 131b4d8 | LEDGER.md Session 1: rule-diff table, F73–F75, gates paste |
| V1 | Backfill + importer — 2019–2024 bid/ask tape, overlap-validated, era-holdout flagged; Gate GV1 | DONE — evidence complete (M1 count appended on completion) | Session 2 | LEDGER.md Session 2: reconciliation table, coverage log, guard pastes, F76/F77 |
| V2 | Frozen-bank pure OOS census — F68 ranking tested on 6 years; Gate GV2 (OWNER) | **DONE — GV2 CLOSED (owner accepted the whole-bank negative → bank PARKS, F85)** | Session 6 (S7 ruling) | evidence/v2-harvest.md (5 GV2 tables); LEDGER.md Sessions 3–7 |
| V3 | Exit lab — excursion recorder + offline replayer, paired verdicts all families; Gate GV3 | TODO (deprioritised — GV2 sent the program to V4 first) | | |
| V4 | New material — session/time-of-day (THE decisive shot), cross-sectional FX, indices, gap family + absorbed S2/S3 analyses; Gate GV4 (OWNER) | IN PROGRESS — V4 session/time-of-day pre-registered (LEDGER S7); impl plan `V4-SESSION-TOD-PLAN.md` delivered to Lane D; awaiting strategy build + census | Session 7 | LEDGER.md Session 7 pre-reg; V4-SESSION-TOD-PLAN.md |
| V5 | Gate upgrade — bootstrap + MDE + EB shrinkage + stitched WF tooling; Gate GV5 | TODO | | |
| V6 | Control layer — intraday equity envelope, portfolio −3% stop, challenge-state risk policy MC; Gate GV6 | TODO | | |
| V7 | Era-holdout → EMBARGO-2 → portfolio → audit; Gates GV7a/b (OWNER) | TODO | | |
