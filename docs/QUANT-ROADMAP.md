# Quant Roadmap — strategies, calibration, and what to do with fast backtests

**Written:** 2026-07-02 (Claude / Fable 5, acting as quant advisor per owner request)
**Audience:** the owner first (decisions + intuition), the implementation agent second (Q-phases at the end).
**Prereqs:** `docs/iterations/iter-tape-trust/PLAN.md` phases T0–T3 (truthful, trusted tape venue). Bug/gap IDs
(B*, F*) refer to `docs/iterations/iter-marketdata-tape/HANDOVER-REVIEW.md`.

---

## 1. Honest starting point

What you have is unusual and good: the **execution/risk half is ahead of the alpha half**. A deterministic
kernel, a pre-trade gate that projects worst-case drawdown, a governor, prop-firm compliance, add-on position
management, content-addressed reproducible runs — most retail algo projects never build any of this, and for
prop-firm trading it's the half that matters more (most FTMO failures are risk failures, not signal failures).

What you don't have yet: **evidence that any of the nine strategies has positive expectancy after honest
costs**, and a methodology that would let you find out without fooling yourself. The nine strategies are
classic indicator entries (breakout/EMA/RSI/MACD/BB/SuperTrend/MTF) explicitly built to exercise the
infrastructure. That's fine — but treat them as *hypotheses*, not assets.

The fast tape path changes what's possible: at ~sub-second per run vs ~50–80 s on cTrader, you go from
"a handful of backtests per evening" to "thousands per hour". That is a superpower **and** a loaded gun:
mass experimentation without discipline manufactures false edges at industrial scale. This roadmap is mostly
about the discipline.

## 2. Rule zero: never tune on a flattering simulator

Until T3/F1 lands, the tape venue fills every trade **spread-free**. Consequences you must internalize:

- Spread is a **fixed tax per round turn**, so the bias is *inversely proportional to target size*. An H1
  trend trade targeting 60 pips barely notices 1 pip; an M15 mean-reversion trade targeting 10 pips has its
  economics overstated by ~10 %+ per trade — easily the difference between "works" and "doesn't".
- Any optimizer will therefore **drift toward higher frequency and tighter targets** on a spread-free venue,
  i.e., exactly toward the configs where the lie is biggest.
- Same logic applies to F4 (gap fills at the stop price): strategies that hold over weekends/news look safer
  than they are, and FTMO's daily-DD rule punishes precisely those tails.

So the hard sequencing rule: **T2 (reconcile) + T3/F1 (spread) before any calibration or sweep is believed.**
Sweeps run before that are plumbing tests, not research.

## 3. The experiment methodology (non-negotiables)

These five rules are the whole game; everything else is tooling.

1. **Out-of-sample or it didn't happen.** Split every dataset: tune on in-sample (IS), report out-of-sample
   (OOS) only. Walk-forward beats a single split: rolling windows (e.g., tune on 6 months, test on the next 2,
   step 2), stitch the OOS segments into one equity curve. That stitched curve is the only number you quote.
2. **Count your trials.** If you test 200 configs, the best one looks great by luck alone. Defenses, cheapest
   first: (a) report how many configs were tried next to every result; (b) demand the edge survives on ≥3
   disjoint OOS windows AND on a second symbol; (c) prefer *parameter plateaus* — pick the middle of a region
   where neighbors are all decent, never the isolated peak of a heatmap. A result that dies when
   `AtrMultiple` moves 1.5→1.75 was never real.
3. **Optimize the right objective.** For prop-firm the objective is **P(pass)**, not net PnL: hit +10 % before
   −10 % total / −5 % daily, within the allowed time, per FTMO phase. Two strategies with equal expectancy can
   have wildly different pass probabilities (frequency and DD shape dominate). You already have
   `PassProbabilityEstimator` (Monte Carlo) — make it the sweep's ranking metric (Q2). Rough intuition: at
   0.5 % risk and +0.25R average per trade, +10 % needs ~80 trades; an H1 strategy producing 3 trades/week
   can't pass a 30-day phase alone. This single arithmetic decides more than any indicator choice.
4. **Monte Carlo the trade sequence.** Reshuffle / block-bootstrap the OOS trade list 1,000× to get DD and
   pass-probability *distributions*, not point estimates. One historical path is one draw. (Trade-level MC is
   cheap and already half-built; bar-level bootstrap is a later luxury.)
