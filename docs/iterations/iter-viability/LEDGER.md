# iter-viability — Session Ledger (append-only)

**Started:** 2026-07-16 — iteration opened at the iter-structural-edge S1 owner gate (ruling
recorded in `../iter-structural-edge/LEDGER.md`, final entry). Findings continue at **F73**
(F1–F72 live in the alpha-loop / structural-edge ledgers and RESEARCH.md).

Every session appends below. Mid-session findings go here immediately (stall-kill safety).
Do NOT delete or edit prior entries — this is an audit trail. Every pre-registration includes
an MDE line (PLAN.md D1); every gate pastes queries/outputs, never asserts.

---

## Session 1 — 2026-07-16 — V0 rule-model truth + old-S2(b) regime conditioning (Lane R)

**Session mode:** MANUAL. Baseline QA (protocol step 1): gates re-run on `iter/viability` @
`d16ef99` before any work — build 0 err · Unit 770/0/6 · Integration 153/0/0 · Sim-fast
144/0/0 (93 s, `scripts/gates.ps1`) — matches the TRACKER handoff claim exactly.

### Pre-registration A — V0 challenge-model truth (not a stats experiment; scope + metric definitions pinned BEFORE implementation)

**Verification sources (fetched this session, 2026-07-16):** ftmo.com/en/trading-objectives/,
academy.ftmo.com/lesson/maximum-daily-loss/, academy.ftmo.com/lesson/minimum-trading-days/,
ftmo.com FAQ (Swing account type; news; weekend), ftmo.com blog (unlimited trading period),
ftmo.com/en/reward-growth-and-scaling-plan/. Rule-diff table to be pasted as the GV0 result
entry, every row cited.

**Metric definitions (unit-pinned, D1 spirit):**
- **P(bust before target)** — per run: anchored untimed challenge windows, one from every
  engine-truth trading-day start `s`: window = daily buckets `[s..end-of-run]`, simulated by
  `ChallengeSimulator` (verdicts Pass / Fail / Incomplete; Incomplete ≡ **censored** — history
  ran out before target or bust, NOT a fail under the verified unlimited-period rule).
  `P(bust) = Fails / (Passes + Fails)` over resolved windows only; censored count reported
  alongside. Unit: probability; denominator = resolved anchored windows.
- **E[time-to-target]** — mean over Pass windows of calendar days from window start date to
  resolution date, inclusive of both endpoints' dates (median also reported). Unit: calendar
  days. (Trading-day-bucket index `DayResolved` retained in the evidence trail.)
- **30d PassRate** — retained UNCHANGED as a *velocity index* (rolling 30-daily-bucket windows,
  Incomplete counts as non-pass). Stays the sv2 composite's survival component at unchanged
  weight so sv2 composites remain comparable; the new metrics are additive fields in
  `ScoreComponents` (D4-safe extension, no in-place semantic edit of the composite).
