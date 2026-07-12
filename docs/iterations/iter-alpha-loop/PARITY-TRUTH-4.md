# Parity Truth 4 — how a venue actually fills an order, and what it charges to hold it

**Date:** 2026-07-12
**Branch:** `iter/alpha-loop`
**Supersedes:** `PARITY-TRUTH-3.md §4.2` (the "fill at the bar close" stop model — wrong, see §1),
and the fill-price rule in `RESTING-ORDER-CONTRACT.md §3` (asserted, never measured).
**Method:** `docs/reference/INVESTIGATION-METHOD.md`.

---

## 0. Verdict

P0–P3 fixed *which* orders each venue placed. They never checked what the venue did with an order once
it was resting, or what it charged to hold the position. Both were wrong, and together they were the
entire remaining parity gap.

| # | Defect | Effect |
|---|---|---|
| **F43** | Both replay venues filled every resting order **at the order's own price** | Long stops ~5 pips optimistic; limits ~0.2 pips pessimistic; short stops a full spread pessimistic (the spread was also **counted twice**) |
| **F44** | Venue-declared symbol specs were captured in memory and **never persisted** | The tape — which never meets a cBot — priced commission and swap off `symbols.json`, which is fabricated |
| **F45** | Swap: rate read as **money** (it is **pips**), then **negated** (it is already signed), and **weekends charged** (they are not) | The tape **CREDITED 1.37** on trades the venue **CHARGED 41.26** for |

**Result: the EURUSD parity gate went from FAIL to PASS with the tolerance budget untouched.** Entry,
exit, stop, lots and both timestamps are now byte-identical to cTrader on every trade.

---

## 1. F43 — a resting order does not fill at its own price

### The claim that was wrong

`PARITY-TRUTH-3 §4.2` diagnosed the exit gap as the tape "filling stops optimistically" and fixed it by
filling at **the bar's close** whenever the bar closed past the stop. That is not a fill mechanic. A stop
triggers the instant price touches it and fills near it; the bar's close happens later, when the position
is already gone. The model would fill a bar that dips through your stop and then trends for four more
hours at *the far end of the move* — inflating every loss on exactly the trend days that matter. It
reduced one number on one trade and corrupted the tape.

It also broke R1: it **inferred** what cTrader did instead of **asking** it.

### The measurement

Take the trade that fix was built around. cTrader closed a long at **1.16283** against a stop at
**1.163295** — 4.65 pips through. Pull the M1 bars cTrader replayed, and the first one to reach the stop:

```
2026-06-01 13:12   O=1.16356  H=1.16358  L=1.16283  C=1.16291
                                          ^^^^^^^ cTrader's fill, exactly
```

The fill is the bar's **low**, to the tick. Not the stop, not the close.

**Why:** cTrader's backtest replays each M1 bar as **four synthetic ticks — O, H, L, C**. There is no
tick at 1.163295. Walking the bar: O (above the stop) → H (above) → **L (breach)** → fill *at that tick*.
The venue never sees the stop price, so it fills at the first tick that breaches it.

### The rule, and its consequences

> **A resting order — limit entry, stop entry, stop-loss or take-profit — triggers on the first O/H/L/C
> tick to breach its level, and fills AT THAT TICK. When the level sits strictly between the bar's open
> and the extreme that reaches it, the fill lands ON THE EXTREME.**

Read the bar on the order's own side of the book first (bid for anything executing at the bid, ask for
anything executing at the ask). Then a **stop fills through** its level, and a **limit fills better** than
its level. Both follow from the same rule; both were previously modelled as "fill at the level".

The old "gap-through" special case is just the branch where the *open* is the first breaching tick.

### Verified on six real fills — two symbols, both directions, all three order types

| # | run | what | level | first breaching tick | cTrader filled |
|---|---|---|---|---|---|
| 1 | d64d9488 | EURUSD short SL | 1.16254 (ask) | ask high 1.16245+1pip | **1.16255** ✓ |
| 2 | d64d9488 | EURUSD long SL | 1.163295 (bid) | bid low | **1.16283** ✓ |
| 3 | d64d9488 | EURUSD short SL | 1.16350 (ask) | ask high 1.16340+1pip | **1.16350** ✓ |
| 4 | d64d9488 | EURUSD sell-limit **entry** | 1.15973 (bid) | bid high | **1.15975** ✓ (2 ticks *better*) |
| 5 | 81729685 | XAUUSD short TP | 3973.665 (ask) | ask low 3964.79+0.01 | **3964.80** ✓ |
| 6 | 81729685 | XAUUSD short TP | 3966.303 (ask) | ask low 3938.46+0.01 | **3938.47** ✓ |

Six for six, to the tick. Note #3: the breaching tick happens to land *on* the stop — which is why any
single trade could have "confirmed" the old model. One symbol, one trade, is not a measurement.

