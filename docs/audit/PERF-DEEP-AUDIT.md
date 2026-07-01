# Deep Performance Audit — Why the backtest is slow, and the way out

**Date:** 2026-07-01
**Author:** Claude (Opus 4.8), grounded code trace + prior-audit reconciliation
**Supersedes the framing of:** `BACKTEST-PERFORMANCE-AUDIT.md` (v2) and `BACKTEST-PERFORMANCE-ACTION-PLAN.md` on the *root cause* (their per-finding fixes remain valid but target the wrong tier).

---

## TL;DR

The system is not slow because it "does a lot of calculation." It is slow because you swapped an
**in-process** backtest for a **three-process, lock-stepped** one:

```
OLD (fast):   cTrader backtest process
              └─ cBot (.NET 6) → your kernel (.NET 6, same process) → decision   ← ZERO process hops

NEW (slow):   ctrader-cli.exe (replays m1 data)                     ← process 1
                └─ cBot (.NET 6, in cTrader)                        ← process 2
                    └──NetMQ──▶ engine (.NET 8, your good design)   ← process 3
                        per H1 bar: bar out ▶ evaluate ▶ commands back ▶ execute
```

You went to .NET 8 to escape the cBot's .NET 6 ceiling (a good call for the engine design). The
**unavoidable consequence** is that the engine can no longer live inside the .NET 6 cBot, so every bar
now crosses a process boundary **twice** (bar out, commands back) to an external engine — and the *data*
is being replayed by yet another external process (`ctrader-cli`). The old version had none of that. The
slowdown is the **IPC + external-process floor**, not the arithmetic. No amount of indicator/journal
micro-optimization removes that floor, which is exactly why the two prior optimization passes moved the
needle so little.

**The fix is not a faster transport. It is removing the transport from the backtest entirely** — run the
.NET 8 engine in-process against local bar data for experiments (that's what `BacktestReplayAdapter`
already is), and keep cTrader-cli only for periodic *truth* validation.

---

## 1. The measurement that still hasn't happened (read first)

Both prior audits are **static traces**. Neither was ever run against a real cTrader backtest — the
`iter-ux-unify` review even found the cBot timing (`CBOT|TIMING`) was **unreachable from the UI** because
`RunEngineNetMqAsync` never passed `--Diagnostics=true` (since fixed, opt-in behind
`Engine:Diagnostics:Enabled`). So the single number that decides everything has never been captured:

```
roundTrip_total_ms ÷ wall_clock_ms   =  fraction of the run we can actually influence
remainder                            ≈  ctrader-cli's own m1 replay (uncontrollable from our code)
```

**Action:** set `Engine:Diagnostics:Enabled=true`, run one representative H1 / 1–3-month cTrader
backtest, read `CBOT|TIMING|bars=… roundTrip total=… mean=… max=… barProc=… tickPubs=… ticks=…` from the
run log. This audit's recommendation holds regardless of the ratio (see §7), but the ratio tells you how
much of the *cTrader path* is even ours to fix.

---

## 2. Correction to the prior audits: data-mode is `m1`, not ticks

The v2 audit's headline hypothesis — "the wall-clock floor is ctrader-cli replaying **millions of ticks**
between bars" — assumes tick-data replay. **It isn't.** `BacktestConfig.DataMode` defaults to **`"m1"`**
(`src/TradingEngine.CTraderRunner/BacktestConfig.cs:13`, `BacktestCliRequest.cs:19`), passed as
`--data-mode=m1` (`BacktestRunner.cs:199`, `BacktestOrchestrator.cs:1089`). So for an H1 / 3-month run:

| | count |
|---|---|
| m1 bars ctrader-cli replays | ~63,000 (3mo × ~21k m1/mo forex) |
| synthetic `OnTick` calls | ~1–4 × m1 = ~60k–250k (cheap now — F11 makes them counter-only) |
| `OnBarClosed` → NetMQ round-trips | **~1,500** (H1 bars) |
| engine bar evaluations | ~1,500 |

That's **not** "millions of ticks," and per-bar engine compute over ~1,500 bars can't be "ages" on its
own (1,500 × 10 ms = 15 s). The real levers are therefore (a) the ~63k m1 replay by ctrader-cli and (b)
the 1,500 cross-process round-trips — **both of which only exist because the backtest is out-of-process.**

