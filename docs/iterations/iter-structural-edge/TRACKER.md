# iter-structural-edge — TRACKER (resume here)

**Branch:** `iter/structural-edge` | **Plan:** `PLAN.md` (S0–S7) | **Conductor plan:** `conductor-structural-edge.plan.json` (repo root)

**Read order for a fresh session:** this file → `PLAN.md` → `RESEARCH.md` → `LEDGER.md` (tail) →
`../iter-alpha-loop/HANDOVER.md` → `../../../AGENTS.md`.
Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. Sessions are MANUAL for now (owner call 2026-07-16);
the conductor plan stays valid — hand stages back to conductor whenever the owner wants.

## Handoff  (overwrite this block, ≤12 lines, no history)

last: **S1 CLOSED early-with-reason (2026-07-16) — Gate G1: the exit layer holds NO D5-surviving
component.** Both highest-prior families refuted in dollars: trend-breakout (`862C5D04`, 8 arms ×
12 cells) and ema-alignment (`23DA6546`, 8 × 6) — **R3's 8/8 and its v6a star (+82% expR) both
replicate as the F70 PartialTp row-splitting artifact while LOSING dollars vs control.** No-TP
(F65's direct test, valid post-F71-fix) is strongly value-destroying (−$25.8k / −$10.1k swings).
Remaining families not run: power (quant review: needs ~6.3k trades/arm, families give 100–900) —
they cannot produce a bankable positive. **D7 EXECUTED**: mtf-trend parked family-level (4 cells,
reversible). F69–F72 filed; F71 FIXED in code (+3 unit tests, preservation verified live).
Speed kit shipped: dev-restart.ps1, parallel gates.ps1, exit_factorial_driver.py (--parallel
validated by determinism probe: concurrent tape runs byte-identical), determinism_probe.py.
gate: build 0/5 · Unit 770 · Int 153 · Sim 144. EMBARGO-2 untouched.
next: **OWNER GATE** — (1) accept G1 negative + early stop; (2) S2 as planned (regime conditioning
on EXISTING census trades, no new runs) and/or quant-review Dukascopy backfill for power;
(3) ratify D7 park. LEDGER.md "S1 CLOSE" has the full gate pack.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| S0 | Truth infra — sv2 scoring + research tools + LEDGER/TRACKER; Gate G0 | DONE | 27b22b2 | LEDGER.md S0 entry: G0 legs 1–3 pasted (F64 exact reproduction + suite counts); tools/research/; SetupScoreSv2Tests + SplitHalfPersistenceTests + DailyCapBreach_Dominates_TargetHit |
| S1 | Exit-layer factorial — per-family component verdict (D5 legs); Gate G1 | DONE (early close w/ reason) | | LEDGER.md S1.1 + S1.2 + "S1 CLOSE": trend-breakout & ema-alignment refuted in dollars (F70 artifact), no-TP value-destroying, remaining families unpowered (quant review), D7 park executed; OWNER GATE pending |
| S2 | Entry noise floor + regime gating; Gate G2 | TODO | | |
| S3 | Cost-aware knobs — cost-drag table; Gate G3 | TODO | | |
| S4 | Re-census under winning config — pooled expR + WF; Gate G4 | TODO | | |
| S5 | EMBARGO-2 dress rehearsal — candidate cards (≥45 days first); Gate G5 | TODO | | |
| S6 | Portfolio phase (conditional) — aggregate challenge + attribution; Gate G6 | TODO | | |
| S7 | Final audit + close — audit vs PLAN.md + bugfix queue + RESUME | TODO | | |
