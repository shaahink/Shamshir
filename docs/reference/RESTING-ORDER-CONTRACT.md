# Resting-order contract — tape vs cTrader

**Status:** normative. Both venues (`TapeReplayAdapter`, `TradingEngineCBot`/`CTraderBrokerAdapter`)
MUST obey this contract for entry-price parity (PLAN.md P2, D11) to hold. Any change to resting-order
behaviour on either venue must keep this doc and `RestingOrderContractTests` in sync.

**Why this exists:** a market order fills at whatever price the venue happens to be at, so tape's
faster (or slower) evaluation path produces a different fill price than cTrader's — the root of the
old F1/F2 entry-price divergence. Resting orders remove that degree of freedom: both venues fill them
by the same deterministic rule (§3), so entry price is reproducible rather than timing-dependent.

> **P4.3 (F43) — this doc previously claimed the fill price was "the price we NAMED … identical by
> construction". That was wrong, and it was never measured.** A resting order does NOT fill at its own
> price on either venue. It fills at the first of the M1 bar's four synthetic O/H/L/C ticks to breach
> the level. §3 below is now the MEASURED rule, verified tick-exact against six real cTrader fills.
> The old claim is what made the residual entry-price deltas look like unexplained "latency" (§4) for
> five sessions, when they were the fill mechanism itself.

---

## 1. Order types

| Type | Tape | cTrader |
|---|---|---|
| `Limit` | `TapeReplayAdapter._pendingLimits` | native `PlaceLimitOrder` |
| `Stop` (entry) | `TapeReplayAdapter._pendingStops` | native `PlaceStopOrder` |
| `Market` | fills same-bar or next-fine-bar (honest fills) | native `ExecuteMarketOrder` |

## 2. Touch rule (when does a resting order fill)

- **Buy limit:** fills when the **ask** trades down to or through the limit price.
  Tape: `SpreadConvention.AskPrice(bar.Low, spread) <= limit.LimitPrice`
  (`TapeReplayAdapter.cs::ProcessPendingLimits`).
- **Sell limit:** fills when the raw **bid** trades up to or through the limit price (a sell-to-open
  trades at bid — no spread adjustment). Tape: `bar.High >= limit.LimitPrice`.
- **Buy stop:** fills when the **ask** trades up through the stop trigger.
  Tape: `SpreadConvention.AskPrice(bar.High, spread) >= stop.StopPrice`.
- **Sell stop:** fills when the raw **bid** trades down through the trigger. Tape: `bar.Low <= stop.StopPrice`.
- **cTrader:** the touch rule for `PlaceLimitOrder`/`PlaceStopOrder` is native cTrader broker-simulation
  behaviour — we do not reimplement it, we trust it matches the above (standard industry convention:
  "fills when the market reaches the named price"). Not independently verifiable without cTrader's own
  source; treated as an assumption until a live divergence proves otherwise (see §5).

## 3. Fill price rule  (MEASURED — P4.3 / F43)

**A resting order fills at the first O/H/L/C tick to BREACH its level — never at the level itself.**

cTrader's backtest replays each M1 bar as four synthetic ticks: Open, High, Low, Close. There is no tick
at an arbitrary price. So when a level sits strictly between the bar's open and the extreme that reaches
it, the fill lands **on the extreme**. Consequences, both real and both previously mismodelled:

- a **stop** fills *through* the stop (worse than named);
- a **limit** fills *better* than the limit.

Read the bar on the order's own side of the book first (bid for anything executing at the bid, ask for
anything executing at the ask — `SpreadConvention.AskBar`), then:

| Order | Side | Reached by price… | Fill |
|---|---|---|---|
| Buy limit | ask | falling | `askOpen <= limit ? askOpen : askLow` |
| Sell limit | bid | rising | `bidOpen >= limit ? bidOpen : bidHigh` |
| Buy stop (entry) | ask | rising | `askOpen >= stop ? askOpen : askHigh` |
| Sell stop (entry) | bid | falling | `bidOpen <= stop ? bidOpen : bidLow` |
| Long stop-loss | bid | falling | `bidOpen <= sl ? bidOpen : bidLow` |
| Short stop-loss | ask | rising | `askOpen >= sl ? askOpen : askHigh` |
| Long take-profit | bid | rising | `bidOpen >= tp ? bidOpen : bidHigh` |
| Short take-profit | ask | falling | `askOpen <= tp ? askOpen : askLow` |