5. **Everything reproducible.** `(DatasetId, ConfigSetId, Seed)` already content-addresses runs — enforce it in
   the sweep runner (T5): the same cell never computes twice, and any number in a report can be regenerated
   from its address. Data provenance (`Source`, `Quality`) rides along.

## 4. Exit calibration done right (correcting FULL-HANDOVER §9)

The handover's MAE/MFE recipe ("optimal SL = 75th percentile of MAE", "optimal TP = median MFE") is
directionally reasonable but statistically loose in three ways worth fixing, because exits are your cheapest
real lever:

1. **Changing the stop changes the population.** MAE percentiles computed from trades run with SL=ATR×3 do not
   tell you what happens at SL=ATR×1.5 — some winners become losers, path-dependently. Percentile rules on the
   old population are answering the wrong question.
2. **Conditioning on winners is survivorship.** "Capture 75 % of winning trades' MAE" ignores that losers also
   had MAE excursions; the objective is expectancy over ALL entries, not winner retention.
3. **No validation step.** Any exit rule fitted on the same data it's evaluated on will look good.

**The correct (and cheap, thanks to the tape) method — the excursion grid:**

1. Run each strategy in **exploration mode**: very wide SL (ATR×4+), no TP, no trailing — the entry signal is
   the only thing being tested. For every trade, record the **per-exit-TF-bar excursion path** (high/low vs
   entry, per m1 or per decision bar; a few hundred bytes/trade). This is new but tiny: an excursion recorder
   in the tape venue or effect executor (Q1 below).
2. **Replay exits offline**: for any candidate (SL, TP, breakeven, trailing) rule, walk each recorded excursion
   path and compute the exact exit — thousands of exit configs evaluated against the same entries in
   milliseconds, *without re-running the engine*. First-touch ambiguity within a bar resolves conservatively
   (SL first), same as the venue.
3. Optimize expectancy (or P(pass)) over the exit grid **on IS windows**, verify on OOS windows (walk-forward,
   §3.1). Prefer plateaus (§3.2).
4. Store the survivors as **per-symbol / per-TF / per-regime calibration tables** that `AddOnAutoTuner` reads
   (the handover's Phase-3 "hot-reload calibration" idea is right — keep it), each stamped with the dataset +
   window it was fitted on.

This turns the vague "auto-tune from volatility" into a measured, versioned, refittable artifact — and it's
exactly the kind of workload the fast path exists for.

## 5. Strategy-bank advice (what I'd actually do with these nine)

- **Triage before inventing.** Run all 9 × {EURUSD, GBPUSD, USDJPY} × {H1, H4} in exploration mode (§4) over
  the longest downloaded window; rank entries by *raw signal quality* (post-cost expectancy of the excursion
  paths under a simple fixed-R exit). Expect several to be flat-to-negative — kill or park them without
  sentiment. Better 3 entries with evidence than 9 with vibes.
- **Exits and sizing carry more than entries.** The add-on system + §4 calibration will likely move results
  more than swapping indicator sets. Do that first.
- **Portfolio over hero strategy.** Pass probability rises fastest by combining 2–4 **low-correlation**
  return streams (measure correlation on OOS *daily PnL*, not signals): e.g., one trend (SuperTrend/Breakout
  family), one mean-reversion (RSI/BB family), one session/time-of-day (SessionBreakout family). The risk
  system already handles per-strategy caps; what's missing is a correlation-aware exposure group (Q3).
- **Time-of-day/session effects are your most defensible retail edge class** — they're structural (liquidity
  regimes), not indicator artifacts. Extend SessionBreakout into a small family (London ORB, NY open, Asia
  range fade) before adding any new indicator strategy.
- **Regime filter: validate, don't trust.** The ATR/ADX thresholds (25/18, 2.5×/0.4×) are hardcoded folklore.
  Sweep them per symbol on IS, check the filter actually improves OOS expectancy of the strategies it gates;
  if it doesn't, simplify (a realized-vol percentile + Kaufman efficiency ratio is easier to reason about
  than ADX). Keep it explainable — no ML needed at this stage.
- **Frequency reality check** (§3.3): if the portfolio's OOS trade rate can't plausibly reach the profit
  target inside a phase window at your risk-per-trade, no amount of tuning fixes it — you need more symbols,
  more sessions, or (only after F1 spread honesty) faster timeframes.