The spread that reproduces all of these is exactly **1.0 pip** — and the venue's own `symbol_spec` then
declared `spread=0.0001`. (`PARITY-TRUTH-3` had recorded "cTrader uses ~0.15-pip actual spread". Also an
inference, also wrong.)

### The double-spread

`ProcessSlTpHits` shifted the bar to the **ask side** to *detect* a short's exit — correctly, since a
short's stop is an ask-side level — and then added the spread to the **fill price** as well. The stop was
already an ask level; the spread was charged twice. Every short stop exited a full pip worse than the
venue's. `SpreadConvention`'s own header warns against exactly this drift.

**Fix:** `VenueFillModel.FirstBreachingTick`, one implementation shared by `TapeReplayAdapter` and
`BacktestReplayAdapter`, applied to all four resting paths (limit entry, stop entry, SL, TP).

---

## 2. F44 — the venue's economics were learned and then thrown away

The cBot has emitted `symbol_spec` since P1, and `SymbolInfoRegistry.UpsertVenueSpec` merges it. But the
merge is **in-memory**, and only the **cTrader** leg has a cBot. Nothing wrote `VenueSymbolSpecEntity`;
the M51 table sat **empty**. So:

- the venue's real commission and swap died with the process that learned them, and
- the **tape leg priced every trade off `symbols.json`** — which is fabricated. It has a EURUSD long
  *earning* 0.5/lot/night. The broker *charges* the equivalent of 7.04.

**Fix:** `IVenueSymbolSpecStore` / `SqliteVenueSymbolSpecStore` — persist on capture, load into the
registry at startup. Both legs are now priced from one source, and that source is the venue (D10). Same
lesson as F32 (spread) and F34 (currency), for the third time.

What the venue actually declares for EURUSD:

```
commType=UsdPerMillionUsdVolume  comm=45
swapLong=-2.445  swapShort=-0.105  SwapCalculationType=Pips  tripleSwapDay=Wednesday
spread=0.0001
```

---

## 3. F45 — three independent bugs in one line of swap

`var swap = -(nights * swapRate * lots);`

Every term in it was wrong.

1. **Units.** The rate is in **PIPS** — the venue says so itself (`SwapCalculationType=Pips`). It was
   consumed as *money per lot per night* and never multiplied by the pip value: an ~8.6× understatement
   on EURUSD. This is the F39 commission-units bug again, in a different cost.
2. **Sign.** The rate is **already signed as a P&L adjustment** (negative = the trader pays). Negating it
   turns the broker's charge into a **credit paid to the trader**. Both of this venue's EURUSD rates are
   negative — it charges on *both* sides — so the tape earned swap whichever way it traded.
3. **Weekends.** Saturday and Sunday rollovers were charged. No broker finances a position over a shut
   market — which is the entire reason Wednesday is billed triple. A Friday→Monday hold billed **3 nights
   instead of 1**.

**The correct model:**

```
swap = nights × ratePips × lots × pipValueInAccountCurrency     // no negation
nights: rollovers crossed, Sat/Sun excluded, Wednesday ×3
```

Checked against the venue's own charges:

| trade | held | nights | model | cTrader charged |
|---|---|---|---|---|
| Long 1.7 lots | Fri 05-29 → Mon 06-01 | **1** (not 3) | -35.6 EUR | **-35.90** ✓ |
| Short 1.97 lots | Wed 06-03 → Thu 06-04 | **3** (Wed triple) | -5.32 EUR | **-5.36** ✓ |
| Short 2.06 lots | intraday 05-28 | 0 | 0 | **0.00** ✓ |

---

## 4. The gate

`research parity --tape <id> --ctrader <id>`, pre-registered tolerance budget **untouched**.

| | before (PARITY-TRUTH-3) | after |
|---|---|---|
| TradeCount | PASS 3:3 | PASS 3:3 |
| **EntryPrice** | **FAIL — worst 2.0 ticks** | **PASS — worst 0.0** |
| Lots | PASS | PASS |
| **ExitPrice** | **FAIL — 0% within, worst 46.5 ticks** | **PASS — 100% within, worst 0.0** |
| Commission | PASS | PASS 0.53% |
| **Swap** | (masked) **FAIL — 103%**, tape *credited* 1.37 vs venue -41.26 | **PASS** |
| **NetPnL** | **FAIL — 3.17%** | **PASS** |

Every trade now matches cTrader on entry, exit, stop, lots and both timestamps **exactly**.

---

## 4b. XAUUSD — what a second symbol found (and why one was never enough)

EURUSD alone would have shipped two more bugs. Re-running the gate on XAUUSD (14 trades, a 2-month
window, price moving 3369 → 4039):

