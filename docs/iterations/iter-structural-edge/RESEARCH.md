# Deep quant research — post-alpha-loop (2026-07-16)

**Session:** owner-requested deep research over the closed alpha-loop artifacts, now that the
machinery is trustworthy end-to-end. **No new backtests were run; no embargoed data (anything
after 2026-07-05) was touched.** Everything below is mined from the live DB
(`src/TradingEngine.Web/data/trading.db`): the R1' census (experiment `075D5240`, 252 one-cell
runs, 74 scored / 178 null), its 4,461 census trades, and per-run daily equity series. Analysis
scripts are reproducible (see §6) and their outputs are pasted, not asserted.

**Headline: the decided portfolio-of-cells direction fails its own Phase-0 gate on recorded
data (F64).** Cell-level performance has ~zero persistence — selecting cells by trailing
performance selects noise. The evidence that *does* survive is structural: the exit layer
truncates winners (F65, corroborating R3's 8/8 `runner-aggressive` pattern from an independent
angle), costs eat a fifth of gross (F66), a quarter-to-a-third of entries never move 0.3R in
the trade's favor (F67), and one strategy family is bank-wide negative (F68). The next
iteration should hunt **structural edges** (exits, entry filters, regime gating, cost-aware
knobs) with cells as *instances*, not the unit of search — see `PLAN.md`.

---

## 1. F64 — Zero cell-level persistence (kills the naive portfolio thesis)

**Method (split-half selection test):** census window 2025-07-04 → 2026-05-05 split at
2025-12-03. Select cells positive in H1 (using per-trade `NetPnLAmount` by `ClosedAtUtc`);
measure the *same* cells in H2. The runs themselves were executed once over the full year with
fixed default configs, so the only fitted step being evaluated is the **selection** — exactly
the step a portfolio-of-cells iteration would perform.

```
cells positive in H1: 38/74  (H1 PnL of selection: $116,518)
same cells in H2:     $-880   -> haircut factor -0.01
persistence: 9/38 H1-positive cells stayed positive in H2 (24%)
H2 return of H1-selected portfolio at 1x: -0.17%/30d
top-8 by H1 PnL -> H2: $-540 = -0.11%/30d (H1 was $58,857)
reverse check: H2-positive cells (13) earned $44,190 in H2, $23,942 in H1 -> factor 0.54
H1-selected portfolio, H2 rolling 30d challenge windows (fresh $100k each):
 k=1x:  4 pass /  5 fail / 82 incomplete   worstDay=-3.00%
 k=2x: 14 pass / 48 fail / 29 incomplete   worstDay=-6.01%
 k=3x: 26 pass / 65 fail /  0 incomplete   worstDay=-9.01%
```

- 24% persistence is *worse than a coin flip* — trailing performance anti-selects at this horizon.
- Scaling a noise portfolio multiplies failure: at 3x the daily-cap/max-loss failures dominate.
- **Independent corroboration:** R4's embargo result (all 4 full-year survivors — which were
  positive in *both* halves — stalled or went negative on truly fresh data) is the same finding
  measured a third way.
- Caveats: H2 (Dec–May) may be a genuinely thinner regime for this bank (only 13/74 cells
  positive in H2 vs 38 in H1) — regime-dependence is a *hypothesis to test* (S2 in PLAN.md),
  not a rescue for cell selection. Absolute PnL levels are contaminated by the bank's
  development history; the persistence measurement is robust to that.

**In-sample portfolio arithmetic (for contrast — this is the mirage F64 dispels):** aggregating
the 35 usable full-year-positive cells (sum of solo $-deltas onto one $100k account) gives
+12.06%/30d at 1x, maxDD 7.53%, 107/276 rolling windows passing. Selecting on the *same* window
you evaluate is exactly the selection step the split-half test shows has zero forward value.
Pairwise daily-delta correlations are ~zero on average (sparse series understate dependence),
**but tails cluster:** the 35-cell pool's worst single day is −4.31% at 1x — near the 5% daily
cap with no scaling. Weekly-bucket correlation confirms: avg −0.001 but max pair +0.684. Any
future portfolio phase must measure joint-tail risk, not Pearson averages.

## 2. F65 — The exit layer truncates the right tail

**Method:** all 4,461 census trades (default configs: fixed SL = 1.5×ATR, TP = 2R, breakeven
OFF, trailing None — packs were `PackId: null` throughout the census).

> **Correction (2026-07-16, F69 — see LEDGER.md S1.1):** the "breakeven OFF, trailing None"
> claim above is wrong for 7 of 9 families. PackId-null runs fall back to each strategy's OWN
> add-ons, and the stored configs (verified against census `EffectiveConfigJson`, run
> `22ca21af`) have BE@1R + a 2–2.5×ATR trail enabled for every family except `mean-reversion`
> and `rsi-divergence`. The numbers in this section stand; the interpretation shifts — they
> describe a baseline that already had exit management, and R3's 8/8 effect is
> {trail 1.0 + Ride relax + PartialTp} vs {fixed 2.5×ATR trail}, both with BE.

