# iter-structural-edge — Session Ledger (append-only)

**Started:** 2026-07-16 — S0 session

Every session appends below. Mid-session findings go here immediately (stall-kill safety).
Do NOT delete or edit prior entries — this is an audit trail.

---

## S0 — 2026-07-16 — Truth infrastructure (sv2 scoring + committed research tools)

**Session mode:** MANUAL interactive session (owner call — the conductor run was cancelled at
launch; the conductor plan `conductor-structural-edge.plan.json` stays committed and TRACKER.md
stays parseable so remaining stages can be handed to conductor later).

### Setup (pre-S0, same session)

- Iteration opened by owner. Plan docs + seed TRACKER + conductor plan committed to `main` @
  `e3c96e1` and pushed (main was ~15 conductor-chore commits ahead of origin — now synced).
- Work branch `iter/structural-edge` created from `e3c96e1`, pushed with upstream. Conductor plan
  got `branchPattern: ^iter/structural-edge$`.
- AGENTS.md RESUME rewritten: iteration OPEN, stage S0.

### S0.1 — sv2 scoring (F63 executed, D4)

- `ChallengeSimulationService.ComputeSurvivalAsync(runId)` (new): buckets the run's real equity
  into engine-truth trading days (`BuildDailyPoints`, the R4 machinery), rolls a 30-day
  `ChallengeSimulator` window from EVERY daily start, returns
  `ChallengeSurvival(PassRate, Windows, Passes, Fails, Incompletes, RuleSetId)`. **PassRate =
  Pass/Windows — Incomplete counts as non-pass on purpose** (R4's headline failure mode was
  velocity; a survival score that forgave incompletes would hide it). Returns null (component
  skipped, composite renormalizes) when: no snapshots, < 30 daily buckets, or no resolvable
  risk-profile→prop-firm rule set. Rule-set resolution extracted to `ResolveRuleSetAsync`
  (shared with `SimulateAsync`, which still throws).
- `SetupScoreService`: placeholder `ComputeFtmoSurvival` DELETED; survival component =
  `PassRate × 100`; score version strings sv1→**sv2** everywhere (`sv2`/`sv2-partial`/`sv2-null`);
  evidence trail persisted in `Components` (FtmoPassRate/Windows/Passes/Fails/Incompletes/
  RuleSetId). Ad-hoc default bucket renamed `default-sv1`→`default-sv2` (new bucket; the old one
  keeps only sv1 rows). **sv1 rows untouched — no retro-rescore (D4); census 075D5240 stays sv1.**
- Tests pinning the PLAN G0 contract:
  - Unit `ChallengeSimulatorTests.DailyCapBreach_Dominates_TargetHit` — a day that ends above
    the profit target but lost more than the daily cap FAILS (breach checked before target).
  - Integration `SetupScoreSv2Tests`: flat 60d equity → 31 windows all Incomplete →
    FtmoSurvival **0** (0/N scores 0, not null); +1%/day compounding 60d → 31/31 Pass →
    FtmoSurvival **100** / PassRate **1.0** (N/N scores 1); no snapshots → survival **null** and
    composite still computed (renormalized), version `sv2-partial`.

### S0.2 — Research tools committed

- `tools/research/split_half.py` + `tools/research/quant_research.py` ported from the 2026-07-16
  research-session scratchpad, parameterized (`--experiment <prefix> --split <date> --db --base`;
  split defaults to census midpoint = 2025-12-03 for 075D5240). + `tools/research/README.md`.
- **`research persistence` CLI verb** (ResearchCli) + `GET api/experiments/persistence` endpoint +
  `SplitHalfPersistenceService` (Web): the F64 split-half table for ANY experiment, one command,
  no python needed. Line-faithful port of split_half.py's F64 math (same selection, same
  window-walk semantics). Integration-tested on a hand-checkable synthetic experiment
  (`SplitHalfPersistenceTests`), including GUID-prefix resolution and null-Composite exclusion.

### S0.3 — LEDGER.md created, TRACKER.md expanded (this file / alpha-loop format)