- **Simulator semantic corrections** (apply to all future scoring; old ExperimentRuns rows are
  never rewritten, D4): (i) daily-loss floor = previous trading-day **close balance** − 5% ×
  initial capital (verified formula; was: day's equity drop vs day-start equity), (ii) breach
  checks use min(day-start, day-close) equity (still daily-granularity-optimistic — intraday
  floating troughs are V6/L1-heartbeat scope, stated plainly), (iii) trading day counted when a
  trade is **opened** that day (verified definition; was: closed), (iv) daily reset corrected to
  **00:00 Europe/Prague** (config had 22:00 interpreted in the ruleset timezone = 2 h early
  year-round).
- **Config scope:** `ftmo-standard.json` corrected to verified 2-step **evaluation** semantics
  (news + weekend holding permitted during Challenge/Verification regardless of account type);
  new `ftmo-verification.json` (5% target) and `ftmo-swing.json` added; live Lane-R DB
  `PropFirmRuleSets` rows upserted to match (seeder is one-shot — `PropFirmRuleSetSeeder`
  returns early when rows exist, so a JSON-only edit would silently not propagate). Weekly (4%)
  / monthly (8%) loss caps are HOUSE rules (FTMO has none) — kept as governor safety brakes,
  ignored by `ChallengeSimulator`, marked house-only in the diff table.

**Pre-registered read-only re-examination:** the R4 candidate runs named in
`evidence/candidate-cards.md` get P(bust)/E[time-to-target] computed from their existing daily
equity buckets (read-only script; no ExperimentRuns writes, no new runs). Question on record
BEFORE computing: R4's verdict was "safe but too slow — 0/12 windows hit +10%/30d"; under the
verified unlimited-period rule, does "slow" convert to "viable" (low P(bust), finite E[time])?

### Pre-registration B — old-S2(b) regime conditioning (zero new runs; scored analysis)

**Data (frozen before analysis):** the 4,461 census trades of experiment `075D5240` (74 scored
cells, window 2025-07-04 → 2026-05-05), split H1/H2 at **2025-12-03** (same split as F64).
Market data: recorded cTrader tape (`tools/research/marketdata.db`), daily closes derived from
H1 bars per symbol. No BacktestRuns are created; EMBARGO-2 (post-2026-07-05) and the 2024
era-holdout are untouched (census window is inside 2025-07→2026-05).

**Family classes (pre-registered by strategy construction, not by observed performance):**
- `continuation` (breakout/trend-following): trend-breakout, ema-alignment, super-trend,
  mtf-trend, macd-momentum, session-breakout, bb-squeeze
- `contrarian` (mean-reversion/reversal): mean-reversion, rsi-divergence

**External regime variables (no performance data in their construction; labeled at entry with
data through the PRIOR completed UTC day only — no lookahead):**
- `RV20`: per-symbol stdev of daily log returns over the trailing 20 trading days; High = ≥
  that symbol's median RV20 over the census window.
- `ER20` (Kaufman): per-symbol |close_t − close_{t−20}| / Σ|daily Δclose| over the same 20
  days; High = ≥ that symbol's census-window median.

**Statistic & inference:** pooled **$ per trade** (position-level dollars, F70 convention —
census cells are solo $100k runs; expR reported as descriptive). Interaction contrast
`β = (cont_H2 − cont_H1) − (contr_H2 − contr_H1)` in $/trade; cluster bootstrap over ISO weeks
(resample weeks with replacement, 2,000 reps, percentile 95% CI) — weeks are the dependence
unit (F64 weekly-bucket correlation analysis). Same machinery for the class × regime
interaction and for regime-mix shares.

**Hypotheses (all three legs required for the positive verdict):**
- (i) class × half interaction β ≠ 0 (95% CI excludes 0);
- (ii) regime mix shifted H1→H2: share of ER20-High (primary) / RV20-High (secondary)
  trade-entry days differs between halves (CI excludes 0);
- (iii) class × regime interaction (full window, external labels) ≠ 0 with sign consistent
  with (i)+(ii) explaining the H1→H2 shift.
**Verdict rule:** all three → "regime-predictable" (earns V4e run-budget consideration ONLY if
the conditional family-class expR split ≥ 0.10R). (iii) alone → "regime sensitivity, half-shift
unattributed" (descriptive; no run budget). Otherwise → null-with-reason.

**MDE line (D1):** MDE_$ = 2.8 × SE_boot(β) at the fixed n (≈4,461 trades; exact SE from the
variance-only bootstrap pass, pasted below BEFORE the point estimate is unblinded). A null with
MDE_$ above the plausible-effect band is recorded as "not detectable at n", never "no effect".

**Pre-reg B amendments (recorded before unblinding, with reasons):**
1. **Tape path correction:** `tools/research/marketdata.db` (named above) contains no tables —
   the recorded tape lives at `src/TradingEngine.Web/data/marketdata.db` (`MarketDataBars`,
   H1 = 91,117 bars, 14/14 census symbols). Analysis uses that path
   (`tools/research/regime_conditioning.py`).
2. **Labeled subset:** tape H1 coverage starts exactly at census start (2025-07-04 per symbol;
   crypto 07-05) — there is NO pre-census history, so the first ~20 trading days per symbol
   cannot carry a 20-day lookback label. 892/4,461 trades (20%, the census's first ~5 calendar
   weeks) are unlabeled and excluded. The exclusion is calendar-only (external, outcome-blind),
   applied identically to both classes; labeled n = **3,569**.
3. **Blinded variance pass output (D1, pasted before any point estimate was seen):**
   ```
   (i)   class x half interaction:  SE_boot = $59/trade   MDE(2.8xSE) = $165/trade
   (iii) class x ER20 interaction:  SE_boot = $54/trade   MDE = $152/trade
   (iii) class x RV20 interaction:  SE_boot = $44/trade   MDE = $123/trade
   ```
   Context: at the census's ~0.5%-risk sizing 1R ≈ $450–500, so MDE_$(β) ≈ 0.3R — only a LARGE
   class×half divergence is detectable at this n. Pre-registered anyway; a small true
   interaction lands as "not detectable at n".

### Results — V0 rule-diff table (gate GV0 evidence; every row verified 2026-07-16 against FTMO's published terms)

| # | Item | FTMO truth (source) | Model before V0 | V0 action |
|---|---|---|---|---|
| 1 | Evaluation time limit | **NONE** — unlimited trading period for Challenge AND Verification (ftmo.com blog "Trade without any time limit"; removed for all accounts bought after the change) | sv2 survival = rolling **30-day** windows, Incomplete counted as non-pass — i.e. a hard 30-day time limit baked into the headline metric | **F74**: untimed anchored windows added; P(bust)/E[time] are the rule-truth metrics; 30d PassRate retained as velocity index only |
| 2 | Profit targets | Phase 1 = 10%, Phase 2 (Verification) = **5%**, funded = none (ftmo.com/en/trading-objectives) | single ruleset, 10% only | `ftmo-verification.json` (5%) added; phase sequencing = V6/V7 scope |
| 3 | Max daily loss — amount | 5% of **initial capital** (fixed $) | ✓ 5% InitialBalance base | unchanged |
| 4 | Max daily loss — reference | floor = **balance at previous midnight CE(S)T** − 5%·initial (academy MDL lesson, worked examples) | day's equity drop vs **day-start equity** | simulator corrected: floor hangs off previous day's close **balance** (`DailyEquityPoint.EndBalance`) |
| 5 | Max daily loss — detection | breached **intraday on floating equity**, "at any moment" | day-close equity only | partially corrected: min(day-start, day-close) equity; full intraday envelope = **V6** + L1 sub-bar heartbeat (stated optimism) |
| 6 | Daily reset | **midnight CE(S)T** | config `22:00` interpreted in `Europe/Prague` by `ResetClock` = 22:00 Prague — **2 h early year-round** (**F73**; the field name `dailyResetTimeUtc` lies — value is timezone-local) | configs → `00:00:00`; live-DB rulesets upserted (seeder is one-shot); `RunDataQuery.PropFirmDayOf` display bucketing aligned |
| 7 | Max total loss | 10% initial, static, any moment | ✓ amount; day-close detection | min(start,close) equity now; intraday = V6 |
| 8 | Min trading days | 4; trading day = CE(S)T day with ≥1 trade **OPENED** (multi-day hold counts entry day only; academy lesson) | counted days with a trade **closed** | `TradesOpened` counted (bucket-tiled so intra-bar opens between snapshots still land) |
| 9 | Weekly / monthly loss caps | **do not exist** at FTMO | 4% / 8% in rulesets | kept as HOUSE governor brakes (ChallengeSimulator ignores them); marked house-only here |
| 10 | News trading | Evaluation: **allowed** (all account types). Funded Standard only: no open/close ±**2 min** around selected high-impact news. Swing: never restricted (FAQ can-i-trade-news / ftmo-swing-account-type) | `allowTradesDuringNews: false`, window 30/15 min (enforcement toggle was already off — inert lie) | evaluation configs → allowed, window 2/2; `ftmo-swing.json` added |
| 11 | Weekend holding | Evaluation: **allowed** regardless of account type. Funded Standard: restricted. Swing: allowed (FAQ) | `allowWeekendHolding: false` (toggle also off) | evaluation configs → allowed |
| 12 | Equity definition | balance + floating P/L ± swaps − commissions | ✓ `BalancePlusFloatingMinusFeesAndSwaps` | unchanged |
| 13 | Scaling / payout (informative) | +25% balance per 4-month cycle (10% growth + 2 payouts + no violations + profitable 3/4 months), split 80/20 → 90/10, cap $2M, payout on demand ≥14 d (reward-growth-and-scaling-plan) | not modeled | informs funded-stage ceiling math; not simulated in V0 |
| 14 | Account types/sizes | 2-step Standard & Swing (Swing = 2-step only), $10k–$200k; 1-step exists with different MDL (3%) | standard only | swing + verification rulesets added; 1-step NOT modeled (out of scope) |

**Config/DB evidence:** `config/prop-firms/` — ftmo-standard corrected, ftmo-verification +
ftmo-swing added, ftmo-aggressive + raw reset-fixed (aggressive variant itself is legacy,
unverified — FTMO no longer advertises it; kept park-never-delete). Live Lane-R DB upsert
output (2026-07-16 19:58 UTC):
```
{'Id': 'ftmo-aggressive',   'reset': '00:00:00', 'target': 0.2,  'news': 0, 'weekend': 0}
{'Id': 'ftmo-standard',     'reset': '00:00:00', 'target': 0.1,  'news': 1, 'weekend': 1}
{'Id': 'ftmo-swing',        'reset': '00:00:00', 'target': 0.1,  'news': 1, 'weekend': 1}
{'Id': 'ftmo-verification', 'reset': '00:00:00', 'target': 0.05, 'news': 1, 'weekend': 1}
{'Id': 'raw',               'reset': '00:00:00', 'target': 0,    'news': 1, 'weekend': 1}
```
Note: the DB rows' news/weekend **enforcement toggles were already off** (NewsFilterEnabled /
WeekendFilterEnabled = false), so census/R4 runs were NOT distorted by the wrong flags — the
lie was in the declared values, not in enforced behavior. The reset-time error (F73) DID affect
engine day-bucketing of every prior run (buckets 2 h early); per-run bucketing stays internally
consistent, so windowed metrics shift only marginally at day edges.

### Results — F74: R4 candidates re-examined under untimed rules (pre-registered read-only script, committed as `tools/research/r4_untimed.py`, corrected semantics)

```
=== 9c98ce41 trend-breakout/XAUUSD/H4+runner-aggressive (63 trades, net $12,450, 319 buckets) ===
  Phase1 10%: untimed 38P/0B/281C  P(bust)=0.000  E[t]=122d med=124d | 30d velocity 0/290 = 0%
  Phase2  5%: untimed 120P/0B/199C P(bust)=0.000  E[t]=62d  med=62d
=== baf739ad (31 trades, net $4,892, 292 buckets) ===
  Phase1 10%: 0P/0B/292C  P(bust)=n/a — target never reached from ANY anchor in 10 months
  Phase2  5%: 0P/0B/292C  P(bust)=n/a
=== 38b4d82f ema-alignment/EURJPY/H1 (58 trades, net $5,341, 303 buckets) ===
  Phase1 10%: 0P/0B/303C — never reached;  Phase2 5%: 23P/0B/280C E[t]=159d med=144d
=== 6d8c8fa0 (39 trades, net $13,110, 303 buckets) ===
  Phase1 10%: 33P/0B/270C  P(bust)=0.000  E[t]=129d med=125d | 30d velocity 0/274 = 0%
  Phase2  5%: 138P/0B/165C P(bust)=0.000  E[t]=65d  med=60d
```

**F74 verdict: the predicted inversion is REAL but partial.** Under the verified unlimited
period, 2 of 4 R4 candidates (`9c98ce41`, `6d8c8fa0`) convert from "safe but too slow" to
**viable-but-slow**: ZERO busts across every anchored window (resolved AND censored — no window
ever breached), Phase-1 target reached in ~4 months median, Phase 1+2 ≈ 6–7 months at 1×
census sizing. The other 2 never reach +10% from any anchor in 10 months of history — untimed
rules don't rescue them (velocity ≈ 0 remains disqualifying in expectation terms). Caveats on
record: E[t] conditions on resolved anchors (downward-biased under censoring — slow anchors
censor first); single-cell, 1× sizing; embargo-period forward evidence (R4) still stands as the
OOS caution. Compression of E[t] via sizing/risk-policy is exactly V6's MC question, now
well-posed.

