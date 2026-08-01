# RESEARCH-PROCESS — how Shamshir finds (and refuses to fake) an edge

**Status: NORMATIVE.** Created 2026-07-16, consolidating the alpha-loop and structural-edge
programs, the senior-quant review (`docs/QUANT-REVIEW-RESPONSE-2026-07.md`), and the adopted
`iter-viability` plan. When an iteration plan and this document disagree, the iteration plan's
*decisions* win for that iteration; this document is then updated at the iteration's close.
Companion normative docs: `INVESTIGATION-METHOD.md` (venue claims), `RESTING-ORDER-CONTRACT.md`
(fill semantics).

The stance, in one line: **the process is built so that "no edge found" is a trustworthy,
bankable outcome** — negatives are believed more than positives, by design.

---

## 1. Units of analysis (what a "cell" is, and what we actually search for)

- **Cell** = (strategy family, symbol, timeframe, pack, risk profile, window). One cell = one
  run = one account (D13). Cells are the unit of *record* and *attribution* — never the unit
  of search or ranking.
- **Family** = one strategy thesis across all its cells (e.g. `mean-reversion` over 14 symbols
  × H1/H4). Pooled family trade counts reach the hundreds per year; cells reach 20–90.
- **Rule** = a structural mechanism that cuts across cells (an exit policy, an entry filter, a
  session window, a cost knob, a sizing policy).

**Why cells cannot be picked (the load-bearing arithmetic):** per-trade PnL has σ ≈ 1–1.2R, so
a cell with n = 20–90 trades/yr has a standard error of 0.11–0.22R on its expectancy — an
order of magnitude larger than the bank's ~+0.02R mean. Ranking cells ranks noise. Measured:
F64 — cells positive in the census's first half kept only 24% positivity in the second half
(worse than a coin flip; trailing performance *anti-selects*). Therefore: **the unit of search
is rule × family, pooled across cells; cells are instances.** Cell structure is still used —
via partial-pooling/EB shrinkage for triage maps — but selection on per-cell results is
forbidden at every level, including "dynamic" strategy pickers (see §7).

## 2. The evidence ladder (how a claim earns belief)

Every candidate claim climbs this ladder; a rung skipped is a claim not made:

1. **In-sample pooled effect** (family-level, position-level dollars) — hypothesis-grade only.
2. **Robustness on the same window** — split-half both halves / drop-any-month jackknife;
   sign consistency at family × instrument-class level; parameter plateau (an effect that dies
   when a knob moves one notch was never real).
3. **Walk-forward** — stitched OOS equity from ≥6-month train windows (short trains fit noise
   and produce pathological OOS ratios).
4. **Era-holdout** — a full held-out year (2024 under iter-viability D3), touched once.
5. **Embargo window** — never-seen recent data (EMBARGO-2: post-2026-07-05), touched once, at
   a pre-declared date. Re-tuning against a touched window is a plan violation.
6. **Demo forward-run** — calendar time on the live venue (listen mode), weekly
   oracle-reconciled. Unfakeable; starts as soon as any candidate exists.

Challenge-readiness (the top of the ladder) additionally requires: pooled expectancy CI
excluding zero on ≥3 years, Monte-Carlo P(pass)/P(bust)/E[time] under the *verified* rule set
with the chosen sizing policy, joint-tail sizing clearance (bootstrap 99th-pct daily loss ×
1.5 < daily cap), and a live parity verdict ≤ 14 days old.

## 3. Statistical discipline (the anti-overfitting protocol)

1. **Pre-registration, always:** hypothesis + exact configs in the iteration LEDGER *before*
   any scored run; persisted in `Experiments.SpecJson`; ≤ 8 variants/session including
   controls. Every pre-registration carries an **MDE line** (minimum detectable effect at the
   planned n, σ ≈ 1–1.2R) — an underpowered test is recognized *before* it runs, and its null
   is recorded as "not detectable at n", never "no effect". (Reference points: two-sample
   whole-system comparison needs ~6,300 trades/arm to resolve 0.05R; paired same-entry
   comparison needs ~500.)
