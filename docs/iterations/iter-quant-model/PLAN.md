# iter-quant-model — Give the quant model a shape: TF-agnostic strategy bank, honest tape, entry surgery, exit lab, evidence-driven picking

**Written:** 2026-07-04 (Claude / Fable 5, deep research session)
**Audience:** implementing agent (OpenCode/DeepSeek) first; owner for the decision block (§2) and the triage program (P5).
**Companion docs:** `docs/QUANT-ROADMAP.md` (methodology — still authoritative), `docs/iterations/iter-tape-trust/HANDOVER.md` (T0–T5 state), `docs/iterations/iter-marketdata-tape/HANDOVER-REVIEW.md` (B*/F* ids).
**Branch base:** wherever `iter/data-mgmt` lands; this is a NEW iteration, one phase per PR-sized commit, gated.

---

## 0. Research findings (evidence, no guesses)

Everything below was verified in code and/or the live DB (`src/TradingEngine.Web/data/trading.db`, read-only) on 2026-07-04.

### 0.1 THE timeframe answer: strategies never run on non-H1. Not indicator values.

The owner's hypothesis was "indicator values break on other TFs". **Wrong layer — the strategies are never invoked at all:**

1. Every one of the 9 strategies hardcodes `public Timeframe EntryTimeframe => Timeframe.H1;`
   (e.g. `src/TradingEngine.Strategies/TrendBreakout/TrendBreakoutStrategy.cs:23`, same line-shape in all 9).
2. `StrategyBankService.GetActive` filters `.Where(s => s.EntryTimeframe == timeframe)`
   (`src/TradingEngine.Host/StrategyBankService.cs:40`). A bar with `Timeframe=M15` therefore matches **zero** strategies.
3. Belt-and-braces: every `Evaluate` also does `context.Bars.GetValueOrDefault(Timeframe.H1)` — even if the bank
   filter were removed, an M15 run's snapshot is keyed `M15`, lookup returns null, strategy skips.
4. The UI happily offers `['h1','h4','d1','m15','m5','m1']` (`web-ui/.../new-backtest.component.ts:18`) and builds
   run-plan rows the engine silently ignores. No warning anywhere; the run "completes" with 0 trades.

**DB evidence:** `BacktestRuns` grouped by venue/period: tape×h1 = 12 runs / 1,394 trades (avg 116); tape×m15 = 2 runs / **0 trades**; tape×m1 = 1 run / **0 trades**. M15 data exists (12,129 bars) — the data was never the problem.

Secondary TF facts that become relevant once P1 unblocks other TFs:
- Indicator **periods are bar-count based** (ATR_14, EMA_50…), so they transfer across TFs by construction; ATR-based SL/TP scales naturally with the TF's volatility. The parameters do NOT need per-TF re-derivation to *run* — they need per-TF re-*validation* to be *good* (P4/P5).
- `AddOnAutoTuner` is already TF-aware (`TrailingBaseFor`/`TfTier`/`ReferenceAtrPips` switch on `Timeframe`) — built for a multi-TF world that the bank never delivered.
- Spread is a fixed tax per round turn ⇒ lower TFs (smaller targets) are hit proportionally harder; do not believe any M5/M15 result until P0.2 (spread honesty) lands (QUANT-ROADMAP §2).

### 0.2 Strategy-bank quant audit (all 9 read line-by-line)

| Strategy | Entry logic (actual, as coded) | Type | Defects found |
|---|---|---|---|
| trend-breakout | latest bar's High > max(High) of prior N bars AND price > EMA50 (mirror for short) | ~state (re-fires on every new high while trending) | re-fires mid-trend; entry at signal-bar close |
| super-trend | SuperTrend(10,3) direction flip + ADX≥20 | edge ✓ | `_prevDirection` private state → cross-symbol pollution in multi-symbol runs |
| ema-alignment | EMA20>EMA50 AND price>EMA20 (state!) — comment says "crossover" but it is a **condition, not an edge** | state | fires every bar in any trend; only re-entry cooldowns throttle it; likely the biggest source of late, arbitrary entries |
| macd-momentum | MACD hist zero-cross + price vs SMA200 + ADX≥20 | edge ✓ | `_lastHist` state (cadence-fragile, cross-symbol pollution) |
| mtf-trend | H4 EMA200 side + H1 RSI cross of 45/55 (pullback resume) | edge ✓ | **DEAD on tape** — tape feeds only the decision TF; H4 EMA never computed → returns null forever, silently ("no signal") |
| mean-reversion | RSI<30 + close in lower ⅓ of bar range → long (limit offset 5p, RR 1.0) | edge-ish ✓ | reasonable; limit fill sides biased (0.4) |
| session-breakout | 05–07 UTC range; breakout entry 07–09 UTC | edge ✓ | `flattenTimeUtc: 12:00` param is **dead** — no code reads it; positions never time-flattened |
| bb-squeeze | BB width ≤ 0.8×avg(prior) latches squeeze; later close outside band fires | edge ✓ | squeeze latch **never expires** — a squeeze from weeks ago can arm an entry much later |
| rsi-divergence | **BROKEN — tautology.** `rsiAtLowest = lowestIdx >= 0 ? rsi : rsi` (always current rsi), so `rsi > rsiAtLowest*0.98` is always true. Net effect: LONG on any fresh N-bar low with RSI<50, SHORT on any fresh N-bar high with RSI>50 — i.e., **fade every fresh breakout, no divergence tested** (`RsiDivergenceStrategy.cs:52-67`) | broken | DB: 8 trades, 12.5% win, avg R −0.80. Root cause is an API gap: `MarketContext.IndicatorValues` exposes only the LATEST value per key, so divergence (needs RSI series at prior pivots) *cannot be expressed*; the tautology was someone papering over that |

**Cross-cutting entry defects:**
- **Cross-symbol state pollution:** one instance per strategy id serves ALL symbols/rows (`StrategyRegistry.CreateStrategies`); `_prevDirection`/`_lastHist`/`_prevRsi`/`_rangeHigh`/`_bbWidthQueue` interleave across symbols in multi-symbol runs → phantom flips/misses.
- **Fill timing optimism:** market entries fill at the signal bar's own close (venue `_lastClose` = decision-bar close) rather than the next bar's open.
- **Indicator history inaccessible:** only latest values surface; strategies that need series (divergence, slope, squeeze age) keep fragile private state or can't be written.

### 0.3 Money/metrics defects that corrupt research

- **R-multiple computed against the FINAL stop, not the initial stop** (`EffectExecutor.cs:147-148` uses `effect.StopLoss` — the trailed/breakeven-moved stop at close). DB proof: TP exits average **R = 6.997** while every config's RR multiple is 2.0–3.0; SL exits average −0.68R (breakeven-moved stops). Every R-based number in the system (analytics, calibration, MAE/MFE-vs-R) is corrupted. The initial stop IS recoverable: `EntrySnapshotJson.stopLoss` stores it per trade.
- **Aggregate reality check** (all runs, EURUSD only): trend-breakout 1,453 trades, 51.1% win, **net −$254,741**. Exits: SL ×955 = −$726k; TP ×237 = +$282k; PARTIAL ×275 = +$184k. Positive-R illusion + negative money = churn: BE/trail giveback plus late state-based entries. This is the "bad win rate" the owner feels — it is really *bad expectancy shape*.
- MAE/MFE exist per trade (decision-bar high/low envelope folded at close — fine as magnitude) but there is **no excursion path**, so exits can't be replayed offline; `ExcursionTracker` (`Services/Helpers/ExcursionTracker.cs`) is dead code with zero callers.
- Dead config: `sizing-policy.json:flattenAtFraction` — no reader anywhere in `src/`.