### Results — old-S2(b) regime conditioning (pre-registered; `tools/research/regime_conditioning.py`, labeled n=3,569)

```
(i) 2x2 class x half — $/trade (n) [expR]
  cont   H1: -34.0 $/t (n=2058) [-0.033R]   H2:  -5.0 $/t (n=650) [+0.050R]
  contr  H1: -51.3 $/t (n= 567) [-0.030R]   H2: +15.0 $/t (n=294) [+0.190R]
  interaction beta = -$37.2/trade   95% CI [-148.5, +82.8]   (MDE $165)
(ii) regime mix H1 -> H2
  ER20-High day-share 0.434 -> 0.556 (+0.122); trade-share delta CI [+0.054, +0.268]  EXCLUDES 0
  RV20-High day-share 0.409 -> 0.577 (+0.167); trade-share delta CI [-0.035, +0.278]  includes 0
(iii) class x regime, full window
  ER20 gamma = +$38.7/trade  CI [-61.3, +146.7]   (MDE $152)
  RV20 gamma = +$41.3/trade  CI [-41.9, +132.2]   (MDE $123)
conditional expR splits: ER20 cont 0.010R / contr 0.012R; RV20 cont 0.031R / contr 0.173R
```

**F75 verdict (pre-registered rule): NOT regime-predictable — null-with-reason.** Legs (i) and
(iii) fail (CIs include 0); only leg (ii) holds for ER20 (the external regime genuinely shifted
toward trending/higher-vol in H2). Per D1 this is recorded as **"not detectable at n"** — the
MDEs ($123–165/trade ≈ 0.25–0.33R) sit far above plausible interaction sizes; the test was run
because it was free, not because it was powered. No run-budget is earned (verdict rule requires
all three legs). **Descriptive flag for V4e (hypothesis, NOT a finding):** contrarian class in
RV20-Low regime: +0.122R (n=478) vs −0.051R in RV-High — a 0.17R conditional split that clears
the 0.10R interest threshold but fails the verdict rule; it goes to the backfilled 2019–2023
data (V2/V4) where ~6× the trades exist to test it honestly. Descriptive caveat: pooled
per-trade expR IMPROVED in H2 for both classes while F64's cell-count lens showed H2 thinner —
the two lenses measure different things (selection persistence vs pooled mean); no
contradiction, but worth remembering when quoting "H2 was a bad regime".

### Session 1 close

- **Findings: F73** (daily-reset config error, 2 h early, + misleading field name; display
  bucketing aligned), **F74** (untimed-rule inversion: 2/4 R4 candidates viable-but-slow, 0
  busts anywhere; 2/4 never resolve), **F75** (regime conditioning: not detectable at n;
  ER-regime mix shift real; RV-Low×contrarian 0.17R split → V4e hypothesis). Findings continue
  at **F76**.
- **Account-type recommendation for the GV0 owner signature: FTMO Swing, $100k, 2-step.**
  Rationale: evaluation terms are identical to Standard (verified — restrictions only bite on
  the funded account); the bank holds multi-day (rsi-divergence median 87 h) and the V4
  weekend-gap family wants Monday entries; Swing removes the funded-stage weekend/news tax at
  the same price. The 1-step variant (3% MDL) is NOT recommended — the intraday-breach model
  (V6) must exist before tighter daily caps are negotiable. **[OWNER GATE GV0 — awaiting
  signature on account type; everything else in V0 is evidence-complete]**
- sv2 extension shipped: `ScoreComponents` + `ChallengeSurvival` carry
  PBustBeforeTarget / ETimeToTargetDays / MedianTimeToTargetDays / untimed P/B/C counts;
  composite formula and 30d PassRate weight UNCHANGED (D4-safe, scores stay comparable).
- EMBARGO-2 and 2024 era-holdout untouched: zero BacktestRuns created this session (analyses
  were read-only; the only DB writes were the 5 PropFirmRuleSets config rows).
- Gates (paste, `scripts/gates.ps1`, post-change):
  ```
  Build succeeded. 0 Error(s)
  Unit         Passed! Failed: 0, Passed: 773, Skipped: 6
  Integration  Passed! Failed: 0, Passed: 155, Skipped: 0
  Sim-fast     Passed! Failed: 0, Passed: 144, Skipped: 0
  ```

---

## Session 2 — 2026-07-16 — V1 backfill + importer (Lane R; same conversation as Session 1, context carried)

**Baseline QA:** tree clean at `1e827a7`; Session-1 gates (above) are 30 min old with no code
changes since — accepted as current baseline.

### Pre-registration — V1 import + overlap validation (criteria fixed BEFORE reconciliation)

**Source:** Dukascopy public datafeed, per-day per-symbol `BID_candles_min_1.bi5` +
`ASK_candles_min_1.bi5` (raw-LZMA; URL months are 0-BASED). 14 census symbols (EURUSD GBPUSD
USDJPY USDCHF USDCAD AUDUSD NZDUSD EURGBP EURJPY GBPJPY XAUUSD XAGUSD BTCUSD ETHUSD).
**Index CFDs: DEFERRED with reason** — no captured cTrader venue specs exist for index symbols;
the tape would price them off fabricated specs (the F44 failure mode). Shortlisting moves to the
V4 build-out after a one-run venue-spec capture per index.

**Decode discipline (evidence pasted before bulk import):** 24-byte big-endian records; field
order ([t,o,c,l,h,v] vs [t,o,h,l,c,v]) chosen by OHLC invariants on real data; per-symbol price
scale (10^k) determined empirically by ratio against the recorded cTrader tape's same-day close;
day-offset base (UTC vs venue time) verified by the reconciliation alignment check.

**Storage semantics (matches `TapeReplayAdapter` P0.2/D3):** bars are **BID** OHLC;
`Spread = askClose − bidClose` per M1 bar, in PRICE units (ask = bid + spread); derived bars'
spread = **median** of constituent M1 spreads; volume = Dukascopy lot-volume sum (unit differs
from cTrader tick-volume — recorded, never compared across sources). `Source='dukascopy'`,
`Quality=0`; the (Symbol,Timeframe,OpenTimeUtc) unique index makes every import idempotent
(INSERT OR IGNORE).

**Import scope:** 2019-01-01 → 2024-12-31 ONLY into `MarketDataBars`. The 2025+ overlap
download stays in the staging archive (`data/backfill/`) — the recorded cTrader tape remains
the sole 2025+ truth in the engine DB. **Disk constraint (recorded):** C: has 5.7 GB free;
the full M1 import (~32M rows ≈ ~6 GB) does NOT fit. This session imports **H1/H4/D1/M15**
derived bars (~2.8M rows ≈ ~0.5 GB) plus the raw compressed archive (~1 GB source of truth,
re-importable any time). **M1 + M5 import is DEFERRED-with-reason** (disk) — command ready
(`python tools/backfill/dukascopy.py import --timeframes M1,M5`); consequence stated plainly:
until M1 lands, 2019–24 tape runs fill on decision-bar granularity instead of M1 fine bars —
a fidelity difference vs the 2025 census that V2's pre-registration must either remove (owner
frees ~8 GB) or carry as a stated sensitivity caveat.

**Overlap reconciliation (2025-07-04 → 2026-05-05, derived H1 + H4 vs recorded cTrader tape),
criteria fixed now:**
1. **Time alignment:** best close-match offset over {−3..+3} h must be 0 for every symbol
   (a nonzero best offset = timestamp-base finding).
2. **Bar-count parity** on common trading days, per symbol (venue session/holiday differences
   reported, not silently absorbed).