2. **Primary metric: position-level dollars**, pooled at family level. Never per-row
   R-multiples across configs that split rows (F70: PartialTp posts two rows/position and
   manufactures R-metric wins — this artifact was the program's best-looking "result" twice).
3. **Inference: stationary block bootstrap** (block ≈ 5–10 trading days) on pooled daily PnL
   deltas → 95% CI + P(Δ>0) pasted in the gate. Multiple testing: pre-registration carries
   most of the load; Bonferroni across the session's arms for the rest.
4. **Whole-system vs paired:** whole-system factorials measure a rule's effect *including*
   interactions (exits change position lifetimes → entry admission under concurrency caps).
   They are reserved for effects expected to be large; if run, research-mode `MaxConcurrent`
   must not bind. Small-effect questions use the **paired exit lab** (excursion recorder +
   offline replayer): same entries, different rules, ~20× power.
5. **Validity floor (D3):** a scored cell needs ≥ 20 trades in-window, data-quality PASS,
   completed run, zero engine warnings — else score = null **with reason** (0 is information;
   null is "insufficient data").
6. **Park, never delete** (`StrategyCellParks`) — all triage is reversible.
7. **Scoring is versioned** (sv1 → sv2; changes are new versions, never in-place). The
   composite score is a *triage* aid; decisions ride on pooled dollars + CIs + challenge-sim
   outputs (P(bust), E[time-to-target], pass-rate as a velocity index).

## 4. Venue truth and costs (what the numbers are allowed to mean)

- **cTrader is the oracle; the tape is a mimic.** Research runs tape-only; parity to the venue
  is a pre-registered tolerance budget that is *never widened to make a result pass*. Fill
  semantics are measured, not assumed (`RESTING-ORDER-CONTRACT.md`; first-breaching-tick,
  spread on the correct side, honest entry timing).
- **Costs are venue-declared, never invented** (commission type/rate, swap rates via
  `symbol_spec`); costs are negative; `Net = Gross + Commission + Swap` is an invariant test.
- **Spread honesty gates timeframe research:** constant-spread flattery is inversely
  proportional to target size — shorter-TF research is legal only where per-bar recorded
  spread exists (bid/ask backfill provides it historically).
- Credential-free gates (Unit/Integration/Sim) never support a claim about cTrader; venue
  claims follow `INVESTIGATION-METHOD.md` and require the live compare-both smoke.

## 5. Session protocol and the audit trail

- Every session: **QA the previous session's claims against artifacts first** (this mechanism
  caught the fake OOS walk-forward, F63, F69, F70) → pre-register → execute → append to
  `LEDGER.md` (append-only; mid-session findings written immediately) → paste gate
  queries/outputs, never assert → fast suites green → RESUME block updated.
- **F-numbers** are the append-only findings ledger (F1… continuing across iterations);
  **D-numbers** are per-iteration locked decisions, ratified by the owner. A claim without an
  artifact behind it does not exist.
- Reproduction tooling: `research persistence` CLI verb + `GET /api/experiments/persistence`;
  `tools/research/` (`quant_research.py`, `split_half.py`, `exit_factorial_driver.py`,
  `determinism_probe.py`); every RunId resolves via `GET /api/runs/{id}`.

## 6. The museum of failure modes (read before trusting any result — each one shipped once)

| Finding | Lesson now enforced |
|---|---|
| F5 | 9 strategies commingled in one account → one cell = one run = one account (D13) |
| F3/F4 | Fabricated swap paid a nonexistent carry; commission off 3,300× on gold → venue declares all economics |
| Fake OOS (2026-07-05) | A walk-forward that wasn't → walk-forward arithmetic unit-pinned; stitched OOS only |
| F63 | Placeholder survival component ranked a whole census → calibrated `ChallengeSimulator` (sv2); composites are triage-only |
| F64 | Cell selection on trailing performance anti-selects (24%) → rule × family pooling; no dynamic pickers |
| F69 | The "bare" baseline actually had BE+trail on 7/9 families → baselines verified against `EffectiveConfigJson`, not docs |
| F70 | PartialTp row-splitting inflated ExpectancyR → R3's 8/8 and its v6a star both died on their own cells → position-level dollars |
| F71 | `TakeProfit.Method` was a dead knob; the run's own config recorded "None" while executing a TP → dead-knob audit tests; "config recorded" ≠ "config executed" |
| F72 (positive) | Census reproduced to the dollar across a major refactor → determinism/regression story holds; keep the gates |
| F24-class | 100%-rejection bug shipped under all-green credential-free gates → live compare-both before "done" on venue paths; rejection-rate alarm in live ops |
| S1.1 ops | In-memory idempotency keys duplicated 23 runs across restarts → keys persisted to DB; batch drivers resume-aware |
| Power (review §2.1) | "No survivor" at MDE 0.17R read as "no effect" → MDE line mandatory in every pre-registration |

## 7. Adaptation doctrine (what may auto-tune, what must not)

- **Tier 1 — adapt freely (risk structure, edge-independent):** volatility-targeted sizing,
  drawdown-proximity scaling, the portfolio intraday stop, the challenge-state risk policy
  (MC-optimized offline, deterministic online).
- **Tier 2 — adapt with proof:** exit-calibration tables refit on rolling windows, deployed
  only if walk-forward shows refit beats frozen (a pre-registered hypothesis; default
  expectation is *no*). Regime-conditional risk scaling (continuous, bounded) only on a large,
  monotone, holdout-surviving effect.
- **Tier 3 — adapt slowly:** portfolio family weights, EB-shrunk, turnover-capped, quarterly
  at most, never on windows < 6 months.
- **Forbidden:** strategy/cell/parameter selection on trailing performance at any horizon
  under ~6 months; binary regime on/off gating at current data scale; widening any tolerance
  or re-touching any embargo because results disappoint. F64 is the tombstone.

## 8. Concurrency (two-lane worktree protocol, iter-viability D9)

Lane R (research/truth) owns the research DB and everything scored — one app instance per DB
file, parallelism *inside* the app/driver only (determinism-probe-validated). Lane D (dev)
builds code in a separate worktree on a branch off the iteration branch, credential-free gates
there, merging at stage gates. `LEDGER.md` has one writer at a time. Docs lanes are always
safe. Full detail: `docs/iterations/iter-viability/PLAN.md` §8.
