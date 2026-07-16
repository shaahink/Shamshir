# Shamshir — Senior Quant Review Brief

**Written:** 2026-07-16 · **Branch:** `docs/quant-review` (snapshot of `iter/structural-edge` @ `a2548ed`)
**Audience:** an external senior quantitative researcher, engaged to review our system, methodology,
and results, and to advise on how to improve the research process and where to hunt next.
**Nothing in this document is asserted without a repo artifact behind it.** Every table is copied
from committed evidence files; file paths are given throughout so any number can be re-derived.

---

## 0. What we are asking you to do

1. **Audit the methodology** (§6): pre-registration, validity floors, split-half, walk-forward,
   embargo windows. Tell us where it is weak, naive, or missing standard tools.
2. **Interpret the results** (§7–§8): a full census of a 9-strategy bank came out ~zero-mean at
   cell level with zero cell-level persistence, but with apparently real *rule-level* structure
   in the exit layer. Is our reading right? Is the current plan (§9) the right response?
3. **Advise on direction** (§11): given the machinery we have (fast honest backtests, prop-firm
   risk stack, parity-verified execution), what class of edges should an operation of this size
   hunt, on which instruments/timeframes/data, and how should the FTMO "velocity" constraint be
   attacked?

The single most important context: **we believe our negative results more than our positive ones,
by design.** The whole program is built so that "no edge found" is a trustworthy, bankable outcome.
We would rather you sharpen the search process than rescue any particular strategy.

---

## 1. The system in one page

Shamshir is a single-owner algorithmic trading engine targeting **prop-firm (FTMO-style) challenge
accounts** on FX/metals/crypto CFDs. .NET 10 / C# 13, SQLite persistence, Angular web UI.

- **Decision core:** a pure, deterministic kernel (event-sourced reducer; no wall-clock, no RNG,
  no I/O). Same code runs backtest and live; byte-identical replay is a standing test gate.
- **Two venues behind one adapter interface:**
  - **cTrader** (real broker platform, via an in-platform cBot speaking NetMQ to the engine) —
    the *truth venue*: ~50–80 s per run, used for parity verification and eventual live execution.
  - **Tape** (recorded market data replayed through an in-process venue model) — the *research
    venue*: sub-second per run, semantics pinned to measured cTrader behaviour (§3.4–3.6).
- **Risk stack ahead of the alpha stack:** pre-trade worst-case drawdown gate, position sizing,
  drawdown-scaled risk, a trading governor (loss bands / cooling-off / profit-lock), FTMO rule
  sets (daily/total loss caps, profit target, min trading days), news/weekend filters.
- **Research platform:** experiments with pre-registered variants, per-cell scored runs
  (cell = strategy × symbol × timeframe × pack × risk profile), versioned composite scoring,
  6-fold walk-forward with an OOS-ratio cull, park-never-delete, and hard embargo windows.
- **Strategy bank:** 9 classic indicator strategies (breakout, mean-reversion, session, EMA/MACD/
  RSI/BB/SuperTrend/multi-TF) — *explicitly built as hypotheses to exercise the infrastructure*,
  not as claimed alpha (see `docs/QUANT-ROADMAP.md` §1).

State of play in one sentence: **the machinery is proven end-to-end; the current strategy bank ×
knob space has produced a trustworthy negative at cell level, structural (rule-level) leads in the
exit layer, and the active iteration is a pre-registered factorial to isolate which exit component
is real.**

---

## 2. History in brief (how we got here)

~40 iterations over 2026-05→07 (docs under `docs/iterations/`). The relevant arc:

| Phase | What happened |
|---|---|
| Engine build-out (iters 17–33) | Trading loop, risk engine, web UI; two diverged loops unified; kernel designed |
| Kernel cutover (iter-35/36) | Event-sourced kernel became THE production engine; golden-snapshot + determinism gates |
| Tape venue (iter-marketdata-tape, iter-tape-trust) | Recorded-data replay venue for research throughput; honest-cost work started |
| **iter-alpha-loop** (2026-07-10→15) | Parity program (P0–P4), platform hardening (X0–X5), full census (R1'), refinement (R3), embargo dress rehearsal (R4), audit (R5). Closed as a **trustworthy negative** |
| Deep research session (2026-07-16) | Post-mortem over the closed loop's data: findings F64–F68 (`docs/iterations/iter-structural-edge/RESEARCH.md`) |
| **iter-structural-edge** (open) | Hunt *rule-level* edges with cells as instances, not the unit of search (`docs/iterations/iter-structural-edge/PLAN.md`) |

A recurring meta-finding worth knowing before you read any result: many early "results" were
destroyed by later QA (a fake OOS walk-forward, a placeholder scoring component, inverted cost
signs, a census that commingled 9 strategies in one account). The current culture is that **every
session's first duty is to attack the previous session's claims against artifacts**, and the
finding IDs (F1…F69) are the audit trail. The results in §7 are the ones that survived that.

---

## 3. Execution architecture

### 3.1 The kernel (`src/TradingEngine.Engine`)

A pure reducer: `Kernel.Decide(state, event) → EngineDecision(state', effects)`. It owns position
lifecycle (FSM: Intended → Submitted → Open → Reducing → Closed), drawdown tracking
(daily/weekly/monthly/max, resets, velocity), the governor, the pre-trade gate, sizing, SL/TP
detection, and breach handling. Determinism is enforced mechanically:

- No `DateTime.UtcNow`, no `Guid.NewGuid()`, no I/O in the engine project — a body-scan test
  (`EnginePurityTests`) greps the source; `PositionId == OrderId` by construction.
- `DeterminismTests` runs identical scenarios twice and asserts byte-identical journals.
- All side effects are explicit `EngineEffect`s executed outside the reducer.

Consequences for research: concurrent tape runs are safe (per-run state, pure kernel), and any
number in the DB can in principle be regenerated from the event journal.

### 3.2 The per-bar loop

Strategies are evaluated on decision-bar close (H1/H4 in everything scored so far). Per bar:
venue advance → drain fills/account feedback → reconcile venue positions → day/week/month roll →
strategy evaluate → entry planning (limit/market) → pre-trade gate → size → submit → SL/TP/exit
detection → equity/breach check → trailing/breakeven evaluation → bar complete.

Position sizing: `lots = floor(riskAmount / (slPips × pipValue) / lotStep) × lotStep`, with
drawdown-proximity scaling (risk halves as the account approaches its loss limits) and a global
risk budget with heat accounting in `PreTradeGate` (`src/TradingEngine.Engine/Kernel/PreTradeGate.cs`).

### 3.3 cTrader as the truth venue

A compiled cBot (`TradingEngineCBot`, C# subset, runs inside cTrader) speaks to the engine over
NetMQ in **lock-step**: each closed bar is sent to the engine (DEALER→ROUTER), the engine runs the
full kernel cycle and replies `bar_done` with buffered commands (submit/close/modify), the cBot
executes them natively and reports executions back. cTrader itself owns fills, SL/TP execution,
commission, and swap — the engine reconciles to the venue's open-position set every bar
(`ExitMode: VenueManaged`). Two launch paths: headless (engine spawns `ctrader-cli` on dynamic
ports) and desktop capture (engine listens; a human runs the cBot in cTrader Desktop).

The cBot also writes its **own independent trade ledger** (`shamshir-report.json`) so that venue
truth survives engine- or CLI-side crashes; reconciliation between that ledger and our DB is part
of the E2E suite.

### 3.4 Tape as the research venue

`TapeReplayAdapter` (`src/TradingEngine.Infrastructure/Adapters/`) replays recorded bars —
14 symbols × 6 timeframes × 1 year (2025-07-04 → 2026-07-05 at alpha-loop start; auto-sync keeps
it accruing) — through the identical kernel loop, at sub-second per run vs 50–80 s on cTrader.
That throughput is the research platform's reason to exist; the discipline problem it creates
(mass experimentation manufactures false edges) is what §6 is about.

**The tape's fill semantics are measured, not assumed.** `docs/reference/RESTING-ORDER-CONTRACT.md`
is normative and was corrected against six real recorded cTrader fills (F43):

- cTrader's backtester replays each M1 bar as four synthetic ticks (O, H, L, C). A resting order
  fills at **the first of those ticks to breach its level — never at the level itself**. Stops
  fill *through* the stop (worse than named); limits fill *better* than named. Gap-through opens
  are just the general rule's `Open` branch.
- One shared implementation (`VenueFillModel.FirstBreachingTick`) is used by the tape, and is
  pinned by unit tests against the recorded venue fills (`VenueFillModelTests`).
- Buy-side touches are evaluated on the **ask** (bid + spread), sell-side on the raw bid
  (`SpreadConvention`); a pre-F43 bug that double-counted spread on short exits was found and
  fixed by this measurement.
- `HonestFills` (default ON) enforces honest *entry timing* — no same-bar clairvoyance.

### 3.5 Costs

One convention everywhere (D9): **costs are negative; `Net = Gross + Commission + Swap`**, guarded
by an invariant test over every `TradeResult` row on both venues.

- **Commission:** venue-declared type; for this broker `UsdPerMillionUsdVolume` — charged per
  side on USD notional at entry and exit prices ($30/M round-turn in scored research runs).
- **Swap:** venue-declared per-night rates, signed, weekends-free, triple-swap Wednesday. Swap
  rates and symbol economics come from the venue itself: the cBot emits a `symbol_spec` message
  (commission + type, swap long/short + calc type, lot/pip/tick size, digits) which overrides the
  static `config/symbols.json` (D10 — "the venue declares them; we never invent them"). This
  killed two earlier silent falsifiers: fabricated swap data that paid a nonexistent XAUUSD carry
  (F3), and a commission formula off by 3,300× on gold (F4).
- **Spread:** constant per-run (1.0 pip in all scored research), applied per the contract above.
  Per-bar recorded spread is on the roadmap, not built (`docs/QUANT-ROADMAP.md` §6).

### 3.6 Parity: the permanent gate between tape and truth

The alpha-loop's P0–P4 phases established a **pre-registered tolerance budget** (never widened to
make a result pass):

| Quantity | Tolerance |
|---|---|
| Trade count | exact |
| Entry price | ≤ 1 tick (limit entries are the research default, D11 — fills at a named price by construction) |
| Position size (lots) | exact |
| Exit price | ≤ 1 tick on ≥95% of matched trades (gap fills listed) |
| Commission | ≤ 2% |
| Swap | ≤ 5% |
| Net PnL | ≤ 1% of gross |

Status: **EURUSD `VERDICT: PASS`** with the budget untouched; fill/swap models pinned against
recorded venue output. Known residuals: **F47** — cTrader prices commission at one reference spot
per run (venue's own artifact; deliberately not matched); **F48** — XAUUSD tape-vs-venue net PnL
diverges ~1.37% (prices/lots/swap exact; only pip-value cross-rate *timing* differs; open,
deferred until an XAUUSD candidate approaches live). Governing doctrine: research stays tape-only;
any candidate presented to the owner must carry a parity verdict ≤ 14 days old.

---

## 4. Risk & prop-firm stack

**Prop-firm rule set `ftmo-standard`** (`config/prop-firms/ftmo-standard.json`): +10% profit
target, −5% max daily loss, −10% max total loss (fixed base), −4% weekly / −8% monthly soft caps,
min 4 trading days, equity = balance + floating − fees/swaps, daily reset 22:00 Prague, high-impact
news windows blocked (30 min before / 15 after), no weekend holding.

**Risk profiles** (`config/risk-profiles/`):

| Profile | Risk/trade | Max daily DD | Max total DD | Max concurrent | Max exposure | SL cap (ATR×) |
|---|---|---|---|---|---|---|
| conservative | 0.25% | 3% | 6% | 2 | 3% | 2.5 |
| standard (research default) | 0.5% | 4% | 8% | 3 | 5% | 5.0 |
| aggressive | 2.0% | 5% | 10% | 5 | 10% | 7.5 |
| raw (diagnostics only) | 5.0% | off | off | 20 | 50% | 25 |

All profiles include drawdown-proximity scaling (risk scales toward a floor as loss caps
approach). The pre-trade gate additionally projects **worst-case portfolio drawdown if every open
SL is hit simultaneously** and blocks entries that could breach. Note for §7: R4 measured this
stack as effective — 0 of 12 embargo challenge windows breached any cap.

**Known gap for portfolio work:** no portfolio risk profile exists — `standard`'s
3-concurrent/5%-exposure caps saturate immediately with 8 cells on one account
(`RESEARCH.md` §6). Building one is gated behind proof of a structural edge (§9, S6).

---

## 5. The strategy bank

All nine are classic indicator systems on OHLCV bars, seeded from `config/strategies/*.json`
(DB-canonical after seed). Common scaffolding: ATR-based initial SL, R-multiple TP, limit-offset
entries (rest a few pips better than signal price, expire after N bars), re-entry cooldowns,
regime filter hooks. Default risk profile `standard` throughout.

| Family | Thesis (from config) | Entry TF(s) | Default SL / TP |
|---|---|---|---|
| trend-breakout | Fresh N-bar high/low above trend EMA, ADX-confirmed, continues | H1/H4 | 1.5×ATR / 2R |
| mean-reversion | RSI extreme + outer-Bollinger rejection snaps back to mean | H1/H4 | 1.5×ATR / 1R |
| session-breakout | Break of quiet Asian/early-EU range in London/NY window; daily flatten 12:00 UTC | H1/H4 | 1.5×ATR / 2R |
| ema-alignment | First pullback to fast EMA after fast/slow crossover | H1/H4 | 1.5×ATR / 2R |
| macd-momentum | MACD histogram zero-cross on trend side of SMA(200), ADX≥20 | H1/H4 | 2×ATR / 3R |
| rsi-divergence | Pivot-based price/RSI divergence, entry on pivot break | H1/H4 | 1.5×ATR / 2R |
| bb-squeeze | Bollinger contraction resolves in breakout direction | H1/H4 | swing-point / 2.5R |
| super-trend | SuperTrend flip confirmed by ADX≥20 | H1/H4 | swing-point / 2R |
| mtf-trend | H1 RSI pullback resuming H4/EMA(200) trend direction | H1 (+H4 context) | swing-point / 2R |

**Baseline exit management — a correction that matters (F69).** The research docs long claimed the
census baseline was "fixed SL/TP, breakeven off, trailing none." Verified against stored
`EffectiveConfigJson`, that is wrong for 7 of 9 families: every family except `mean-reversion` and
`rsi-divergence` already ran **breakeven @ +1R plus a wide trail** (2.0–2.5×ATR; `mtf-trend` uses
a 10-bar structure trail) as its default. All census numbers stand; interpretations involving "the
baseline had no exit management" were re-written when this was caught
(`docs/iterations/iter-structural-edge/LEDGER.md` S1.1).

**Add-on packs** (DB-seeded, components individually toggleable): `runner-aggressive` =
breakeven + tight ATR trail (1.0×) that *relaxes* to 3.0× while ADX ≥ 25 ("Ride") + 50% partial
take-profit at 1R. Also `breakeven-only`, `scalp-tight`, and S1's factorial packs. Pack semantics:
a run's `PackId` replaces the strategy's own add-ons; `PackId: null` falls back to the strategy
defaults above; `StripAddOns` gives the bare SL/TP config.

Two strategies had **fake logic found and fixed** during hardening: `rsi-divergence` originally
compared current RSI to itself (divergence never tested), and `ema-alignment` was a state
condition (true on every trend bar), not an event. Both were rewritten (real pivots / real
crossover-then-first-pullback) before any scored research. Worth knowing when you judge the bank:
these are infrastructure-exercise strategies, not curated alpha, and we treat them accordingly.

---

## 6. The research platform and its discipline

**Unit of record: the cell** — (strategy, symbol, timeframe, pack, risk profile, window). One cell
= one run = one account (D13; an earlier census that commingled 9 strategies in one account was
voided, F5). Trade-level attribution (`StrategyId + Symbol + EntryTimeframe` on every
`TradeResult`) allows pooled, family-level analysis across cells.

**Universe:** 14 symbols (EURUSD, GBPUSD, USDJPY, USDCHF, AUDUSD, USDCAD, NZDUSD, EURGBP, EURJPY,
GBPJPY, XAUUSD, XAGUSD, BTCUSD, ETHUSD) × {H1, H4}. M15 exists in the tape but is deliberately
excluded from scored research until per-bar spread honesty lands (spread bias is inversely
proportional to target size — `docs/QUANT-ROADMAP.md` §2).

**Windows:** census window 2025-07-04 → 2026-05-05. **EMBARGO-1**: 2026-05-06 → 2026-07-05,
touched exactly once (R4, §7.3). **EMBARGO-2**: everything after 2026-07-05, untouched, accruing
via auto-sync; first touch gated to ≥45 accrued days (~September 2026).

**Scoring** (versioned; formula changes = new version, never in-place):

- **sv1** (census era): composite 0–100 = Expectancy 30% (mean R mapped 0R→0, 0.5R→100) +
  FTMO-survival 25% + MaxDD 15% + monthly consistency 15% + walk-forward OOS ratio 15%.
  *Known flaw, disclosed everywhere it matters:* the sv1 survival component was a placeholder
  drawdown proxy (F63) — it never checked profit target or daily caps. Census scores are
  therefore ranking aids, not calibrated survival estimates.
- **sv2** (current, S0): survival component replaced by the real `ChallengeSimulator` — rolls a
  30-day FTMO-semantics challenge window from *every* trading-day start of the run's actual daily
  equity path; PassRate = passes/windows with **incomplete counted as non-pass** (velocity failure
  is the observed failure mode; a score that forgave it would hide it). Unit/integration tests pin
  the semantics (0/N → 0, N/N → 100, daily-cap breach dominates target-hit). sv1 rows were *not*
  retro-rescored (the census is a dead direction; re-scoring it would spend truth-budget on it).

**Validity floor (D3):** a scored cell needs ≥ 20 trades in-window, market-hours-aware data
quality PASS, completed run, zero engine warnings. Below floor → score = **null with reason**,
never 0 (0 is information; null is "insufficient data").

**Anti-overfitting protocol** (tightened over time; current form = D5 of
`iter-structural-edge/PLAN.md`):

1. Every variant **pre-registered in an append-only ledger before any run** (hypothesis + exact
   configs); the experiment row persists the registration (`Experiments.SpecJson`); sessions are
   capped (≤ 8 variants including controls, down from ≤ 12 in the alpha loop).
2. Survival claims need **all three legs**: sign-consistency across ≥ 75% of the family's cells,
   positive in **both** split halves at family level, and walk-forward OOS ratio ≥ 0.5 on the top
   variant. (Walk-forward: 6 rolling folds, ~35d train / ~15d test, parameters chosen in-sample
   per fold, stitched test-window PnL; OOS ratio = Σ test profit / Σ chosen-params train profit.)
3. **Park, never delete** (`StrategyCellParks` with reasons) — reversible triage.
4. Embargo windows are one-touch; re-tuning against a touched window is a plan violation.
5. Every gate pastes queries/outputs into the ledger — claims are never asserted without artifacts,
   and each session's first duty is QA of the previous session's claims.

**Reproduction tooling** (committed in S0, `tools/research/`): `research persistence --experiment
<id> --split <date>` prints the F64 split-half table for any experiment from the live DB (also
`quant_research.py`, `split_half.py`). The S0 gate reproduced the research-session numbers to $0.

---

## 7. Results to date

### 7.1 R1' census — the baseline (experiment `075d5240`, `evidence/scoreboard-s1p.md`)

252 cells (9 × 14 × 2), defaults, one cell per run, window 2025-07-04 → 2026-05-05.
**74 scored, 178 null-with-reason** (all: below the 20-trade floor — H4 cells especially).
44 of 74 scored cells had positive expectancy. Top of the table:

| # | Cell | Score | Trades | ExpR | MaxDD% |
|---|------|-------|--------|------|--------|
| 1 | trend-breakout/XAUUSD/H4 | 100 | 39 | 0.689 | 0.03 |
| 2 | mean-reversion/GBPUSD/H1 | 96.5 | 32 | 0.519 | 0.28 |
| 3 | mean-reversion/AUDUSD/H1 | 96.1 | 33 | 0.693 | 0.09 |
| 4 | rsi-divergence/AUDUSD/H1 | 92.0 | 47 | 0.642 | 1.94 |
| 5 | mean-reversion/GBPJPY/H1 | 90.1 | 22 | 0.467 | 0.09 |
| 6 | ema-alignment/EURJPY/H1 | 88.6 | 39 | 0.384 | 0.75 |
| 7 | mtf-trend/EURJPY/H1 | 87.6 | 31 | 0.387 | 1.36 |
| 8 | trend-breakout/NZDUSD/H4 | 83.7 | 43 | 0.353 | 0.01 |

(Full top-20 + CSV in `evidence/scoreboard-s1p.md` / `.csv`. Scores are `sv1-partial` — see the
F63 caveat in §6.)

### 7.2 R3 refinement — the one generalizing pattern (`evidence/scoreboard-s2.md`, `-s3.md`)

24 pre-registered variants over two sessions (packs, risk profiles) on top-census cells:

- **`runner-aggressive` raised raw expectancy on every trend-family cell tried — 8/8** (gains
  +54% to +221% in ExpR). In about half the cells the gain came free (DD/consistency held); in
  the other half it traded consistency and/or DD for edge.
- **`scalp-tight` lost everywhere it was tried** (including 0/3 on mean-reversion, one edge
  inversion to negative) — treated as closed.
- **`conservative` risk on mean-reversion/AUDUSD/H1** produced the cleanest single result of the
  program: edge up, DD halved, consistency up, 6/6 walk-forward test windows profitable,
  OOS ratio 1.58.
- **Scale-invariance** twice confirmed: 4× risk left ExpR/consistency unchanged with DD scaling
  ~2× (not 4×).
- The walk-forward cull did real work: two variants with spectacular single-window numbers
  (+221% ExpR; $8.9k cumulative test PnL) were **parked at OOS ratio 0.0** because the fold-wise
  in-sample optimization never found a net-positive parameter set — the exact failure mode the
  gate exists for (`evidence/scoreboard-s3.md`).

### 7.3 R4 embargo dress rehearsal — the headline negative (`evidence/candidate-cards.md`)

The 4 full-year survivors (composite 90–100, walk-forward OOS ratios 1.58–4.32) ran once on the
never-touched 2026-05-06 → 2026-07-05 window, full risk stack on, then 3 rolling 30-day
challenge simulations each over their real daily equity:

| Candidate | Full-yr score | Embargo net (trades) | Challenge windows |
|---|---|---|---|
| trend-breakout/XAUUSD/H4 + runner-aggressive | 100 (OOS 2.15) | +$401 (3) | 0/3 pass, 0 breach |
| mean-reversion/AUDUSD/H1 + conservative | 98.3 (OOS 1.58) | +$109 (4) | 0/3 pass, 0 breach |
| ema-alignment/EURJPY/H1 + runner-aggressive | 97.3 (OOS 4.32) | **−$1,921** (6) | 0/3 pass, 0 breach |
| ema-alignment/EURJPY/H1 + aggressive risk | 90.3 (OOS 3.23) | **−$810** (7) | 0/3 pass, 0 breach |

**0/12 windows reached +10% in 30 days; 0/12 breached any loss cap (worst single day 1.47%).**
Read: a return-*velocity* failure, not a safety failure — with the honest caveat that 3–7 trades
per 60 days is too thin to call the full-year edges fake; it *is* enough to say none is
challenge-ready. The plan forbade re-touching the window, and it was not re-touched.

### 7.4 Post-mortem research on the closed loop (F64–F68, `iter-structural-edge/RESEARCH.md`)

No new runs; mined from the census's 4,461 trades and per-run daily equity.

- **F64 — zero cell-level persistence (the load-bearing negative).** Split the census at
  2025-12-03; select cells positive in H1; measure the same cells in H2: 38/74 positive in H1
  (+$116,518 on selection) → **−$880 in H2**; only **9/38 (24%) stayed positive** — worse than a
  coin flip; trailing performance *anti-selects*. The reverse direction persists at factor 0.54,
  and R4 is the same finding measured a third way. In-sample the same 35-cell portfolio
  aggregates to +12%/30d — the mirage the test dispels. Selecting cells on trailing performance
  selects noise; the planned portfolio-of-cells direction was killed by its own Phase-0 gate
  before any machinery was built.
- **F65 — the exit layer truncates the right tail.** 71% of all exits are stop-outs; winners
  capture a mean 42% of max favorable excursion; 12–20% of trend/divergence trades that reached
  +1R died at ≤0. Independently corroborates R3's 8/8 pack effect from the trade-path side —
  two measurement lines pointing at one structural lever (with F69's correction: the effect is
  {tighter-then-relaxing trail + partial TP} vs {fixed wide trail}, both with BE).
- **F66 — costs eat 20.9% of gross** on positive cells ($166.6k gross → $131.8k net), with
  **swap ≈ commission in magnitude** (multi-day holds; `rsi-divergence` median 87 h).
- **F67 — entry noise floor:** 20–37% of entries never move +0.3R in favor (worst:
  session-breakout 37%). Entry filters existed but were mostly off in the census.
- **F68 — the bank is ~zero-mean with structure:** pooled expectancy per family across ALL
  census trades: mean-reversion **+0.10R** (best; 5.2 h median hold), rsi-divergence +0.08,
  macd-momentum +0.05, trend-breakout +0.04, super-trend −0.00, session-breakout −0.02,
  bb-squeeze −0.04, ema-alignment −0.05, **mtf-trend −0.22** (park candidate). Bank average
  ≈ +0.02R — a noise engine with a slight positive tilt, which is *why* per-cell selection
  (n = 20–90 trades/yr) can never work; the unit of analysis must pool to rule × family
  (n in the hundreds to thousands).

---

## 8. What has actually worked — instruments, timeframes, periods

This is the direct answer to "which timeframes, periods, and instruments have been successful,"
with the required honesty about what "successful" means at each level of evidence.

**Strength-of-evidence ladder used below:** (i) census-scored full-year — weakest (in-sample,
sv1-partial); (ii) walk-forward survived; (iii) embargo-tested — strongest, and at that level
**nothing has succeeded yet** in the challenge-velocity sense.

### Instruments

- **AUDUSD is the standout symbol:** 6/6 scored census cells positive-expectancy (only symbol
  with a perfect record), home of the program's best-evidenced single result
  (mean-reversion/AUDUSD/H1 + conservative — 6/6 walk-forward test windows, OOS 1.58, and one of
  only two candidates still positive on the embargo window).
- **XAUUSD (gold):** 3/3 scored cells positive; trend-breakout/XAUUSD/H4 was the census #1
  (score 100, ExpR 0.689) and stayed (modestly) positive on the embargo. Caveat: F48 (~1.4%
  tape-vs-venue PnL conversion residual) attaches to gold specifically.
- **EURJPY and NZDUSD:** 5/6 and 5/7 scored cells positive. EURJPY/H1 hosted the two strongest
  full-year trend candidates (ema-alignment, mtf-trend) — **both of which went net-negative on
  the embargo window**; treat EURJPY's full-year strength with suspicion.
- **Metals/crypto H4 breakouts:** XAGUSD (4/6 positive), ETHUSD (2/3), plus XAUUSD — the
  trend-breakout family's positive cells cluster on higher-volatility non-major instruments at H4.
- **Weak or mixed:** EURGBP (3/9 positive — most-scored, mostly flat), GBPJPY (1/4), USDCAD (2/5),
  BTCUSD (2/5), USDCHF (2/4), and the USD majors EURUSD/GBPUSD/USDJPY roughly break even
  (3/6, 3/6, 3/4).

### Timeframes

- Only **H1 and H4 have ever been scored.** H1 produces ~3.6× more scored cells (58 vs 16 —
  H4 usually fails the 20-trade floor on a 10-month window); positive-cell rates are similar
  (55% H1, 75% H4 among scored).
- The pattern is **family × TF, not TF alone:** mean-reversion works at H1 (short holds, 5.2 h
  median); trend-breakout's winners are H4 (and its `runner-aggressive` lift was strongest
  there). mtf-trend is negative everywhere it scored.
- Shorter TFs (M15) are deliberately untested — the constant-spread model overstates
  tight-target economics, and the roadmap's rule is "no shorter-TF hunts justified by frequency
  alone" (D6): the velocity problem is to be solved by aggregation, not by moving down the
  noise/cost curve.

### Periods (this is the part we most want your judgment on)

- **Jul–Nov 2025 (census H1) was broadly favorable for this bank:** 38/74 cells positive.
- **Dec 2025–May 2026 (census H2) was thin:** only 13/74 cells positive; the H1-selected
  portfolio returned −0.17%/30d. Whether that is a genuine regime shift (the bank is
  trend-tilted; ranging winter?) or ordinary noise around a zero-mean bank is **an open
  hypothesis, pre-registered for testing** (S2 of the current plan: condition pooled family
  expectancy on the existing regime detector's labels, on recorded trades first).
- **May–Jul 2026 (embargo):** flat-to-negative for all four candidates, 3–7 trades each.
- Honest summary: **we have exactly one year of scored history, and the bank's performance is
  strongly period-dependent within it.** Any "instrument X works" claim above is conditional on
  a period that F64 shows does not extrapolate at cell level. This is precisely why the current
  program pools across cells and defends with embargo windows rather than picking winners.

---

## 9. The current program — iter-structural-edge (open)

Full plan: `docs/iterations/iter-structural-edge/PLAN.md`. Core reframe (D1): **the unit of
search is a structural rule × strategy family, pooled across cells; cells are instances, never
the ranked unit.** Highest-prior target first (D2): the exit layer, where two independent
evidence lines converge (F65 + R3's 8/8).

Stages: S0 truth infra (**done** — sv2 scoring live, research tools committed, G0 gate passed
with exact reproduction of F64 from the live DB) → S1 exit factorial (**pre-registered, ready to
run**) → S2 entry noise floor + regime gating → S3 cost-aware knobs (swap-aware hold caps,
expectancy floors) → S4 re-census under winners (sv2) → S5 EMBARGO-2 first touch (≥45 accrued
days, ~Sep 2026) → S6 portfolio phase, **conditional** on a rule-level edge surviving S5 →
S7 audit.

S1's design (from the ledger, S1.1): 8 pre-registered arms on all 12 census-scoreable
trend-breakout cells — {control = strategy's own BE+2.5×ATR trail, bare (StripAddOns), BE-only,
trail-only (1.0×ATR), trail+Ride, partial-only, full runner-aggressive, no-TP pure trail} —
96 runs, family-level evaluation only (pooled ExpR delta, per-cell sign counts, split-half both
halves, MFE-capture/giveback deltas must move, walk-forward on the best arm). The `bare` arm
exists because of F69 — it measures what the research doc *believed* the baseline was.

**Stop rule, stated up front:** if S1–S3 produce no structural effect satisfying all three D5
legs and S4's pooled expectancy doesn't beat baseline, **the 9-family bank is declared exhausted
at rule level too**, and the next conversation is sourcing different strategy material — not more
search on this bank.

---

## 10. Known limitations and open issues (disclosed, not hidden)

Execution/model:
- **F48:** XAUUSD tape-vs-venue net PnL ~1.37% residual (pip cross-rate timing). Open; deferred.
- **F47:** venue prices commission at one reference spot per run; we deliberately don't match it.
- Constant 1-pip spread per run (no per-bar recorded spread yet); no slippage model beyond the
  limit-entry contract; M1-tick-quantized fills are the venue's own semantics, but real live
  fills will differ from CLI-backtest fills in ways not yet measured.
- **F25:** venue symbol specs merge in-memory but the `VenueSymbolSpecs` DB table is never
  written — spec truth is process-lifetime only, refreshed on each cTrader connect.
- **F26:** the pre-trade gate's worst-case commission estimate doesn't dispatch on
  `CommissionType` (order-of-magnitude wrong for per-million symbols; sizing-side only).
- **F28:** `SwapCalculationType` captured but not dispatched on (flat nights×rate×lots always).
- `SymbolInfoRegistry` is a process singleton — a venue spec captured by one run can affect the
  next run in the same process (bit us once, F24; flagged for per-run scoping).

Research/statistical:
- One year of usable history, one market draw; absolute PnL levels in the census are contaminated
  by the bank's own development history (strategies were debugged on overlapping data). The
  *persistence* and *pooled-delta* measurements are designed to be robust to that; levels are not.
- sv1 census scores carry a placeholder survival component (F63) — ranking aid only.
- Walk-forward OOS ratios > 1 on all three R3 finalists (test outperformed train) is unusual and
  was noted but not root-caused; the arithmetic is unit-test-pinned.
- Trade counts everywhere are small by institutional standards: per-cell n = 20–90/yr; family
  pools n = 337–731; the whole census is 4,461 trades.
- No formal multiple-testing correction beyond pre-registration + variant caps + the three-leg
  survival rule; no bootstrap/Monte Carlo on trade sequences is currently wired into gates
  (a `PassProbabilityEstimator` exists but isn't part of scoring).

Process/tooling (context for any recommendation you make about verification):
- cTrader Desktop CLI can crash after backtest completion (report-generation bug in the installed
  build), silently dropping engine-side data — the cBot's independent ledger is the guard.
- Credential-free gates can all be green while a live-cTrader-only bug ships (F24 shipped a
  100%-rejection bug that only a live compare-both caught). Live parity smoke before "done" is
  now doctrine for venue-path changes.

---

## 11. Where we specifically want your guidance

1. **Methodology audit.** Is the three-leg survival rule (≥75% cell sign consistency + both
   split halves positive + WF OOS ≥ 0.5) a reasonable defense at these sample sizes? Would you
   add formal tools — e.g., stationary block bootstrap on pooled trade sequences, deflated
   Sharpe / SPA-style reality checks across the variant count, purged/embargoed CV instead of
   our single split-half — and which are worth the complexity at n≈500–3,000 trades per family?
2. **The unit-of-analysis move.** Do you agree cell selection was doomed a priori given +0.02R
   bank mean and per-cell n, and that pooling to rule × family is the right response? Is there a
   better hierarchy (e.g., partial pooling / hierarchical shrinkage across cells) that uses the
   cell structure instead of discarding it?
3. **The exit-layer hypothesis.** F65 (42% MFE capture, 71% stop-outs) + R3 (8/8) is our
   strongest internal signal. Does the S1 factorial isolate it correctly? What would convince
   *you* that an exit-rule effect is real rather than a volatility-regime artifact — and does
   the pre-registered requirement that MFE-capture/giveback move alongside ExpR suffice?
4. **Regime question (S2).** Given 38/74 → 13/74 positive cells across the two half-years:
   what's the cleanest test you'd run on recorded trades to separate "trend-tilted bank in a
   ranging half" from noise, and would you condition live deployment on a regime classifier at
   this data scale at all?
5. **The velocity problem.** FTMO demands +10% in ~30 days under a 5% daily cap. Our safe edges
   produce single-digit trades per month. Aggregation of thin, low-correlation edges is our
   declared answer (portfolio phase, gated). Is that sound at this scale, or is the honest answer
   that this bank's trade frequency × expectancy arithmetic can never clear the bar and the
   search should move (to more symbols? session/time-of-day families? different signal classes)?
   How would you size a portfolio of ~0.1R-expectancy streams against a 5% daily cap with
   clustered tails (worst joint day −4.3% at 1×)?
6. **Strategy material.** If the stop rule fires and this bank is exhausted: for a solo operator
   with this infrastructure (fast honest tape, prop-firm risk stack, one year of M1-derived data,
   no tick data, no order book), what class of edges would you hunt next, and what data would you
   buy first? The roadmap's prior is session/time-of-day structure over new indicators
   (`docs/QUANT-ROADMAP.md` §5) — confirm or redirect.
7. **Data sufficiency.** One year × 14 symbols at H1/H4. How much history would you require
   before believing any of the above at all, and does extending history backward (different
   spread/cost regime, pre-2025 markets) help or pollute?
8. **Process.** Anything you would change about pre-registration granularity, gate design,
   embargo cadence (we can only "mint" ~45–60 fresh embargo days per quarter), or the
   scoring composite itself (weights are hand-set; survival is now the only calibrated component)?

---

## Appendix A — how to verify any claim in this document

- **F64 table from the live DB:** `research persistence --experiment 075D5240 --split 2025-12-03`
  (ResearchCli verb; also `GET /api/experiments/persistence`). Python equivalents in
  `tools/research/` (README there).
- **"No lies" invariant** (completed runs' trade counts match their trade rows):
  `SELECT COUNT(*) FROM BacktestRuns WHERE Status='completed' AND TotalTrades != (SELECT COUNT(*)
  FROM TradeResults t WHERE t.RunId = BacktestRuns.RunId);` → must be 0.
- **Embargo-2 untouched:** no `BacktestRuns` row with `BacktestFrom >= '2026-07-06'` may exist
  before the S5 ledger entry does.
- **Pre-registrations:** `Experiments.SpecJson` + the append-only ledgers
  (`docs/iterations/iter-alpha-loop/LEDGER.md`, `docs/iterations/iter-structural-edge/LEDGER.md`).
- **Test gates:** build + Unit 767 / Integration 153 / Sim-fast 144 (all green at S0 close);
  determinism + purity gates under `tests/`.
- Every RunId cited in the evidence files resolves via `GET /api/runs/{id}` (trades, equity,
  journal, effective config).

## Appendix B — reading list (in order, all in-repo)

1. `docs/iterations/iter-structural-edge/RESEARCH.md` — F64–F68, the quantitative core (15 min)
2. `evidence/candidate-cards.md` — the R4 embargo result in full (10 min)
3. `docs/iterations/iter-structural-edge/PLAN.md` — current program + decisions D1–D8 (10 min)
4. `docs/iterations/iter-structural-edge/LEDGER.md` — S0/S1.1 state incl. F69 (5 min)
5. `evidence/scoreboard-s1p.md`, `-s3.md`, `-s2-wf.md` — census + refinement detail (10 min)
6. `docs/iterations/iter-alpha-loop/PLAN.md` + `HANDOVER.md` — the closed loop, parity program,
   decisions D1–D14 (15 min)
7. `docs/reference/RESTING-ORDER-CONTRACT.md` — measured fill semantics (5 min)
8. `docs/QUANT-ROADMAP.md` — the 2026-07-02 methodology roadmap this program grew from (10 min)
9. `docs/reference/INVESTIGATION-METHOD.md` — how venue claims get verified here (5 min)

Glossary of recurring notation: **cell** = strategy×symbol×TF(×pack×risk) instance; **pack** =
add-on exit-management bundle; **expR** = mean R-multiple per trade; **F*n*** = numbered finding
in the audit trail; **D*n*** = numbered locked decision; **sv1/sv2** = scoring versions;
**R1'/R3/R4/R5, S0–S7** = stage names in the alpha-loop and structural-edge plans respectively.