3. **Matched-bar close deltas:** median |Δclose| per symbol in pips — expectation ≤ ~1 pip for
   FX majors (different liquidity pools); metals/crypto reported on their own scales. A
   one-sided sign bias (>60% same sign) = systematic finding.
4. **Spread sanity:** per-symbol median per-bar spread reported next to the venue
   `TypicalSpread`; every spread ≥ 0; spread medians wildly off venue reality (>5×) = finding.
Any systematic divergence is a **finding, not a shrug** (PLAN V1).

**Era-holdout guard (D3, gate GV1):** after import, paste
`SELECT COUNT(*) FROM BacktestRuns WHERE BacktestFrom <= '2024-12-31' AND BacktestTo >= '2024-01-01'`
— must be 0 and stays 0 until the V-final gate ledger entry exists. (2024 bars EXIST in the DB
by design; the holdout is on *runs*, exactly like EMBARGO-2.)

### Evidence — decode discipline pinned (probe 2025-07-07, all 14 symbols; `tools/backfill/dukascopy.py probe`)

- **Field order = [t, open, close, low, high, volume]**: 0 OHLC-invariant violations on every
  symbol vs 1,188–1,356 violations/1,440 bars for the [t,o,h,l,c] alternative. Decisive.
- **Timestamp base**: offsets 0..86340, all divisible by 60 → seconds from the file's UTC day
  start, 1,440 M1 candles/day (final cross-check = reconciliation offset leg, must be 0 h).
- **Per-symbol price scales** (ratio vs recorded-tape same-day H1 close median, all ≈1.000):
  1e5 EURUSD GBPUSD USDCHF USDCAD AUDUSD NZDUSD EURGBP · 1e3 USDJPY EURJPY GBPJPY XAUUSD XAGUSD
  · 1e1 BTCUSD ETHUSD. Stored in the archive's `Meta` table with evidence strings.
- **Ops note:** HTTPS to `datafeed.dukascopy.com` stalls ~75 s/request from this network while
  plain HTTP answers in ~0.1 s (measured; 503s under 16-way HTTPS parallelism). The tool uses
  HTTP deliberately — public data, and every bar is validated against the recorded venue tape.

**Cost-conservatism decision point flagged for V2's pre-registration (D3):** the imported
Spread column stores Dukascopy's TRUE recorded per-bar spread. Dukascopy is an ECN-style feed —
its spreads are likely TIGHTER than the CFD venue's constant (`VenueSymbolSpecs.TypicalSpread`,
all 14 symbols captured 2026-07-15). Using them raw would be cost-OPTIMISTIC. Era-conservative
semantics (D3 "recorded per-bar spread where available, else ≥ today's") therefore belong in
the RUN layer — V2 must pre-register its spread policy (recommended: per-bar
`max(barSpread, TypicalSpread)`), never by distorting stored data.

### Results — overlap reconciliation (gate GV1 evidence; derived H1/H4 vs recorded cTrader tape, 2025-07-04 → 2026-05-05, 99.3% file coverage)

```
symbol   tf  offs matched tapeOnly dukaOnly med|dC|bps  p90bps sign+%    medSpr  venueSpr
EURUSD   H1    +0    5164        0       30       0.17    0.34   11.8   0.00004   0.00010
EURUSD   H4    +0    1172      120      132       0.17    1.53   11.8   0.00004   0.00010
GBPUSD   H1    +0    5142       21       31       0.22    0.51   11.6   0.00007   0.00010
USDJPY   H1    +0    5139       24       31       0.13    0.34   23.5   0.00400   0.01000
USDCHF   H1    +0    5140       24       30       0.39    0.87    5.4   0.00008   0.00010
USDCAD   H1    +0    5164        0       30       0.37    0.58    4.4   0.00013   0.00010
AUDUSD   H1    +0    5163        0       31       0.60    0.92    4.7   0.00009   0.00010
NZDUSD   H1    +0    5094       69       29       0.70    1.20    4.7   0.00010   0.00010
EURGBP   H1    +0    5137       27       30       0.35    0.69    5.0   0.00007   0.00010
EURJPY   H1    +0    5139       24       31       0.16    0.44   12.5   0.00900   0.01000
GBPJPY   H1    +0    5163        0       31       0.20    0.60   25.6   0.01600   0.01000
XAUUSD   H1    +0    4850       29       53       0.61    1.14    2.7   0.63000   0.01000
XAGUSD   H1    +0    4731      145       57       1.35    6.31   41.2   0.03800   0.01000
BTCUSD   H1    +0    7146      143       55       5.13    9.70   83.6  50.00000   0.10000
ETHUSD   H1    +0    7196       71       77       3.51    7.36   29.2   4.00000   0.10000
(H4 rows omitted here for brevity — same shape; full output in the session task log and
reproducible via `python tools/backfill/dukascopy.py reconcile --from 2025-07-04 --to 2026-05-05`)
```

**Criteria vs outcome:**
1. **Time alignment: PASS** — best offset 0 h for all 28 symbol×TF combos (UTC file base +
   venue EET/EEST H4/D1 bucketing both confirmed against the venue's own bars).
2. **Bar counts: PASS with explanation** — initial run showed ~2,180 dukaOnly H1 bars/symbol;
   root cause verified (not shrugged): Dukascopy day files pad closed-market hours with
   zero-volume flat filler rows (Saturday = 1,440/1,440 filler; Sunday pre-21:00 = 1,260;
   weekdays ≈ 6). Importer now skips volume==0 records — which also matches cTrader's own
   bar-emission (no bar for a tickless minute). Residual dukaOnly ≈ 30 (H1) / ~130 (H4) and
   tapeOnly ≈ 120 (H4) are Sunday-session-open edge bars, symmetric and small. Crypto
   (24/7) showed no such gap — consistent with the mechanism.
3. **Close deltas: PASS** — FX medians 0.13–0.70 bps (≈0.1–0.7 pip), p90 ≤ 1.2 bps H1;
   metals/crypto 1.3–5.3 bps (different liquidity pools, expected).
4. **Spread sanity: PASS for Dukascopy, FINDING for the venue constant** — per-bar medians:
   FX 0.4–0.7 pip, XAU $0.63, BTC ~$50: plausible ECN levels, all ≥ 0.

**F76 — systematic half-spread-level bid offset between feeds.** The recorded tape's close
sits ABOVE Dukascopy's bid close in 74–98% of matched FX bars (sign+% column), magnitude ≈
half of Dukascopy's spread (sub-pip); direction INVERTS for BTCUSD (84% duka-above). This is a
feed-level difference in effective bid definition — corroborating the "tape half-spread bias"
suspicion from iter-quant-model, now measured. **Consequence:** benign for 2019–24 research
(decisions and fills use one self-consistent source; the ≥2025 import refusal already prevents
source-mixing inside a window); relevant only to cross-source *level* comparisons, which
nothing planned does at sub-pip resolution.

**F77 — `VenueSymbolSpecs.TypicalSpread` is a placeholder, not a measurement.** Every captured
value equals exactly 1 × PipSize (FX 0.0001, JPY/metals 0.01, crypto 0.1). For XAUUSD that
claims a $0.01 spread against a real-world ~$0.30–0.60 — nonsense. Two consequences: (a) the
tape's legacy constant-spread fallback has been UNDER-charging spread cost on metals/crypto
whenever per-bar spread was absent (all recorded bars have Spread=NULL → every metals/crypto
tape run to date); (b) the V2 era-conservative floor CANNOT be `max(barSpread, TypicalSpread)`
with these values — V2's pre-registration needs real venue spread estimates (live tick capture
or FTMO published typicals). Filed for L1/V2; no code change this session (the fix belongs
with the V2 spread-policy decision, not buried here).

### Owner directions logged mid-session (2026-07-16 evening) — deployment/ops architecture, NO action this session

Owner statements, recorded verbatim in intent and mapped to where they land; these shape L2/L3
design and any future deployment iteration:

1. **Separate the live engine from backtest/research.** Partially in plan already (D9 lane
   separation; L3 puts live on its own always-on box; "one app per DB file" doctrine). Owner
   elevates it to a DEPLOYMENT principle: the live trading process must not share a process —
   and ideally not a machine — with research/backtest workloads. Lands in: L3 design.