The `Open` branch is the old "gap-through" case — price was already past the level when the bar began,
so the open is itself the first breaching tick. It is no longer a special case, just the general rule.

**One implementation:** `VenueFillModel.FirstBreachingTick`, shared by `TapeReplayAdapter` and
`BacktestReplayAdapter`. The stop/target levels are already on the exit side of the book, so **no spread
is added to the fill** — the pre-F43 code shifted the bar to the ask side for detection and then added
the spread to the fill price again, counting it twice on every short exit.

**cTrader:** native limit/stop semantics. No longer an assumption — measured (§5).

## 4. Expiry — the failure mode this contract exists to prevent

**`LimitOrderExpiryBars` / `Intent.Entry.LimitOrderExpiryBars` counts DECISION-timeframe bars, on
BOTH venues, always.** This is the load-bearing rule.

- **cTrader:** the cBot's `OnBarClosed` subscribes ONLY to the decision timeframe (whatever `Periods`
  the run configured, e.g. H1/H4) — there is no finer subscription. `ProcessLimitExpiry` (called once
  per `OnBarClosed`) therefore decrements `BarsRemaining` exactly once per decision bar, unconditionally.
- **Tape:** `TapeReplayAdapter` runs **dual-resolution exits by default** (`ExitTimeframe` defaults to
  `M1` — `BacktestOrchestrator.cs:1111` — for SL/TP fidelity within a decision bar). Its
  `OnBarObserved` is called once per decision bar but internally loops over every FINER (M1) bar inside
  that window to detect SL/TP touches at intrabar resolution.