```
Exit reasons:  SL n=3161 avgR=-0.67 · TP n=982 avgR=+2.06 · TimeFlatten n=318 avgR=+0.33
MFE capture (trades with MfeR>0.5): n=2865, mean captured R/MFE = 0.42
giveback (MfeR>=1 but net<=0): rsi-divergence 19.5% · mtf-trend 16.9% · trend-breakout 12.4%
                               · super-trend 12.2% · ema-alignment 12.1%
```

- 71% of all exits are stop-outs; winners capture on average **42%** of their maximum favorable
  excursion; 12–20% of trend/divergence trades that reached +1R died at ≤0.
- **This is the same deficiency R3 found from the opposite direction:** `runner-aggressive`
  (breakeven + ATR trail that relaxes while ADX is strong + partial TP — i.e., replace the fixed
  2R cap with "let winners run") improved *every* trend cell tried, 8/8. Two independent
  measurement lines pointing at one structural lever is the strongest alpha evidence in the
  whole program — and it is a **rule-level** effect with pooled n in the hundreds, not a
  cell-level effect with n≈30.

## 3. F66 — Cost drag structure

```
37 positive cells: gross=$166,581  commission=-$17,330  swap=-$17,460  net=$131,791
costs eat 20.9% of gross; swap ≈ commission in magnitude
```

Swap is as large as commission because several families hold multi-day (`rsi-divergence`
median hold 86.8h). Frequency raises commission share (`session-breakout`, 136 trades). Any
velocity push must be cost-aware: swap-aware hold caps / flatten rules and a per-trade
expectancy floor are cheap structural knobs (S3 in PLAN.md).

## 4. F67 — Entry noise floor

20–37% of entries never move even +0.3R in the trade's favor (`session-breakout` worst at
37.2%, most families ~25%). Census defaults ran with entry filters mostly OFF
(`EntryFilter` optional, `SpreadVolNoTradeFilter` opt-in, regime filter default-off). The
entry layer has obvious untested headroom — but it must be tested structurally (filter ON/OFF
across a whole family, split-half evaluated), not per-cell.

## 5. F68 — Family triage (the bank is ~zero-mean with a structure-dependent tilt)

Per-family expectancy across ALL census trades (not just positive cells):

| family | n | wr% | expR | note |
|---|---|---|---|---|
| mean-reversion | 491 | 50.7 | **+0.10** | best; short holds (5.2h median) |
| rsi-divergence | 497 | 34.2 | +0.08 | big winners (avgWinR 2.22), 87h holds, giveback 19.5% |
| macd-momentum | 544 | 44.3 | +0.05 | |
| trend-breakout | 731 | 43.5 | +0.04 | R3's 8/8 pack effect lives here |
| super-trend | 460 | 41.5 | −0.00 | |
| session-breakout | 492 | 47.4 | −0.02 | highest frequency, highest never-ran (37%) |
| bb-squeeze | 487 | 39.8 | −0.04 | |
| ema-alignment | 422 | 41.0 | −0.05 | |
| mtf-trend | 337 | 31.2 | **−0.22** | bank-wide worst — park candidate unless S1 exits rescue it |

The bank averages ≈ +0.02R/trade before structural improvements — a noise engine with a slight
positive tilt. That is *why* cell selection can't work: per-cell n (20–90 trades/yr) can never
distinguish +0.02R from noise. **The unit of analysis must move up** to rule × family, where
pooled n is in the hundreds.

## 6. Engine/system readiness facts (verified in code this session)

- **Run plan is already row-based:** `RunPlanJson = [{StrategyId, Symbol, Timeframe, PackId}]` —
  a portfolio run is just N rows on one account; the engine executes this today (it's what F5's
  commingling was: a *scoring attribution* problem, not an execution one).
- **Per-cell attribution exists at trade level:** `TradeResults.StrategyId + Symbol +
  EntryTimeframe`. Account-level only for `EquitySnapshots` — per-cell scoring inside a
  portfolio run must reconstruct from trades.
- **Kernel PreTradeGate already enforces:** global + per-strategy `MaxConcurrentPositions`,
  opt-in per-group exposure caps (`ExposureGroups`, P5.4), global `MaxExposure`, and a risk
  budget with heat (`PreTradeGate.cs:82–218`).
- **But no portfolio risk profile exists:** `standard` = 0.5%/trade, maxConcurrent **3**,
  exposure 5% — an 8-cell portfolio hits those caps immediately. A dedicated profile +
  per-cell budget partitioning is required machinery for any future portfolio phase.
- **Packs are DB-seeded** (`AddOnPackSeeder`): `runner-aggressive` = BE + AtrMultiple trail +
  Ride (ADX-relaxed) + PartialTp, all `Mode: Auto`. Components are individually toggleable —
  a factorial isolation of which component carries the 8/8 effect is directly expressible.
- **ChallengeSimulator** (R4, 14 tests) exists with API endpoint; `ComputeFtmoSurvival` in
  `SetupScoreService` is still the F63 placeholder (25% of composite).

**Reproduction:** analysis scripts `quant_research.py` (census pool, correlations, portfolio
pools A/B/C, exit quality) and `split_half.py` (F64 test, cost drag, weekly correlations) were
run against the live DB this session; outputs above are verbatim. Scripts to be committed under
`tools/research/` in S0 so every number here is one command away from re-verification.