```
  [PASS] TradeCount   tape=14 ctrader=14
  [PASS] EntryPrice   worst 0.0 ticks
  [PASS] Lots         worst delta 0.00
  [PASS] ExitPrice    100% within, worst 0.0 ticks
  [PASS] Swap         1.37%
  [FAIL] Commission   9.88%   <-- and it is NOT our bug; see below
```

The fill model (F43) and the swap model (F45) both generalise to a completely different price scale
untouched. Two commission findings did not.

### F46 — the closing side was billed at the ENTRY price (ours; fixed)

`Compute` charged `perSide × 2`, with `perSide` computed once, at the entry price. Commission scales with
notional, and notional scales with price. On EURUSD price moves ~0.5% across a trade, so entry ≈ exit
notional and the error hid at 0.53% — inside the budget. On gold it is worth several percent. Each side is
now billed on its own notional at its own price.

### F47 — cTrader prices backtest commission at ONE reference spot, not per trade (theirs; OPEN)

After F46, XAUUSD commission still failed at 9.88%. The venue's own numbers say why:

| lots | entry price | cTrader commission |
|---|---|---|
| 0.27 | 3368.91 | **-5.58** |
| 0.27 | 3381.19 | **-5.58** |
| 0.18 | 3577.47 | **-3.72** |
| 0.18 | 3609.64 | **-3.72** |

Same lots at different prices → **identical commission**. Across all 14 trades cTrader charged a constant
**-20.67 EUR per lot round-turn**, unchanged while gold moved 18%. cTrader's backtest commission is
**price-independent**: it is computed once, against a single reference price, not against each trade's own
notional.

Working backwards: 10.33 EUR/side ÷ 0.8652 (the USD→EUR rate implied by the venue's own `TickValue`) =
$11.94/lot/side; at the run's 30-per-million that is a notional of **~$3,981/oz** — a single price, sitting
at the top of the run's range. EURUSD hides this completely, because its spot and its backtest prices are
the same number to within a percent.

**We should NOT match this.** Pricing a backtest's commission at today's spot makes the result depend on
*when you ran it* — the same run would report different costs tomorrow. Our per-trade notional model is the
correct one; cTrader's is the artifact. Unlike the M1 tick synthesis (§5), which is a faithful consequence
of the data resolution and worth reproducing, this one is simply wrong.

**Owner decision required** (P4 is otherwise green): accept a documented commission divergence on
price-mobile symbols, or drop `--commission` entirely and reconcile commission from the venue's reported
per-trade values. The discriminating experiment, if we want certainty about the reference price: run the
same cell over a window with a very different closing price and see whether the implied $/oz moves with the
window (last bar) or stays put (live spot).

---

## 5. What this does NOT fix — read before trusting a backtest number

The tape now reproduces **cTrader's M1 backtest**, artifacts included. cTrader's M1 tick synthesis is not
live execution:

- a **stop** fills at the M1 extreme, which is *worse* than a real stop fill (real slippage is small);
- a **limit/TP** fills at the M1 extreme, which is *better* than a real limit fill (a real limit fills AT
  the limit — you do not get a free 27-point improvement, as XAUUSD trade #6 above did).

Both are consequences of there being only four ticks per bar; with finer data they converge to the level.
So the two legs now agree **because they share the same model**, which is exactly what a parity gate is
for — it proves our engine models the venue. It does **not** prove the venue models reality.

**The limit/TP side of this flatters results, and the alpha loop (X0) will inherit that bias.** Closing it
needs tick-resolution data on both legs, and it is a separate decision — a real one, with a real cost,
and it should be made deliberately rather than discovered later. Flagged as the top realism item for X0.

---

## 6. Method note — why five sessions missed this

Every one of these defects was guarded by a **green test that asserted the assumption instead of the
venue**:

- `RestingOrderContractTests` asserted *"a limit fills at exactly the named price, never a better one"* —
  the very rule that was false. It passed, because the code under test was written from the same belief.
- `TradeCostCalculatorTests` asserted a negative swap rate produces a **credit**.
- `RulePressureTests.WeekendHolding_CrossesThreeRollovers` asserted 3 nights. The trade *does* cross three
  rollovers; the broker charges one. The test's name was a true statement about the calendar and a false
  statement about money.

A test written from the same reasoning as the code cannot falsify that reasoning. It only records it.
**The fill rule and the swap model are now pinned against real venue output** (`VenueFillModelTests`,
`VenueSwapModelTests`) — six recorded fills and three recorded swap charges. If someone "simplifies" them
back, those tests fail loudly and name the run that proves it.

And the residual that P2 waved through — entry prices "close but not bit-identical … attributed to
latency, not a defect in the fill mechanism" — *was* the fill mechanism. A small delta is still a delta.
"Small enough to be latency" is a guess. The way to settle it was to look at the M1 bar the venue traded.