2. **Docker-friendly.** Constraint recorded so nobody plans a fantasy: the LIVE leg cannot be
   containerized — unattended live requires cTrader **Desktop** in listen mode (Windows GUI;
   the headless CLI is backtest-only, F-established). Docker therefore applies to the
   research/backtest/web side (.NET 10 + SQLite volumes are container-clean). Live stays a
   Windows VPS with cTrader Desktop + engine service. Lands in: L3.
3. **Logs sufficient for full trace** — running unattended on a VPS means the logs ARE the
   debugger. Structured pipe-format logging exists (e.g. `SETUP_SCORE|…`); L3 already requires
   alerting on disconnect / order-rejection rate / breach proximity / missed heartbeat; owner
   adds: end-to-end traceability of every order decision from signal to venue ack. Lands in:
   L1 (heartbeat) + L3 (runbook/alerting).
4. **Telegram integration** for remote alert + control from anywhere. New item — alerts first
   (cheap, read-only), control commands only with explicit auth design (a control channel is an
   attack surface). Lands in: L3.
5. **Dashboard:** either a minimal live dashboard or the existing web dashboard with live
   cleanly separated from backtest — without duplicating the stack (owner: no DRY violation).
   Lands in: L3/UI backlog; note `RunQueryService` is already split behind `ILiveRunReader`
   (god-classes refactor), which is the seam such a separation would build on.

No code or plan-stage changes made for these this session — logged for the L-track design
sessions (the EMBARGO-2 wait window, Aug–Sep, is the natural slot per PLAN §7).

6. **Multi-prop replication (owner, added later same session):** once a pass exists, open
   accounts at OTHER prop firms with the same engine — small per-firm returns × several firms'
   capital beats waiting on one firm's scaling plan. Machinery note: the rule model is already
   firm-generic (`PropFirmRuleSets` config + `ChallengeSimulator` take any ruleset), so a new
   firm costs one verified-terms JSON (a V0-style rule-truth pass against THAT firm's contract —
   never assume FTMO semantics transfer; trailing-drawdown firms are materially harsher for a
   multi-day bank and some firms keep time limits/consistency rules). Also multiplies L3 ops
   surface (one VPS/process per firm session) and payout/firm-solvency risk is diversified.
   Lands in: V6 (per-firm P(bust)/E[time] under each firm's verified rules) + L3/L4.

### Delivered while the backfill downloads — L1 correctness-before-money fixes (PLAN §7 L1; sanctioned concurrent work)

- **F26 FIXED:** `PreTradeGate.CandidateWorstCase` now dispatches on `CommissionType`. The old
  per-lot read of a $45/M rate overstated FX round-trip commission ~9× and rejected trades near
  the daily floor that cost cents. The pure kernel needs no cross-rate service:
  `notional(account) = lots × price × PipValuePerLot / PipSize` (PipValuePerLot is already
  account-currency). Pinned by `PreTradeGateCommissionTests` — same near-floor scenario accepts
  under per-million math AND still rejects under genuine per-lot pricing.
- **F28 FIXED (fail-loudly form):** venue-declared `SwapCalculationType` now travels
  `VenueSymbolSpec → SymbolInfo`; `TradeCostCalculator` throws `NotSupportedException` on any
  non-Pips denomination with a nonzero rate instead of silently pricing it with the
  Pips formula (the only venue-verified one, P4.4/F45). Adapter catches → run warned →
  unscoreable. Zero rates pass (0 is 0 in every denomination). Pinned by
  `TradeCostSwapTypeTests` incl. the −$24.45 Pips regression value.
- **UNIQUE start-record race FIXED** (god-classes SURVEY debt): `SqliteBacktestRunRepository.
  SaveAsync` catches the UNIQUE violation from the queued-record writer winning the race and
  re-applies the write as an update — the queued→running status upgrade can no longer be
  silently lost. Race reproduced deterministically via a save-interceptor rival writer
  (`BacktestRunStartRecordRaceTests`).
- NOT touched: sub-bar account heartbeat (cBot change → requires live compare-both; queued
  behind the L0 smoke debt). Gates after: build 0 err · Unit 778/0/6 · Integration 156/0/0 ·
  Sim-fast 144/0/0.

### Delivered while the backfill downloads — V5 tooling pre-delivery: stationary block bootstrap + MDE calculator

`tools/research/block_bootstrap.py` (stdlib-only, importable): Politis–Romano stationary
bootstrap SE/CI + the D1 MDE formula. GV5 discipline honored ahead of schedule — synthetic
hand-checks pasted, not asserted:
```
(a) iid N(0,1) n=2000: block-bootstrap SE=0.02336  theory=0.02236  err=4.5%
(b) AR(1) phi=0.6 n=2000: block SE=0.04090  theory=0.04472  err=8.5%  | naive iid SE=0.02196 (must understate)
(c) MDE(SE=1) = 2.8016  (expected 2.8016);  z(0.975)=1.9600 (expected 1.9600)
SELFTEST PASS
```
Case (b) is the reason the tool exists: on serially dependent data the naive bootstrap
understates the SE by ~2× — exactly the overconfidence D5′ is designed to kill. V2's gate
tables use this module; the V5 session still owes EB shrinkage + stitched walk-forward.

### Results — V1 download complete + import executed (gate GV1 evidence, part 2; owner asked "once all the download done, log it" — this is that log)

**Archive (durable, outside repo — `C:\ShamshirData\backfill\dukascopy-raw.db`, 708 MB blobs;
read-only snapshot `dukascopy-raw.snapshot-20260717.db`, 778 MB):**
```
75,078 / 75,096 files (99.976%) — 2019-01-01 -> 2026-05-05, 14 symbols, BID+ASK M1 daily files
per symbol: EURUSD/GBPUSD/USDCAD/USDCHF/USDJPY/AUDUSD 100% · XAUUSD/GBPJPY/EURGBP/ETHUSD -1
· XAGUSD -2 · BTCUSD/EURJPY -3 · NZDUSD -6   (18 sticky-301 files ≈ 18 symbol-days of 37,548;
retryable any time via `download`; the earlier 61 overlap stragglers all recovered in sweeps)
```
Range note: sweeps also filled 2025-01-01→07-03 (a gap between the two planned windows) —
archive-only by construction; the import refuses ≥ 2025.

**Import into `MarketDataBars` (Source='dukascopy', 2019-01-01 → 2024-12-31 ONLY), verified:**
```
D1     23,034 (spread 100%)      2019-01-01 22:00 -> 2024-12-30 22:00   [venue-day 21/22:00 UTC opens]
H1    541,519 (spread 99.99%)    2019-01-01 22:00 -> 2024-12-31 21:00
H4    136,529 (spread 99.99%)
M15 2,165,344 (spread 99.99%)
total 2,866,426 bars; dukascopy rows >= 2025-01-01: 0; ctrader rows: 6,906,483 (untouched)
sample EURUSD H1 2020-03-16 00:00: O 1.1156 C 1.11177 spread 0.00011 (1.1 pips — COVID-era
width captured; era-conservative costs per D3 are now DATA, not assumption)
```

**Era-holdout + embargo guards (post-import paste):**
```
era-holdout guard (runs intersecting 2024, started >= 2026-07-16): 0
embargo-2 guard (runs from >= 2026-07-06): 0
```

**M1 amendment (append-note, reason recorded):** the pre-registration deferred M1/M5 on disk
(5.7 GB free). During the session a WAL checkpoint + temp cleanup freed the drive to 11.9 GB —
the deferral's sole reason lapsed, so the **M1 import was executed** (~32M rows, reversible via
`DELETE ... WHERE Source='dukascopy' AND Timeframe='M1'`); result appended below when it
completes. M5 stays deferred — nothing consumes it. With M1 present, 2019–24 tape runs fill on
fine bars exactly like the 2025 census — the V2 fill-granularity caveat DISSOLVES.