- **F30 (found + fixed 2026-07-11):** before this fix, `ProcessPendingLimits`/`ProcessPendingStops`
  were called with `decrementExpiry: true` on **every fine bar** inside that loop — so a 3-bar expiry
  order burned all 3 lives within the first ~3 minutes of a decision window, instead of surviving the
  intended ~3 decision bars (e.g. 12 hours on H4). cTrader, having no concept of fine bars, would give
  the identical order its full ~12-hour survival window. This is a silent, severe fill/no-fill
  divergence that looks exactly like "the strategies disagree" — the plan's own warned failure mode.
  **Fix:** `TapeReplayAdapter.OnBarObserved` now decrements expiry at most once per decision-bar window
  (`decrementExpiry: isNewDecisionBar && !decrementedThisWindow`, set on the first fine bar processed),
  matching the cBot's once-per-decision-bar cadence exactly. Touch/fill detection is UNCHANGED — it
  still runs every fine bar (that's the SL/TP fidelity feature working as intended); only the expiry
  countdown was fine-bar-granular by mistake.

## 4b. Fill reporting — the second failure mode this contract exists to prevent

**F31 (found + fixed 2026-07-11, live):** flipping D11 live for the first time (research default →
LimitOffset) revealed that a resting order which fills *natively inside cTrader* — i.e., without our
engine ever sending a synchronous command that gets an immediate reply — was invisible to the engine.
Live repro: XAUUSD H4 trend-breakout, 2025-08-01→2025-10-01, limit entries — tape `e907e647`/`a59183c1`
= 12 trades; cTrader `921ce1e4`/`02c56355` = **0 trades, 0 fills, 0 cancellations reported**, despite
cTrader's own account balance moving (confirming positions genuinely opened on the venue side).

Root cause, two compounding bugs in `TradingEngineCBot.cs`:

1. `ExecuteSubmitOrder` passed the literal string `"Shamshir"` as the **label** to both
   `PlaceLimitOrder` and `PlaceStopOrder`, instead of the order's unique `clientOrderId`. Every
   resting order therefore shared the same label, so `ProcessLimitExpiry`'s cancel-matching
   (`order.Label == clientOrderId`) could never find its order — pending orders were removed from our
   own tracking on "expiry" but **never actually cancelled on the venue**, where they lived on
   indefinitely.
2. The cBot subscribed to `Positions.Closed` but had **no `Positions.Opened` handler**. When one of
   those immortal pending orders eventually got triggered by price — entirely outside our
   `submit_order`/`bar_done` command-response flow — nothing told the engine. No `_positionMap` entry,
   no `RecordOpen`, no exec message. The fill was real (cTrader's own balance and eventual close
   events would have reflected it) but completely invisible to our own ledger.

**Fix:** (a) `PlaceLimitOrder`/`PlaceStopOrder` now pass `clientOrderId` as the label, repairing the
expiry-cancel match. (b) Added `Positions.Opened += OnPositionOpened` — correlates a newly-opened
position back to a tracked pending order via `Position.Label == clientOrderId`, populates
`_positionMap`, calls `RecordOpen`, and sends a spontaneous `exec` "entry_fill" message (the same
venue-initiated pattern `OnPositionClosed` already uses for server-side SL/TP closes). Market orders
are untouched (still labelled `"Shamshir"`) — their immediate-fill path already handles this
synchronously, and a non-GUID label is what lets `OnPositionOpened` correctly ignore them rather than
risk double-processing.

**Re-verified live post-fix:** same config — tape `26664e81` = 12 trades, cTrader `438b5977` = **12
trades** (exact count match). Per-trade entry prices were close but not bit-identical (deltas of
$0.15–$1.5 on a ~$3300–3800 XAUUSD price, ≈0.01–0.04%).

> **P4.3 (F43) correction.** P2 attributed those residual deltas to "the pre-existing F23 entry-latency
> effect … **not a defect in the fill mechanism itself**". That attribution was wrong. They *were* the
> fill mechanism: the tape filled resting orders at the named price while cTrader filled them at the
> first breaching M1 tick (§3). Two sessions then built on the mistaken conclusion. A residual delta
> that is small is still a delta — "small enough to be latency" is a guess, not a measurement, and the
> way to settle it was to check the fill against the M1 bar the venue actually traded (R1).

**Watch out (next time this breaks):** if a resting-order run reports fewer engine-side trades than
cTrader's own account balance implies, check `Positions.Opened` wiring and the order label FIRST —
this exact failure mode (mechanism silently invisible to the engine despite genuinely executing) has
now happened via two unrelated root causes in the same session (F24 via a corrupted risk-sizing input,
F31 via missing fill correlation) and both looked identical from the outside ("0 trades on cTrader").

## 5. Verification

- **`tests/.../Adapters/VenueFillModelTests.cs` — the fill rule (§3) pinned against SIX REAL cTrader
  fills**, reproduced tick-exact from the M1 bars the venue replayed (EURUSD + XAUUSD, both directions,
  limit entry + stop-loss + take-profit; runs `d64d9488` / `81729685`). This is the only test in the
  repo that establishes the fill rule, and it does so from venue output, not from reasoning.
- `tests/.../RestingOrderContractTests.cs` (§6) drives synthetic bar sequences through the tape adapter,
  asserting fill/no-fill decisions, fill prices and expiry at both single- and dual-resolution (M1 exit).
  Note its two limit tests previously asserted "fills at exactly the named price, never better" — the
  §3 rule they were meant to guard. They asserted the assumption, so they went green while the venue
  disagreed. **A test written from the same reasoning as the code cannot falsify that reasoning** — it
  must be anchored to venue output (INVESTIGATION-METHOD R1).
- Live parity: `research parity --tape <id> --ctrader <id>` is the authoritative cross-check.

## 6. Watch-outs (for whoever touches this next)

- A limit that never fills is a **skipped trade**, not a bug — expect trade counts to DROP vs the
  market-entry baseline once D11 flips the research default. That is expected.
- If trade counts diverge after a resting-order change, **suspect expiry semantics before the
  strategy** — that is the historical failure mode (F30) and the reason this doc exists.
- Do not change `ExitTimeframe`'s default (M1) to "fix" this contract — the fine-bar SL/TP fidelity
  it buys is a separate, valuable feature (F6/F7 gap-through and intrabar-shadow fidelity). The
  contract is that the fine-bar loop drives TOUCH detection but not EXPIRY counting.
