# iter-viability — TRACKER (resume here)

**Branch:** `iter/viability` | **Plan:** `PLAN.md` (V0–V7) | **Rationale:** `../../QUANT-REVIEW-RESPONSE-2026-07.md`

**Read order for a fresh session:** this file → `PLAN.md` (incl. §6 owner-asks map) →
`../../QUANT-REVIEW-RESPONSE-2026-07.md` → `../iter-structural-edge/LEDGER.md` (tail: S1 close +
owner-gate ruling) → `../iter-structural-edge/RESEARCH.md` (F64–F68) → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL (owner call 2026-07-16).

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **Sessions 1+2 COMPLETE (2026-07-16/17, one conversation).** S1 = V0 rule-truth (F73 reset
2h early fixed everywhere; simulator corrected to verified FTMO semantics; sv2 + P(bust)/E[time];
F74 untimed inversion: 2/4 R4 candidates viable-but-slow; F75 regime null at MDE) — GV0 owner
signature still pending (rec: Swing $100k 2-step). S2 = V1 backfill DONE: Dukascopy archive
99.976% (75,078/75,096 files, durable at `C:\ShamshirData\backfill\` + read-only snapshot);
overlap reconciliation PASS (offset 0 everywhere, FX deltas sub-pip; **F76** half-spread feed
offset; **F77** venue TypicalSpread = 1-pip placeholder → metals/crypto tape costs were
optimistic); 2019–24 imported: 2.87M M15/H1/H4/D1 bars with per-bar spread (99.99%), M1 (~32M)
imported after disk freed to 11.9 GB — 2019–24 runs fill on fine bars like the census. Guards
pasted: era-holdout 0, embargo 0. **GV1 evidence complete.** L1 delivered concurrently: F26 +
F28 fixed, start-record race fixed (all test-pinned); V5 block-bootstrap + MDE tool selftest
PASS. Gates: build 0 err · Unit 778/0/6 · Int 156/0/0 · Sim 144/0/0. Owner ops directions
logged (live/research separation, Docker limits, trace logs, Telegram, dashboard split,
multi-prop replication). Findings continue at **F78**.
next: (1) **GV0 owner signature** — account type. (2) **Session 3 = V2 pre-registration + the
frozen-bank pure-OOS census 2019–2023** (2024 untouched): MUST pre-register the spread policy
(F77 — venue floor values are degenerate; recommend per-bar Dukascopy spread, stated
raw-vs-floored sensitivity), MDE line via `tools/research/block_bootstrap.py`, D13 one-cell-
per-run, sv2 scoring. **L0 live compare-both smoke = standing debt, next cTrader session.**

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| V0 | Challenge-model truth — FTMO terms verified, sv2 metrics corrected, account type signed; Gate GV0 (OWNER) | IN PROGRESS — evidence complete, awaiting GV0 owner signature on account type | 131b4d8 | LEDGER.md Session 1: rule-diff table, F73–F75, gates paste |
| V1 | Backfill + importer — 2019–2024 bid/ask tape, overlap-validated, era-holdout flagged; Gate GV1 | DONE — evidence complete (M1 count appended on completion) | Session 2 | LEDGER.md Session 2: reconciliation table, coverage log, guard pastes, F76/F77 |
| V2 | Frozen-bank pure OOS census — F68 ranking tested on 6 years; Gate GV2 (OWNER) | TODO | | |
| V3 | Exit lab — excursion recorder + offline replayer, paired verdicts all families; Gate GV3 | TODO | | |
| V4 | New material — session/time-of-day, cross-sectional FX, indices, gap family + absorbed S2/S3 analyses; Gate GV4 (OWNER) | TODO | | |
| V5 | Gate upgrade — bootstrap + MDE + EB shrinkage + stitched WF tooling; Gate GV5 | TODO | | |
| V6 | Control layer — intraday equity envelope, portfolio −3% stop, challenge-state risk policy MC; Gate GV6 | TODO | | |
| V7 | Era-holdout → EMBARGO-2 → portfolio → audit; Gates GV7a/b (OWNER) | TODO | | |
