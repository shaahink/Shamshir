# iter-pass-economics — honest costs, a windowed objective, a broader box

**Status: RATIFIED — GE0 passed 2026-07-27 (owner "proceed"; D1–D8 adopted unedited).** Successor program drafted after the GV4
clean stop, from `docs/AUDIT-POST-VIABILITY-2026-07.md` (the four-pass audit) + owner direction
(2026-07-27). Nothing here relitigates GV4: the parks stand *at the cost model that ran*. This
program changes the three things the audit shows were actually binding — the cost model (never
the pre-registered one), the objective (5-year pooled stationarity, not challenge economics),
and the instrument set (one box) — and it pre-commits to not searching where the arithmetic
already forbids a win.

**The reframe in one line:** stop demanding one frozen system be positive over five pooled years
at fictional costs; measure **challenge-pipeline economics** (windowed P(pass) × funded EV −
fees) at **true venue costs**, on a universe where gross signal can actually clear the toll —
and admit adaptivity only with walk-forward proof, never ML.

**Read order for a fresh session:** this file → `docs/AUDIT-POST-VIABILITY-2026-07.md` →
`docs/reference/RESEARCH-PROCESS.md` → `docs/iterations/iter-viability/LEDGER.md` tail (Session 8).

Audit menu mapping: V-COST→E0/E2 · V-CTRL(objective half)→E1 · V-EXIT→E5 · V-IDX→E4 ·
V-CTRL(control half)→E6 · V-XS→Track X (optional) · V-EVT→deferred (data acquisition, not merit).

---

## 0. Lineage (unchanged inheritances)

| Inherited | Status here |
|---|---|
| Findings ledger F1–F86 (next free: **F87** = the spread-override finding/fix) | Continues, same numbering |
| EMBARGO-2 (post-2026-07-05, one touch) + 2025+ terminal holdout | Sacred, untouched |
| 2024 era-holdout, still clean | **ONE program-wide shot**, at GE5 only (D6 below) |
| Pre-registration + MDE line + ≤8 variants/session; position-level pooled dollars; block bootstrap | Unchanged (RESEARCH-PROCESS §3) |
| Park-never-delete; D13 one-cell-one-run; null-with-reason | Unchanged |
| Two-lane worktree concurrency (iter-viability D9) | Unchanged; Lane-D slices below are agent-deliverable |
| L-track (iter-viability §7) | Stands; **L0 live compare-both smoke is still the standing debt** at next cTrader session |
| Ops debts | **Off-machine backup of trading.db + Dukascopy archive FIRST** (power loss already happened once); doc corrections from audit §2–§3 |

## 1. What the last program actually established (the license for this one)

1. **The −$20/pos was toll, not signal.** Mid-to-mid signal ≈ **+0.4 pips in both censuses**
   (V2 +$4.31, V4 +$6.62/pos) vs a 2.3–2.5-pip-equivalent cost stack. Correct epitaph: a
   0.4-pip gross edge cannot pay that toll at intraday frequency — not "no edge exists".
2. **The pre-registered cost model never ran.** Every census run charged a flat 1-pip spread
   (`StartRunRequest.SpreadPips = 1` → `ReplayVenueRunner` → top-priority override in
   `TapeReplayAdapter.GetSpread()`); per-bar Dukascopy spread was never read on tape. FX majors
   were over-taxed ~2×, metals/crypto under-taxed 60–500×, commission flat $30/M vs venue
   $45/$25/$0. Three spread sources are live at once (fills / floating-PnL / strategy tick).
