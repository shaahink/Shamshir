# Senior Quant Review — Response to the 2026-07 Brief

**Written:** 2026-07-16 · **Reviewer:** Claude (acting as the external senior quant the brief
addresses) · **Inputs reviewed:** `SENIOR-QUANT-BRIEF-2026-07.md`, `iter-structural-edge/
{RESEARCH,PLAN,LEDGER}.md`, `QUANT-ROADMAP.md`. No DB queries were run for this review; every
number cited is from the committed artifacts. Where I state FTMO product facts I flag them as
**[verify]** — my knowledge is early-2026 and the rule model must be checked against FTMO's
current terms before anything downstream changes.

---

## 0. TL;DR

1. **The machinery and discipline are top-decile.** Pre-registration, embargo hygiene, the F70
   catch, position-level dollars, and F72's exact reproduction across a refactor are better
   process than most professional desks run. Nothing below asks you to loosen it.
2. **But the program is statistically underpowered by roughly an order of magnitude**, and this
   changes how the "trustworthy negatives" should be read. S1.1's arms (n = 225–916 trades)
   can only detect exit effects ≥ ~0.17R; the plausible size of a real structural exit effect
   is 0.03–0.10R. "No D5 survivor" was therefore close to a foregone conclusion *regardless of
   the truth*. The negatives are honest, but they are "not detectable at this n," not "not
   there." §2.1 has the math; §3 Q1 has the fix (paired offline exit replay ≈ 20× power, block
   bootstrap in gates).
3. **The single highest-value experiment available costs almost nothing: backfill 2019–2024
   history (Dukascopy M1 bid/ask is free) and run the frozen bank on it as pure OOS.** Six
   years of data no strategy was ever developed on, available now, no embargo budget spent.
   It settles F68 ("is the +0.02R bank mean real, is mean-reversion's +0.10R real") with more
   force than every remaining S-stage combined. Bid/ask bars also give you per-bar spread —
   unlocking honest M15 research — for free. §6 P1.
4. **The challenge model needs re-verification before it drives more decisions.** As of my
   knowledge FTMO removed the 30-day Phase-1 time limit in 2022 **[verify]**; if so, the
   "velocity problem" is not a cliff but an economics problem, and R4's headline ("0/12 windows
   hit +10%/30d, 0/12 breached") reads *completely differently*: safe-but-slow **passes** an
   untimed challenge. Also: FTMO's **Swing** account type permits weekend holding and news
   trading **[verify]** — this bank holds multi-day (rsi-divergence median 87 h) and is
   currently paying a needless product-mismatch tax. §4.
5. **"Dynamic auto-tuning" splits into a safe half and a forbidden half, and your own F64 is
   the dividing line.** Safe and valuable now: challenge-state-aware risk policy (a stochastic
   control problem your ChallengeSimulator can optimize offline), a portfolio-level intraday
   stop that mechanically truncates the daily-cap tail, vol-targeted sizing. Forbidden:
   anything that re-selects strategies/cells/parameters on trailing performance at horizons
   under ~6 months — F64 measured that this anti-selects (24% persistence). §5 is a doctrine
   you can adopt verbatim.