### What broke / observations

- `dotnet build` failed at start: Angular src (touched 2026-07-16 00:01) was newer than wwwroot →
  known .NET 10 static-assets gotcha; fixed by `npm --prefix web-ui run build` first.
- First live CLI run printed `VERDICT: FAIL error=unknown`: the success payload serialized
  `"error": null` (PersistenceReport.Error) and the CLI treated property-presence as failure.
  Fixed both sides: `[JsonIgnore(WhenWritingNull)]` on Error + ValueKind check in the verb.
- **Observation (not fixed, pre-existing):** `CliArgs.Verb` joins the first TWO positionals, so
  bare `research score <runId>` produces verb `"score <runId>"` and falls through to usage —
  the `score` verb appears reachable only via the API/controller path. `persistence` takes
  options only, so it is not affected. Candidate for a later bugfix queue.
- Ported `split_half.py` prints cost drag as `-20.9%` where RESEARCH.md §3 pasted `20.9%` —
  same formula, commission/swap are stored negative; the sign was hand-cleaned in the research
  doc. Numbers identical in magnitude ($166,581 / $17,330 / $17,460 / $131,791 — exact match).

### Gate G0 — PASSED (all three legs, at final code state)

1. **sv2 tests green** — included in the suite runs below.
2. **F64 reproduction from the live DB** — app launched on :5134, then
   `research persistence --experiment 075D5240 --split 2025-12-03` printed (verbatim):

   ```
   experiment baseline-sv1-prime (075d5240)
   census 2025-07-04 -> 2026-05-05, split 2025-12-03, H2 span 153d

   === SPLIT-HALF SELECTION TEST (F64) ===
   cells positive in H1: 38/74  (H1 PnL of selection: $116,518)
   same cells in H2:     $-880   -> haircut factor -0.01
   persistence: 9/38 H1-positive cells stayed positive in H2 (24%)
   H2 return of H1-selected portfolio at 1x: -0.17%/30d
   top-8 by H1 PnL -> H2: $-540 = -0.11%/30d (H1 was $58,857)
   reverse check: H2-positive cells (13) earned $44,190 in H2, $23,942 in H1 -> factor 0.54
   H1-selected portfolio, H2 rolling 30d challenge windows (fresh $100,000 each):
    k=1x:  4 pass /  5 fail / 82 incomplete   worstDay=-3.00%
    k=2x: 14 pass / 48 fail / 29 incomplete   worstDay=-6.01%
    k=3x: 26 pass / 65 fail /  0 incomplete   worstDay=-9.01%
   VERDICT: PASS scored=74 h1Positive=38 persisted=9 h1Pnl=116518 h2Pnl=-880
   ```

   Every figure matches RESEARCH.md §1 exactly ($0 delta, tighter than the ±$1 gate). The
   committed python port reproduces the same block + F66 cost drag.
3. **Fast suites green (re-run AFTER the last code change):** build 0err/5warn ·
   Unit **767**/0/6 (+1 vs 766 baseline) · Integration **153**/0/0 (+5 vs 148) ·
   Sim-fast **144**/0/0 (= baseline). No app process left running (host killed before suites).

### Carried forward

- S1 (exit-layer factorial) is next; OWNER GATE sits after S1, but S1 itself is open to run.
  Pre-registration discipline (D5): variants in this ledger BEFORE any scored run.
- EMBARGO-2 untouched: no run rows created this session (verification: the S0 work created zero
  `BacktestRuns` rows; the app was only read from).

---

## S1.1 — 2026-07-16 — Exit-layer factorial, family 1: trend-breakout (manual session)

### QA of prior claims (protocol §4 step 1) → **F69**

