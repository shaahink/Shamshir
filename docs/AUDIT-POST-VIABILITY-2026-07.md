# Post-Viability Deep Audit — what the −$20 actually was, and where search can honestly go next

**Written:** 2026-07-27 (Claude, at owner request after the GV4 program stop)
**Inputs:** four independent audit passes — (A) harvest/LEDGER evidence extraction, (B) read-only
trade-ledger decomposition of experiments `4F56B1AE` (V2) and `5D06CE0B` (V4), (C) searched-space
coverage map across alpha-loop → structural-edge → viability, (D) engine/data capability inventory.
All DB access was read-only (`mode=ro`). No verdict, park, or holdout was touched.

**Stance:** this audit does **not** relitigate the GV4 stop. The stop rule fired exactly as
pre-registered and the parks are correct *at the cost model that ran*. The audit asks two questions:
what does the evidence actually establish, and what search remains honest under
`docs/reference/RESEARCH-PROCESS.md`. F85's own scope line is the license: the negative
"does **not** refute edge in structurally different strategy classes never tested here"
(`LEDGER.md:1204-1206`).

---

## 1. Headline: the −$20/position is the toll, not the signal

The program never decomposed net PnL into signal vs cost — both harvest scripts load
`GrossPnLAmount` and discard it (`tools/research/v2_harvest.py:178,252,262`;
`v4_harvest.py:171,245,255`). Doing the decomposition directly on the trade ledger
(identity `Net = Gross + Commission + Swap`, verified exact on all 221k rows; spread recovered
from the fill convention, verified empirically — longs exit at bar+0.000 pips, shorts at
bar+1.000, IQR ±0.1):

| $/position | V2 `4F56B1AE` (n=101,572) | V4 `5D06CE0B` (n=119,670) |
|---|---|---|
| **Net (the verdict)** | **−20.06** | **−20.01** |
| Spread cost (as charged, embedded in fills) | −10.66 | −16.32 |
| Commission | −6.74 | −10.27 |
| Swap | −6.97 | −0.03 |
| **Mid-to-mid signal (all costs stripped)** | **+4.31 (+0.40 pips)** | **+6.62 (+0.41 pips)** |
| Win rate (net) / avg hold | 39.1% / 22.5 h | 41.2% / 2.3 h |

Two structurally different families, independently, show the same picture: **≈ +0.4 pips of gross
signal vs ≈ 2.3–2.5 pips-equivalent of cost.** ScoreJson totals reconcile to the ledger to the cent.

Caveats, stated plainly:
- The +0.4 pips is small. No bootstrap CI was run on it (naive SE ≈ $1.5–2/pos ⇒ ~2–3 SE from
  zero); the safe claim is "signal ≈ zero-to-slightly-positive, definitely not anti-predictive."
- This is an accounting decomposition of executed trades, not a zero-cost counterfactual re-sim
  (at different spread the SL/TP touch paths differ slightly).
- **It does not resurrect the parked strategies.** +0.4 pips does not clear any realistic cost
  floor. What it changes is the *epitaph* — from "these signal classes have no edge" to
  "**a ~0.4-pip gross edge cannot pay a 2.3–2.5-pip toll at intraday frequency**" — and therefore
  what a rational next program looks like.

Second-order facts from the same decomposition:

- **Per-family signal (spread added back):** session-breakout **+16.5**, ema-alignment **+14.6**,
  mtf-trend +6.4, trend-breakout +4.3, mean-reversion +2.4, rsi-divergence +0.9,
  macd-momentum +0.7; only bb-squeeze (−3.1) and super-trend (−6.5) are genuinely
  signal-negative. All four V4 session strategies are +5.6 to +8.4.
- **rsi-divergence is killed by swap, not signal:** −$27.98/pos swap over 131 h median holds.
- **day-of-week's −$40.4 is a sizing artifact:** it trades ~4× the lots (comm −18.5, spread
  −29.7); its signal is in line with the family.