### 0.4 Tape venue accuracy (vs cTrader) — current honest status

Tape = `TapeReplayAdapter` (in-process, bars from `marketdata.db`, dual-resolution M1 exits, VenueManaged like cTrader). Structurally sound: SL-first-conservative same-bar detection (`EngineReducer.DetectSlTpExit`), gap-through fills at open, intrabar min-equity tracked, limit expiry per decision bar, costs via `TradeCostCalculator` (commission+swap). **But:**

1. **Spread is undercharged ~2×.** Recorded bars are cTrader chart bars = **BID** bars. Correct model: long buys at ask = bid + FULL spread, sells at bid; short sells at bid, buys back at ask. Current code charges HALF spread per side: long entry `mid + halfSpread` (`TapeReplayAdapter.cs:278`), short SL/TP detection on bar shifted by `halfSpread` (`:349-353`), closes at `mid ± halfSpread` (`:405`). Net: every round trip pays ~½ `TypicalSpread` instead of the full spread. On EURUSD that's ~$5/lot/round-trip optimism; proportionally worse on lower TFs.
2. **Short market entries pay no spread at all** (fill at `midPrice`, `:278`) — under the bid-bar convention that one is actually correct; it's the *long* side and the *short exit* side that are half-charged. The asymmetries must be unified under one written convention.
3. **Limit fills test the wrong book side:** buy limit fills when `bar.Low <= limit` (bid low; a buy fills at ASK — should be `low + spread <= limit`), sell limit when `bar.High + halfSpread >= limit` (`:314-316`) — mixed convention.
4. **Tick channel spread ≠ fill spread:** synthetic tick ask = close + `PipSize` (`:193`), fills use `TypicalSpread/2` — two different spread constants.
5. **V4 reconcile (tape vs cTrader, same config) has NEVER been executed** — infrastructure (compare-both + `/api/backtest/analytics/reconcile`) is ready, needs owner's cTrader. Until it runs, "tape is accurate" is an untested claim.
6. Aux-TF starvation (0.2): tape ignores `IStrategy.RequiredTimeframes` — feeds decision TF only.

### 0.5 Add-on & parameter-scaling audit (TF/symbol agnosticism)

Question asked: are the add-ons TF-agnostic, and can indicator/add-on values be trustfully formula-guessed per timeframe/symbol?

**Plumbing: already TF-aware.** `KernelTrailingEvaluator` resolves add-ons ONCE at entry via `AddOnResolver.ResolveAtEntry(opts, bar.Timeframe, vol)`; `AddOnAutoTuner.Tune(tf, vol)` switches on the real run TF with clamped, monotonic outputs; frozen for the position's life (replay-deterministic, `ADDON_RESOLVED` journaled). Once P1 unblocks non-H1 runs, Auto-mode add-ons adapt with zero work. Verified TF paths: `TrailingBaseFor` (2.0→3.5 by TF tier), `TfTier` for TP RR, `ReferenceAtrPips(tf, spread)`.

**Values: three classes, three verdicts.**

1. **Scale-free / correctly normalized (trustworthy as-is):** R-based triggers (BE trigger-R, partial trigger-R/fraction), ATR-multiples (SL, trailing), RR-multiples (TP), spread-multiples (BE offset = ⌈spread×1.5⌉+1), ADX floors (ADX is dimensionless). These transfer across TF *and* symbol by construction. Most of the tuner's structure is in this class — good design.
2. **Folklore constants (usable as fallback, never as truth):** the TF-tier bases (2.0/2.5/3.0/3.5), `ReferenceAtrPips = TypicalSpread × {3,5,10,15,20,35,60,100}` — reference *volatility derived from spread* (a liquidity constant) is a guess; for XAUUSD (spread 30 pips) it implies H1 refATR = 600 pips, for BTCUSD (spread 50) = 1,000 — order-of-magnitude plausible for FX, shaky for metals/crypto. The `volFactor` clamp [0.7, 1.5] limits the damage. The tuner's own doc-comment says "STARTING heuristics". Verdict: keep as fallback; replace the reference scale with **measured** per-symbol×TF rolling-median ATR (P3.4b) and the values themselves with Exit-Lab calibration (P3.4).
3. **Raw-pip violations (break on other symbols, must be normalized):** `breakeven.offsetPips: 1.0` in Custom-mode strategy JSONs; `stopLoss.maxPips: 100–300`; **`RiskProfile.MaxSlPips = 100` — on XAUUSD 100 pips = $1.00 of gold, on BTCUSD = $100; the PreTradeGate will reject nearly every XAU/BTC trade as `SL_TOO_WIDE`**; `limitOffsetPips: 5.0` (EntryPlanner); `maxSlippagePips: 2.0`. These are EURUSD-calibrated constants wearing a generic name.

**The trustworthy "formula" is dimensional analysis, not magic numbers.** Adopt a units doctrine (D9): every distance-like config value must be expressed in one of {bars, ATR×, R×, spread×, fraction-of-range}; raw pips are banned except at the symbol boundary (`SymbolInfo`). Then TF/symbol agnosticism is *structural* — ATR carries the scale — and the only remaining per-cell question is "which multiple is best", which is exactly what the Exit Lab (P3) + walk-forward (P4) answer with evidence. Do **not** TF-scale bar-count indicator periods by formula (EMA50@H1 ≠ EMA200@M15 behaviorally; markets are not self-similar across TFs — session structure and news clustering break the equivalence, and the scaling explodes on M1). Keep canonical periods as priors, sweep a *small* grid per cell, demand plateaus.

**Other add-on findings:** trailing/BE cadence is once per *decision* bar (known F3 — coarse on H4, fine on M1; venue-side exit-TF trailing stays a measured-first idea per QUANT-ROADMAP). `AddOnResolver` notes DynamicSlTp resolved per-bar in `BarEvaluator`, not at entry (B6, documented, intentional). Limit entries (`EntryPlanner.PlanLimitOffset`) preserve SL/TP distances around the limit price correctly, but the offset is raw pips and — more important quant-wise — **limit entries carry unmeasured adverse selection**: you fill preferentially when price moves against you and miss the runners. Nothing measures fill-rate or the counterfactual today (no missed-proposal ledger). See P3.6.

### 0.6 What is genuinely good (don't touch)

- Kernel purity/determinism, `PreTradeGate` (worst-case DD projection incl. round-trip commission), `KernelSizing` (drawdown scaling), `GovernorMachine` (loss bands/streak/cooling-off/profit-lock), prop-firm rule sets, content-addressed runs `(DatasetId, ConfigSetId, Seed)`, sweep runner (T5), `Experiments` project with `PassProbabilityEstimator` + fold-based `VariantScorer` (walk-forward-shaped, just unwired from sweeps/UI), M1 dual-resolution exits, intrabar equity envelope. The execution/risk half remains ahead of the alpha half (QUANT-ROADMAP §1) — this iteration builds the alpha half's foundations.
- Symbols registry already defines XAUUSD, XAGUSD, BTCUSD, ETHUSD, US30, NAS100 with per-category economics — gold/crypto are a *data + validation* problem, not an engine problem.
- Data on disk today: **EURUSD only**, 2026-01→07, D1/H4/H1/M15/M5/M1 (181k M1 bars). Six months of one symbol cannot support walk-forward research — P5 fixes this.