6. **Path:** verify the rule model (P0) → backfill + frozen-bank OOS (P1) → exit lab with
   paired stats, fix F71, skip the remaining whole-system factorials (P2) → new strategy
   material: session/time-of-day family + cross-sectional FX + indices (P3, start now in
   parallel, don't wait for the stop rule) → statistical upgrade of gates (P4) → challenge
   sizing policy + portfolio governor (P5) → portfolio phase as planned (S6/P6). §6.

---

## 1. Verdict on the program

**What is right (keep all of it):** the kernel/tape/parity stack, D13 one-cell-per-run, the
append-only ledgers, park-never-delete, embargo one-touch, sv2's incomplete-counts-as-fail,
evaluating on position-level dollars (F70 was an excellent catch that most shops never make),
and the culture of attacking the previous session's claims. F72 — census reproduced to the
dollar across a major refactor — is the kind of regression evidence most quant platforms only
claim to have.

**What the program under-weighs:** three things, in order of importance —

1. **Statistical power** (§2.1). The gates are calibrated to reject almost everything at the
   sample sizes the platform can currently generate. That protects against false positives
   (good) at the cost of near-certain false negatives (unpriced).
2. **Breadth and history** (§2.2). 14 symbols × 2 TFs × 1 year is a very small information
   set. The fundamental law (IR ≈ IC × √breadth) says your problem is breadth, and both of the
   cheap breadth axes — more history, more instruments — are under-exploited.
3. **The objective's specification** (§2.3 / §4). The 30-day rolling pass-window may be
   modeling a constraint that no longer exists, the daily-cap simulation is close-to-close
   (FTMO breaches intraday on floating equity), and the account *type* (standard vs Swing)
   materially changes what the bank is allowed to do.

The bank itself: I agree it is ~zero-mean as measured. The only family with a coherent
multi-line story is **mean-reversion** (+0.10R pooled, short holds → minimal swap drag, one of
two families whose default config really is bare, the cleanest R3 result, still positive on the
embargo window albeit n=4). It is also the family whose economics are *least* flattered by the
constant-spread model at H1. If anything in this bank is real, it is that family — and P1's
backfill test (§6) is the decisive, nearly-free experiment that says so or kills it.

---

## 2. The three load-bearing problems

### 2.1 Power: the D5 gate cannot see effects of the size that plausibly exist

Per-trade PnL in R units has σ ≈ 1.0–1.2R (your census: 71% stop-outs at −0.67R avg, TP wins
at +2.06R). Standard two-sample arithmetic at 80% power, 5% two-sided:

| Comparison | Detectable ΔexpR at available n | n/arm needed for Δ = 0.05R |
|---|---|---|
| S1.1 whole-system factorial, arm vs control (n ≈ 250–900/arm) | **≥ ~0.17R** | ~6,300 |
| Paired same-entry exit replay (σ_d ≈ 0.4R at ρ ≈ 0.9) | ~0.05R at n ≈ 500 | ~500 |

The whole-system factorial needed roughly **10–25× more trades** than exist to detect a
plausible exit effect on pooled dollars. The per-cell sign-consistency leg is worse: with
per-cell n ≈ 20–90, a **true** +0.05R effect produces a positive cell sign only ~64% of the
time, so P(≥9/12 cells positive | true effect) ≈ **0.30**. Legs (a)+(b)+(c) jointly would
reject a genuinely real 0.05R effect the large majority of the time. Two consequences:

- **Read the S1.1 negative correctly:** it refutes "there is a *large* (≥0.2R) exit effect,"
  and it destroyed the F70-inflated evidence for one. It does not tell you whether a 0.05R
  exit effect exists. Given F65's descriptives, that question is still open — but it is only
  answerable with paired statistics (§3 Q3).
- **Anything that ever passes D5 as currently written is either enormous or lucky.** A gate
  that passes ~nothing real also can't rank what matters.

The confound S1.1 itself documented compounds this: exit arms changed position lifetimes and
therefore entry admission under `MaxConcurrent=3` (position counts 253–647 across arms vs
control 567). The factorial compared *different entry populations*, adding variance and bias.
Fix for any future whole-system factorial: research-mode `MaxConcurrent` set high enough to
never bind. Better fix: don't run whole-system factorials for exit questions at all (§3 Q3).

### 2.2 Breadth: one year × 14 symbols × 2 TFs is the binding constraint

4,461 trades/year for the whole bank means family-level pools accrue ~500/year. At that rate,
reaching the ~2,500–6,000 trades that family-level claims need takes 5–10 calendar years of
forward accrual — or **one afternoon of historical backfill**. Dukascopy's free archive
(M1 and tick, bid/ask, 2003→present, FX + metals + index CFDs) gives you:

- **Pure OOS for the existing bank.** Pre-2025 data was never seen by any strategy's
  development. Freeze the bank exactly as configured, run the R1'-shaped census over
  2019–2024, and the +0.02R bank mean / +0.10R mean-reversion / F68 family ranking are tested
  out-of-sample at 5–6× the current n — without touching EMBARGO-2.
- **Per-bar spread for free** (bid/ask bars), which is the roadmap's stated gate for honest
  M15 research and removes the biggest known flattery in the cost model.
- **Regime coverage**: 2020 (COVID vol), 2022 (rates/trend year), 2023–24 (chop). One year of
  history is one regime draw; the S2 regime question is nearly unanswerable inside it and
  nearly trivial across six.

Era caveats are manageable and should be pre-registered: use conservative (today's or slightly
wider) spreads on old data; interpret only at rule × family level; treat 2025+ as the
terminal holdout. This is the program's best information-per-effort trade anywhere on the
board, and it requires an importer, not new science.

### 2.3 The objective: verify what FTMO actually requires before optimizing for it

Everything in §4. Headline: the 30-day window baked into sv2 and R4 may be self-imposed
**[verify]**; the daily-loss check needs intraday equity fidelity; and the account type
(standard vs Swing) decides whether weekend/news constraints even apply. Optimizing P(pass)
against a mis-specified rule set is the one place where this program's rigor could currently
be aimed at the wrong target.

---

## 3. Direct answers to the brief's §11 questions

**Q1 — Methodology audit.** The three-leg rule is directionally sound but mis-calibrated at
your n (§2.1). Concretely:

- **Add a stationary block bootstrap** (Politis–Romano, block ≈ 5–10 trading days) on pooled
  daily PnL deltas per family; report 95% CI and P(Δ>0) in every gate. Cheap in your existing
  Python tooling; this becomes the primary inference tool.
- **Replace leg (a)'s per-cell sign count** with sign consistency at the *family/instrument-class*
  level (trend vs MR × FX-major vs metal/crypto — units big enough to have meaningful signs),
  or keep cells but require only bootstrap-CI-excludes-zero overall.
- **Split-half → multiple splits.** One temporal split at a suspected regime boundary
  confounds "variant is noise" with "H2 was bad for the whole bank" (control's H2 base is
  $671 — deltas against it are noise). Use month-block jackknife (drop any month, sign holds)
  and/or 3–4 split points; with backfilled history, purged K-fold CV with embargo gaps.
- **Deflated Sharpe / SPA:** overkill at 8 pre-registered arms; a Bonferroni ×8 on bootstrap
  p-values or a simple SPA is enough. Pre-registration is already carrying most of the
  multiple-testing load. Keep it.
- **Pre-register a power analysis** with every experiment: minimum detectable effect at the
  planned n, stated next to the hypothesis. An underpowered test should be recognized as such
  *before* it runs, and its null read accordingly.
- **Walk-forward:** 35-day train windows fit ~30 trades — the OOS-ratio > 1 anomaly on all
  three R3 finalists is the classic signature of train windows too short to estimate anything
  (tiny, noisy train PnL in the denominator). With backfill, train ≥ 6 months; and replace the
  ratio with the stitched OOS curve + bootstrap CI (the ratio conflates fit quality with
  window luck).

**Q2 — Unit of analysis.** Yes, cell selection was doomed a priori: per-cell SE of expR at
n = 20–90 is 0.11–0.22R against a +0.02R signal — pure noise ranking, and 24% persistence is
exactly what noise plus mild regime drift produces. Pooling to rule × family is right. The
refinement: **partial pooling / empirical-Bayes shrinkage** (cell expR_i ~ N(θ_family, τ²))
instead of discarding cell structure — shrunk cell estimates are what you want for triage maps
and (later) portfolio weights, and τ̂ itself tells you whether cells differ at all (I expect
τ̂ ≈ 0 on current data, which *is* the F64 conclusion, now with a parameter). One afternoon in
`tools/research/`.

**Q3 — Exit layer, post-refutation.**
(a) **Do not run the remaining three families as whole-system factorials.** Trend-breakout's
negative plus §2.1's power math says ema-alignment and super-trend (same trade class, largely
same instruments) will produce the same uninformative null at ~100 runs each. The
mean-reversion contrast is the only interesting one, and it's better served by (b).
(b) **Yes — build the entry-stream-identical exit lab first.** This is the single best
research-infrastructure investment available: excursion recorder (wide-SL exploration runs,
one per family × cell) + offline exit replayer. It converts the exit question from a
6,300-trades-per-arm problem into a ~500-paired-trades problem (~20× power), evaluates
thousands of exit configs in milliseconds, removes the MaxConcurrent confound entirely, and is
already sketched in `QUANT-ROADMAP.md` §4/Q1. Fix F71 on the way so "no TP" is expressible;
then the pure F65 truncation test runs offline, paired, across all four families in one
session. Cheap interim step available today: re-evaluate S1.1's existing arms restricted to
entry timestamps common to arm & control — a poor-man's paired comparison from data already on
disk.
(c) **What would convince me an exit effect is real:** paired same-entry delta with
block-bootstrap CI excluding zero; effect monotone/plateaued in its knob (doesn't die when
trail 1.0→1.25×); same sign in ≥3 of 4 families *and* both instrument classes; survives
drop-any-month jackknife; mechanism metrics move as predicted (MFE capture up, giveback down —
your instinct to demand this is correct); and the sign reproduces on backfilled pre-2025 data.
That's a bar a real 0.05R effect *can* clear with the exit lab — unlike D5-as-written.