**M1 import completed (append):**
```
M1 rows: 32,170,346  withSpread: 32,167,301 (99.99%)  range 2019-01-01 22:00 -> 2024-12-31 21:59
leak >= 2025: 0   disk after: 5.2 GB free
```
2019–24 tape runs now fill on M1 fine bars with true per-bar spreads — same fill machinery as
the 2025 census, better cost truth. Total backfill in `MarketDataBars`: **35,036,772 bars**.

**GV1 gate checklist:** overlap reconciliation table pasted (above) ✓ · per-bar spread present ✓
· era-holdout flagged as a standing guard query with named baseline ✓ · importer + archive
durable and re-runnable ✓ · M1 fine bars imported ✓. **GV1: evidence complete.**

### Evidence — era-holdout guard baseline (run BEFORE import)

```
era-holdout guard (runs intersecting 2024): 4
embargo-2 guard (runs from >= 2026-07-06): 0
```
The 4 rows are pre-D3 debris, examined individually: `209a08f1`, `8f3d8367`, `3ddd35e4`,
`44797180` — all replay-venue EURUSD/h1 attempts on 2024-01 dated 2026-07-08, all errored
"No bars found for any symbol/timeframe combination", 0 trades. They could not have touched
2024 data because none existed in any DB before this import. Kept (park-never-delete), named
here as the guard's permanent baseline. **Guard condition from this entry forward:** zero rows
from `SELECT COUNT(*) FROM BacktestRuns WHERE BacktestFrom <= '2024-12-31' AND BacktestTo >=
'2024-01-01' AND StartedAtUtc >= '2026-07-16'` until the V7 era-holdout gate entry exists.

---
## Session 3 — 2026-07-17 — V2 frozen-bank pure-OOS census: pre-registration (Lane R)

**Session mode:** MANUAL. Baseline QA (protocol step 1): gates re-run on `iter/viability` @
`bfa1bfd` before any work — build 0 err · Unit 778/0/6 · Integration 156/0/0 · Sim-fast
144/0/0 (`scripts/gates.ps1`) — matches the Session-2 close and TRACKER handoff exactly.
Session-2's M1 append-note was closed out by the import-owning session before this entry was
appended (LEDGER one-writer, D9).

### Pre-registration — V2 frozen-bank pure-OOS census 2019–2023 (gate GV2, OWNER)

Everything in this section was fixed BEFORE any 2019–2023 run was created. The 2019–2023
window has never been scored by any strategy in this repo's history (the data itself arrived
in Session 2, postdating all strategy development) — this is the program's cleanest OOS test
and the gate that decides its center of gravity (PLAN V2).

**Census definition (replicates experiment `075D5240` exactly, new window):**
- Cells: the frozen bank — 9 strategies × 14 symbols × {H1, H4} = **252 one-cell runs** (D13).
  Strategies: bb-squeeze, ema-alignment, macd-momentum, mean-reversion, mtf-trend,
  rsi-divergence, session-breakout, super-trend, trend-breakout. Symbols: the 14 census
  symbols. **Parked cells are included** (4 mtf-trend D7 cells + 2 R3 cell-parks): parks bind
  candidacy, not research — verified in code that `StrategyCellParks` has no run-creation
  enforcement; symmetric evidence is the point, and GV2 is where residence/park decisions get
  updated.
- Window: **2019-01-01T00:00 → 2023-12-31T00:00** (To < 2024-01-01 keeps the era-holdout guard
  clean by construction; Friday 2023-12-29 is the last tradable day either way). 2024 untouched
  (era-holdout, D3); 2025+ terminal holdout; EMBARGO-2 untouched.
- Run config, identical to 075D5240: venue=tape, $100k solo, riskProfileId=standard
  (0.5%/trade), CommissionPerMillion=30, SpreadPips=1 (inert on tape — kept for exactness),
  Seed=42, honestFills, governor+regime enabled, PackId=null (bank defaults), speed=10.
  Indicator warmup is cold-start from window start, same as the census. Strategy configs are
  the frozen bank as of `bfa1bfd` — which INCLUDES the F71/F26/F28 dead-knob and cost-truth
  fixes (D8: dead knobs get fixed, logic is not rewritten). The shared `ConfigSetId` of the
  batch will be pasted in results as the frozen-bank fingerprint.
- Data: `MarketDataBars` Source='dukascopy' 2019–2024 (bid OHLC + per-bar Spread, 99.99%
  coverage; M1 fine bars present → fill granularity identical to the 2025 census). No source
  mixing is possible inside the window (import refuses ≥2025; unique (Symbol,TF,OpenTimeUtc)).
- Scoring: sv2 into a new experiment (`v2-frozen-bank-oos`), same Scoring weights as 075D5240
  ({PassProbability:0.4, ExpectancyR:0.3, MaxDrawdown:0.2, FoldConsistency:0.1}) so composites
  stay comparable (D4). Driver: `tools/research/census_driver.py` (committed this session;
  exit_factorial_driver pattern: resume-by-VariantLabel, label `census/{strategy}/{sym}/{tf}`,
  per-run timeout 90 min, `--parallel 3` — determinism probe PASS from S1 covers concurrent
  tape runs on this machine).