**F69 — the census "default" baseline already contained exit management for 7 of 9 families.**
RESEARCH.md §2 (F65 method line) claims the census ran "default configs: fixed SL = 1.5×ATR,
TP = 2R, breakeven OFF, trailing None". Checked against the artifact, not the doc: census run
`22ca21af` (trend-breakout/XAUUSD/H4, `075D5240`) has `EffectiveConfigJson.positionManagement` =
BE **enabled** (trigger 1R, offset 1 pip) + trailing **enabled** (AtrMultiple **2.5**). Stored
strategy configs (which PackId-null runs fall back to, per `ApplyPack` D4 semantics):

| family | Breakeven | Trailing |
|---|---|---|
| bb-squeeze | ON | AtrMultiple 2.0 |
| ema-alignment | ON | AtrMultiple 2.5 |
| macd-momentum | ON | AtrMultiple 2.5 |
| mean-reversion | off | None |
| mtf-trend | ON | Structure 10-bar |
| rsi-divergence | off | None |
| session-breakout | ON | AtrMultiple 2.0 |
| super-trend | ON | AtrMultiple 2.5 |
| trend-breakout | ON | AtrMultiple 2.5 |

F65's *numbers* stand (they are measured from trades), but the *interpretation* shifts: MFE
capture 0.42 / 71% SL exits describe a baseline that already had BE@1R + a wide trail (for the
trend families), not a bare fixed-SL/TP config. R3's 8/8 stays valid as measured — but the
effect is {trail 1.0×ATR relaxing to 3.0 via Ride + PartialTp} vs {fixed 2.5×ATR trail}, both
with BE. The factorial below adds a `bare` arm (StripAddOns) to measure what the research doc
believed the baseline was. RESEARCH.md gets a correction note referencing this entry.

### Pre-registration (D5 — BEFORE anything scored; committed before first run)

**Hypotheses:** (a) the R3 8/8 `runner-aggressive` effect on trend-breakout is carried by a
subset of {tighter-then-relaxing trail (trail/Ride), PartialTp} relative to the TRUE control
(BE + 2.5×ATR trail, F69); (b) removing the 2R TP cap (no-TP pure trail) repairs the F65
truncation — MFE capture and giveback must move, not just expR; (c) the baseline's own BE+trail
(control vs bare) has a measurable family-level effect — quantifying it for the first time.

**Cells (all 12 census-scoreable trend-breakout cells; XAGUSD/H1 is R3-PARKED — it runs and
pools, but stays parked for any selection):** XAUUSD/H4, ETHUSD/H4, NZDUSD/H4, USDCAD/H4,
AUDUSD/H4, EURGBP/H4, XAGUSD/H1, NZDUSD/H1, EURGBP/H1, AUDUSD/H1, GBPUSD/H1, EURUSD/H1.

**Constants (census parity):** window 2025-07-04 → 2026-05-05 (split-half boundary 2025-12-03);
tape venue; risk `standard`; balance 100k; commission 30/M; spread 1 pip; HonestFills on;
governor on; regime on; one cell per run (D13); idempotency keys `s1-tb-<variant>-<sym>-<tf>`.

**Variants (exactly 8, the D5 ceiling, incl. 2 controls).** Pack components copied verbatim
from `runner-aggressive` so each arm isolates one component (trail = AtrMultiple 1.0, ATR
trail relax via Ride AdxFloor 25 → 3.0, PartialTp 50% @ 1R, BE trigger 1R offset 1):