**Q4 — Regime (S2).** The cleanest test on recorded trades, runnable today: a 2×2
**family-class × half interaction** — pooled expR of {trend families} vs {mean-reversion(+rsi)}
in H1 vs H2, bootstrap the interaction term. If trend collapsed in H2 while MR held, that's a
genuine trend/range signature; if everything collapsed uniformly, it's a bank-wide draw and
the regime detector won't rescue it. Then condition on the *pre-registered external* regime
variable (realized-vol percentile + Kaufman efficiency ratio computed from bars, not the
in-house detector's folklore thresholds) before spending run budget. Deployment: at one year
of history I would **not** gate on/off by regime; at most, continuous risk scaling (e.g.,
0.5×–1.5×) if the conditional split is large, monotone, and survives the backfill. One year
contains ~2 regime draws; a classifier fitted to it is a story, not a model.

**Q5 — Velocity.** Full treatment in §4. Short answers: (i) verify the time limit first —
if unlimited, velocity reframes from cliff to economics and your "safe but slow" candidates
are viable challenge material at patient sizing; (ii) the aggregation thesis is sound *given
real edges*, but the current pool cannot be scaled — your own joint-tail number (worst day
−4.31% at 1×) already touches the cap, so velocity must come from breadth (more streams:
instruments, sessions, signal classes), not leverage; (iii) a portfolio-level intraday stop at
−3% mechanically truncates the daily-cap tail and is worth more than any correlation
estimate; (iv) sizing thin streams: size k so the empirical/bootstrap 99th-percentile joint
daily loss × 1.5 safety < 5%, recompute monthly — with clustered tails, never from Pearson
correlations (you already know this — F64's weekly max-pair 0.68 vs avg ≈ 0).

**Q6 — Strategy material.** Confirm the roadmap's prior, with sharpening. For a solo operator
with M1 bars, no ticks, no book, hunt **structural/calendar effects, not indicator
recombinations** — the effect sizes are larger and the parameter surface smaller:
1. **Session/time-of-day family** (London ORB, NY-open drive/fade, Asia range, day-of-week,
   session-VWAP reversion). Clock-keyed entries have few knobs → less overfit surface. Highest
   prior, testable on existing data today, and M15 execution becomes honest once per-bar
   spread lands (P1 gives it to you historically for free).
2. **Cross-sectional FX momentum/carry** — rank 14–28 pairs weekly, long top/short bottom
   basket. A genuinely different signal class (relative, not absolute), breadth-native (the
   whole cross-section is one strategy), swap-aware by construction (carry *is* the signal),
   and it exercises the portfolio machinery you'll need anyway. Needs the multi-row run plan
   (exists) + portfolio risk profile (known gap).
3. **Index CFDs** (US30/NAS100/DAX…): FTMO offers them **[verify]**, session structure is
   strongest there, and the tape schema is symbol-agnostic. Data: same Dukascopy source.
4. **Post-news drift/fade** using a historical economic-calendar dataset (cheap to acquire) —
   you already have news windows plumbed; the inverse trade is a documented effect class.
Deliberately still absent: ML signal models, sub-M15 anything. Right call at this maturity.

**Q7 — Data sufficiency.** For family-level rule claims at the effect sizes in play: **≥3
years minimum, 5+ preferred** (spans a vol cycle; 2025-only misses every crisis regime).
Extending backward helps, not pollutes, under three pre-registered conditions: (i) H1/H4
wide-target families only (cost-insensitive to era spread drift) until per-bar spread is
applied, or use era-conservative spreads; (ii) interpret at rule × family level only; (iii)
keep 2025+ as the terminal holdout so the freshest data is never fitted. The one real
pollution risk is regime non-stationarity making pre-2020 FX microstructure less relevant —
acceptable at H1/H4, and the persistence *test* (does 2019–22 rank order survive into
2023–24?) is itself the most valuable output.

**Q8 — Process.** Keep: pre-registration granularity, embargo one-touch, parks, artifact
pasting. Change: (i) power analysis pre-registered per experiment (Q1); (ii) primary decision
metric = pooled $ delta with bootstrap CI + challenge-sim outputs; demote the composite score
to triage-only and stop hand-tuning its weights; (iii) wire `PassProbabilityEstimator` (or
sv2's simulator in MC mode) into gates — one historical path is one draw, bootstrap the trade
sequence for P(pass)/P(bust) *distributions*; (iv) walk-forward per Q1; (v) embargo cadence:
45–60 days/quarter is fine once backfill exists — add an **era-holdout** discipline (develop
on 2019–23, 2024 untouched until a final gate) so you mint years, not weeks, of honest OOS;
(vi) F71-class bugs: add a config-effectiveness audit test (every knob in `EffectiveConfigJson`
must be provably read by some code path — a dead-knob scan would have caught F71 before it
cost a pre-registered hypothesis).

---

## 4. The FTMO path — arithmetic, product, and control

### 4.1 First: re-verify the rule model [P0]

As of my early-2026 knowledge, all **[verify]** against current FTMO terms:
- **No time limit** on Phase 1 (+10%) or Phase 2 (+5%) since Aug 2022; min 4 trading days each.
- **Swing account type**: weekend holding and news trading permitted (standard bans both).
  This bank holds multi-day and currently pays the news/weekend constraint tax for no reason.
- **Max daily loss = 5% of *initial* balance (fixed $), breached intraday on floating equity;
  resets midnight CE(S)T** — the config's "22:00 Prague" and any close-to-close daily
  simulation both need checking against that (sv2's PassRate is optimistic on breach detection
  until the intraday equity envelope from the roadmap lands).
- Funded: no target, 80→90% split, scaling +25% per 4 months given +10% cumulative.

If the no-time-limit fact holds, **R4 inverts**: 0/12 breaches with positive-but-slow PnL is
what *passing* an untimed challenge looks like. The 30-day PassRate stays useful as a velocity
index, but the primary metrics become **P(bust before target)** and **E[time to target]**.

### 4.2 The pass arithmetic (illustrative, assuming a real +0.10R pooled edge, σ ≈ 1.2R)

Target +10% needs net +10%/riskPerTrade in R. Gambler's-ruin with drift (barriers at target
and max-loss), no time limit:

| Risk/trade | R needed | ~Trades @ +0.1R | P(target before −10%) | E[time] @ 2 trades/day |
|---|---|---|---|---|
| 0.5% | +20R | ~200 | ~0.94 | ~4–5 months |
| 1.0% | +10R | ~100 | ~0.80 | ~2–2.5 months |
| 2.0% | +5R | ~50 | ~0.67 | ~5–6 weeks |

(2 trades/day ≈ the pooled mean-reversion family run as a portfolio; the daily cap barely
binds below 1% risk — R4's worst day was 1.47%.) Two readings:

- **Untimed challenge:** patient sizing (0.5–1%) with a real +0.1R edge passes with 80–94%
  probability per attempt; Phase 1 + 2 ≈ 6–8 months to funded. Slow, boring, viable.
- **Challenge-as-option framing** (absent from your docs, worth adopting): the fee (~€540/100k)
  is an option premium. You don't need P(pass) ≈ 95%; you need positive EV per attempt and a
  bankroll for retries. At a verified edge, 1–2% risk with P ≈ 0.67–0.80 and 5× faster passes
  can dominate on EV. **But this is only rational after the edge is verified out-of-sample —
  at zero edge, aggressive sizing still passes sometimes and then funds an account that earns
  nothing.** Your edge-first sequencing is correct; keep it.

**Funded-stage economics, honestly:** if mean-reversion's +0.10R is real, ~500 trades/yr ×
$50/trade (0.5% risk, 100k) × 80% split ≈ **$20k/yr per 100k account** — scaling to $40–80k/yr
at 200–400k allocation. That is this bank's realistic ceiling. Raising the ceiling is P3's
job (new material/breadth), not more tuning of these nine.

### 4.3 The two control-layer builds that raise P(pass) at *any* fixed edge [P5]

1. **Portfolio-level intraday stop:** hard flatten + halt-for-day at −3% intraday equity.
   Mechanically truncates the daily-cap tail (breach probability → ~0), which then allows
   higher aggregate sizing until the *total*-loss cap binds instead. The governor already has
   loss bands; this is an extension, and the challenge simulator must model the same policy
   (requires the intraday equity envelope for honesty).
2. **Challenge-state-aware risk policy:** risk/trade as a function of (distance to target, DD
   headroom, phase). The challenge is a stochastic control problem, and the policy can be
   optimized offline by Monte Carlo over bootstrap-resampled trade streams with the existing
   `ChallengeSimulator`. Under no-time-limit the optimal shape is mostly "small constant risk
   + hard daily stop + de-risk after +8% to lock the pass" — boring, but it should be *proven*
   by MC, and it changes materially if a time limit exists (aggressive-early/lock-late). This
   is the legitimate, edge-independent version of "auto tuning" (§5).

---

## 5. Auto-tuning doctrine — what may adapt, what must not

The owner's goal "perform dynamically with auto tuning and adjustments" is achievable, but
F64 draws a hard line through the middle of it: **selection on trailing performance
anti-selects at ≤5-month horizons in this system (24% persistence).** Any adaptation scheme is
a bet that the thing being adapted persists; risk structure persists, market alpha mostly
doesn't. Doctrine:

**Tier 1 — adapt freely (risk, not alpha; build now):**
- Volatility-targeted sizing (ATR-based sizing already does most of this; extend to
  realized-vol percentile scaling per symbol).
- Drawdown-proximity scaling (built) + the intraday portfolio stop (§4.3.1).
- Challenge-state risk policy (§4.3.2) — MC-optimized offline, deterministic online.
- These require no new edge evidence: they exploit the *known structure of the rules*.

**Tier 2 — adapt with proof (calibration, gated on the exit lab):**
- Rolling refit of exit-calibration tables (per symbol × TF × regime) via the excursion-replay
  grid, deployed **only if** walk-forward shows refit beats frozen on stitched OOS with a
  bootstrap CI — "does adaptation help" is itself a pre-registerable hypothesis, and the
  default expectation from F64 is *no*. Version every table with its fit window (the roadmap's
  hot-reload calibration design is right).
- Regime-conditional risk scaling (continuous, bounded 0.5×–1.5×) only if S2's conditioning
  effect is large, monotone, and survives backfill.

**Tier 3 — adapt slowly (portfolio weights):**
- Empirical-Bayes-shrunk family weights, turnover-capped, re-estimated quarterly at most,
  never on windows < 6 months. Equal-weight is the benchmark to beat; at current τ̂ ≈ 0 it
  probably won't be beaten.

**Forbidden (F64 is the tombstone):**
- Strategy/cell switching on trailing performance; per-cell re-ranking; online parameter
  chasing; widening any tolerance or re-touching any embargo because live results disappoint.
- Binary regime on/off gating at one year of history.

---

## 6. Recommended program (amends iter-structural-edge; keeps its gates and ledger discipline)

| Phase | What | Effort | Gate |
|---|---|---|---|
| **P0 — Rule-model truth** | Verify FTMO terms (time limits, Swing, daily-loss definition/reset, scaling) against current contract; correct `ftmo-standard.json` + sv2 metrics (add P(bust), E[time-to-target]; keep 30d PassRate as velocity index); decide account type | ½ session | Rule-set diff + corrected metric definitions in ledger |
| **P1 — Backfill + frozen-bank pure OOS** | Dukascopy importer (M1 bid/ask, 2019→2024, 14 symbols + shortlist indices); validate importer on the overlap year vs existing tape; freeze bank configs; pre-register; run R1'-census over 2019–24; family-level pooled verdicts with bootstrap CIs | 1–2 sessions | The F68 table reproduced OOS or refuted; per-bar spread present in historical tape |
| **P2 — Exit lab** | Fix F71 (+ dead-knob audit test); excursion recorder + offline `ExitReplayer`; paired exit factorials for all 4 families offline (incl. the pure no-TP F65 test); **skip remaining whole-system factorials** | 1–2 sessions | Paired delta tables with bootstrap CIs; F65 monetization confirmed or closed |
| **P3 — New material** (parallel-izable with P1/P2) | Session/time-of-day family (3–4 clock-keyed variants); cross-sectional FX momentum/carry basket; indices data + 1–2 session strategies on them; all pre-registered, backfill-era IS with 2024 era-holdout and 2025+ untouched | 2–3 sessions | Family-level pooled verdicts, same discipline |
| **P4 — Gate upgrade** | Block bootstrap + power analysis into D5 (replace per-cell sign leg per §3 Q1); stitched-OOS walk-forward with ≥6-month trains; EB shrinkage tool | 1 session | Re-stated D5′ in PLAN; tools committed |
| **P5 — Control layer** | Intraday equity envelope → honest daily-cap sim; portfolio intraday stop; challenge-state risk policy MC-optimized | 1–2 sessions | MC P(pass)/P(bust) tables at 3 risk levels, policy vs constant-risk |
| **P6 — Portfolio (= S6, conditional)** | As planned, gated on ≥1 edge surviving P1/P2/P3 era-holdout + EMBARGO-2; joint-tail sizing rule (99th pct × 1.5 < 5%) | 2–3 sessions | As PLAN G6 |

**On the current S1.2 decision** (fix F71 vs proceed to ema-alignment): **neither as posed.**
Fix F71, but spend the run budget on P2's recorder, not on three more underpowered
whole-system factorials. The stop rule's "different strategy material" conversation should
also start **now** (P3), not after S3 — §2.1 shows S1–S3 as designed were never going to
produce a survivor even if a modest real effect exists, so waiting on them to fire the stop
rule burns months to learn what the power math already says.

**EMBARGO-2 stays sacred** and becomes the final gate for anything P1–P3 produces, exactly per
the existing D3/S5 discipline.

---

## 7. What success looks like, restated with numbers

A funded-account program is viable when: (1) at least one family/basket shows pooled expR ≥
+0.05R with a bootstrap CI excluding zero on ≥3 years including the era-holdout; (2) the
challenge MC (honest intraday daily-cap, verified rule set, chosen sizing policy) shows
P(pass) ≥ 0.6 per attempt with P(bust) ≤ 0.25 at the chosen risk tier, making the fee-EV
clearly positive with retries; (3) the aggregate's bootstrap 99th-percentile daily loss × 1.5
clears 5%; (4) a live parity verdict ≤ 14 days old. Until (1) exists, every downstream
question is premature — which is why P1 is the program's next real move: it is the cheapest
experiment that can produce (1), and the most honest one that can kill it.

---

## Postscript (2026-07-16, same day — after S1 closed)

Between this review's delivery and the S1 owner gate, the active session: fixed F71 in code
(verified behavior-preserving live), re-ran the no-TP arm (hypothesis (b) refuted decisively —
uncapping the 2R TP swung trend-breakout **−$25.8k** and dropped MFE capture to 0.22; the cap
does protective work, so F65's truncation story is heavily qualified), ran the S1.2
ema-alignment factorial (control beat every variant on dollars; **R3's star cell v6a
reproduced its +82% ExpectancyR while losing $1,109 vs its own control** — the program's
single strongest historical alpha claim demonstrated to be the F70 row-splitting artifact on
its very own cell), skipped the remaining families early-with-reason citing §2.1's power
analysis, and executed the D7 park (mtf-trend). Two calibrations of this review from those
results: (i) the §2.1 power critique applies to *small* effects — the no-TP swing shows the
whole-system factorial does detect large ones, so the clean negatives on what was tested are
stronger than a pure MDE argument implies; (ii) the early stop is exactly the correct use of
the power analysis. The successor plan is `docs/iterations/iter-viability/PLAN.md` (adopted at
the gate); this document is its rationale and should be read with it.