**Spread policy (the F77 decision, fixed now):**
- **Primary: raw recorded per-bar Dukascopy spread** — this is what the engine already does
  (`TapeReplayAdapter` P6.2: per-bar Spread when present; `TypicalSpread` fallback is reachable
  on <0.01% of bars, so F77's degenerate constants are immaterial here). D3's letter
  ("recorded per-bar spread where available") is satisfied by data, not assumption.
- **Why raw is defensible as the base case:** F76 measured the venue's effective bid offset at
  Dukascopy half-spread scale for FX (venue spread ≈ duka spread scale); duka's XAUUSD median
  ($0.63, 2025 overlap) is at/above the real-world venue range (~$0.30–0.60, F77); FX medians
  0.4–0.7 pip sit at/above typical raw-account majors' spreads while runs also charge $30/M
  commission (≈ $6/lot round trip). The known residual risk is duka-tighter-than-venue on some
  symbols/eras — bounded by the stress below, not by an unverifiable floor table (FTMO's
  published per-symbol spreads are dynamic/JS-only, nothing citable to pre-register; noted for
  L-track live tick capture to close properly).
- **Sensitivity (pre-registered, post-hoc analytic): re-report every family verdict under
  1.5× and 2× spread stress.** Per trade: ask-side fill minute m (entry for Long, exit for
  Short — P0.2 full-spread convention), s_m = duka M1 bar spread at m (fallback: nearest prior
  M1 within 2 h, else symbol-median), Δ$ = (k−1) × (s_m / PipSize) × PipValuePerLot × lots,
  subtracted from NetPnl; family re-pooled, CIs recomputed. Limitation stated plainly: analytic
  stress prices the cost channel only, not path effects (wider spread → different SL/TP
  touches); **escalation rule:** if any family's raw verdict is within ±1 MDE of flipping
  under 1.5× stress, that family is flagged "cost-fragile" and an in-run stressed rerun of its
  cells is proposed at GV2 (owner call — needs a small spread-multiplier knob, not built now).

**Primary hypotheses (PLAN V2's three questions, made operational; metric = pooled family
$/trade, position-level dollars, F70 convention; expR always reported as descriptive):**
- **H-MR (does mean-reversion's edge survive?):** mean-reversion pooled $/trade over 2019–2023
  > 0 with 95% stationary-block-bootstrap CI excluding 0 (weekly-scale blocks; committed
  `block_bootstrap.py`).
- **H-RANK (does the F68 ranking hold?):** Spearman ρ between the frozen census family vector
  and the OOS family vector, same metric on both sides, primary = $/trade; positive with 95%
  cluster-bootstrap CI (resample ISO weeks, recompute family means, 2,000 reps) excluding 0.
  Honesty note: 9 families is a coarse rank test — only strong persistence is detectable; a
  wide CI straddling 0 reads "not detectable at n", not "ranking is noise".
- **H-BANK (is the bank mean real?):** bank-pooled $/trade CI vs 0. Stated caveat (D1): at
  census sizing the bank-pooled MDE is ≈ $13/t ≈ 0.03R — the +0.02R question stays below MDE
  even at 6× n unless the true effect is larger; a null here is "not detectable at n".
- **Frozen census-comparison vectors (2025-07-04→2026-05-05, 075D5240, pasted so the OOS
  comparison is against numbers fixed today):**
  $/trade: MR +19.6 · macd +4.0 · trend-breakout +3.2 · session-breakout −24.1 ·
  rsi-divergence −28.6 · super-trend −38.7 · bb-squeeze −49.3 · ema-alignment −52.0 ·
  mtf-trend −119.1 (bank-pooled **−$26.0/t**, n=4,461).
  expR (F68): MR +0.10 · rsi +0.08 · macd +0.05 · tb +0.04 · st −0.00 · sb −0.02 · bb −0.04 ·
  ema −0.05 · mtf −0.22 (bank ≈ +0.02R). The two metrics ALREADY disagree on rsi-divergence in
  the census (+0.08R vs −$28.6/t) — the F70 lesson; both will be shown, dollars decide.

**MDE line (D1) — blinded by construction (computed from census trades only; no 2019–23 run
existed when computed). Stationary block bootstrap (`block_bootstrap.py`, weekly-scale blocks,
4,000 reps, seed 42) on 075D5240 per-family $/trade; n_proj = 6× census n (60 vs 10 months,
census trade-rate assumption — MDE restated at ACTUAL n in results):**
```
family                n  mean$/t   sd$/t  SEboot   MDE@n  n_proj   MDE@6n
trend-breakout      731      3.2     614    26.6      75    4386       30
macd-momentum       544      4.0     585    34.1      96    3264       39
rsi-divergence      497    -28.6     657    30.2      85    2982       35
session-breakout    492    -24.1     447    27.5      77    2952       31
mean-reversion      491     19.6     541    29.4      82    2946       34
bb-squeeze          487    -49.3     549    28.9      81    2922       33
super-trend         460    -38.7     605    31.4      88    2760       36
ema-alignment       422    -52.0     620    33.0      93    2532       38
mtf-trend           337   -119.1     571    36.1     101    2022       41
BANK-POOLED        4461    -26.0            11.1      31   26766       13
```
At census sizing 1R ≈ $450–500 → per-family MDE@6n ≈ **0.06–0.09R**: V2 is genuinely powered
for the 0.10R-class question it was built to answer (the review §2.1 deficit, closed by data).

**Deliverables (gate GV2 evidence tables):**
1. Era × family table: pooled $/trade + n per family per calendar year (2019, 2020-vol, 2021,
   2022-trend, 2023-chop) with block-bootstrap 95% CIs; family totals with CIs (raw + 1.5× +
   2× spread stress).
2. D5′ legs applicable to frozen configs: bootstrap CI on pooled family dollars (leg 1); sign
   agreement at family × instrument-class level (FX-major / JPY-cross / metal / crypto, leg 2);
   drop-any-month jackknife sign stability over the 60 months (leg 4). Stitched walk-forward
   (leg 3) does not apply — nothing is refit; noted, not skipped silently.
3. H-MR / H-RANK / H-BANK verdicts per the rules above; per-cell signs demoted to descriptive
   (D5′).
4. Residence/park recommendations per family for the owner (GV2 decides V3/V4 weighting).
5. sv2 scores in experiment `v2-frozen-bank-oos` (id pasted at creation); `research
   persistence` runnable against it (owner 5-minute check, PLAN §5).

**Guards (pasted before launch, re-pasted after):** era-holdout `SELECT COUNT(*) FROM
BacktestRuns WHERE BacktestFrom <= '2024-12-31' AND BacktestTo >= '2024-01-01' AND
StartedAtUtc >= '2026-07-16'` = 0 and stays 0 (V2 windows end 2023-12-31T00:00 by
construction); EMBARGO-2 `... WHERE BacktestFrom >= '2026-07-06'` = 0 and stays 0. D13
one-cell-per-run holds by driver construction (every run body has exactly one row).

**Operational plan (stated, not evidence):** M1-import completion confirmed (456bc81 +
independent re-count this session: M1 32,170,346 rows, spread 99.99%, dukascopy ≥2025 = 0)
before any run starts (one-writer doctrine: the census reads marketdata.db). App = single
Lane-R instance. Pilot = 2 runs (mean-reversion/EURUSD/H1, trend-breakout/XAUUSD/H4) measuring
5-year wall time AND per-run DB growth (census anchor: 10-month H1 ≈ 124 s at 41.5
decision-bars/s → ballpark 30–40 h sequential, ~10–14 h at --parallel 3); extrapolation pasted
before the batch proceeds. Batch tranched H4-first, resume-safe (resume-by-label) across
app/session restarts.

**Pre-launch disk finding + pre-registered deviation (owner decision 2026-07-17: "Both"):**
trading.db = 4.8 GB, C: free = 5.1 GB (99% full). Measured census weight: the 252 runs of
075D5240 wrote 1,389,819 EquitySnapshots + 1,842,414 Journal + 855,873 Bars rows; Journal
averages ~851 content-bytes/row (vs ~195 equity, ~140 bars) — the kernel event journal is the
dominant mass. At 6× windows, V2 projects ≈ 11M Journal rows ≈ 10–12 GB: **the batch as-is
cannot fit.** Deviation, pre-registered BEFORE launch: `census_driver.py --prune-journal`
deletes each run's Journal rows only after the run is terminal-completed AND sv2-scored.
Journal is a backtest replay/debug artifact — sv2 scoring, verdict tables, ChallengeSimulator,
and `research persistence` read TradeResults/EquitySnapshots only; every result-bearing record
(trades, equity, per-run Bars, scores) is kept in full, capping batch growth at ≈ 3.5 GB.
Failed/unscored runs keep their journal for debugging; 075D5240's journal is untouched. Driver
also refuses new submissions below 1.5 GB free (resume-safe stop). Owner frees additional C:
space in parallel ("Both"). No local trading.db snapshot is possible before the batch (4.8 GB
copy vs 5.1 GB free) — stated plainly; resume-by-label + append-only results are the
mitigation, and the **off-machine backup decision is now urgent** (~13 GB
irreplaceable/expensive-to-rebuild data, single copy on C:).

### GV0 note — owner query answered, gate still OPEN

Owner asked (2026-07-17): "swing 100 one step?" Answer, per Session-1 verified terms
(rule-diff row 14) re-confirmed today against ftmo.com/en/trading-objectives: **FTMO's 1-step
evaluation has no Swing variant** (Swing = 2-step only), and 1-step carries **3% max daily
loss** vs the 2-step's 5% — the harshest possible constraint for a bank whose median winner
holds multi-day, and one our simulator models optimistically until V6's intraday envelope
exists (Session-1 recommendation against 1-step stands). Recommendation remains **FTMO Swing
$100k 2-step**; GV0 awaits explicit signature. V2 does not depend on the account type.

### Evidence — guards pasted BEFORE launch (pre-registration commit precedes run 1)

```
era-holdout guard (2024 runs since 2026-07-16): 0
embargo-2 guard (runs from >= 2026-07-06): 0
```
---

## Session 3 (continued) — 2026-07-17 — F78: the pilot fires — governor cooling-off deadlock

### F78 — GovernorMachine cooling-off never releases: permanent entry lockout after any 5-loss streak

**The pilot did its job.** Both pre-registered pilot runs starved: `census/mean-reversion/
EURUSD/H1` (65fc6f83) = 8 trades in 5 years, ALL in Jan 2019; `census/trend-breakout/XAUUSD/H4`
(e66fc1d0) = 15 trades, ALL Jan–Mar 2019 — while `BarClosed`/`EquityObserved` journal events
continue uniformly through 2023 (engine alive, entries dead).

**Evidence chain (all read-only, pasted from the orphan run's surviving journal):**
1. e66fc1d0 journal: `OrderProposed` steady at 111/116/115/131/128 per year 2019–2023 —
   signal generation healthy for all 5 years. `OrderFilled`: 30 in 2019, **zero after**.
2. Every post-2019-03 proposal carries `DecisionReason` alternating between
   `GOVERNOR:CoolingOff: N bars remaining` and
   `GOVERNOR:CoolingOff: 5 consecutive losses >= pause 5` — for 4.7 years.
3. Code (`GovernorMachine.cs`): `ApplyTradeClosed` resets `ConsecutiveLosses` **only on a
   win**; `ApplyBar` expires the pause but leaves the counter; `EvaluateStatic` re-arms
   CoolingOff whenever `ConsecutiveLosses >= StreakPauseAt` (default 5; CoolingOffBars=24).
   During the pause no trade can open ⇒ no win can occur ⇒ **deadlock by construction**. The
   existing unit test verified pause-expiry → Normal but never Evaluated after expiry.
4. **The 2025 census has the same disease** — 075D5240 trades by month:
   1069 → 975 → 624 → 511 → 321 → 281 → 192 → 174 → 173 → 132 (monotonic ~8× decay).
   Cells fall permanently silent as each hits its first 5-loss streak. The pilot merely ran
   long enough (5y) for ~all cells to hit one.

**Blast radius (to be re-read at GV2 with this lens; no re-litigation tonight):** every
long-window result to date ran under this governor — 075D5240 (F68 family stats are
early-window-biased and truncated at first 5-streak; F64's "H2 thinner" is at least partly
mechanical; F75's leg-(ii) H1→H2 trade-mix shift likewise — H2 n was 944 vs H1 2625), R3/R4
candidate velocity ("too slow", 0/12 windows) partly mechanical, F74's untimed E[t] overstated.
Direction of bias on per-trade stats is UNKNOWN (post-streak entries are excluded — a
selection at streak boundaries), so $/t comparisons against the census carry a stated
mechanical-truncation caveat from here on. Live trading would have hit the same lockout
(L-track dodged a bullet).

**Fix (L1 correctness-before-money pattern, F26/F28/F71 precedent — behavior corrected to the
rule's own stated intent, no logic rewrite):** `ApplyBar` clears `ConsecutiveLosses` when the
cooling-off completes ("served the pause = fresh slate"; without it the stale counter re-arms
the pause forever). Pinned by `CoolingOff_Expiry_ClearsStreak_AndDoesNotRearm` (repro-shaped:
fails on the old code via the post-pause Evaluate).

**Ops fixes shipped alongside (pilot exposure):** (a) run-submission re-validates coverage via
`GetInventoryAsync` — a full-table GROUP BY that costs ~2 min at the new 35M-row scale; the
20 s cache TTL made every submission re-pay it and timed out the pilot's second POST (orphaning
e66fc1d0 — adopted below). `BootstrapMarketDataStore` TTL → 60 min + invalidate-on-write/delete
(out-of-process importers are already forbidden during batches by the one-writer doctrine);
driver POST timeout → 360 s. (b) Driver prune verified: 64,671 journal rows reclaimed on the
scored pilot run; e66fc1d0's journal is deliberately KEPT as F78 evidence.

### Pre-registration amendments (recorded BEFORE the batch, Session-1 amendment precedent)

1. **Frozen-bank definition** now = configs + engine as of the F78-fix commit (includes
   F71/F26/F28/F78 — D8: dead knobs get fixed, logic is not rewritten). All V2 runs execute
   under the FIXED governor.
2. **Experiment `2E1BDB18` is retired** — it contains only the two F78-contaminated pilot rows
   (kept, park-never-delete, named F78 evidence). The census runs under a fresh experiment,
   same spec (id pasted at creation below).
3. **H-RANK caveat strengthened:** the frozen census comparison vectors were measured under
   F78 suppression; the rank test stands but a weak/absent correlation is now also explainable
   by the census's own truncation — verdict language must say so.
4. **MDE line unchanged and conservative:** census trades (the SE source) were F78-truncated;
   the fixed engine should produce MORE trades per cell, so actual OOS n ≥ n_proj and actual
   MDE ≤ the pre-registered $30–41/t.
5. **Pilot is re-run under the new experiment before the batch; expectation revised:** cell
   trade counts should now EXCEED census-rate × 6 (the census rate itself was suppressed).

**Gates after F78 fix (paste):**
```
Build succeeded. 0 Error(s)
Unit         Passed! Failed: 0, Passed: 779, Skipped: 6   (+CoolingOff_Expiry_ClearsStreak_AndDoesNotRearm)
Integration  Passed! Failed: 0, Passed: 156, Skipped: 0
Sim-fast     Passed! Failed: 0, Passed: 144, Skipped: 0
```
Golden/sim results unmoved — the pinned short windows never reached a 5-loss lockout, which is
also why the bug survived every prior gate.

### F79 — "daily" drawdown was cumulative: static floor + protection latch (the second suppressor)

The post-F78 pilot rerun (experiment `19EB5D91`, runs d3a1e138 / 3793e3b5) improved (8→54,
15→76 trades, both sv2 PASS) but still truncated: MR/EURUSD stopped mid-2020, TB/XAUUSD still
all-2019. A deliberately unpruned diagnostic rerun (`5e358281`, adopted as evidence, journal
kept) shows proposals steady all 5 years and, from 2019-11-22, every one rejected:
`PROTECTION_MODE_ACTIVE` ×427 + `WorstCaseDDWouldBreachDaily` ×63 — `protectionCause:
DailyDrawdown` latched at equity $95,227 (−4.77% CUMULATIVE; never a bust, never a real 5%
day) and still identical at the last 2023 proposal.

**Root cause (code, three sites sharing one mistake):** `DailyDdBase.InitialBalance` was used
as the *anchor*, not just the *allowance base* —
- `DrawdownReducer.Apply`: daily DD = (initial − equity)/initial — cumulative DD relabeled
  "daily"; any account below ~4.75% of initial re-breached the daily cap every day forever
  (protection cleared each midnight per `ClearsOn`, then instantly re-entered).
- `PreTradeGate` worst-case floor: `initial × (1 − 5%)` — a STATIC $95k floor. Over-protects
  below initial (the 63 rejections) and **UNDER-protects above high-water** (day start $110k
  would still have a $95k floor — a 13.6% one-day loss allowed; caught before any live run).
- `KernelDailyDdGuardEvaluator`: same static floor for the flatten watchdog.
**Verified FTMO semantics (V0 rule-diff rows 4–5):** floor = day-start balance − 5% × initial —
day-anchored numerator, initial-denominated allowance. Fixed in all three sites (`DailyStart`
mode unchanged — it was already day-anchored).

**Tests:** old pins encoding the bug were rewritten, not deleted: `Drawdown_DailyBase_
Configurable` (unit) pinned the static floor → replaced by `Drawdown_DailyBase_SelectsAllowance
Base_NotAnchor` + `Drawdown_DailyFloor_IsDayAnchored_InInitialBalanceMode`; three sim scenarios
(`MaxDailyLossBreach_Midday_HaltsTrading`, `MaxTotalLoss_PermanentHalts`,
`DailyDdBreach_EntersProtectionAndHaltsTrading`) breached only via CUMULATIVE decline spread
over 8–12 days — under verified semantics that is correctly a non-breach; rewritten to realize
genuine single-day losses (seeded positions stopped out intra-day). Gates after:
```
Build succeeded. 0 Error(s)
Unit 780/0/6 · Integration 156/0/0 · Sim-fast 144/0/0
```

**Blast radius addendum:** the census/prior-run suppression is F78 + F79 JOINTLY (streak
lockout + cumulative-daily latch); every statement in the F78 blast-radius paragraph carries
both. Parked observation (one line, V6/rule-model scope): `ProtectionState.ClearsOn` lets a
MaxDrawdown breach clear on the next day under "NextTradingDay" policy — an FTMO total-loss
bust should be terminal; ChallengeSimulator handles busts independently so sv2 is unaffected.

**Amendment 6:** experiment `19EB5D91` retired (its 2 pilot rows ran F78-fixed but F79-broken;
kept, park-never-delete). Third experiment created post-F79; pilot re-run under it (expectation
unchanged: trades must now span all five years).