| # | Label | Mechanism | What it isolates |
|---|---|---|---|
| 1 | `control` | PackId null (strategy's own add-ons: BE + 2.5×ATR trail) | The census baseline, re-run under today's engine (also a free god-classes-refactor regression check vs `075D5240` originals) |
| 2 | `bare` | StripAddOns=true (SL 1.5×ATR / TP 2R only) | The baseline RESEARCH.md assumed; measures BE+trail's own contribution (control − bare) |
| 3 | `be-only` | pack `breakeven-only` | BE without any trail |
| 4 | `trail-only` | new pack `s1-trail-only`: trail AtrMultiple 1.0, BE off, no Ride/Partial | The tighter trail alone |
| 5 | `trail-ride` | new pack `s1-trail-ride`: trail 1.0 + Ride (ADX≥25 → relax 3.0), BE off | Trail + the "let winners run" relax |
| 6 | `partial-only` | new pack `s1-partial-only`: PartialTp 50%@1R, BE off, trail off | The partial-TP leg alone |
| 7 | `runner-full` | pack `runner-aggressive` (BE + trail 1.0 + Ride + Partial) | R3's 8/8 config (replication + reference) |
| 8 | `no-tp-trail` | pack `s1-trail-only` + StrategyOverrides TakeProfit.Method=`None` | Pure trail, uncapped — the direct F65 truncation test |

**Runs:** 8 × 12 = 96, sequential, sv2-scored into experiment `s1-exit-factorial-tb`
(SpecJson = this pre-registration), variantLabel `<label>/trend-breakout/<SYM>/<TF>`.

**Evaluation (family level, D5 in full):** pooled expR delta vs `control` (census originals as
engine-drift cross-check); per-cell sign-consistency count (survival needs ≥75% = ≥9/12);
split-half at 2025-12-03 — BOTH halves positive at family level; MFE-capture + giveback deltas
(an expR gain with unchanged F65 metrics is investigated, not banked); 6-fold walk-forward on
the single best variant (OOS ratio ≥ 0.5). No per-cell ranking claims of any kind.

**Stop conditions:** a variant erroring/warning on >2 cells is reported as-is (null scores, D13);
no mid-flight config edits — a bad variant dies as pre-registered.

### Execution record

96/96 variant-cells completed on tape and sv2-scored into experiment `862C5D04` (Status
Completed 2026-07-16 16:02). The session was interrupted twice by external background-task
kills; the app's idempotency-key store is **in-memory**, so the first resume re-created 23
already-run variants as new runs (deterministic tape → byte-identical results; verified run 1:
same 39 trades, same score). Fix: driver made resume-aware (skip variants with a completed
scored run), and a dedupe pass removed the 23 stale `ExperimentRuns` rows (119 → exactly 96,
one per variant; the duplicate `BacktestRuns` stay as audit trail). Run health: 0 failed, 0
warnings, 33 below-floor D13 nulls — all on long-hold arms (bare 6, be-only 4, trail-only 7,
trail-ride 9, partial-only 1, no-tp-trail 6), every one `trades=<N> below floor 20 (D3)`.
No code changed this session → gates unchanged from S0 (build 0/5 · 767 · 153 · 144).

### Results (pasted from `s1_evaluate.py` against the live DB)

```
variant           n   expR  dExpR     net$ sign+      H1$      H2$      dH1      dH2 MFEcap gvbk%   SL   TP
control         567  0.123 +0.000    23422     -    22751      671       +0       +0   0.42  20.7  422  145
bare            317  0.127 +0.004    13356  4/12    12256     1099   -10495     +428   0.54  37.2  197  120
be-only         447  0.167 +0.043    24365  4/12    21852     2514     -900    +1843   0.49  30.5  295  152
trail-only      253  0.145 +0.021    10586  4/12     9486     1099   -13265     +428   0.44  31.5  181   72
trail-ride      225  0.143 +0.020    10456  4/12     9356     1099   -13395     +428   0.46  37.1  156   69
partial-only    655  0.481 +0.358    13720 11/12    16180    -2460    -6572    -3131   0.68  19.5  287  166
runner-full     916  0.472 +0.348    21588 12/12    24328    -2740    +1577    -3411   0.61  13.7  466  181
no-tp-trail     253  0.145 +0.021    10586  4/12     9486     1099   -13265     +428   0.44  31.5  181   72

dollar sign-consistency vs control (per-cell net$ delta > 0):
bare 6/12 · be-only 6/12 · trail-only 4/12 · trail-ride 5/12 · partial-only 5/12 ·
runner-full 5/12 · no-tp-trail 4/12

position counts (rows minus PARTIAL rows): control 567 · bare 317 · be-only 447 ·
trail-only 253 · trail-ride 225 · partial-only 453 · runner-full 647 · no-tp-trail 253

engine drift (control vs census originals): 0/12 cells drifted — n and net$ EXACT on all 12
```

### Findings

**F70 — PartialTp row-splitting inflates row-level ExpectancyR; R-metrics are not comparable
across partial/non-partial variants.** `partial-only` gains +0.358 expR while LOSING 41% of
net dollars vs control; `runner-full` shows 12/12 per-cell expR sign-wins but only **5/12
dollar sign-wins**. Mechanism: each PartialTp position posts two TradeResult rows (R3 had
noted the row doubling but still evaluated on ExpectancyR); the 50%-at-1R row is a
mechanically near-guaranteed positive R. **Retro-effect: R3's "8/8 runner-aggressive" was an
ExpectancyR effect measured on split rows over a single window — it replicates here on expR
(12/12) and FAILS on dollars, split-half, and sign-consistency.** Family evaluation from now
on uses position-level dollars (this session's tables already do).

**F71 — `TakeProfit.Method` is a dead config knob for hand-rolled strategies.** The
`no-tp-trail` arm executed as an EXACT duplicate of `trail-only` (72 TP exits) despite the
run's own `EffectiveConfigJson` recording `takeProfit.method="None"`. Root cause:
`TrendBreakoutStrategy.cs:126` calls `SlTpHelpers.RRMultiple(..., pm.TakeProfit.RrMultiple, ...)`
directly and never reads `Method` (same pattern: `RsiDivergenceStrategy.cs:122/154`;
`MacdMomentumStrategy.cs:102` ignores PositionManagement.TakeProfit entirely, uses its own
param). `SlTpResolver`/`SlTpCalculator` both support "None", but these strategies don't route
through them. **"Disable TP" is currently not expressible for these families without a code
change.** Hypothesis (b) is untestable as pre-registered → bugfix-queue candidate; the pure
no-TP test re-runs after the fix.

**F72 — god-classes refactor is behavior-preserving on this slice.** The `control` arm
reproduced the census originals EXACTLY — trade count and net$ to the dollar on all 12 cells —
across the 9342fab refactor and every change since. S1 comparisons vs census-era conclusions
are apples-to-apples.

**Observations (logged, not chased):** (1) BE-less trail arms halve the closed-position count
(253–317 vs 567) — same unexplained pattern as R3's scalp-tight trade-count drops; suspected
long-hold × MaxConcurrent(3) interaction: exits change position lifetimes, which changes which
signals can enter. The factorial therefore measures the WHOLE-SYSTEM effect of an exit config,
not "same entries, different exits" — the entry-stream-identical comparison is the exit lab
(`RecordExcursions` + `ExitReplayer`), a candidate follow-up. (2) Everything good is
H1-concentrated (the F64 regime shift): even control−bare (+$10.1k in-sample) is an H1 story
(+$10.5k H1, −$0.4k H2). (3) In-memory idempotency keys can't dedupe across app restarts —
operational note for every future batch.

### Verdict (Gate G1, family 1 of 4 — trend-breakout)

**No exit-layer component survives D5 at family level for trend-breakout.**
- Leg (a) sign consistency ≥9/12: FAIL for every variant on dollars (best 6/12 — coin flip).
  The only ≥9/12 readings (runner-full 12/12, partial-only 11/12) are on the F70-contaminated
  expR metric.
- Leg (b) split-half both-halves-positive: FAIL for every variant (runner-full dH1 +$1,577 /
  dH2 −$3,411; partial-only fails both; be-only fails H1).
- Leg (c) walk-forward: NOT RUN — no variant passed legs (a)+(b), so nothing qualifies for WF
  (D5 requires all three; running WF on a failed candidate cannot rescue it).
- Hypothesis (a) refuted as tested: the R3 8/8 effect does not translate into a family-level
  dollar edge over the true control (BE + 2.5×ATR trail); it fails split-half exactly the way
  F64 failed cell selection. Hypothesis (b) untestable (F71). Hypothesis (c): the incumbent
  BE+trail beats bare by +$10.1k in-sample (H1-concentrated) — the control config earns its
  place, but it is the incumbent, not a new edge.

**mtf-trend park decision (D7) is NOT executed this session** — it belongs to the mtf-trend
family session (needs that family's own exit-factorial result first).

**Next session (S1.2):** either (i) proceed to the next family (`ema-alignment`) with this
session's dollar-based discipline, or (ii) fix F71 first so the no-TP arm is testable across
all remaining families in one pass — owner's call; (ii) is one small code change + tests and
makes the remaining factorials complete.

---

## S1.1-addendum + S1.2 — 2026-07-16 — F71 fixed; ema-alignment factorial (same session, owner delegated: "continue and i trust your vote")

### F71 fix (option ii chosen)

- `SlTpHelpers.TakeProfitFor(opts, entry, sl, direction, atr, symbol)` added — dispatches on
  `TpOptions.Method`: `"None"` → null TP; `"AtrMultiple"` → ATR distance; anything else →
  historical `RRMultiple(opts.RrMultiple)` (every seeded config uses RrMultiple, so the fix is
  behavior-preserving by construction). Call sites: `TrendBreakoutStrategy.cs`,
  `RsiDivergenceStrategy.cs` (both directions); `MacdMomentumStrategy.cs` honors `"None"` only
  (its TP distance stays its own `TpRrMultiple` parameter, unchanged). 3 unit tests pin
  None→null, RrMultiple→identical-to-historical, AtrMultiple→ATR distance.
- Gates after fix: build 0err/5warn · Unit **770**/0/6 · Integration 153/0/0 · Sim-fast 144/0/0.
- **Live behavior-preservation check:** `control/XAUUSD/H4` re-run post-fix (run `8a899381`):
  39 trades, net $13,082.44 — EXACTLY the census original. VERDICT: PASS.
- The 12 invalid-as-executed `no-tp-trail` ExperimentRun rows were deleted (their BacktestRuns
  stay as audit trail) and the arm re-executed post-fix under the same pre-registered labels.
  First corrected results confirm TP=None is now real: XAUUSD/H4 36→10 trades, ETHUSD/H4
  12→11, all long-hold profiles. Corrected family verdict appended below when the arm
  completes.

### Corrected no-tp-trail results (12/12 cells, TP genuinely off — 0 TP exits)

```
variant           n   expR  dExpR     net$ sign+      H1$      H2$      dH1      dH2 MFEcap gvbk%   SL   TP
control         567  0.123 +0.000    23422     -    22751      671       +0       +0   0.42  20.7  422  145
no-tp-trail     219  0.042 -0.082    -2351  6/12    -2351        0   -25102     -671   0.22  36.4  219    0
```

**Hypothesis (b) REFUTED for trend-breakout, decisively.** Removing the 2R TP cap (pure
ATR-1.0 trail, uncapped upside) takes the family from +$23.4k to **−$2.4k** (a −$25.8k swing),
DROPS MFE capture to 0.22 (control 0.42 — the trail gives back far more than the cap costs),
nearly doubles giveback (36.4% vs 20.7%), and chokes the trade stream (219 rows; 8/12 cells
below the D3 floor; zero H2 closes — multi-month holds + the MaxConcurrent(3) cap starve new
entries). Compared against `trail-only` (same trail WITH the 2R cap, +$10.6k): **the TP cap
adds ~$13k to the identical trail config.** F65's interpretation is now qualified in full:
the truncated MFE was real, but harvesting it by uncapping is strongly value-destroying for
this family — the 2R cap does protective work. The S1.1 verdict (no D5 survivor) stands and
is now complete across all 8 pre-registered arms.

### S1.2 pre-registration (D5 — BEFORE any ema-alignment run; same discipline as S1.1)

**Family:** `ema-alignment` (routes TP via `SlTpResolver` natively — no-TP arm valid without
the F71 fix, and valid with it). **Cells (all 6 census-scoreable):** EURJPY/H1, USDJPY/H1,
AUDUSD/H1, ETHUSD/H1, EURGBP/H1, USDCHF/H1. **Control** = strategy's own add-ons (F69: BE@1R +
2.5×ATR trail). **Variants:** the identical 8 arms as S1.1 (control, bare, be-only,
trail-only, trail-ride, partial-only, runner-full, no-tp-trail) with identical pack configs —
S1.1's packs reused verbatim. **Constants:** identical to S1.1 (census window, split
2025-12-03, tape, standard risk, 100k, 30/M, 1 pip, HonestFills, governor+regime on, D13).
**Runs:** 8 × 6 = 48, experiment `s1-exit-factorial-ema` (SpecJson = this entry), labels
`<variant>/ema-alignment/<SYM>/<TF>`, keys `s1-ema-alignment-<variant>-<sym>-<tf>`.
**Evaluation:** position-level DOLLARS (F70 discipline): pooled net$ delta vs control, per-cell
dollar sign-consistency (≥75% = ≥5/6), split-half both halves, MFE-capture/giveback deltas;
6-fold WF only if a variant passes the first two legs. **Note:** ema-alignment/EURJPY/H1 was
R3's v6a star (runner-aggressive +82% ExpectancyR) — this factorial retests that claim on
dollars at family level.

### S1.2 results (48/48 runs, experiment `23DA6546`, 0 failed, 0 warnings, 20 D13 nulls)

```
variant           n   expR  dExpR     net$ sign+      H1$      H2$      dH1      dH2 MFEcap gvbk%   SL   TP
control         215  0.048 +0.000    -1038     -     2236    -3274       +0       +0   0.42  23.1  163   52
bare            111 -0.042 -0.090    -4713   2/6    -1778    -2935    -4015     +340   0.48  43.3   77   34
be-only         184  0.011 -0.038    -3570   1/6     4026    -7596    +1789    -4322   0.49  31.2  128   56
trail-only      107 -0.027 -0.075    -4406   1/6    -1941    -2464    -4178     +810   0.40  41.5   82   25
trail-ride      107 -0.019 -0.067    -3971   2/6    -1169    -2801    -3406     +473   0.41  43.4   81   26
partial-only    241  0.394 +0.345    -3731   5/6    -1007    -2724    -3243     +550   0.67  20.6  114   58
runner-full     323  0.349 +0.301    -4459   6/6     1555    -6014     -681    -2740   0.59  14.1  184   60
no-tp-trail     106 -0.165 -0.213   -10127   1/6    -6232    -3895    -8468     -621   0.19  40.4  106    0

dollar sign-consistency vs control: best 3/6 (trail-only, partial-only) — coin flip.
engine drift: 0/6 (control == census exactly, again).
```

**Verdict: no D5 survivor for ema-alignment either — control (already net-negative on the
year, −$1,038) beats EVERY variant in dollars.** The F70 artifact replicates exactly:
`runner-full` +0.301 expR with 6/6 per-cell expR sign-wins, but 2/6 dollar wins and −$3.4k.
**R3's star v6a reproduced its +82% ExpectancyR on EURJPY/H1 (0.384→0.698 — R3's exact
number) while LOSING $1,109 vs its own control ($6,450 → $5,341).** The alpha-loop's single
strongest alpha claim is now demonstrated, on its own cell, to be the row-splitting artifact.
`no-tp-trail` again catastrophic (−$10.1k, MFE capture 0.19, 0 TP exits — F71 fix confirmed
working here too).