---

## 1. Shape of the quant model (the thesis this iteration builds toward)

One paragraph the owner can hold onto: **Shamshir's edge is not any single indicator entry — it is the loop.** A deterministic engine + a fast honest venue + excursion-level trade forensics + walk-forward discipline lets us (a) measure raw entry quality per (strategy, symbol, TF, session/regime) cell, (b) calibrate exits per cell from recorded excursions instead of folklore multiples, (c) assemble a small portfolio of 2–4 low-correlation cells sized by the governor, and (d) rank everything by **P(pass FTMO phase)**, not net PnL. Strategies are disposable hypotheses; the measurement machine is the asset. This iteration turns the raw pieces (MAE/MFE columns, auto-tuner heuristics, sweep runner, pass estimator) into that machine — and unblocks the two axes (timeframe, symbol) that multiply the search space from 9 cells to hundreds.

---

## 2. Owner decisions (defaults locked unless overridden)

| # | Decision | Default (recommended) | Alternatives |
|---|---|---|---|
| D1 | TF semantics | `EntryTimeframe` comes from the **run-plan row**; one strategy **instance per row** (strategy×symbol×TF) | keep singleton instances + pass TF per call (rejected: state pollution) |
| D2 | R-multiple history | Recompute new trades vs initial stop AND backfill old rows from `EntrySnapshotJson.stopLoss` (one-off migration script) | new-only (old analytics stay wrong) |
| D3 | Spread convention | Bars are BID; ask = bid + spread; spread = per-bar recorded when present else `TypicalSpread`; both replay venues identical | mid-bar convention (must then shift *both* sides half) |
| D4 | Entry-fill timing | Market fills at **next M1 bar open** (ask/bid per side) when M1 exists; decision-bar close fallback; per-run toggle `HonestFills` default ON for tape | keep close-fills (retains optimism) |
| D5 | ema-alignment fate | Convert to edge: crossover within last K bars + first pullback touch of fast EMA | keep as state entry, rely on cooldowns (measurably worse; keep only if triage proves otherwise) |
| D6 | `flattenTimeUtc` | Implement as a real loop-level time-flatten behavior (needed for session strategies + FTMO daily-DD hygiene) | delete the param |
| D7 | Data purchase order (P5) | Majors 6 (EURUSD, GBPUSD, USDJPY, AUDUSD, USDCHF, USDCAD) + XAUUSD, TFs {M1, M15, H1, H4}, max history ctrader-cli allows (target ≥ 2y) | add BTCUSD/US30/NAS100 immediately (only if the FTMO account type allows them) |
| D8 | bb-squeeze latch | Expire squeeze latch after `BbPeriod` bars without breakout | keep infinite latch |
| D9 | Units doctrine | Every distance-like config value in {bars, ATR×, R×, spread×, fraction}; raw pips banned outside `SymbolInfo`; config linter enforces (P2.6) | keep pips, hand-fix per symbol (guaranteed silent XAU/BTC errors) |
| D10 | Entry tactic | Treat entry execution (Market / Limit@ATR-fraction / Stop-confirm) as a **sweepable dimension** measured with a missed-fill counterfactual ledger (P3.6), not a fixed per-strategy choice | keep per-strategy hardcoded entry method |
| D11 | Reference scales | Measure per-symbol×TF reference ATR (rolling median over downloaded history, stored table, refreshed at ingest) — replaces `spread × TF-factor` guess in the tuner | keep the spread-derived guess |

---

## 3. Phases

Sequencing rule (unchanged from QUANT-ROADMAP §2): **no calibration/sweep result is believed before P0 lands.** P0→P1→P2 are engine-correctness and can merge independently; P3→P4 build the research machine; P5 is the owner-facing program that uses it; P6 backstops fidelity against cTrader; P7 is FTMO ops polish.

### P0 — Truth repair (metrics + venue economics) — DO FIRST

**P0.1 R vs initial stop.**
- Add `InitialStopLoss` to `PositionState` (set once at `PositionLifecycle.CreateIntended`/first fill; never mutated by BE/trailing) → carry onto the `PublishTradeClosed` effect → `EffectExecutor.HandlePublishTradeClosed` computes `riskDistance = |entry − InitialStopLoss|`.
- Keep the final stop: write it into `ExitDetailJson` (`finalStopLoss`) so giveback analysis still has it.
- New `TradeResults.InitialStopLoss` column + EF migration. Backfill script (D2): parse `EntrySnapshotJson.stopLoss`, recompute `RMultiple` in place; log rows where JSON missing.
- **Failing test first:** open → move stop to BE → close at TP; assert R == (tp−entry)/(entry−initialSl), not the ~∞ the current code gives.
- Gate: Unit+Integration green; golden byte-identical expected (R lives on TradeResult, not StepRecord) — if golden moves, STOP and investigate before re-baselining.

**P0.2 One spread convention, both replay venues (D3).**
- Write the convention as a doc-comment on both adapters: *bars are bid; ask(t) = bid(t) + spread(t); longs buy@ask sell@bid; shorts sell@bid buy@ask.*
- `TapeReplayAdapter` + `BacktestReplayAdapter` changes: long market entry `close + spread` (full); short entry `close` (unchanged); short SL/TP detection bar shifted by FULL spread; long detection raw (unchanged); `ClosePositionAsync` long→bid, short→`+spread`; buy-limit reached when `low + spread <= limit`; sell-limit when `high >= limit`; floating PnL marks long@bid short@ask(full); tick channel ask uses the SAME spread source as fills.
- Expect characterization baselines to move — refresh them in ONE dedicated commit titled `REBASELINE: full-spread convention (P0.2)` with before/after net-PnL deltas quoted in the commit body.
- Gate: a unit test per fill path (8 paths: {long,short} × {market, limit, SL, TP}) asserting the exact fill price under a synthetic 2-pip spread.

**P0.3 Honest entry timing (D4, tape only).**
- When M1 exit bars exist, market entries queue and fill at the NEXT fine bar's open (± spread per side) instead of `_lastClose`; expose `HonestFills` toggle on run config (default ON; OFF preserves old behavior for A/B).
- Gate: characterization A/B run shows the delta; reconcile harness (P6) later confirms which is closer to cTrader.

### P1 — TF-agnostic, symbol-safe strategy bank

**P1.1 Instance-per-row (D1).**
- `StrategyConfigEntry` gains `EntryTimeframe` (+ optional `HigherTimeframe` stays per-strategy params). `StrategyRegistry.CreateStrategies` takes the run plan and creates ONE instance per row (strategy×symbol×TF), binding `(Symbol, EntryTimeframe)` into the config it hands the factory.
- `StrategyBankService.GetActive(symbol, tf, regime)` matches instances by bound symbol+TF (replaces both the `EntryTimeframe==timeframe` filter and the run-plan string set).
- This also fixes cross-symbol state pollution (0.2) for free — each instance sees one symbol's bars only. Add a regression test: two symbols interleaved, assert SuperTrend flips track per-symbol.

