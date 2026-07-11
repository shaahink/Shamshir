# P3 evidence — exit + spread parity

**Date:** 2026-07-11 (same session as P0/P1 QA + P2, iter-alpha-loop)
**Method:** code read of gap-through/spread-convention logic (both already largely correct), one real
gap found and fixed live, re-verified via compare-both.

---

## 0. Verdict

**P3(a) (gap-through fills) and P3(c) (exit-side spread direction) were already correct — verified by
code read, no fix needed. P3(b) (one spread number for both venues) had the same class of gap P1/P2
kept finding: tape silently ignored the run's configured `spreadPips` and always fell back to the
static `symbols.json` value, while cTrader always honoured `--spread` (fed from the same config
field) — found and fixed (F32), unit-tested, and live-verified.** Gate battery green throughout:
build 0err/5warn · Unit 728/0/6 (+3 from P2 baseline) · Integration 121/0/0 · Sim-fast 144/0/0.

---

## 1. P3(a) — Gap-through fills: already correct

`TapeReplayAdapter.ProcessSlTpHits` (`Infrastructure/Adapters/TapeReplayAdapter.cs:596-641`): when a
bar's `Open` already lies beyond the stop-loss level (price gapped through before the bar started),
the fill price is the bar's `Open`, not the named stop — correct real-market modelling (a stop-loss is
a market order once triggered; it fills at whatever price is available after a gap). Take-profit is
deliberately NOT given the same treatment — a TP behaves like a limit order and fills at the named
price even on a favourable gap (you don't get bonus profit from a gap on a take-profit). This
asymmetry is correct and matches how real brokers behave; confirmed by code read, `BacktestReplayAdapter`
has the equivalent logic. cTrader's own gap-through behaviour is native (its own backtest engine, not
our code) and was not independently live-verified against a synthetic gap this session — flagged as an
assumption in `RESTING-ORDER-CONTRACT.md`'s spirit, same caveat as the touch-rule assumption there.

## 2. P3(c) — Exit-side spread direction: already correct

`ProcessSlTpHits` shifts a SHORT position's check bar to the ask side (`SpreadConvention.AskBar`)
before detecting SL/TP hits, and applies the same ask adjustment to the final fill price — correct,
since a short closes by BUYING (at ask, the costlier side). A LONG position's exit uses the raw
(bid) bar and price, unadjusted — correct, since a long closes by SELLING (at bid). Internally
consistent with the entry-side convention already verified in P2 (`TapeReplaySpreadConventionTests.cs`,
8 existing passing cases). No fix needed.

## 3. P3(b) — One spread number for both venues (F32, found + fixed + live-verified)

**Before this fix:** `TapeReplayAdapter.GetSpread()` only ever consulted a per-bar recorded spread
(rare — only present for live-tick-captured data) or fell back to the symbol's static
`symbols.json` `TypicalSpread` (e.g. 0.3 price units / 30 pips for XAUUSD). Meanwhile
`BacktestOrchestrator` already unconditionally passes the run's configured `SpreadPips`
(`StartRunRequest.SpreadPips`, default 1) to cTrader via `--spread={cfg.SpreadPips}` — a
pre-existing, always-on wire (same pattern P1 already established for `commissionPerMillion`). So a
compare-both run explicitly asking for a shared spread (e.g. `"spreadPips": 1` in the config) got
1 pip on the cTrader leg and 30 pips on the tape leg — a 30× cost-model mismatch neither leg's own
gate battery could ever catch (credential-free tests don't exercise cTrader; live cTrader runs never
compared tape's spread against the config value directly).

**Fix:** added `spreadPipsOverride` constructor parameter to `TapeReplayAdapter` (same pattern as
`commissionPerMillion`), wired unconditionally from `cfg.SpreadPips` in `BacktestOrchestrator.cs`
(mirroring the pre-existing commission wiring exactly). `GetSpread()` now prefers the override (converted
to price units via `pips × PipSize`) over the per-bar recorded value and the static registry fallback.
3 new unit tests in `TapeReplaySpreadOverrideTests.cs`: no-override baseline unchanged, override wins
over registry, and an explicit `0` override is honoured (not treated as "unset").

**Side effect, deliberate (documented per the P1(f) precedent — "that is correct, not a regression"):**
every tape run (not just compare-both/parity ones) now uses the run's configured `SpreadPips`
(default 1) instead of the symbol's static realistic spread, exactly matching what cTrader has
always done. For XAUUSD this means the *default* tape spread cost shrinks from 30 pips to 1 pip
unless a caller explicitly configures a wider one. This is the direct, intended consequence of "one
number, fed to both venues identically" — flagging it clearly since it changes backtest realism for
every tape-only research run going forward, not just parity-guard sessions.

### Live verification

Compare-both, XAUUSD H4 trend-breakout, 2025-08-01→2025-10-01, limit entries (post-D11), same config
used throughout P1/P2 (`spreadPips: 1`):

| Run | RunId | Trades | GrossPnL | Commission |
|---|---|---|---|---|
| tape (pre-F32) | `26664e81` | 12 | 2902.84 | -133.80 |
| cTrader (pre-F32) | `438b5977` | 12 | 2129.57 | -45.36 |
| tape (post-F32) | `da7b3427` | 13 | 2957.53 | -134.40 |
| cTrader (post-F32) | `7c2be39b` | 12 | 2129.57 | -45.36 |

Trade count shifted 12→13 on tape after the fix — expected and correct: a tighter, cTrader-matching
1-pip spread (down from the static 30-pip default) means limit-order touch conditions
(`AskPrice(bar.Low, spread) <= LimitPrice`) are easier to satisfy, so tape now fills orders it
previously missed due to an artificially wide spread. This is the fix working as intended, not a new
divergence.

**Residual gap, not addressed this session:** commission still differs materially (-134.40 vs -45.36,
≈3×) even with the spread number now unified. Attributed to the same already-documented F23
entry-latency effect (the two venues resolve the same strategy signal on different bars, producing
different position sizes since lot sizing depends on the realized SL distance) rather than a new P3
defect — consistent with the residual gaps already noted in `evidence/p2-limit-entry-parity.md` §2.
A clean commission-model comparison needs either F29's fix (bar-aware trade matching) or P4's
`research parity` verb; not pursued further given session time.

---

## 4. Gate battery

build 0err/5warn · Unit 728/0/6 · Integration 121/0/0 · Sim-fast 144/0/0 — all re-verified green after
the F32 fix (one Sim-fast run showed a flaky, order-dependent failure in an unrelated test,
`VenueSizingParityTests.CtraderHello_SurfacesDemoBalance_ThatBacktestMustNotAdopt`, confirmed to pass
both in isolation and on a clean full-suite re-run — not a P3 regression).