### Concurrency determinism probe (speed work, owner-requested)

`tools/research/determinism_probe.py --reference 2a914d70,20e90a70`: 4 tape runs launched
concurrently (2 copies × 2 cells) — **all byte-identical to their sequential references**
(trades exact; net to the 12th decimal: $13,082.435819450915 / $660.3900024746592).
**VERDICT: PASS — `exit_factorial_driver.py --parallel N` is safe on this machine/build.**
Validated for S4's re-census (3–4× wall-time cut on ~250-run batches).

### S1 CLOSE — early stop with reason (PLAN §2: "any stage may end early with a null-with-reason")

**Gate G1 verdict: the exit layer holds NO D5-surviving structural component in this bank.**
Evidence: the two HIGHEST-PRIOR families both refuted decisively — `trend-breakout` (D2's
designated start family, 12 cells, 8 arms: best dollar sign-consistency 6/12, every arm fails
split-half, no-TP −$25.8k swing) and `ema-alignment` (carrier of R3's best single result,
which replicated as an artifact: +82% expR / −$1.1k dollars). Both factorials show the same
mechanism (F70) with independent data.

**Why the remaining factorials (super-trend, mean-reversion, session-breakout-class) are NOT
run:** (1) Power — the 2026-07-16 quant review (`docs/QUANT-REVIEW-RESPONSE-2026-07.md`,
delivered in a parallel session) computes ~6,300 trades/arm needed to resolve effects of this
size; family arms deliver 100–900. A positive from a smaller family could not clear D5's own
bar even at 6/6 sign-consistency — the instrument cannot produce a bankable positive there,
only more confirmatory negatives. (2) The two families already run carried ALL of the prior
evidence (R3's 8/8 lived entirely in trend-style cells; v6a was ema-alignment). (3)
mean-reversion's "contrast" arm is near-degenerate anyway: its control IS bare (F69 — BE off,
trail None). Remaining engine-hours are better spent at the owner gate deciding between the
quant review's recommendations (Dukascopy 2019–24 backfill for pure-OOS power; exit-lab
entry-stream-identical calibration) and S2.