- **News/weekend awareness:** FTMO's killers are gaps. Before going live-ish, add a calendar-based flat-before-
  news/weekend toggle per strategy and let Monte Carlo tell you if it raises P(pass). (`NewsWindows` config
  already exists in `LoadedConfig` — wire a per-strategy "flatten before window" behavior.)

## 6. Kernel & platform ideas (the "wild" list, each with method + when)

| Idea | What/why | Method sketch | When |
|---|---|---|---|
| **Excursion recorder** | Foundation of §4; also gives honest MAE/MFE per exit-TF bar | Venue-side: while scanning exit bars, append (barTime, highVsEntry, lowVsEntry) per open position; persist per trade in sweep/exploration mode | Q1 (first) |
| **Sweep runner + results grid** | Turns the tape speed into research throughput | T5 in iter-tape-trust; rank by P(pass) & OOS expectancy; plateau heatmaps | with T5 |
| **Walk-forward harness** | §3.1 automated: window splitter, per-window config freeze, stitched OOS curve + report | Orchestrator-level loop over (train, test) windows reusing the sweep runner; new report page | Q2 |
| **P(pass) as a first-class metric** | §3.3; the right objective | Wire `PassProbabilityEstimator` to any run/sweep's OOS trade list + phase rules; show on run detail + sweep grid | Q2 |
| **Correlation-aware exposure groups** | Two EUR longs ≈ one bet; the gate currently sums risk without correlation | Config: symbol→group map + per-group open-risk cap enforced in `PreTradeGate` (pure, deterministic, golden-safe as opt-in config) | Q3 |
| **Per-bar recorded spread** | Upgrade F1's `TypicalSpread` constant to reality; enables honest M15 research | Recorder captures `Symbol.Spread` at bar close into the shard schema (one column, default null); venue uses per-bar spread when present | Q3 |
| **Intrabar equity envelope → daily-DD guard fidelity** | FTMO daily DD breaches happen intrabar; the gate should see the trough | T3/F2 delivers the venue side; then feed the watermark into the breach watchdog path | after T3 |
| **Bar-level block bootstrap (synthetic stress)** | One history = one draw; stress DD/pass-prob beyond it | Generate N synthetic tapes by block-resampling real bars (preserve session structure); run the portfolio across them; report DD distribution | Q4 (luxury) |
| **Venue-side exit-TF trailing mode** | Close F3 only if V4 measures it as material | New opt-in venue behavior: venue applies the frozen trailing rule per exit bar; kernel still owns the *decision* to arm it | only if V4 says so |
| **Tick tape (P6)** | Only where m1 reconcile proves insufficient (very tight trails/scalps) | As original plan D2 (columnar per (symbol,month), SQLite index) | last |

Deliberately absent: ML/NN signal models, alternative brokers, HFT anything. Wrong risk/reward for a
one-owner prop-firm system at this maturity; revisit after the portfolio passes phases on evidence.

## 7. Suggested sequence (ties everything together)

```
iter-tape-trust T0–T2      → tape truthful + trusted (reconcile artifacts committed)
iter-tape-trust T3 (F1,F2) → costs + drawdown honest
iter-tape-trust T5         → sweep throughput
Q1: excursion recorder + exploration runs on owner's downloaded set → entry triage (§5.1)
Q2: walk-forward harness + P(pass) metric → exit calibration grid (§4) for surviving entries
Q3: portfolio assembly (correlation groups, per-strategy caps) + per-bar spread → portfolio-level MC + P(pass)
Q4: stress (bootstrap tapes), news/weekend flattening validation, then forward-test via cTrader listen mode
     with the weekly oracle re-validation habit (run one reconcile per week; drift = investigate)
```

Each Q-phase is plannable as its own iteration doc when reached; don't front-load detail that T2's reconcile
numbers might invalidate.

## 8. What success looks like (so we know when to stop tuning)

A portfolio of 2–4 strategies where: the stitched walk-forward OOS curve is positive after honest costs on
≥2 symbols; Monte Carlo P(pass phase-1) ≥ ~60–70 % at your chosen risk; max daily-DD tail (95th pct) clears
the 5 % rule with margin; every number regenerable from a content address; and a cTrader oracle reconcile
that stays green weekly. Then — and only then — the interesting conversation is live capital, not more
backtests.