**P1.2 De-hardcode H1 in all 9 strategies.**
- `EntryTimeframe => _config.EntryTimeframe`; every `context.Bars.GetValueOrDefault(Timeframe.H1)` → `GetValueOrDefault(EntryTimeframe)`. `RequiredTimeframes` = `[EntryTimeframe]` (+ `HigherTimeframe` for mtf-trend). Indicator requests already default to the evaluation TF — verify `IndicatorCache.BuildKey` distinguishes TF in the key (it must, or an H1+M15 run of the same strategy collides; add a test).
- Gate: golden H1 byte-identical (same instances, same order for existing plans); NEW acceptance test: M15 tape run on EURUSD produces ≥1 proposal for trend-breakout.

**P1.3 Aux-TF feed (kills the mtf-trend silent death).**
- Orchestrator computes the union of `RequiredTimeframes` across planned rows. Tape path: load aux-TF bars from `IMarketDataStore` and interleave them into the engine's bar stream in time order (aux bar emitted when its close time ≤ current decision bar close; it only updates the indicator window, never drives the loop). cTrader path: set cBot `Periods` to the union.
- **MISSING_DATA verdict:** when a strategy's required TF has no bars (or a required indicator key is absent), emit `StrategyVerdict(Reason: "MISSING_DATA: no H4 bars")` instead of "no signal". Surface it in the monitor's verdict panel.
- Gate: mtf-trend fires on tape with H4 present; with H4 absent the journal shows MISSING_DATA (not silence).

**P1.4 UI guardrails.**
- new-backtest: a row whose TF (or aux TF) has no inventory shows a warning chip (inventory lookup already exists); run monitor shows per-strategy verdict funnel counts (proposals / gated / MISSING_DATA / no-signal) so "0 trades" is never mute again.

### P1.5 — Close the P1 static-review gaps — **Done** (2026-07-05, same session)

**Added 2026-07-05** after a static review of the `iter/quant-model--p1-tf-agnostic` commits (edeb3a6, e376a1b,
71ea2d7, 6d41398). Two of the three findings below are CONFIRMED via full code trace (not guesses) and mean
**P1's own headline claim — "non-H1 strategies now trade" — is not actually true for tape runs yet**. Do this
phase before touching P2.1 (indicator series API), which builds directly on the same indicator pipeline and
would otherwise inherit both bugs silently.

