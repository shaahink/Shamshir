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
**Session 4 (2026-07-17, batch mid-flight):** audited the running batch at 131/250. **F80**
(finalize race) had nulled 4 good cells and the 90m timeout had abandoned 3 more (S3.1) — **all 7
recovered by re-scoring, none re-run** (all were `completed` with real trades). Census clean: no
duplicate rows, all 14 nulls are the legitimate D3 floor. **H1's 344m warm-up proved one-time,
not per-symbol** (BTCUSD/H1 first touch ran at full speed) → batch ETA **~6.5 h**, left running,
no intervention. Driver hardened: scores only once the persisted row is terminal, warns instead
of abandoning, new `--rescore-nulls` recovery mode.

**F82 (session 4) — V2's headline risk, RESOLVED by owner call → re-running.**
`PreTradeGate.cs:174` (`WorstCaseDDWouldBreachOverall`) is ABSORBING: an account parked within one
worst-case of the $90k floor rejects every entry forever (no trade ⇒ no recovery ⇒ no trade),
silently — `completed`, no error, no warning. **35/122 trading cells (29%) stopped years early**,
34 pinned at ≥9% DD (median 9.82%); `trend-breakout` lost 12/15 cells by median 2021-06.
Truncation selects the LOSING cells ⇒ later eras survivorship-biased upward. The gate is
defensible; the **census design** (5 y, one $100k account, no reset) was the defect.
**Owner chose (a) research mode** → `maxDdEnabled: false` per run (Amendment 7). NO engine change:
the toggle already existed and is plumbed (`StartRunRequest` → `RunsController:162` →
`RunConfigAssembler:193`). **Pilot on two PROVABLY-DEAD cells PASSED**: trend-breakout/NZDUSD/H4
38 trades dead 2019-06 → **477 trades, last 2023-12-29**, maxDD 21.4% (research mode = account is
a measuring device, not a challenge); wall unchanged 2.5m.

**F83 (session 4) — the near-miss that nearly faked the entire re-run.** The app's idempotency
store is **IN-MEMORY, process-lifetime**, so a bare `v2-census-<cell>` key REATTACHED Amendment 7's
pilot to the ORIGINAL gate-ON runs (`ca332ae7`, `a19fec05`) — `maxDdEnabled` never applied.
Unnoticed, all 252 cells would have reattached and the driver would have scored a byte-identical
census into the new experiment and printed `BATCH DONE`: F82 "fixed" on paper, unchanged in fact,
no error anywhere. Keys now namespaced by experiment (`v2-census-{exp[:8]}-...`). **Caught ONLY
because the pilot was re-pointed at cells that had to change, with a falsifiable gate** — two
healthy cells would have sailed through, exactly as the original pilot did while 29% of the census
died. Doctrine: *a pilot that cannot fail the hypothesis proves nothing.*

Experiments: `95F32D08` RETIRED (gate-ON) · `CCA30637` RETIRED (F83-contaminated) · **live =
`4F56B1AE-7269-41CC-8D6C-60E920742EE7`** (park-never-delete throughout). sv2 composites are NOT
comparable across experiments (DD component moves mechanically once the floor is off).
`v2_harvest.py` built + validated (all 5 deliverables, §0 F82 integrity section, partial banner);
**F81** — `block_bootstrap.py` was never importable (unguarded module-level `parse_args`), fixed.

next: (1) **252-cell research-mode census running under `4F56B1AE`** (~9 h, detached, survives
session close), log `C:\ShamshirData\logs\v2-census-rm.log`. Resume/monitor:
`python tools/research/census_driver.py --experiment 4F56B1AE-7269-41CC-8D6C-60E920742EE7
--parallel 3 --prune-journal`. (2) On `BATCH DONE`: `--rescore-nulls` sweep (F80 stragglers),
then `python tools/research/v2_harvest.py --experiment 4F56B1AE-...` → the 5 GV2 tables. Expect
§0 to report ~0 truncated cells — if it does NOT, F82 is not actually off and the batch is void.
(3) GV2 owner gate (incl. F78/F79 blast-radius re-read + 2025-census-rerun decision). (4) **GV0
signature — open 4 sessions, blocking V0** (rec: Swing $100k 2-step). (5) L0 live compare-both
smoke = standing debt, next cTrader session. Findings continue at **F84**.

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