3. **The pass assessment itself can kill a good strategy — in three distinct, now-precise ways**
   (the owner's 2026-07-27 hunch, confirmed by the record):
   - **Stationarity demand:** the verdict instrument was pooled 2019–2023 expectancy CI — it
     requires an all-weather edge. FTMO is a ~30-day windowed, repeatable game; a time-varying
     edge can have positive challenge economics while failing the pooled test.
   - **Composite measurement:** every census run had the governor (cooling-off) and regime gate
     ON (audit §4) — the censuses measured *strategy + control layer*, not the signal. F78/F79/
     F82 are prior art for the control layer mechanically suppressing results.
   - **DD approximation + triage composites:** sv2's daily-close sim is optimistic on intraday
     breaches (iter-viability D2 rationale); composites are triage-only but shaped attention.
4. **The arithmetic any search must respect** (audit §6): with g = gross pips/position and
   c = true toll, net edge needs **g ≫ c**. Only three moves exist: raise g (horizon,
   instruments, information), shrink c's share (fewer/larger trades, carry as revenue), or
   change the objective (challenge economics). This program does all three and nothing else.

## 2. Decisions (owner ratifies/edits at GE0)

| # | Decision | Rationale |
|---|---|---|
| D1 | **Primary objective becomes challenge-pipeline EV**: MC over ~30-day windows drawn (block-bootstrap) from a candidate's trade stream → P(pass P1) × P(pass P2) × E[funded income] − fees, under the verified FTMO rule set and a stated retry policy. Reported **next to** pooled $ CI, never instead of it. **Mandatory baseline: the zero-edge floor** — the same MC on a zero-edge synthetic with matched vol/frequency. A candidate must beat the zero-edge EV floor with CI clearance. **Owner directive (2026-07-27): passing is necessary, not sufficient — the candidate must be a winning system in its own right: positive net expectancy at true costs on its deployment-conditional trade stream (CI excluding zero), so E[funded income] rests on real edge, not variance.** | The stationarity critique (§1.3) is real, but windowed evaluation + retry economics makes pure variance look good (the prop-firm lottery). The zero-edge floor is the anti-lottery guard: "passes sometimes" is meaningless unless it beats what a coin flip earns net of fees. |
| D2 | **Signal measurement and pass simulation are separated.** Research runs = research mode: `maxDdEnabled` off (F82 precedent) **and governor cooling-off + regime gate OFF** — the run measures the signal. The control layer exists only inside the challenge MC (E6) and the live path. The two are never blended in one number. | The censuses measured a composite (§1.3). A good signal gated off by cooling-off is invisible forever. Separation makes each layer independently attributable — and the control layer becomes a *tunable policy* evaluated by MC, not a hidden tax on research. Owner note (2026-07-27): the governor and DD scaler date to the engine's initial concept phase, pre-evidence — treating them as re-optimizable policy (E6) rather than fixed truth is confirmed direction. |
| D3 | **True-cost mandate (F87):** after E0, no scored run may use the flat spread override. `SpreadPips` nullable end-to-end; null ⇒ per-bar recorded spread; venue-true commission by instrument class; the three spread sources unified; gross/spread/commission/swap become standing columns in every harvest. **Arithmetic gate:** a family/instrument advances only if IS gross signal exceeds its true toll — we do not research what arithmetic forbids. | The pre-registered cost model never ran (§1.2). The arithmetic gate is the owner's "let's not try what we theoretically know won't work", made mechanical. |
| D4 | **Adaptation doctrine, amended once:** F64's tombstone ("trailing performance anti-selects, 24%") is demoted to **contaminated evidence** — it was measured under the F78/F79-broken engine, where cells fell permanently silent after their first loss streak, which mechanically destroys any persistence measurement. Cell-level and sub-6-month trailing selection **stay forbidden**. What opens: **family-level, ≥6-month-window, at-most-quarterly re-weighting may be tested** as a pre-registered walk-forward hypothesis with a frozen-weights control (default expectation: refit does NOT beat frozen), and **only after H-WINDOW shows cross-window persistence exists** (E3). **No ML anywhere** (owner directive). | The owner's "time-tuned auto system / has been working recently-ish" deserves an honest test, not a doctrine veto resting on bugged data — but the test is sequenced so the selector is only built if persistence is measurable first. RESEARCH-PROCESS §7 tiers otherwise unchanged. |
| D5 | **Universe governance:** instruments enter only with venue-true specs (cTrader capture per F44) and per-bar spread data. Indices (US500 + shortlist) enter at E4, born at true costs. Metals/crypto stay OUT until someone re-opens them at honest costs (they were understated 60–500×). Sub-M15 excluded everywhere. | The owner's "US500 and other" + "reviewing the stuff we experiment on". Session structure was tested on the instrument class with the weakest session prior; indices have the strongest documented session anomalies and the strategies are already built. |
| D6 | **Holdout discipline:** the 2024 era-holdout is spent **once, program-wide**, at GE5, on the full frozen candidate set — not per-phase. EMBARGO-2 untouched until a 2024 survivor exists. | Multiple phases each "earning a shot" quietly multiplies looks at the holdout. One shot, one frozen set, one verdict. |
| D7 | **Pre-registered program stop rule:** if at GE5 no candidate (FX survivors, indices, exits-enhanced, or Track-X prototype) beats the zero-edge floor at true costs on IS + walk-forward, the program stops cleanly. The remaining honest directions are V-EVT (event/calendar — blocked on data acquisition, not merit) or none. No family-swapping, no window-shopping. | GV4's stop rule worked because it was written before the result existed. Same here. |
| D8 | **Lane split:** Lane R (research) = E1–E3 analyses, all scored runs, ledger. Lane D (agent-deliverable slices) = F87 engine work (E0), indices importer + specs (E4), MC-harness extensions (E1/E6). Per D9 protocol, merge at gates. | Deliver-as-agent-plans; the code slices are cleanly separable from truth work. |

## 3. Phase map

```
E0 ops + cost truth (F87)        (1)    backup, doc fixes, nullable SpreadPips,
                                        venue commissions, harvest columns        [GE1]
E1 objective truth               (1)    pass-assessment audit; challenge-MC EV
                                        defined; zero-edge floor computed         [GE2, OWNER]
E2 re-verdict at true costs      (1–2)  offline cost-swap on V2/V4 ledgers →
                                        targeted true-cost re-runs; per-symbol
                                        tables; universe pick                     [GE2, OWNER]
E3 windowed edge + adaptivity    (1–2)  H-WINDOW persistence → H-ADAPT only if
                                        persistence exists                        [GE3]
E4 indices: US500 + shortlist    (2)    data+specs, then session census at
                                        true costs                               [GE4, OWNER]
E5 exits on survivors (paired)   (1)    exit lab (already built) on gross-
                                        positive families only                    [folds into GE3/GE4]
E6 control layer + ladder        (1–2)  challenge-state policy MC, portfolio
                                        superposition, ONE 2024 shot → embargo
                                        → demo (L2)                               [GE5, OWNER]
X  cross-sectional prototype     (1)    OPTIONAL parallel: offline D1-bar
                                        carry/momentum (audit V-XS Phase 0)       [kill-test built in]
```

Sessions are ceilings. E0 blocks everything scored; E1 can run in parallel with E0 (offline).
E4's data work (Lane D) can start any time after GE1; its census waits for GE2's universe call.
Track X touches no engine code and can run whenever a session is free.

## 4. Stages

### E0 — Ops + cost truth (F87) [gate GE1]
Order matters: **(a) off-machine backup** of trading.db + the Dukascopy archive (owner-assisted;
the power loss already happened once). (b) Doc corrections owed by the audit: `SYSTEM-REFERENCE.md`
§1 ("N rows one account" is false — sequential passes, fresh $100k each) and the LEDGER's
per-bar-spread claim. (c) **F87** (Lane D): `SpreadPips` nullable end-to-end, null ⇒ per-bar
recorded spread (the null path already prefers it); venue-true commission by class ($45/M FX,
$25/M metals, $0 crypto — from `VenueSymbolSpecs`); unify the three spread sources (fills /
floating-PnL / strategy tick); gross/spread/commission/swap as standing harvest columns.
**Gate GE1:** pinned test flips (the old "override must win" test becomes "null ⇒ recorded
per-bar spread"); one probe cell demonstrably charges recorded spread; `Net = Gross + Comm + Swap`
invariant still exact.

### E1 — Objective truth [OWNER GATE GE2, jointly with E2]
The owner's question — "does the way we assess passing kill good strategies?" — answered as
three deliverables, offline, zero scored runs:
1. **Assessment inventory:** exactly what the program's verdict instrument was (pooled 5-yr $
   CI, governor+regime on, daily-close DD, sv2 composite) and which of §1.3's three kill
   mechanisms each phase of history was exposed to. Quantify where journal data allows: how many
   entries did cooling-off/regime gates block in the V2/V4 ledgers?
2. **Challenge-MC objective (D1):** implement pipeline-EV on top of the existing
   `ChallengeSimulator`/`PassProbabilityEstimator` (V0-verified FTMO semantics): windowed MC
   over block-bootstrap-resampled trade streams → P(pass P1/P2), P(bust), E[time], funded EV,
   fees, retry policy. GV0 finally resolves here: owner picks account type; author `ftmo-1step`
   ruleset if 1-step is wanted (3% MDL — none of the existing rulesets model it).
3. **Zero-edge floor:** the same MC on zero-edge synthetics at 2–3 vol/frequency profiles. This
   number is pasted into every candidate report from now on.
**Gate GE2 (joint with E2):** owner ratifies the objective definition + the floor, signs the
account type.

### E2 — Re-verdict at true costs (H-COST) [OWNER GATE GE2]
Two steps, cheapest first:
1. **Offline cost-swap** on the existing V2/V4 ledgers (221k positions): replace the charged
   flat 1-pip spread with the recorded per-bar spread at each trade's bars, commission with the
   venue-true rate. Approximation caveat stated (fill paths differ slightly at different
   spread). Produces the tables the program never had: per-family, **per-symbol**, per-TF, per-era
   at true costs.
2. **Targeted true-cost re-runs** (engine, D2 research mode, D3 costs) only for families whose
   offline CI crosses or approaches zero — the decomposition points at **session-breakout**
   (signal +16.5 $/pos, never individually refuted) and **ema-alignment** (+14.6), H1/H4 on the
   tightest-spread majors. Pre-registered with MDE lines; ≤8 arms.
**Kill-test (pre-registered):** every family CI < 0 at true costs ⇒ the single-symbol intraday
FX box is closed **permanently** — no future program re-opens it at any cost model.
**Gate GE2:** true-cost tables pasted; owner picks the surviving universe that E3/E5 work on.

### E3 — Windowed edge + adaptivity (H-WINDOW → H-ADAPT) [gate GE3]
Runs offline on E2's ledgers. Sequenced so the selector is only built if its premise measures true:
1. **H-WINDOW:** per surviving family — rolling ~6-month-window net $ and challenge-MC P(pass)
   across 2019–2023: is the edge time-varying with identifiable "on" periods? Decisive statistic:
   **cross-window persistence** (does a good window predict the next one — lag-1 sign persistence
   / rank autocorrelation at family level, with CI). This is the owner's "has been working
   recently-ish" intuition as a falsifiable measurement.
2. **H-ADAPT (conditional — runs only if H-WINDOW persistence CI excludes zero):** pre-registered
   walk-forward test of family-level, ≥6-month-window, quarterly re-weighting (EB-shrunk,
   turnover-capped, Tier-3) vs the frozen-weights control. ≤8 variants including control. No
   cell-level selection, no monthly rebalance, no ML (D4).
**Kill-tests:** H-WINDOW persistence ≈ 0 ⇒ recency selection is dead *on evidence* — H-ADAPT is
never built (this is "not trying what we know won't work", applied to our own idea). H-ADAPT ≤
frozen control on stitched walk-forward ⇒ adaptivity closed; frozen survivors continue alone.

### E4 — Indices: US500 + shortlist (H-IDX) [OWNER GATE GE4]
The audit's point: the session/ORB machinery V4 built points at the wrong instrument class —
overnight/opening-range effects on equity indices are the best-documented session anomalies in
the literature, and the four session strategies already exist (zero strategy-code lift).
1. **Data + specs (Lane D, can start after GE1):** Dukascopy `.IDX` tickers + scale probe;
   one cTrader venue-spec capture per index (F44 discipline); real `symbols.json` entries;
   session-gap-aware reconcile. Verify FTMO's current index product list (US500/US30/NAS100/
   GER40 assumed — confirm before pre-reg).