**P1.5.1 — Indicator requests are still pinned to H1 (CRITICAL, reintroduces the exact P1 bug).**
Root cause: `IndicatorRequest`'s `Timeframe` parameter defaults to `Timeframe.H1`
(`src/TradingEngine.Domain/IndicatorRequest.cs:4`), and **none of the 9 strategies' `RequiredIndicators`
getters pass `Timeframe: _config.EntryTimeframe`** when constructing their requests — P1.2 de-hardcoded the
strategies' own bar lookups (`context.Bars.GetValueOrDefault(_config.EntryTimeframe)`) and `RequiredTimeframes`,
but missed the indicator-request layer entirely. `MtfTrendStrategy.RequiredIndicators` (lines 35-36) makes the
same mistake *explicitly*: its RSI/ATR requests hardcode `Timeframe: Timeframe.H1` even though only its EMA
request correctly uses `_config.HigherTimeframe`.
- **Effect (traced through the real pipeline, not assumed):** `IndicatorSnapshotService.RecomputeIndicatorsAsync`
  is called per decision-bar-close with `tf` = the bar's own timeframe. For a request whose `Timeframe` (H1,
  by default/mistake) differs from `tf`, it looks up `Bars[symbol][H1]` — but the orchestrator only ever loads
  bars for the decision TF (plus P1.3's hardcoded mtf-trend/H4 aux). For any run-plan row whose
  `EntryTimeframe != H1`, `Bars[symbol][H1]` was never populated, so the lookup misses and the code `continue`s
  — the indicator is **never computed**, `context.IndicatorValues.TryGetValue(...)` always returns false, and
  the strategy returns null unconditionally. **Every one of the 9 strategies produces zero signals on any
  non-H1 timeframe row** — this is the identical "0 trades on M15" defect from PLAN.md §0.1 that P1 exists to
  fix, just relocated one layer down.
- **Why this wasn't caught:** PROGRESS.md's own "what's NOT in P1" section admits *"No M15 acceptance test
  (requires live M15 data not guaranteed by test harness)"* — the one test that would have caught this
  (PLAN.md's own P1.2 gate: *"M15 tape run on EURUSD produces ≥1 proposal for trend-breakout"*) was skipped.
  The test that WAS added this session, `IndicatorCacheKeyTests.cs`, only proves `IndicatorCache.BuildKey` CAN
  distinguish timeframes when handed hand-constructed `IndicatorRequest`s with different `Timeframe` values —
  it never checks what the real strategies actually pass, so it gave false confidence. Similarly,
  `StrategyScenarioTests.cs`'s `MtfTrendScenarios`/`SuperTrendScenarios` etc. compute indicators via
  `StrategyTestHelper.ComputeIndicators` directly from a bar slice — they bypass `IndicatorSnapshotService`
  entirely and would never exercise this bug either.
- **Fix:** every strategy's `RequiredIndicators` getter must pass `Timeframe: _config.EntryTimeframe` explicitly
  (mtf-trend: on the RSI/ATR entries only; keep EMA on `_config.HigherTimeframe`).
- **Failing test first:** a real end-to-end acceptance test (drive `EngineRunner` or the tape harness over
  synthetic M15 bars, NOT `StrategyTestHelper`) for a single-TF strategy (trend-breakout or super-trend — one
  indicator each, simplest to fixture) asserting ≥1 `OrderProposed` on M15. This is the literal P1.2 gate that
  was deferred — land it now, don't defer it again. Additionally, a cheap per-strategy unit test: construct
  each real strategy with `EntryTimeframe = Timeframe.M15` and assert every `RequiredIndicators` entry's
  `Timeframe == Timeframe.M15` (mtf-trend: RSI/ATR == M15, EMA == HigherTimeframe).
- Gate: the new M15 acceptance test passes; golden/H1 suites stay byte-identical (H1 is still the default, so
  existing fixtures shouldn't move).

**P1.5.2 — Aux-TF preload leaks future data into every decision (CRITICAL, lookahead bias).**
Root cause: `EngineRunner.cs`'s P1.3 aux-bar preload (`_preloadedAuxBars`) reads the **entire run's H4 bar
range** up front (`IMarketDataStore.ReadBarsAsync(sym, auxTf, from, to, ct)` for the whole `[from,to]` window)
and `list.AddRange(barList)`s it into `IndicatorSnapshotService.Bars[symbol][H4]` in one shot, then calls
`RecomputeIndicatorsAsync` **once**, before the main per-bar loop even starts. Nothing re-slices that list to
"bars closed as of the current decision time" and nothing re-triggers the aux-TF recompute as the loop
advances — the normal per-bar call (`TradingLoop.cs:64` / `BarEvaluator.cs:67`,
`RecomputeIndicatorsAsync(symbol, bar.Timeframe, ct)`) only ever passes the DECISION bar's own timeframe, but
internally still recomputes any strategy's aux-TF-timeframe indicator request too, reading whatever is
currently sitting in `Bars[symbol][H4]` — which, because the full future range was dumped in at t=0, is always
the complete, run-end-inclusive series.
- **Effect:** `mtf-trend`'s H4 `EMA_200` is computed exactly once, from the full date range, and that single
  value is read for **every decision bar throughout the entire backtest** — i.e. the H4 trend filter used at
  the START of a 6-month run already knows the H4 trend as of the END of the run. This is textbook lookahead
  bias and it directly contradicts PLAN.md's own P1.3 design (*"aux bar emitted when its close time ≤ current
  decision bar close; it only updates the indicator window, never drives the loop"*) — the shipped code does
  not gate by close time at all; it does the opposite of what the plan specified. This affects mtf-trend at
  **any** EntryTimeframe, including H1, since the H4-aux-load path is independent of P1.5.1's bug.
  Consequence: no mtf-trend tape result (existing or future) should be trusted for P3/P4 calibration until
  this is fixed — it undermines the exact "honest backtesting" goal P0 was built for, for this one strategy.
- **Why this wasn't caught:** golden H1 fixtures stayed byte-identical because mtf-trend was previously DEAD
  on tape (0 trades, per PLAN.md §0.2) — this is genuinely NEW code path with no prior baseline to diff
  against, and the only test touching it (`MtfTrendScenarios.DoesNotThrow_DuringEvaluation`) computes
  indicators via `StrategyTestHelper` directly (bypassing `EngineRunner`/the preload path entirely), so it
  cannot see this bug either, by construction.
- **Fix:** maintain a per-aux-TF cursor and feed aux bars incrementally, gated by `closeTime <= currentDecisionBar.closeTime`,
  re-triggering `RecomputeIndicatorsAsync` for that aux TF each time a new aux bar becomes eligible — this is
  exactly what PLAN.md's P1.3 agent-guidance section already prescribed; the implementation just didn't do it.
- **Failing test first:** construct a synthetic H4 series where EMA computed over bars `[0..N/2]` gives a
  DIFFERENT sign/direction than over the full `[0..N]`; assert a decision bar at sim-time `N/2` sees the
  "as-of-then" EMA, not the full-range one. This test fails against current `main` of this branch and passes
  once point-in-time gating lands.
- Gate: the new lookahead test passes; a real mtf-trend tape run's H4 EMA value should now visibly change over
  the course of the run (quote the before/after in the PR body — "constant across N bars" → "varies").

**P1.5.3 — Silent H1 fallback on unparseable run-plan timeframe (LOW severity, cheap fix, same bug class).**
`StrategyRegistry.CreateStrategies`'s run-plan branch: `EntryTimeframe = Enum.TryParse<Timeframe>(entry.Timeframe,
ignoreCase: true, out var tf) ? tf : Timeframe.H1` — silently substitutes H1 on a parse failure instead of
throwing. Currently unreachable in practice (`RunPlanBuilder.FromRows` always upper-cases validated dropdown
values before this runs), but it is a silent-failure landmine of exactly the kind this whole iteration exists
to eliminate, waiting for the first future caller (sweep runner, hand-edited `RunRows` JSON, a new API surface)
that sends an unrecognized TF string. Change to throw `InvalidOperationException`, matching the existing
fail-loud pattern the same method already uses for unknown strategy IDs / missing config entries.
- Gate: a unit test constructing a run-plan row with `Timeframe: "bogus"` asserts the call throws rather than
  silently binding H1.

**P1.5.4 — MISSING_DATA verdict was never implemented (documentation gap, fold into P1.5 or P2's verdict funnel).**
PLAN.md's P1.3 gate explicitly promises: *"with H4 absent the journal shows MISSING_DATA (not silence)"* —
grepping the full `src/` and `tests/` trees for `MISSING_DATA` returns zero hits. This wasn't disclosed in
PROGRESS.md's "what's NOT in P1" deviations list (which only calls out the warning chip and verdict funnel
UI, not this specific verdict reason string). Low severity on its own — file it as a known-still-missing item
under the existing P2/verdict-funnel work rather than a separate fix, but don't let it get lost.

---

### P2 — Entry surgery (the strategy bank becomes honest hypotheses)

**P2.1 Indicator series API.**
- `IndicatorSnapshotService` keeps a ring buffer (last 64 values) per sig key; `MarketContext` gains `IndicatorSeries: IReadOnlyDictionary<string, IReadOnlyList<double>>` (latest last). `BuildStrategyIndicatorValues` populates both.
- Port `_lastHist` (macd), `_prevRsi` (mtf), `_prevDirection` (supertrend), bb-width queue to read the series — deletes cadence-fragile private state. (Instance-per-row already removed the pollution; this removes the fragility.)

**P2.2 rsi-divergence rewrite (real divergence).**
- Pivot-based: find the two most recent swing lows (bullish case) in the lookback window from bars (fractal 2-2 or N-bar pivot); require price lower-low AND RSI(series at those pivot bars) higher-low; entry on confirmation (close above the divergence bar's high), SL below the second pivot low. Mirror for bearish.
- **Failing test first** with a constructed fixture (synthetic bars producing a known divergence) + a negative fixture (no divergence → no signal).
- Delete the tautology lines with prejudice.

**P2.3 Edge semantics (D5, D8).**
- ema-alignment → crossover event within last K bars + first pullback touch of fast EMA (edge). trend-breakout → single-fire: signal only when the breakout bar's high crosses a level the PRIOR bar had not (plus per-level cooldown). bb-squeeze → latch expires after `BbPeriod` bars (D8).
- Each change gets an A/B tape run in the PR description (same window, before/after: trades, win%, net, avg hold).

**P2.4 Time-flatten behavior (D6).**
- Loop-level: a per-strategy optional `FlattenAtUtc` (from config) closes that strategy's open positions at the first bar ≥ the time. Session-breakout config already carries `12:00` — wire it. (Also the building block for P7 news/weekend flattening.)

**P2.5 Thesis metadata.**
- Each strategy config gains `thesis` (one sentence), `expectedTradesPerWeek`, `expectedHoldBars`. Surfaced on the strategy page; used by the P4 frequency reality check. Forces every hypothesis to state its falsifiable claim.

**P2.6 Units doctrine (D9) — normalize the raw-pip fields.**
- Convert: `breakeven.offsetPips` → `offsetSpreadMultiple`; `limitOffsetPips` → `limitOffsetAtrFraction`; `stopLoss.maxPips` + `RiskProfile.MaxSlPips` → `maxSlAtrMultiple` (or per-symbol override table); `maxSlippagePips` → spread-multiple; `trailing.stepPips` → ATR-fraction (tuner already computes it that way).
- Backward compat: keep old JSON fields readable with a deprecation warning for one iteration; seeder migrates values using the symbol's current reference ATR.
- **Config linter**: startup validation (and a `dotnet run --lint-config` CLI) that fails on any raw-pip field in strategy/pack/profile configs. This is what makes gold/crypto safe by construction.

**P2.7 Stop orders (entry on confirmation).**
- Add `OrderType.Stop` end-to-end: both replay venues (fills when ask/bid crosses the stop level, gap-through at open, same expiry semantics as limits), cBot command mapping, `EntryPlanner` `StopConfirm` method (e.g. buy stop at signal bar high + spread-multiple buffer). Needed by the P2.2 divergence rewrite and by any breakout strategy that should demand confirmation instead of filling at close.

### P3 — Excursion recorder + Exit Lab (automated MAE/MFE exploitation)

This is the owner's "utilize MAE/MFE automatically" ask, done per QUANT-ROADMAP §4 (excursion grid), which corrects the naive percentile recipe.

**P3.1 Recorder.** In `TapeReplayAdapter`'s fine-bar loop (it already iterates open trades per M1 bar), when run config has `RecordExcursions=true`: append `(fineBarOpenTime, highVsEntryPips, lowVsEntryPips)` per open position. Persist per trade on close: new table `TradeExcursions(RunId, PositionId, PathJson)` (compact JSON array; a few hundred bytes/trade). Write-through the existing trade persistence channel.

**P3.2 Exploration mode.** One-click run preset: SL = ATR×4, TP = none, BE/trail/partials OFF, governor OFF, `RecordExcursions=true`. (All toggles already exist; this is a named preset in the UI + orchestrator.)

**P3.3 ExitReplayer service (pure).** Input: excursion paths + a grid of exit rules (SL ATR-multiple × TP RR × BE trigger-R × trailing ATR-multiple × partial trigger/fraction). Walk each path; SL-first-conservative within a bar (same rule as the venue); output per-cell: expectancy (R vs initial stop — honest after P0.1), win%, avg hold, DD contribution. Thousands of exit configs × thousands of trades in milliseconds, zero engine re-runs.
- **Validation gate:** replaying the exit rule an actual run used must reproduce that run's exits (within one fine bar / tick-size tolerance) — this test proves the replayer before anyone trusts a grid.

**P3.4 Calibration tables.** Survivors (per strategy×symbol×TF, optionally ×regime) stored in a `ExitCalibrations` table: chosen params + fitted dataset id + IS/OOS windows + fit date. `AddOnResolver` gains `Mode=Calibrated`: read the table, fall back to `Auto` heuristics when absent. Auto-tuner stops being folklore and becomes a fallback.

**P3.4b Measured reference scales (D11).** At ingest (and on demand), compute per (symbol, TF): rolling-median ATR(14), median bar range, median spread (once P6.2 records it) → `ReferenceScales` table. `AddOnAutoTuner` + the P2.6 unit conversions read it; `ReferenceAtrPips(tf, spread)` becomes the fallback of the fallback. One honest number replaces a family of guessed factors.

**P3.5 Exit Lab UI.** Page per (strategy, symbol, TF): heatmap of the exit grid (expectancy color, P(pass) toggle), **plateau highlighting** (cells whose neighbors are all ≥ x% of peak — pick plateau centers, never isolated peaks), "apply to calibration" button. Also render the classic MAE/MFE scatter (already an endpoint) with the chosen SL/TP lines overlaid.

**P3.6 Proposal ledger + entry-tactic lab (D10).** Record EVERY proposal (accepted, gated, and — new — venue outcome: filled / expired-unfilled) with the signal price and time. For unfilled limit orders, the excursion recorder still tracks the counterfactual path from the signal (as if filled at signal close) for N bars. Output: per cell, fill-rate, expectancy-conditional-on-fill vs counterfactual-expectancy-of-missed — the honest answer to "should this strategy enter Market, Limit@0.25×ATR, Limit@0.5×ATR, or StopConfirm?". Entry tactic then becomes a sweep dimension in P4, per cell, instead of a hardcoded per-strategy choice.

### P4 — Research metrics: P(pass), walk-forward, scoreboard

**P4.1 P(pass) everywhere.** Wire `PassProbabilityEstimator` (exists in `TradingEngine.Experiments`) to: run detail page, sweep results grid (new column, default sort), exit-lab grids. Inputs: OOS trade list + active prop-firm rule set + risk-per-trade.

**P4.2 Walk-forward harness.** Orchestrator-level: split [from,to] into rolling (train, test) windows (e.g. 6m/2m step 2m — configurable); per window: sweep on train → freeze best plateau cell → run on test; stitch test segments into ONE OOS equity curve; report page shows stitched curve + per-window chosen params + **trials count** (configs tried — printed next to every result, per QUANT-ROADMAP §3.2). Reuses the sweep runner and content-addressing; `VariantScorer`'s fold logic is prior art to lift.

**P4.3 Scoreboard page ("what do I trade?").** Matrix strategy×symbol×TF: latest OOS expectancy, P(pass), trades/week, data coverage badge, calibration freshness, correlation group. Ranked default view = the owner's symbol/strategy picker, answering that question with evidence instead of vibes. Kill/park buttons write `Enabled=false` with a reason string.

**P4.4 Frequency reality check.** On the scoreboard: `needed trades ≈ target% / (risk% × OOS avgR)` vs actual OOS trades per 30 days (per QUANT-ROADMAP §3.3 arithmetic); red-flag cells that cannot plausibly pass a phase alone.

### P5 — Data + triage program (owner-driven; agent supports)

**P5.1 Download (D7).** Majors 6 + XAUUSD; TFs {M1, M15, H1, H4}; max available history. Prereq: the download port allocator (known gap D85) OR run downloads sequentially. Verify ingest dedupe on re-runs (B7 landed).
**P5.2 Non-FX correctness tests.** Per-category (Metal/Crypto/Index) unit tests over `PipCalculator` + `TradeCostCalculator` using the registry's XAUUSD/BTCUSD/US30 rows (pipSize 0.01/1.0/1.0, contract 100/1/1): pip value, lot sizing at 0.5% risk, commission/swap application. Silent financial errors are the project's stated nightmare — no gold/crypto run before these are green.
**P5.3 Exploration triage.** All strategies × symbols × {M15, H1, H4} exploration runs (P3.2) over full history; rank raw entry quality (fixed-R exit over excursion paths, post-cost); produce the kill/park/keep list with evidence attached. Expect several to die — that is success, not failure.
**P5.4 Portfolio assembly.** Correlation groups: config map symbol→group (EUR-bloc, JPY-bloc, metals…) + per-group open-risk cap enforced in `PreTradeGate` (pure, opt-in, golden-safe). Measure pairwise correlation of OOS daily PnL between surviving cells; assemble 2–4 low-correlation cells; portfolio-level Monte Carlo P(pass).

### P6 — Oracle backstop (tape accuracy, finally measured)

**P6.1 Execute V4.** Owner runs compare-both (tape + cTrader, identical config, H1 EURUSD first). Acceptance: identical trade count; entries within 1 bar; per-trade net delta explained by the spread model; MaxDD within tolerance. Findings → `RECONCILE-FINDINGS.md`; every unexplained divergence becomes a bug with an id. Repeat after P0.2/P0.3 to choose the `HonestFills` default.
**P6.2 Per-bar recorded spread.** Recorder captures spread at bar close (one nullable column on the shard schema + `MarketDataBars`); venues prefer it over `TypicalSpread`. This is what makes M5/M15 research honest (session-dependent spread widening is exactly where naive backtests lie most).
**P6.3 Weekly drift habit.** Documented one-liner: one compare-both run per week while iterating; drift = investigate before continuing research.

### P7 — FTMO ops (after evidence exists)

- **P7.1** Intraday daily-DD guard: breach watchdog consumes the intrabar min-equity envelope (already computed) instead of bar-close equity.
- **P7.2** Flatten-before-news/weekend behaviors per strategy (built on P2.4's mechanism); validate with Monte Carlo that each raises P(pass) before enabling.
- **P7.3** Phase tracker page: live P(pass|current state) during a challenge (days left, distance to target/limits), with the governor's risk step-down rules made phase-aware.

---

## 4. Verification matrix (gates per phase)

| Phase | Machine gate | Evidence artifact |
|---|---|---|
| P0.1 | new unit test (R vs initial stop after BE move); golden byte-identical | migration + backfill row counts in PR body |
| P0.2 | 8 fill-path price tests; characterization re-baseline commit isolated | before/after net deltas quoted |
| P0.3 | A/B characterization run | delta table in PR |
| P1 | golden H1 identical; M15 acceptance run ≥1 proposal; cross-symbol state test; indicator-key TF test | DB query: non-H1 runs now trade |
| P1.5 | M15 acceptance test (real, engine-driven) green; per-strategy indicator-TF unit test green; lookahead test green; bad-TF-string test throws | before/after H4-EMA-varies-over-run quote in PR |
| P2 | divergence fixtures (pos+neg); per-change A/B tape runs; config linter fails on a raw-pip fixture | A/B tables in PRs |
| P3 | replayer-reproduces-actual-run test; proposal ledger records unfilled-limit counterfactuals | one exit-lab heatmap screenshot |
| P4 | stitched OOS curve renders; trials counter shown | scoreboard screenshot |
| P5 | non-FX cost tests green; coverage view shows downloads | triage kill/keep list doc |
| P6 | reconcile endpoint output committed | RECONCILE-FINDINGS update |

Standing gates for every phase: `dotnet build`; Unit + Integration suites; fast Simulation filter (`RequiresCTrader!=true`); golden/determinism suites; no new `DateTime.UtcNow`/`Guid.NewGuid` in Engine (purity test).

---

## 5. Explicitly out of scope (this iteration)

- ML/NN signal models, new indicator strategies (until P5 triage says the current families are exhausted), tick-level tape (P6 of iter-marketdata-tape — only if M1 reconcile proves insufficient), alternative brokers, live-account wiring, HFT anything.
- Perf: Skender full-window recompute per bar (~500-bar window) is acceptable for H1/M15 research; incremental indicators only if M1-decision-TF sweeps become a real workload (measure first).

## 6. Agent guidance — tricky parts & suggested approach per task

General method for every phase (non-negotiable): (1) write the failing test first when the change is behavioral; (2) one phase = one commit/PR with its gate output pasted in the body; (3) never mix a re-baseline with a logic change; (4) run the golden/determinism suites before AND after; if golden moves when the plan says it shouldn't, STOP and report — do not re-baseline to make it pass; (5) new DB columns are nullable-with-default so old rows stay readable; (6) grep-gate deletions (`grep -r` proves dead code is gone).

**P0.1 (R vs initial stop).** Trap: `PositionState` is a record inside the pure kernel — thread `InitialStopLoss` through `PositionLifecycle.CreateIntended` → every `with`-mutation site (grep `CurrentStopLoss =` to find stop-move sites and confirm they DON'T touch the new field). The `PublishTradeClosed` effect is constructed in `EngineReducer`/`PositionLifecycle` close paths (3 sites: SL/TP close, venue close, force close) — miss one and R is right for some exits only; add one test per exit path. Backfill: write it as an idempotent script (re-runnable), parse `EntrySnapshotJson` defensively (some early rows may lack it), report counts (updated/skipped/failed), and take a DB backup file copy first (there is precedent: `trading.bak-*.db`).

**P0.2 (spread convention).** Do the two adapters in ONE commit with the shared convention comment pasted in both — they have drifted before (that's how the half-spread asymmetry happened). Build a tiny table-driven test: one fixture bar + 2-pip spread, 8 cases with hand-computed expected fill prices in the test as literals. Only after those pass, run the characterization suite and re-baseline in the separate `REBASELINE:` commit. Trap: `ProcessSlTpHits` reconstructs the shifted bar for shorts — the shift amount and the fill-price re-adjustment must use the SAME constant; extract one `Ask(bar)`/`AskPrice(p)` helper instead of scattering `+ spread`.

**P0.3 (honest fills).** Implement as a small pending-market-order queue inside `TapeReplayAdapter` (mirroring `_pendingLimits`): `SubmitOrderAsync` enqueues, the next fine bar in `OnBarObserved` fills at its open ± side-adjustment. Trap: orders submitted on the LAST bar of the run must still fill (flush at disconnect using `_lastClose`) or trade counts will mysteriously differ from the A/B baseline. Keep the toggle read once at construction — do not branch per-bar on config lookups.

**P1.1 (instance-per-row).** The tricky part is DI lifetime: strategy instances are created by `StrategyRegistry.CreateStrategies` from `LoadedConfig` — extend the signature to accept the run-plan rows and construct a `StrategyConfigEntry` clone per row with `Symbol`/`EntryTimeframe` bound (record `with`). Do NOT try to make the strategies themselves row-aware. `IndicatorSnapshotService` and `KernelTrailingEvaluator` both hold `IReadOnlyList<IStrategy>` — they receive the per-row list automatically, but `RequiredIndicators` de-dup (`computed` HashSet) must now key on TF too: verify `IndicatorCache.BuildKey` includes the request TF (write the collision test first: same strategy on H1+M15, assert two distinct keys). Golden risk: instance ORDER affects proposal order — preserve run-plan row order exactly.

**P1.3 (aux-TF feed).** Don't feed aux bars through the venue's bar channel (they would drive the kernel loop). Cleanest seam: in the orchestrator/engine-runner warmup + per-bar path, maintain an aux-bar cursor per aux TF read from `IMarketDataStore`; before `BarEvaluator.EvaluateAsync(bar)`, append every aux bar with `closeTime <= bar.closeTime` into `indicatorSnapshot.Bars[symbol][auxTf]` (same 500-cap ring). It's the same structure `BarEvaluator` already maintains for the decision TF — you're just a second writer. Trap: warmup — pre-load enough aux history before the first decision bar or mtf-trend spends the first 200 H4 bars starved (compute required warmup from `RequiredBarCount` of the strategies that declare the aux TF).

**P2.1 (indicator series).** Ring buffer per sig key: `Queue<double>` capped at 64 inside `RecomputeIndicatorsAsync` right where `IndicatorValues[sigKey]` is assigned — one write point. Expose as `IReadOnlyList<double>` snapshot in `BuildStrategyIndicatorValues`. Do NOT try to backfill series during warmup replay — the buffer fills naturally as warmup bars are replayed through the normal path; assert in tests that after N warmup bars the series has min(N, 64) entries.

**P2.2 (divergence rewrite).** Build the pivot finder as a pure static (`PivotFinder.FindSwingLows(bars, strength)`) with its own unit tests before touching the strategy. Fixture data: construct bars arithmetically (e.g. two V-shapes with controlled lows) — do not fish real data for a divergence, you'll never pin the assertion. The RSI series values at pivot indices come from P2.1's series (`series[^(barsAgo+1)]`); off-by-one is THE bug class here — assert the series index against the bar index in the fixture.

**P2.6 (units linter).** Do conversions in the seeder/config-load layer, not scattered through strategies: load old field → convert using `ReferenceScales` (or symbol ATR at load time) → populate the new normalized field → log a deprecation warning. The linter is a pure function over the loaded config object (testable), wired into startup AND a CLI flag. Do not delete old JSON fields this iteration — one-iteration deprecation window (the owner hand-edits these files).

**P3.1 (excursion recorder).** Record in the venue's fine-bar loop where `ProcessSlTpHits` already iterates `_openTrades` — but buffer paths in memory per position and persist ONCE at close (via the existing trade persistence channel), never per-fine-bar (181k M1 bars × open positions would melt SQLite). Cap path length defensively (e.g. 10k points → downsample by 2× when exceeded) so a never-closing exploration trade can't OOM. Serialize as `[[t,hi,lo],...]` compact arrays, pips as `double` rounded to 0.1.

**P3.3 (exit replayer).** Keep it a PURE static over `(path, entrySide, rule)` → `ExitOutcome` — no DB, no DI — so the grid loop is trivially parallel (`Parallel.ForEach` over rules × trades). The validation gate (reproduce a real run's exits) is the hard part: expect small mismatches from spread handling — the recorder stores bid-relative excursions; document whether paths are bid or mid ONCE and adjust per side in the replayer, same convention doc as P0.2. Tolerance: one fine bar in time, one tick in price.

**P4.2 (walk-forward).** Reuse the sweep runner per window rather than inventing a new executor; the harness is a coordinator that (a) derives window boundaries, (b) calls sweep on train, (c) picks the plateau cell (rule: highest median of the 3×3 neighborhood, ties → smaller SL), (d) queues the frozen config on test. Persist every window's choice — the report must show parameter drift across windows (instability = the real result). Trap: content-addressing means re-runs are cheap, but the plateau pick must be deterministic (no `MaxBy` on unstable float ties without a tiebreaker).

**P5.2 (non-FX tests).** Write the expected pip values by hand from first principles in the test comments (XAUUSD: pip 0.01, contract 100 → pip value $1/lot; BTCUSD: pip 1.0, contract 1 → $1/lot; US30: pip 1.0, contract 1 → $1/lot) and assert `PipCalculator` agrees — if it doesn't, the CODE is wrong, not the test. Check `TradeCostCalculator` swap handling for the crypto rows (swap 0 — must not NaN/throw) and the commission=0 index rows.

**P6.1 (reconcile).** Before comparing, pin BOTH runs' configs byte-identical except venue (same seed, same pack, governor state). First divergence to expect: trade COUNT from cTrader's tick-level intrabar behavior vs tape M1 — reconcile entries by `ClientOrderId`/time-window, not by index. Write findings as numbered F-ids in `RECONCILE-FINDINGS.md` even when the answer is "explained, no action".

## 7. Beyond current implementation — quant feature ideas (the wild list, ranked by leverage)

Each idea: what + why + rough method. None are commitments; the scoreboard decides.

1. **Proposal counterfactual ledger** (P3.6, listed here because it generalizes): every gated/rejected/unfilled proposal gets a counterfactual outcome tracked for N bars. Answers with data: "is the governor/gate/limit-entry costing or saving money?" — today every filter's value is faith.
2. **Session fingerprinting:** label every bar (Asia/London/NY/overlap, day-of-week, minutes-to-major-news once a calendar lands) and cut ALL research by session labels. Time-of-day edges are the most defensible retail edge class (structural liquidity), and the labels are nearly free to add to the excursion/scoreboard pipeline.
3. **Regime-conditioned calibration:** P3.4 tables keyed by regime label too; at entry the resolver picks the row for the CURRENT regime. Validates (or kills) the regime detector with money numbers instead of thresholds-folklore — sweep the regime.json thresholds themselves per QUANT-ROADMAP §5.
4. **Meta-allocator (portfolio governor v2):** monthly re-weighting of enabled cells by rolling OOS P(pass) contribution (not recent PnL — that's performance-chasing). Pure config-in/config-out, auditable, no ML. The existing rotation service (win-rate based, `RotationMode.PerformanceBased`) is the primitive to replace — win-rate is the wrong objective.
5. **Block-bootstrap synthetic tapes:** resample real bars in session-preserving blocks (e.g. whole London sessions) into N synthetic histories; run the portfolio across them for DD/P(pass) *distributions*. Cheap once the tape venue exists; the strongest antidote to "one history = one draw".
6. **MAE-based early-exit rule ("loser triage")**: from excursion paths, find the (barsHeld, adverseATR) frontier beyond which P(recovery to +0.5R) < x% — exit early instead of riding to full SL. This is the highest-value automated MAE use: it attacks the −$726k SL bucket directly. Fit on IS, verify OOS, per cell (P3.3 grid gains a time dimension).
7. **Spread/volatility-aware NO-TRADE filter:** skip entries when currentSpread > k × medianSpread or ATR percentile > 95 (news spikes). Needs P6.2 recorded spread; likely the cheapest win-rate improvement available on lower TFs.
8. **Entry-quality score decomposition:** for each strategy, regress OOS trade R on observable-at-entry features (ATR percentile, session, distance-to-EMA in ATR, squeeze age…). Not for prediction — for *diagnosis*: it tells you WHICH conditions your entries pay in, feeding filter design by evidence. Plain OLS on a handful of features; explainable.
9. **Adaptive `blockWhileSameDirectionOpen` → pyramiding policy:** the current re-entry config forbids adds; trend strategies may earn more with structured adds (add at +1R, move stop to entry). Model as an exit-lab dimension over excursion paths (an "add" is a second excursion starting mid-path) before any engine change.
10. **Data-quality sentinel:** per ingest, verify bar continuity (gaps vs expected session calendar), OHLC sanity (H≥max(O,C) etc.), and cross-TF consistency (M1 aggregates ≈ recorded H1). Research on holey data manufactures phantom edges; this belongs with P5.1 and is one honest afternoon of work.

## 8. Suggested execution order & sizing

`P0.1 → P0.2 → P1.1–P1.2 → P1.3–P1.4 → P1.5 → P2.2 → P2.1 → P2.3–P2.5 → P3 → P4 → (owner: P5, P6 in parallel) → P7`.
Each arrow is a separately landable commit/PR with its gate. P0+P1 together are roughly one focused agent session; P1.5 is small (half a session — two targeted bug fixes plus their failing tests) but **non-optional**: P2.1 (indicator series API) extends the exact pipeline P1.5.1 patches, and would silently inherit the H1-pinning bug into the new ring-buffer series if built on top of it unfixed. P2 one session; P3 one-to-two; P4 one-to-two. If forced to cut: **P0, P1, P1.5, P3 are the spine** — truth, reach (actually verified, not just claimed), and the exit lab; P2 fixes ride along where cheap, and P2.2 (rsi-divergence) can simply stay disabled until rewritten.
