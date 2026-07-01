# Backtest Performance — Measured Baseline (PROGRESS)

**Date:** 2026-07-01
**Measured by:** independent review pass, real cTrader-cli runs (creds from `appsettings.Development.json`, EURUSD, `--data-mode=m1`, cached data).
**Method:** `--Diagnostics=true` wired into the E2E harness (`CBOT|TIMING`) + engine-side `TimingReport` (`%TEMP%\shamshir-profiling`). Wall-clock = full `dotnet test` duration for one cTrader E2E test (includes cTrader-cli startup/login/replay/teardown).

---

## The headline number: the engine is NOT the bottleneck

| Run | Bars | Wall-clock | Engine CPU (`totalEngineMs`) | Engine % of wall | evaluateMs (indicators) | pumpMs |
|-----|------|-----------|------------------------------|------------------|-------------------------|--------|
| EURUSD H1, 3 days | 95 | ~36 s | 539 ms | **1.5 %** | 167 | 349 |
| EURUSD H1, 1 month | 553 | ~49 s | 1,311 ms | **2.7 %** | 714 | 525 |

**Scaling:** 5.8× the bars → engine CPU 2.4×, but wall-clock only **1.36×**. Linear fit: `wall ≈ 33 s (fixed) + ~28 ms/bar`.

### What this proves
- **The kernel engine is 1.5–2.7 % of wall-clock.** The entire engine-side audit focus (F2 indicators, F3 journal, F4 PRAGMAs, F8 event bus) targets a slice that cannot move the wall-clock. Even making indicators 10× faster would save <1.5 % of a run.
- **~33 s is a fixed cTrader-cli cost** (process startup, login/connection, data validation, cBot start/stop, and the known post-run "Message expected" teardown crash-recovery). It does not scale with bars.
- **The per-bar cost (~28 ms) lives in cTrader-cli's M1 data replay**, not our code. `--data-mode=m1` is already cTrader's *faster* server mode (`tick` is slower + heavier). There is no coarser server data mode.
- Within the engine, at scale **`evaluateMs` (indicators) becomes the top component** (714 ms of 1,311 ms at 1 month) — so F2 was the right *engine* target — but it is still noise against wall-clock.

### Projection to the owner's stated profile (H1/H4, 1–3 months, cached data)
- H1 3-month (~1,650 bars): ~33 + 46 = **~80 s**.
- H4 3-month (~550 bars): **~49 s**.

Neither predicts "takes for ages" on **cached** data. The gap between this and the owner's experience is almost certainly one of:
1. **First-run data download** — a range/symbol not yet cached forces cTrader-cli to download M1 data from the server before replaying; months of M1 across symbols can be minutes. (These test ranges were pre-cached.)
2. **Larger scope than stated** — more symbols, lower timeframe (M5/M1 = 6k–30k bars/month), or longer range.
3. A hang/retry hitting the 30-min run timeout.

**Open action:** reproduce the owner's *actual* slow run (exact symbol/TF/range + observed wall-clock, and whether the data was freshly downloaded) — the levers differ per cause.

---

## Where the time actually goes (measured decomposition)

Instrumented the cTrader-cli **process wall-time** (`BacktestCli`, opt-in `Diagnostics`) and split it by the first timestamped CLI log line ("Establishing connection"):

| Run | date range | M1 bars | cTrader-cli proc | proc-start → "connection" (pure .NET startup) | connection → end (login + M1 load/validate + replay + teardown) |
|-----|-----------|---------|------------------|----------------------------------------------|-----------------------------------------------------------------|
| 3-day | 15–18 Jan | ~4,300 | 25.6 s | **~3.0 s** | **~22.5 s** |
| 1-month | 1 Jan–1 Feb | ~44,640 | 36.1 s | ~3 s | ~33 s |

Two-point fit of proc wall-time: **`proc ≈ 25 s fixed + ~0.24 ms per M1 bar`** (~0.34 s/day of data).

```
cTrader backtest wall-clock ≈
    ~3 s    cTrader-cli .NET/plugin process startup                                 ← cTrader-cli internal
  + ~20 s   cTrader-cli login + M1 data loading/validation + teardown (+ known crash)← cTrader-cli internal, DOMINANT, ~fixed even on cached data
  + ~0.24 ms/M1-bar  M1 replay (scales with DATE RANGE, NOT timeframe)               ← cTrader-cli internal
  + <3 %    our engine CPU (evaluate/pump/journal/completeBar)                        ← what the audit optimised; irrelevant to wall-clock
  + our web/test overhead (equity poll, reconciliation, teardown)                    ← small
```