2. **Census (after GE2):** pre-registered — 4 session strategies + session-breakout ×
   shortlisted indices × H1 (M15 only where per-bar spread exists), 2019–2023 IS, D2 research
   mode, D3 true costs (index spreads are wide — the flat 1-pip override would have faked this
   class, which is why E4 sequences after E0). MDE line at pooled-family level.
**Kill-test:** pooled family CI < 0 at true costs ⇒ indices closed for session strategies.
**Gate GE4:** verdict tables pasted; owner folds survivors into the E6 candidate set.

### E5 — Exits on survivors (paired lab) [folds into GE3/GE4]
Only for families gross-positive at true costs (E2/E4 output). The tooling is **already built**
(`ExitReplayer`, `ExitGridEvaluator`, excursion recorder via `CustomParams["RecordExcursions"]`,
`TradeExcursions`); the *experiment* never ran. MFE capture was 0.42 (F65) — the paired question
"can exits raise $/position enough to clear the true toll?" has ~20× the power of the dead
whole-system factorials. Exploration-mode recordings, IS only, pre-registered grid.
**Kill-test:** no exit rule beats the frozen exit's pooled $ CI ⇒ exits closed as a lever.

### E6 — Control layer + candidate ladder [OWNER GATE GE5]
Built when a candidate exists (any of E2/E3/E4/E5/X survivors); edge-independent parts may start
earlier in a free Lane-D slot.
1. **Control layer as policy** (the V6 that never ran): intraday equity envelope (honest
   daily-DD breach), portfolio intraday stop, challenge-state risk policy (risk/trade as
   f(distance-to-target, DD headroom, phase)) — MC-optimized offline via the E1 harness,
   deterministic online. Reported as policy-vs-constant-risk P(pass)/P(bust)/E[time] tables.