**Corollary lever nobody has pulled:** `--data-mode=m1` for an H1 strategy makes ctrader-cli replay 60
m1 bars per decision bar purely to price intrabar SL/TP fills. If a run doesn't need intrabar fill
precision, `--data-mode=h1` skips that entirely. It's a per-run fidelity/speed dial that's currently
hard-defaulted.

---

## 3. Per-bar cost decomposition (grounded, still needs §1 to weight)

For one H1 decision bar on the cTrader path, in order:

1. **ctrader-cli replays ~60 m1 bars** to advance one hour (m1 mode). *External process; uncontrollable
   except via data-mode.*
2. **cBot serialize + DEALER send** of the bar (`TradingEngineCBot.cs:223-238`). F6 single-pass serialize
   is already in.
3. **NetMQ hop → engine**, kernel evaluate (indicators F2, journal serialize F3), commands built.
4. **NetMQ hop ← engine**, cBot executes orders and replies `bar_result` (`:310-317`).
5. cBot **blocks** the whole time (`:243-320`) so ctrader-cli can't replay ahead — the round-trip is on
   the critical path by design (and must stay lock-stepped for correctness — the next bar's reconciliation
   assumes this bar's orders exist at the venue).

Items 2–5 are **pure overhead that the in-process design does not have.** In an in-process replay, "the
engine evaluates the bar" is a method call — no serialize, no socket, no second process, no block.

---

## 4. The transport question, answered honestly

> "if i have to change my transport protocol use redis or queues or graphql…"

**Don't.** None of these help, and some hurt:

- **Redis / message queues** add a broker hop and *more* latency than raw loopback NetMQ. They solve
  fan-out/durability/decoupling — problems you don't have in a single-machine lock-step backtest. They do
  nothing about the fact that each bar waits for a round-trip to an external process.
- **GraphQL** is an API query language for request/response over HTTP. It is irrelevant to a hot
  per-bar loop and would be strictly slower than NetMQ.
- **The only transport-level win is micro:** `TCP_NODELAY` on the DEALER/ROUTER pair (F7, still
  unimplemented). Worth doing for the *live* path and cheap, but it shaves milliseconds — it does not
  change the architecture's floor.

The transport is a symptom, not the disease. The disease is *having a transport in the backtest at all.*

---

## 5. Your "untruthful" replay adapter is actually most of the way there

`BacktestReplayAdapter` (`src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs`) already:

- models **commission + swap** via `TradeCostCalculator.Compute` (`:389`);
- models **spread** on floating PnL and exits via directional bid/ask half-spread (`:451-460`);
- detects **SL/TP against bar OHLC** using the *exact same* `EngineReducer.DetectSlTpExit` the engine
  uses (`:278`) — venue-managed exits, reasoned close events, byte-identical to the kernel;
- prices SL/TP exits at the **stop/target price**, not the bar close (`:283-285`).

It is **not** a toy. It has exactly **one** material fidelity gap vs cTrader:

- **Intrabar resolution.** It feeds bars at the *strategy* timeframe (`GetAsync(_symbol, _timeframe, …)`,
  `:107`), so SL/TP and trailing are evaluated against **H1** OHLC. cTrader with m1 data resolves the
  **within-bar path** — crucially, *which of SL or TP is hit first* when both lie inside one H1 bar, and
  where a trailing stop sits bar-by-bar. For a breakout-with-runner strategy this is the one thing that
  actually differs.

Close that gap and the replay is "truthful enough" to iterate on, with cTrader reserved to *prove* it.

---

## 6. The hidden blocker: you have no local data

The store is **empty** — `data/trading.db` is 249 KB, `Bars=0`, `BacktestRuns=0`. Worse, the `Bars`
table is keyed **per `RunId`** (`.schema Bars`), so it was never designed as a canonical, reusable
history — each run's bars are siloed to that run.

This is *why* you're stuck on the slow path: **ctrader-cli is the only backtest path that brings its own
data.** The in-process replay can't run because nothing has been ingested. So "just use the fast path"
requires a real prerequisite: **get m1 (and H1) history into a canonical local store, once.** cTrader can
export it, or the cBot can dump it during a single capture run; after that, in-process replay runs forever
with no cTrader-cli involvement.

---

## 7. Recommendation — a two-tier backtest, and a data pipeline to unlock it

This is the scalable architecture for "wild experiments" (multi-symbol, dynamic MAE/MFE, regime
detection). It keeps your good .NET 8 engine and does **not** move anything back to .NET 6.

**Tier 1 — Fast, in-process, for experimentation (the default while iterating):**
- .NET 8 engine + `BacktestReplayAdapter`, driven from a **canonical local m1 store**. No ctrader-cli, no
  cBot, no NetMQ, no second process. This is the moral equivalent of your old in-cBot speed, but with the
  modern engine — because a backtest has no reason to cross a process boundary.
- Upgrade the replay to **dual-resolution**: feed m1 bars to the venue's exit/trailing detection while the
  strategy still *decides* only on H1 closes. This recovers cTrader's intrabar fidelity (§5) at in-process
  speed. **No kernel decision change** — the strategy timeframe is unchanged; only the venue's fill
  resolution gets finer.
- Expect **10–100×** over the cTrader path (removing external replay + per-bar IPC + serialize).

**Tier 2 — cTrader-cli, for truth (periodic, not every iteration):**
- Keep exactly what you have. Run it occasionally on a promising config to validate.
- Add a **reconciliation harness**: run the *same* (dataset, config, seed) through both tiers and assert
  the trade ledger matches within tolerance (you already have `verify-ctrader-run.ps1` +
  report-vs-DB reconciliation to build on). This is what lets you *trust* Tier 1. "Truthful vs my replay
  adapter" stops being a worry once you can prove they agree on demand.

**Prerequisite — the data pipeline (unblocks everything):**
- A canonical `MarketData` store keyed by (symbol, timeframe, time) — **not** per-RunId — with m1 as the
  base resolution and H1+ derived or stored alongside. Ingest once from cTrader (export or a one-shot cBot
  capture run), dedupe, reuse forever.

**Live trading is untouched:** cBot ↔ engine over NetMQ stays — at one bar/hour, IPC latency is irrelevant
and the split is correct.

### Why this beats "keep optimizing the cTrader path"
The prior action plan's F2/F3/F7/F11 fixes are all real, but they optimize *inside* the slow
architecture. Even done perfectly they can't beat the ctrader-cli replay + round-trip floor, and they do
nothing for scale (multi-symbol multiplies the round-trips). Tier 1 deletes the floor instead of shaving
it, and multi-symbol becomes just more in-process iterations.

---

## 8. Measure-first checklist (before writing code)

1. **Capture the ratio (§1)** on one real H1/1–3-month cTrader run. Records how much of the *cTrader path*
   is round-trip (ours) vs ctrader-cli replay (not ours).
2. **Time an in-process baseline** for the identical strategy/symbol/range once data exists (§6): run the
   `BacktestReplayAdapter` path and record wall-clock + bars/sec. This is the Tier-1 ceiling and tells you
   immediately whether the engine per-bar compute (F2/F3) is even worth touching.
3. Only then decide how much (if any) of the old F2/F3/F7 work is worth doing.

Put the numbers in `docs/audit/PROGRESS.md` (still missing). Don't accept "faster/slower" without them —
run-to-run variance is ±20%.
```

---

## Appendix — file cites used

| Claim | File:line |
|---|---|
| data-mode default `m1` | `CTraderRunner/BacktestConfig.cs:13`, `BacktestCliRequest.cs:19` |
| cli arg `--data-mode` | `CTraderRunner/BacktestRunner.cs:199`, `Web/.../BacktestOrchestrator.cs:1089` |
| cBot bar send + blocking round-trip | `Adapters.CTrader/TradingEngineCBot.cs:223-320` |
| OnTick now counter-only (F11) | `Adapters.CTrader/TradingEngineCBot.cs:177-194` |
| replay models costs/spread/SL-TP | `Infrastructure/Adapters/BacktestReplayAdapter.cs:278,283-285,389,451-460` |
| replay feeds strategy-TF bars only | `Infrastructure/Adapters/BacktestReplayAdapter.cs:107` |
| Bars table keyed per RunId; store empty | `data/trading.db` schema; `SELECT COUNT(*) FROM Bars` = 0 |