**Two decisive facts:**
1. **~23 s of every run is cTrader-cli fixed cost** (startup + login + data load/validate + teardown), present even on cached data — which is why repeated runs are still slow. It is entirely inside cTrader-cli; no engine/host change of ours reduces it.
2. **Replay scales with the date range, not the chart timeframe.** `--data-mode=m1` replays M1 data regardless of whether you produce H1 or H4 bars — so **H4 is NOT faster than H1 for the same range.** Only a shorter range (or fewer runs) cuts replay.

## Controllable levers, in priority order

Owner confirmed the slow case is **cached/repeated runs** (not first-run download). So the target is the ~23 s cTrader-cli fixed cost + replay — most of which we do **not** control. Realistic levers:

1. **Fewer cTrader-cli invocations.** Each backtest launch pays the full ~23 s fixed cost. If config iteration or multi-symbol/multi-TF work launches cTrader-cli repeatedly, that fixed cost multiplies — this is the biggest *controllable* win. Batch/consolidate runs; avoid re-running unchanged passes.
2. **Shorter date ranges when iterating.** Replay ≈ 0.34 s/day; timeframe does **not** matter (see fact #2). Use the shortest range that exercises the change, expand only for final runs.
3. **Environment: exclude cTrader-cli + `.algo` + data dir from Windows Defender / AV.** ~3 s of pure process startup + heavy per-run data loading is consistent with on-access AV scanning of the CLI binary and data files. A Defender exclusion is a cheap thing to try and could shave real seconds off *every* run. (Env change; measure before/after here.)
4. **Engine micro-opts (F2/F3/PRAGMAs/etc.): do NOT pursue for speed.** Measured at <3 % of wall-clock. Correct as hygiene; not a wall-clock lever. Stop optimising them for performance.
5. **The ~23 s cTrader-cli login/data-load/teardown is not reducible from our code.** It is inside cTrader-cli. The only structural escape would be a *different backtest engine* (e.g. our own replay venue with local bar data — the `BacktestReplayAdapter` path already exists and has none of this cTrader-cli overhead), but that abandons cTrader's execution fidelity. Owner decision, out of scope for a perf tweak.

---

## Measurement limitations (known)
- The cBot's `CBOT|TIMING` (blocked round-trip window, tick-publish count) is emitted via cBot `Print()`, which does **not** reach cTrader-cli stdout in this CLI build — so it is not captured in the artifact log even with `--Diagnostics=true`. The engine-side split (evaluate/pump/completeBar) + total wall-clock is sufficient to reach the conclusion above; the exact round-trip-vs-replay split within cTrader-cli would need the cBot to route timing over `Diag()` (NetMQ) instead of `Print()` (a cBot change + `.algo` rebuild). Deferred — the conclusion (engine <3 %) does not depend on it.

---

## Update (iter-marketdata-tape) — the "structural escape" is now built

Lever #5 above ("our own replay venue with local bar data … abandons cTrader's execution fidelity") is exactly
what iter-marketdata-tape implements — and the fidelity worry is addressed by the reconciliation harness rather
than by abandoning it:

- **P1–P3 (built + tested):** a canonical `marketdata.db`, an NDJSON recorder/ingester (cBot `--Record`), and
  `TapeReplayAdapter` — an in-process fake venue with **dual-resolution** exits (decide on H1, detect SL/TP on
  m1) that has **none** of the ~23 s cTrader-cli fixed cost or the per-bar IPC. Selectable via `Venue=tape`.
- **P0 reconcile (built + tested):** `LedgerReconciler` + `ShamshirReportParser` diff the engine DB against
  cTrader's own `shamshir-report.json`, classifying divergences (RawMoney = bug, Aggregation = expected). See
  `RECONCILE-FINDINGS.md`. This is how the fast path earns trust: run the same short config through both and
  reconcile.

So the measured conclusion here (cTrader-cli is the floor; the engine isn't the problem) and the new fast path
are two halves of the same answer: stop paying the cTrader-cli floor on every experiment; keep cTrader as the
periodic oracle.