2. **Combination ("mix and mash"):** offline portfolio superposition of survivor equity curves
   (`quant_research.py` §C / `split_half.py`) → joint-tail sizing (bootstrap 99th-pct daily loss
   × 1.5 < daily cap) → challenge-MC on the combined book. No engine multi-symbol work needed
   for a research verdict; the medium-lift shared-account venue is built only if a combined
   candidate goes live.
3. **The ladder:** freeze the candidate set → **the ONE 2024 era-holdout shot (D6)** → survivors
   → EMBARGO-2 (per its own rules) → L2 demo forward-run (calendar time, unfakeable).
**Gate GE5:** candidate cards with pooled $ CI + challenge-EV vs zero-edge floor + 2024 verdict;
owner go/no-go. **D7 stop rule applies here.**

### Track X — Cross-sectional FX carry/momentum prototype (OPTIONAL, parallel)
The audit's flagship new signal class (V-XS), kept as an optional single session because it is
the strongest-literature-prior direction and costs zero engine work: offline Python prototype on
D1 bars — weekly/monthly rank of the pair universe by carry (venue swap rates are already signed
pips/night in `VenueSymbolSpecs`) + momentum, long-top/short-bottom basket, honest cost stack,
stitched walk-forward. Swap flips from cost to revenue; toll amortizes over weekly holds; breadth
gives power natively. **Kill-test:** stitched walk-forward ≤ 0 after costs ⇒ stop before any
engine work. Survivor joins the E6 ladder like everyone else.

