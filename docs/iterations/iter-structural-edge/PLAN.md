# iter-structural-edge — Hunt rule-level edges, not cell-level luck

**Status: PROPOSED (owner gate to open).** Written 2026-07-16 from the deep research in
`RESEARCH.md` (F64–F68). Supersedes the portfolio-of-cells direction decided at alpha-loop
close (`iter-alpha-loop/HANDOVER.md` §0 item 3) **on evidence**: the split-half selection test
(F64) is the moral equivalent of that plan's own Phase-0 OOS-honesty gate, runnable today on
recorded data — and it failed. Per that decision's own stop rule ("if they don't hold OOS, the
portfolio is built on sand → stop there"), the portfolio is not abandoned but **demoted to the
conditional final phase (S6)**, gated on a structural edge existing first.

**Read order for a fresh session:** this file → `RESEARCH.md` (the evidence, F64–F68) →
`iter-alpha-loop/HANDOVER.md` (what the machine already proved) → `LEDGER.md` here (append-only,
created at S0).

---

## 0. Decisions (proposed by Claude 2026-07-16; owner ratifies/edits this block before launch)

| # | Decision | Rationale |
|---|---|---|
| D1 | **Unit of search = structural rule × strategy family, pooled across cells.** Cells are instances, never the ranked unit. | F64: per-cell n=20–90/yr cannot distinguish +0.02R from noise; 24% split-half persistence. Rule-level effects pool hundreds of trades (R3's 8/8 pack effect, F65's n=2,865). |
| D2 | **Exit layer first.** S1 isolates which `runner-aggressive` component (BE / ATR-trail / Ride / PartialTp) carries the 8/8 effect, per family. | Two independent evidence lines (F65 MFE-capture 0.42 + R3 8/8) point at one lever. Highest prior in the program. |
| D3 | **EMBARGO-2 = all data after 2026-07-05.** Untouched by every S0–S4 activity; first and only touch at S5. A 60-day window completes ~2026-09-04; S5 shall not run before ≥45 accrued days exist (owner may extend the wait, never shorten below 45). | Same discipline that made R4's negative trustworthy. Auto-sync (X4) keeps it accruing. |
| D4 | **F63 executes as scoring v2 (sv2), not a retro-rescore.** Wire `ChallengeSimulator` into `SetupScoreService` in S0; sv2 is used for everything scored from S4 on. The dead census stays sv1 (per close-out decision 5). | Never run another census ranked by a placeholder. |
| D5 | **Anti-overfit, tightened for rule-search:** every variant pre-registered in the ledger BEFORE running; ≤ 8 variants/session; a structural effect claims survival only if (a) sign-consistent across ≥ 75% of that family's census-scoreable cells AND (b) positive in BOTH split halves at family level AND (c) walk-forward OOS ratio ≥ 0.5 on the top variant. Parks, never deletes. | The search space is smaller than R3's (components, not knob grids) — the bar per claim is higher. |
| D6 | **Velocity stays an aggregation problem** (unchanged from close decision 2). No shorter-TF hunts justified by frequency alone. S6 (portfolio, conditional) is where velocity is solved — atop proven rule-level edges, with joint-tail risk measured, not Pearson averages. | F64's in-sample mirage showed selection+scaling multiplies failure (4→48 failed windows at 2x). |
| D7 | **Family triage is allowed but parks only:** `mtf-trend` (bank-wide −0.22 expR, F68) may be parked at family level if S1's exit fix does not lift it to ≥ 0 pooled expR. Same for `session-breakout` w.r.t. its 37% never-ran rate after S2. | Dead weight burns sessions; parks are reversible. |
| D8 | Tape venue only until S5; parity verdict ≤ 14 days old for anything presented to the owner (inherited D12). F48 stays deferred unless an XAUUSD-class candidate reaches S5 as a finalist. | Unchanged from alpha-loop. |

**Findings ledger continues at F69** (F64–F68 are pre-filed in `RESEARCH.md`).

---

## 1. Objective

Find at least one **rule-level (structural) edge** that survives split-half, cross-cell sign
consistency, walk-forward, and an untouched embargo window — then, and only then, solve
challenge velocity by aggregation (S6). A trustworthy "no structural edge in this bank either"
is an acceptable outcome and ends the program's current bank (see §7 stop rule).

## 2. Phase map