- **"M15 worse than H1" (V4's H-TF answer) is a pure cost artifact:** gross signal is identical
  (M15 +6.56 vs H1 +6.71); the entire gap is the toll (M15 pays 18.02 spread + 11.29 comm vs
  H1's 13.72 + 8.71). The LEDGER's "finer execution captures nothing" is wrong in mechanism —
  it captures the same and pays more.
- **Hold-duration gradient (net/pos):** V2: <4h −112.6 → 4–24h +11.5 → 1–3d +58.5 → >3d +144.9.
  Survivorship-dominated (SL exits resolve fast — 85,344 of V4's 119,670 exits are SL), so not a
  tradable lever by itself, but directionally consistent with the toll-share story.

## 2. The pre-registered cost model never actually ran

The V2/V4 pre-registration states the engine charges "raw recorded per-bar Dukascopy spread —
this is what the engine already does … `SpreadPips=1` (inert on tape)" (`LEDGER.md:548,563-567`).
**That is false.** `StartRunRequest.SpreadPips` is a non-nullable `double = 1`
(`src/TradingEngine.Web/Dtos/Runs/StartRunRequest.cs:9`) and `ReplayVenueRunner.cs:227` passes it
unconditionally as `spreadPipsOverride`, which is the top-priority branch in
`TapeReplayAdapter.GetSpread()` (`:432-442`). **Every census run charged a flat 1-pip spread; the
per-bar Dukascopy `Spread` column was never read on any tape run.** Confirmed empirically from
fill prices (above) and pinned, ironically, by `TapeReplaySpreadOverrideTests` ("the run's
spreadPips override must win").

Materiality, by instrument class:
- **FX majors: conservative.** Dukascopy median EURUSD spread ≈ 0.4 pips; the censuses charged 1.0.
  V4 (FX-only) was, if anything, over-taxed — its direction of verdict is safe.
- **JPY crosses: roughly fair** (recorded medians near 1 pip). V4's aggregate recorded-spread cost
  (from the stress table's linear back-out, $16.5/pos) ≈ what was charged ($16.3) — a wash.
- **Metals/crypto: wildly optimistic, 60–500×.** XAUUSD charged $0.01 vs $0.63 recorded median;
  BTCUSD $0.10 vs ~$50. The LEDGER's dismissal of the V2 crypto positives (ema-alignment +39.7,
  session-breakout +41.5) was therefore **right in substance but wrong in mechanism** — it cited
  the F77 `TypicalSpread` fallback, which the same LEDGER correctly notes was almost never reached
  (`:563-567`); the actual mechanism is the always-on 1-pip override. The two *metal* positives
  (mean-reversion +17.2, trend-breakout +5.8 at n=3,688) were never addressed at all — same
  understatement applies.
- **Commission** ran at the $30/M override everywhere vs venue-captured $45/M FX (undercharged),
  $25/M metals and $0 crypto (overcharged).
- Three spread sources are live simultaneously in the engine: fills use the override; floating
  PnL/drawdown uses `symbols.json` `TypicalSpread` directly (`TapeReplayAdapter.ComputeFloatingPnL:772`);
  the strategy-facing tick uses half of `TypicalSpread` (`BarEvaluator.cs:283-291`).

**Doc corrections owed:** `SYSTEM-REFERENCE.md` §1 ("a run executes N rows on one account" — false,
see §5) and the LEDGER per-bar-spread claim. Proposed **F87**: unify the spread sources, make
`SpreadPips` nullable end-to-end (small lift — the null path already prefers per-bar recorded
spread), and make gross/spread/commission/swap standing columns in every future harvest.

## 3. Reporting cracks (change the record, not the stop)

1. **No decomposition anywhere** (§1) — the largest evidentiary gap in the program.
2. **"Every era negative for every strategy — including 2022-trend" (`LEDGER.md:1367`) is
   point-estimates-only.** By the program's own CI rule, 6 of 20 V4 era×strategy cells are "not
   detectable," including 2022-trend for both highest-n strategies (asia-range −4.9 [−11,+2],
   london-orb −4.3 [−10,+2]).
3. **session-breakout was never individually refuted:** −6.4 CI [−13, +0], harvest verdict "hold";
   it parked with the bank at the owner ruling. Its ex-spread signal is the strongest in the corpus.
4. **"Nothing cost-fragile" is tautological** — the escalation flag can only fire on a verdict
   within ±1 MDE of *flipping*; all-negative verdicts can't flip under deepening stress.
5. **No per-symbol table exists in either harvest**; no H1-vs-H4 comparison exists for V2 (the
   `.tf` field is loaded and never grouped).
6. Cosmetic but telling: `v4-harvest.md` carries V2/GV2 copy-paste remnants (lines 63, 118); the
   harvest's stop-rule restatement widened the trigger to include NOT-DETECTABLE vs the
   pre-registered CI ≤ 0 (moot — the verdict was strictly refuted).

## 4. What was searched — and the axes that were never varied

Three programs, ~470 scored census cells, two exit factorials, all inside **one box**:
single-instrument, bar-close technical signals on own-symbol OHLC, 14 FX/metal/crypto CFDs,
M15–H4, fixed-fractional 0.5% sizing, one strategy per account, governor+regime always on.

Never varied (verified in code, not inferred): no exogenous data ever entered a signal (no
calendar, no rates/carry, no positioning/COT, no cross-asset); no cross-sectional or
relative-value structure; no portfolio run ever scored; no D1+ or sub-M15 horizon
(median holds 5 h–5 d); exit space was one small fixed grid (the paired exit lab experiment
never ran); sizing variants (fixed-lot, vol-target) never used. One correction to the folklore:
**"always market orders" is false** — all 13 strategy configs use `LimitOffset` resting entries;
`StopConfirm` exists and was never tested.

V4's own pre-registered menu contained the unbuilt alternatives — (b) cross-sectional FX
momentum/carry, (c) index CFDs, (d) weekend-gap family, (e) F67 filter analysis
(`iter-viability/PLAN.md:118-130`) — collapsed at GV2 to the single session/TOD shot. The
2026-07 quant review's "new material" list (Q6) had four entries; only #1 was ever tested.

**Holdouts still clean:** 2024 era-holdout (guard query = 0, importer refuses ≥2025) and
EMBARGO-2 (post-2026-07-05). Both remain available for exactly one honest shot each.

## 5. Capability constraints that price the directions

| Capability | Status | Lift |
|---|---|---|
| Per-bar recorded spread on tape runs | dead — always-on 1-pip override (§2) | **small** |
| Spread-multiplier knob for in-engine cost stress | absent (LEDGER wished for it) | **small** |
| Fixed-lot / fixed-$ sizing profiles | implemented, needs a JSON profile each | **small** |
| D1 runs | data present (1.8–2.4k bars/symbol), parser supports | **none** |
| W1 | **silently falls back to H1** (`RunRequestParser:58`) — trap | small |
| Exit lab (excursion recorder + `ExitReplayer` + grid + API) | **already built** (M36/M37, P3.x/P4.5.3); Partial-TP deliberately unsupported; the *experiment* never ran | **~none** |
| Multi-symbol, one shared account (cross-sectional/portfolio) | **not supported** — N rows execute as sequential passes, each with a fresh $100k (`ReplayVenueRunner:175,250`); `SYSTEM-REFERENCE.md` §1 claims otherwise and is wrong | **medium** (tape-only); large with cTrader parity |
| Strategy sees peer symbols | `MarketContext` has no symbol dimension | **medium** (additive field) |
| Strategy sees account state | excluded by design (purity) | medium |
| Vol-targeted sizing in kernel | modifiers exist only on legacy oracle path | medium |
| Index CFDs (US30/NAS100/SPX500/GER40) | no bars, no venue specs; Dukascopy pipeline works; deferred per F44 discipline, not blocked by code | **medium** (tickers+scale probe + 1 cTrader spec-capture per index) |
| Economic calendar / news events | **no data exists**; engine seam (`ExternalVerdicts`, gate branch) ready | **medium** (mostly acquisition + point-in-time hygiene) |
| Portfolio equity aggregation | offline superposition only (`quant_research.py` §C, `split_half.py`); no shared-account curve | follows multi-symbol |

## 6. The arithmetic any new program must respect

Let *g* = gross pips captured per position, *c* = toll per position (spread + commission + swap).
The whole searched box measured **g ≈ +0.4 pips against c ≈ 2.3–2.5 pips** (as modeled; on a live
FTMO major-pair book c is plausibly ~0.6–1.2 pips-equivalent [verify per-symbol] — smaller, but
still ≫ 0.4). Net edge requires **g ≫ c**. Only three moves exist:

1. **Raise g** — stronger effects (different information, different markets) or longer horizon
   (D1/weekly captures are 5–20× the toll; M15 captures are 1–3×).
2. **Shrink c's share** — fewer, larger-target trades; carry direction makes swap revenue.
3. **Change the objective** — P(pass challenge) economics, where DD shape and frequency matter as
   much as expectancy (the control layer V6 never built).

A structural self-critique falls out: the program's power doctrine (huge n, $3–5 MDE) selects FOR
high-frequency cells — i.e., it was best-powered exactly in the regime where winning was
arithmetically impossible. Low-frequency/high-g hypotheses need pooling across symbols/markets
(breadth) to reach power, which is what cross-sectional structure provides natively.

## 7. Directions menu

Every direction below is pre-registrable under RESEARCH-PROCESS.md unchanged. None touches
EMBARGO-2. The stop rule bound "this data/market class" — items 2–4 are different classes by the
F85 scope line; item 1 is a *new hypothesis on new evidence* (the §1–§2 findings did not exist at
GV4), with the never-spent 2024 era-holdout as its honest judge.

### D1 — Cost-truth re-verdict (V-COST) · smallest, and a prerequisite for everything
Fix F87 (nullable `SpreadPips` → per-bar recorded spread; venue-true commissions; one spread
source), add gross/spread/comm/swap harvest columns, then re-score V2/V4 in-sample at true costs.
Pre-register: **H-COST — at true venue costs, does any family's IS CI cross zero?** Any that does
earns ONE shot on the clean 2024 era-holdout. Candidates the decomposition points at:
session-breakout (signal +16.5, never individually refuted) and ema-alignment (+14.6), likely at
H1/H4 on the tightest-spread majors. ~1–2 sessions. Honest expectation: majors' true spread saves
~0.5 pips/position — probably not enough alone, which is why D5 pairs with it.
**Kill-test:** CI < 0 at true costs for all families ⇒ the box is dead at any realistic toll; close permanently.

### D2 — Cross-sectional FX carry/momentum (V-XS) · the flagship new signal class
Rank the 10-pair universe (extendable) weekly/monthly by carry (venue swap rates — swap is already
signed pips/night in `VenueSymbolSpecs`) and momentum; hold a long-top/short-bottom basket.
Different information (relative, not absolute), breadth-native power, toll amortized over weekly
holds, and swap becomes the *revenue* line the DB shows is material (±$7/pos). Strong literature
prior. **Phase 0 is an offline Python prototype on D1 bars + the honest cost stack — zero engine
work, ~1 session, decisive.** Engine work (multi-symbol shared-account venue, medium; peer-bars
MarketContext field, medium) only if the prototype clears IS + walk-forward.
**Kill-test:** prototype's stitched walk-forward ≤ 0 after costs ⇒ stop before any engine work.

### D3 — Index CFDs + session structure (V-IDX)
Overnight drift / opening-range effects on equity indices are the best-documented session
anomalies in the literature — the session logic V4 already built points at the wrong instrument
class. FTMO offers indices [verify current product list]. Medium lift: Dukascopy `.IDX` tickers +
scale probe, one cTrader venue-spec capture per index (F44 discipline), real `symbols.json`
entries, session-gap-aware reconcile. Sequence after D1's cost fix so indices are born with an
honest cost model (their spreads are wide; the current 1-pip flat would fake them).

### D4 — Event/calendar family (V-EVT)
Post-news drift/fade, pre-event flattening. Needs historical economic calendar acquisition with
point-in-time hygiene (medium, mostly data); the engine seam is ready (`ExternalVerdicts`,
`NEWS_WINDOW` gate branch, replay-stable verdict freezing). Event-anchored moves are large
multiples of the toll. Third priority on data-acquisition grounds, not on merit.

### D5 — The exit experiment that was never run (V-EXIT) · pairs with D1
The tooling exists (`ExitReplayer`, `ExitGridEvaluator`, excursion recorder via
`CustomParams["RecordExcursions"]`, `TradeExcursions` table). The entries are gross-positive
(§1) and MFE capture was 0.42 (F65) — the paired question "can exits raise pips/position enough
to clear the (corrected) toll?" has ~20× the power of the dead whole-system factorials and was
V3's whole point. Run it on the D1-corrected cost model, exploration-mode recordings, IS only;
survivors join D1's 2024 shot.
**Kill-test:** no exit rule beats the frozen exit's pooled $ CI on IS ⇒ exits are closed as a lever.

### D6 — Control layer + P(pass) objective (V-CTRL) · edge-independent
V6 as specified: intraday equity envelope (honest daily-DD breach), portfolio intraday stop,
challenge-state risk policy MC-optimized via `PassProbabilityEstimator`/`ChallengeSimulator`,
GV0 resolution (author `ftmo-1step` if wanted). Converts *any* small edge into challenge
economics; also the only work that improves the live path with zero new signal research. Build
when a candidate exists, or in parallel — it is independent by construction.

### Non-directions (explicitly)
More single-symbol intraday FX indicator families (genuinely exhausted — two families = same
toll-dominated number); ML on the same own-symbol OHLC box (same toll, worse overfit surface);
sub-M15 anything (toll share only grows); touching EMBARGO-2 or re-tuning on spent windows.

## 8. Recommended sequencing

- **Track A (D1 + D5, one program, ~2–3 sessions):** "Was the box dead at TRUE costs, and do
  exits push the gross-positive families over?" → at most one pre-registered 2024 shot.
  Cheap, decisive either way, and fixes the cost instrument every later direction needs.
- **Track B (D2 Phase-0 prototype, ~1 session, parallel):** offline cross-sectional carry/momentum
  on D1 bars. No engine work until it clears.
- Whichever track produces a candidate → D6 control layer + era-holdout/embargo ladder.
  D3 (indices) is the next data expansion after D1's cost fix; D4 behind it.
- **Ops debts first, regardless of pick:** off-machine backup of trading.db + Dukascopy archive
  (the power loss already happened once); doc corrections from §2–§3; L0 live compare-both smoke
  at next cTrader session.

---

*Evidence pointers: decomposition SQL + scripts in the session scratchpad; harvest gross-discard at
`v2_harvest.py:178,252,262` / `v4_harvest.py:171,245,255`; spread override at
`StartRunRequest.cs:9` + `ReplayVenueRunner.cs:227` + `TapeReplayAdapter.cs:432-442`; sequential-pass
venue at `ReplayVenueRunner.cs:175,206-254`; exit-lab tooling under `src/TradingEngine.Services/ExitLab/`.*