**D7 EXECUTED — mtf-trend parked at family level (4 census-scoreable cells: BTCUSD/H1,
EURJPY/H1, EURUSD/H4, USDJPY/H4; reversible `StrategyCellParks` rows).** D7's condition ("if
S1's exit fix does not lift it to ≥ 0 pooled expR") is met in the strongest form: S1 found no
exit fix at all, so no rescue lever exists for the bank-wide-worst family (F68, −0.22 expR).

**OWNER GATE (after S1, per plan) — what the owner decides next:**
1. Accept G1's negative verdict + the early stop (or order the remaining factorials anyway).
2. Direction for S2+: as planned (entry noise floor / regime conditioning on existing census
   trades — no new runs needed for hypothesis (b)), and/or fold in the quant review's
   data-power recommendation (Dukascopy backfill) which also unblocks honest power for
   everything downstream.
3. Ratify the D7 park (reversible).

---

## OWNER GATE ruling — 2026-07-16 (recorded by the quant-review session on the owner's instruction to merge all work and prepare the next session; owner may amend by a further ledger entry)

1. **G1 ACCEPTED, early stop ACCEPTED.** The exit layer holds no D5-surviving structural
   component in this bank; remaining factorials stay un-run (power, per the gate pack above).
2. **Direction: `iter-viability` ADOPTED** (`docs/iterations/iter-viability/PLAN.md`, drafted
   by the external quant review) as the successor program. Mapping of this iteration's
   remaining stages: S2(b) regime conditioning survives as a zero-run analysis in
   iter-viability Session 1; S2(a)/S3 are absorbed into V4/V3; S4–S7 are superseded by
   V2/V5/V7 (re-census happens as the frozen-bank pure-OOS census on backfilled data; the
   embargo dress rehearsal and portfolio phase keep their exact discipline inside V7).
   **Session 1 = V0 (FTMO rule-model truth) + the S2(b) regime analysis. Session 2 = V1
   (Dukascopy 2019–24 backfill).** This is the gate-vote next-move with V0 added (cheap, and
   it recalibrates the metrics everything downstream reports).
3. **D7 park RATIFIED** (mtf-trend, 4 cells, reversible rows stand).

This iteration's branch (`iter/structural-edge` @ 68aa53e) merges to `main` with this ruling;
its LEDGER stays the audit trail for S0–S1. Findings continue at **F73** in
`docs/iterations/iter-viability/LEDGER.md`. EMBARGO-2 remains untouched; the 2024 era-holdout
(iter-viability D3) is added alongside it.