```
S0 truth infra (1 session)        → sv2 scoring + committed research tools     [gate G0]
S1 exit factorial (2–3 sessions)  → which exit component is real, per family   [gate G1, OWNER GATE]
S2 entry/regime gating (1–2)      → noise-floor + regime-conditional families  [gate G2]
S3 cost-aware knobs (1)           → swap/commission structural rules           [gate G3]
S4 re-census under winners (1–2)  → sv2, D13 one-cell-per-run, walk-forward    [gate G4, OWNER GATE]
S5 EMBARGO-2 dress rehearsal (1)  → first touch of post-2026-07-05 data        [gate G5, OWNER GATE]
S6 portfolio phase (conditional, 2–3) → aggregation + risk machinery           [gate G6, OWNER GATE]
S7 final audit + close (1)        → R5-style artifact audit                    
```

Sessions are ceilings, not quotas. Any stage may end early with a null-with-reason.

## 3. Stages

### S0 — Truth infrastructure (1 session)
1. **sv2 scoring (F63 executed):** replace `ComputeFtmoSurvival` with `ChallengeSimulator`-backed
   survival (rolling windows over the run's real daily equity, FTMO-standard semantics). Version
   the score (`sv2`); sv1 rows untouched. Unit tests pin: a run whose equity path passes 0/N
   windows scores 0; N/N scores 1; daily-cap breach dominates target-hit.
2. **Commit research tools:** port `quant_research.py` + `split_half.py` (scratchpad, this
   session) to `tools/research/`, parameterized by experiment id and split date; add a
   `research persistence` CLI verb that prints the F64 table for any experiment. Outputs must
   reproduce `RESEARCH.md` §1 numbers from the live DB.
3. Create `LEDGER.md` + `TRACKER.md` here (alpha-loop format).

**Gate G0 (machine-checkable):** sv2 tests green; `research persistence --experiment 075D5240
--split 2025-12-03` reproduces the pasted F64 numbers (±$1 on sums); fast suites green
(baseline: Unit 766/0/6 · Integration 148/0/0 · Sim-fast 144/0/0).

### S1 — Exit-layer factorial (2–3 sessions) [OWNER GATE after]
**Hypothesis (pre-registered):** the R3 8/8 `runner-aggressive` improvement is carried by a
subset of its components; letting winners run (trail/Ride) repairs the F65 truncation on trend
families; the same components may *hurt* mean-reversion (its edge is short holds, 5.2h median).

Per session, per family (start: `trend-breakout`, then `ema-alignment`, `super-trend`,
`mean-reversion` as the contrast family):
1. Pre-register in the ledger: variants from {BE-only, trail-only(AtrMultiple), trail+Ride,
   partial-only, full runner-aggressive, **no-TP pure trail**} — ≤ 8 per session including any
   controls; every census-scoreable cell of that family; census window; tape venue; default risk.
2. Run one-cell-per-run (D13), sv2-scored, same experiment-row discipline as R1'.
3. Evaluate at FAMILY level: pooled expR delta vs baseline, sign consistency across cells,
   split-half both-halves-positive (D5), MFE-capture and giveback deltas (the F65 metrics must
   actually move — an expR gain with unchanged MFE capture is suspicious and gets investigated,
   not banked).
4. Walk-forward (6-fold, existing machinery) the single best variant per family.

**Gate G1:** for each family, a ledger verdict: which component(s) survive D5, with the pasted
family-level table (pooled expR, per-cell sign count, split-half halves, WF OOS ratio). A
survival claim without all three D5 legs is a plan violation. `mtf-trend` park decision (D7)
executed and recorded either way.

### S2 — Entry noise floor + regime gating (1–2 sessions)
**Hypotheses (pre-registered):** (a) enabling the existing `SpreadVolNoTradeFilter` and/or
session-window filters cuts the never-ran (<0.3R MFE) rate materially without cutting pooled
expR; (b) the H1→H2 bank-wide regime shift (38→13 positive cells, F64 caveat) is predictable by
a *pre-registered* regime variable (existing regime detector's trend/range classification) —
i.e., trend families' pooled expR conditional on regime label differs from unconditional.
Test (b) as a **conditioning analysis on existing census trades first** (no new runs needed);
only if the conditional split is large does it earn run-budget as a filter variant.
**Gate G2:** never-ran rate and pooled expR deltas pasted per family; regime-conditioning table
pasted; any surviving filter meets D5 in full.

### S3 — Cost-aware knobs (1 session, may merge into S1/S2 if effects are small)
Pre-registered: swap-aware hold-cap/flatten for multi-day families (`rsi-divergence` 87h median,
F66) and a per-trade expectancy floor for high-frequency families. Evaluate: net-vs-gross gap
delta at family level, D5 discipline.
**Gate G3:** cost-drag table (gross/commission/swap/net) before vs after, pasted.

### S4 — Re-census under the winning structural config (1–2 sessions) [OWNER GATE after]
The full R1'-shaped census (one cell per run, D13 null-with-reason, sv2 scoring) under the
S1–S3 surviving config as the new default. Walk-forward the top decile. This is where we learn
whether structural fixes lift the *bank* (F68's +0.02R mean) — the census-level pooled expR is
the headline number, not any cell's rank.
**Gate G4:** census complete with the D13 query pasted; pooled expR vs the 075D5240 baseline;
top-decile walk-forward table. Owner decides S5 entry.

### S5 — EMBARGO-2 dress rehearsal (1 session) [OWNER GATE after]
First and only touch of post-2026-07-05 data (D3: ≥45 accrued days; 60 complete ~2026-09-04).
Run the S4 survivors AND the family-level pooled config on the embargo window; sv2 challenge
windows; report as-is (R4 format, candidate cards). **The pooled family verdict matters more
than any cell's** — that is the direct test of D1.
**Gate G5:** candidate cards + pooled-family embargo table. No re-touch, no re-tune.

### S6 — Portfolio phase (CONDITIONAL: only if G5 shows ≥1 rule-level edge held) (2–3 sessions)
Now — and only now — the velocity problem, on proven material:
1. Paper-aggregation first (existing per-run equity series, the `RESEARCH.md` §1 method) with
   **joint-tail metrics** (worst combined day, tail co-occurrence counts), not Pearson averages.
2. Portfolio risk machinery: a dedicated risk profile (maxConcurrent sized to member count,
   per-cell budget partition of the heat cap), `ExposureGroups` by currency/asset class, and
   per-cell attribution report inside one multi-row run (trade-level keys already exist).
3. One real multi-row portfolio run on tape; reconcile per-cell attribution vs the members'
   solo runs; `ChallengeSimulator` on the aggregate at 1x/1.5x/2x.
**Gate G6:** aggregate challenge table + attribution reconciliation pasted. Owner decides
anything live-facing (F48 becomes load-bearing here if XAUUSD is a member).

### S7 — Final audit + close (1 session)
R5-style: fresh session audits every stage against this plan from artifacts, compiles ≤5-item
bugfix queue, updates AGENTS.md RESUME, hands the owner pack.

## 4. Session protocol (every session, non-negotiable — inherited verbatim from alpha-loop §4)
1. Read this plan + ledger tail; **QA the previous session's claims against artifacts before
   building on them** (this caught F24/F49–53/F59–62 last iteration — it is the mechanism).
2. Pre-register before running anything scored. Deliver YOUR stage only.
3. Append to `LEDGER.md` (what ran, what broke, finding numbers); update `TRACKER.md`; leave a
   RESUME block in AGENTS.md.
4. Gates: paste queries/outputs, never assert. Fast suites green before "done".

## 5. Verification matrix (owner, 5 minutes, any time)
- `research persistence --experiment <id> --split <date>` — the F64 table for any experiment.
- D13 "no lies" (completed-scoped) returns 0:
  `SELECT COUNT(*) FROM BacktestRuns WHERE Status='completed' AND TotalTrades != (SELECT COUNT(*) FROM TradeResults t WHERE t.RunId=BacktestRuns.RunId);`
- Embargo untouched until S5: no `BacktestRuns` row with `BacktestFrom >= '2026-07-06'` before
  the S5 ledger entry exists.
- Every scored experiment: `Experiments.SpecJson` holds the pre-registration; row counts match
  the ledger.

## 6. Stop rule (§7 of the owner pack, stated up front)
If S1–S3 produce **no** structural effect satisfying D5, and S4's pooled expR does not improve
on baseline, the current 9-family bank is exhausted at rule level too. Stop; the next
conversation is about sourcing a different class of strategy material (different signal
families, different data), not more search on this bank. That outcome would be banked knowledge,
same as the alpha-loop close — the machinery survives either way.