## 5. Exclusions (pre-committed — "what we already know won't work")

- More single-symbol intraday FX indicator families at any cost model short of E2's re-verdict
  (two structurally different families = same toll-dominated number).
- **ML in any form** (owner directive; also: same toll, worse overfit surface).
- Sub-M15 anything (toll share only grows).
- Cell-level or sub-6-month trailing-performance selection (D4 keeps the F64 fence even while
  re-testing its evidence at family level).
- Metals/crypto until re-opened at honest costs (D5).
- Touching EMBARGO-2, re-touching spent windows, widening any tolerance because a result
  disappoints.
- Variance-lottery candidates: anything whose challenge-EV beats zero but not the zero-edge
  floor (D1) — that's FTMO's fee model working as designed, not an edge.

## 6. Traceability — owner's asks (2026-07-27) → where they land

| Owner ask | Where it lands |
|---|---|
| "D1 to D6?" | Audit menu mapped in the header; V-COST→E0/E2, V-CTRL→E1+E6, V-IDX→E4, V-EXIT→E5, V-XS→Track X, V-EVT deferred |
| "Not try what we theoretically know won't work" | §5 exclusions + D3 arithmetic gate (g > true c required to advance) + H-ADAPT conditional on H-WINDOW persistence |
| "Check how we assess FTMO passing — maybe it kills good strategies" | E1 (three kill mechanisms named in §1.3; assessment inventory; new windowed objective) + D2 (signal measurement separated from pass simulation) |
| "Some combination" | E6.2 portfolio superposition + joint-tail sizing + Tier-3 weights (E3 H-ADAPT if persistence exists) |
| "Time-tuned auto system, no ML" | E3 H-WINDOW → H-ADAPT (family-level, ≥6-month, quarterly, walk-forward-proven vs frozen control); ML excluded in §5; F64 evidence demoted as contaminated (D4) |
| "System that's been working recently-ish — prev program demanded passing on ALL our data" | §1.3 stationarity critique; D1 windowed challenge-pipeline EV objective; E3 measures whether "recently working" is even informative before acting on it |
| "US500 and other" | E4 (D5 universe governance; born at true costs) |
| "Reviewing the stuff we experiment on; mix and mash" | E2 per-symbol/per-family true-cost tables (never existed before) → GE2 universe pick; Track X as the new-information option |

## 7. Session protocol + verification

Inherited verbatim (RESEARCH-PROCESS §5): QA prior session's claims against artifacts first →
pre-register (with MDE) → execute → append LEDGER.md → paste gate outputs, never assert → fast
suites green → RESUME block updated. Owner 5-minute verification matrix as iter-viability §5,
plus: zero-edge floor present in every candidate report; no scored run after GE1 with a
non-null `SpreadPips`; era-holdout/embargo guard queries still 0 until GE5's ledger entry exists.
