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
