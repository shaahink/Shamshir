# Review — Backtest Performance Work (iter/ux-unify)

**Reviewer:** Claude (Opus 4.8), independent verification pass
**Date:** 2026-07-01
**Branch:** `iter/ux-unify`
**Owner's actual path:** the **cTrader** venue (UI → `venue=ctrader` → `RunEngineNetMqAsync` → cTrader-cli → cBot → NetMQ → kernel). The whole audit is built on this path; this review is re-centred on it.

---

## Bottom line

The fixes are real, and — importantly — they are **actually deployed and active** on the cTrader path (I verified the built `src.algo` is current with the source edits, not stale). The profiling harness is well-built. **But the one thing the action plan said to do first — measure the cTrader path — was never done on a real run, and in fact could not be done from the UI at all.** So the cBot fixes were shipped blind, which is exactly what the plan's #1 ground rule forbade. That, combined with the audit's own leading hypothesis, is the most likely reason you saw no improvement.

Four concrete findings:

1. **The cBot fixes are live, not stale.** The deployed `src.algo` (`bin/Debug/net6.0`, built 2026-07-01 01:10) is newer than the cBot source edits (06-30 22:39). F6/F11/F12 are compiled in and behaviourally on (F11/F12 suppressed because `Verbose` defaults false). So "I ran the old bot" is *not* the explanation.
2. **The Phase-0 measurement was never obtainable on your path.** The UI's cTrader launch never passed `--Diagnostics=true` to the cBot, so the cBot's round-trip-window and tick-publish timing (`CBOT|TIMING`) was *never emitted* on a real UI run — only inside the E2E test harness. The fixes were tuned against static reasoning, not a measured cTrader bottleneck. **I fixed this** (opt-in wiring, below) so you can finally measure.
3. **The audit's own hypothesis predicts "no visible improvement."** For H1/H4 over 1–3 months, the audit says the wall-clock floor is **cTrader-cli replaying millions of ticks between bars — which we cannot control.** F11 removes *our* per-tick PUB work, but if cTrader-cli's own replay dominates, wall-clock barely moves. Only the measurement in (2) can tell you whether the fixes could ever have helped. Until then, "no improvement" is fully consistent with the fixes working perfectly and simply not being the bottleneck.
4. **The kernel change (F3) had shipped with a broken determinism gate.** The handover claimed "63/63 byte-identical"; the real state was **62/63**. Fixed here (test-harness only). This affects the cTrader path too, since the engine kernel is shared between venues.

---

## 1. What actually runs on the cTrader path, and where the fixes land

`BacktestOrchestrator.cs:362` routes `venue=ctrader` to `RunEngineNetMqAsync`. That method:
- resolves a **prebuilt** `src.algo` from disk (`ResolveAlgoPath:1280`) — **no auto-rebuild**; it loads whatever `.algo` exists (Release preferred, else Debug), or throws;
- launches `ctrader-cli backtest <algo> …` with a fixed arg list (`:1078`);
- runs the shared kernel engine in-process, fed by the cBot over NetMQ.

Fix-by-fix, on this path:

| Fix | On the cTrader path? | Active now? |
|-----|----------------------|-------------|
| F11 suppress tick/acct PUB | **Yes** | Yes — `Verbose=false` default ⇒ suppressed |
| F12 gate Print/Diag | **Yes** | Yes — `Verbose=false` default ⇒ suppressed |
| F6 single-pass Serialize | **Yes** | Yes — compiled into the deployed `.algo` |
| F3 defer journal serialize | Yes (engine) | Yes |
| F4 SQLite PRAGMAs | Yes (engine) | Yes |
| F2 indicator quote reuse | Yes (engine) | Yes |
| F7 TCP_NODELAY | Would apply | **Not implemented** — audit pulled it *forward* as a top lever for your profile |

**Staleness check (ruled out):** `src.algo` mtime `2026-07-01 01:10` > cBot source commit `a7701b1 2026-06-30 22:39`. The fixes are in the bot you run. If in doubt on any future run, each run records an **`AlgoHash`** (`ComputeAlgoHash`) — compare it across runs to confirm you rebuilt.

---

## 2. The Phase-0 measurement gap (the core problem) — now fixed

The action plan's ground rule #1: *"Measure before and after every phase… record a before/after wall-clock number in `PROGRESS.md`."* The fast-track was even more specific for your profile: *"split the cBot's blocked round-trip window from total run time, and count tick-publish calls. This tells you instantly whether F11 or cTrader's own replay dominates."*

None of that happened:

- **`docs/audit/PROGRESS.md` does not exist.** No before/after numbers were recorded anywhere. The only figure is an inline "pump ~578→446 ms (95-bar test)" in the F3 commit message — an *engine-CPU* micro-number, not a cTrader wall-clock number, and not the cBot side where the audit says the cost is.
- **The cBot timing was unreachable from the UI.** The agent *did* build the cBot timing harness (`_timingRoundTrips`, `_timingRoundTripTotalMs/MaxMs`, `_timingTickPublishCount`, emitted as `CBOT|TIMING` in `OnStop`). But it only fires when the cBot's `Diagnostics` parameter is true, and **`RunEngineNetMqAsync` never passed `--Diagnostics=true`.** Only the E2E test harness set it. So on every real UI cTrader run, the flagship Phase-0 metric produced *nothing*.
- The engine-side profile (`%TEMP%\shamshir-profiling`) is reachable only if `Engine:Diagnostics:Enabled` config is set — and it's absent from `appsettings.json`, so off by default, and undocumented for users. Even when on, engine CPU is *not* the cTrader-path bottleneck per the audit.

**Net:** the cBot fixes were shipped against static reasoning, never against a measured cTrader bottleneck — the exact "optimize blind" the plan prohibited.

**Fix applied in this review (safe, opt-in, no behaviour change):**
1. `RunEngineNetMqAsync` now adds `--Diagnostics=true` **iff** `Engine:Diagnostics:Enabled=true` (same switch as engine timing). Measurement-only — it does **not** set `Verbose`, so F11/F12 stay suppressed and results are unchanged. No `.algo` rebuild needed: the deployed bot already reads the `Diagnostics` param and emits `CBOT|TIMING`.
2. After the run, the orchestrator scrapes `CBOT|TIMING` / `CBOT|STOP` from ctrader-cli stdout into the **run log**, so you can read the round-trip-window, tick-publish count, and tick count from the UI.
3. Fixed the misleading log ("`logs/profiling/`" → the real `%TEMP%\shamshir-profiling\` + cBot timing in the run log).

**How to get the number that ends the guessing:** set `Engine:Diagnostics:Enabled=true` (config/user-secrets/env), run one cTrader backtest, and read `CBOT|TIMING` in the run log:
```
CBOT|TIMING|bars=N|roundTrip(ms) total=T mean=… max=…|barProc(ms)=…|ckpt(ms)=…|tickPubs=P|ticks=K
```
- `roundTrip total` ÷ **total wall-clock** = the fraction of the run that is engine round-trip (what F2/F3/F7 can touch).
- The remainder ≈ cTrader-cli tick replay (**uncontrollable**).
- `tickPubs` should be ~0 with the fixes active (F11). If a *Verbose* baseline shows it was huge and `roundTrip` was a large share, F11 was the right call; if `roundTrip` is a tiny share, no engine-side fix will ever move your wall-clock and the answer is a different lever (higher TF, shorter range, data-mode, or accepting cTrader-cli's floor).

---

## 3. The F3 kernel change broke the determinism gate (now fixed)

**Claimed:** "Determinism golden 63/63 byte-identical." **Actual at HEAD before this review: 62/63.**

```
Failed  JournalSourceOfTruthTests.Journal_OrderAndFill_ShareOrderId
  System.Text.Json.JsonReaderException: The input does not contain any JSON tokens.
  at OrderIdOf(StepRecord r) : JsonDocument.Parse(r.EventJson)
```

**Why:** F3 moved `EventJson`/`EffectsJson` serialisation off the pump thread into `SqliteStepRecordSink.Map`, leaving both `""` on the raw `StepRecord` (payload now in `RawEvent`/`RawEffects`). The golden harness's `ListJournalWriter` writes records **directly, bypassing the sink**, so `OrderIdOf` parsed `""` → threw, **and** the determinism byte-identical check (which serialises `r.EffectsJson`, now `""`) silently degraded to `"" == ""` — the exact guard the plan said F3 must not break.

**Fix (test-harness only, no kernel/production change):** `ListJournalWriter.Append` now re-materialises `EventJson`/`EffectsJson` from `RawEvent`/`RawEffects` with the *same* `JsonSerializerOptions` as the sink. Test passes and effect-payload determinism coverage is restored. **Gate re-run: genuine 63/63.**

**Production impact of F3 itself: none.** Every `src` reader of `EventJson`/`EffectsJson` reads the **DB entity** (populated by the sink); integration journal tests pass **7/7**.

---

## 4. F3 changed the *shape* of persisted `EffectsJson` (flagged, untested)

- Before: `Serialize(decision.Effects)` — declared type `IReadOnlyList<EngineEffect>`.
- After (sink): `Serialize(r.RawEffects)` — declared type `IReadOnlyList<object>` (`StepRecord.RawEffects`).

`EngineEffect` is `abstract record EngineEffect;` (no members). System.Text.Json serialises by *declared* element type: `object` ⇒ runtime-type (full derived props); the abstract base ⇒ effectively empty. So persisted `EffectsJson` content very likely **changed** (probably more complete now). Not covered by any golden fixture. **Action:** verify any report/frontend consumer of `EffectsJson` still parses correctly. `BacktestQueryService` parses `EventJson` (not effects), so the OrderId path is safe.

---

## 5. Per-finding verification

| Finding | Impl? | Matches audit? | Notes |
|---------|-------|----------------|-------|
| F4/F14 PRAGMAs | Yes | Yes | 5 pragmas on every `ConnectionOpened`, both hosts. Correct. |
| F3 journal defer | Yes | Yes, but | Broke a golden test + weakened the determinism guard (§3); shape change unflagged (§4). Test now fixed. |
| F11 tick/acct PUB | Yes | Yes | On cTrader path, suppressed via `Verbose=false`. Effect **unmeasured** (§2). |
| F12 Print/Diag | Yes | Yes | Same. |
| F6 single-pass Serialize | Yes | Yes | `SerializeToNode`. Compiled into deployed `.algo`. |
| F2 quote reuse | Yes | Yes, fragile | `IndicatorSnapshotService.cs:41` `(_indicators as SkenderIndicatorService)!` NREs if `IIndicatorService` is ever decorated. Safe today; brittle. |
| F7 TCP_NODELAY | **No** | **Gap** | Audit fast-track pulled this forward as a top lever for your profile; skipped. |
| F5 / F8 / F9 / F1 / F2-incremental | No | OK | Correctly deferred (low value or high risk). |
| Phase-0 profiling | Partial | **Gap** | Harness built, but cBot side unreachable from UI (§2) and no baselines recorded. |

---

## 6. Profiling harness — good, know its blind spots

Good: `TimingReport`, `Stopwatch` stages, opt-in via `_diagnostics` (zero overhead off), survives cancellation. Keep it.

Blind spots:
1. **Engine CPU only** (evaluate/pump/completeBar). Does not capture wall-clock, the cBot round-trip, or cTrader-cli tick replay — the parts the audit says dominate the cTrader path. The *cBot*-side timing (now reachable, §2) is the relevant one for you.
2. Engine profile emits only with `Engine:Diagnostics:Enabled=true` (absent from `appsettings.json`).
3. No baselines recorded (`PROGRESS.md` missing).

---

## 7. Recommendations (priority order)

1. **Run the measurement that was skipped.** Set `Engine:Diagnostics:Enabled=true`, run *one* representative cTrader backtest (your usual H1/H4, 1–3 months), and read `CBOT|TIMING` from the run log + the engine JSON in `%TEMP%\shamshir-profiling`. Compute `roundTrip total ÷ wall-clock`. **This single ratio decides everything below.**
2. **If round-trip is a small share of wall-clock** (likely, per the audit): cTrader-cli tick replay is your floor. Stop micro-optimising the engine/cBot; the real levers are dataset-shaped — higher timeframe, shorter range, `--data-mode`, or accepting the floor. No amount of F2/F3/F11 tuning will help.
3. **If round-trip is a large share:** F7 TCP_NODELAY (skipped) is the next lever, and you can now confirm F11 helped by comparing a `Verbose`-on baseline (huge `tickPubs`) vs the current suppressed run.
4. **Record before/after in `docs/audit/PROGRESS.md`.** Don't accept "faster/slower" without it — the profiling guide itself warns of ±20% run-to-run variance.
5. **Harden the F2 cast** (`(_indicators as SkenderIndicatorService)!`) and **decide on the `EffectsJson` shape change** (§4).

---

## 8. Changes made in this review (all uncommitted)

```
M tests/TradingEngine.Tests.Simulation/GoldenReplay/KernelBacktestLoopGoldenTests.cs
  └─ ListJournalWriter.Append mirrors SqliteStepRecordSink.Map (re-materialise EventJson/EffectsJson
     from RawEvent/RawEffects). Unbreaks the golden test + restores determinism coverage. Test-only.

M src/TradingEngine.Web/Services/BacktestOrchestrator.cs
  └─ RunEngineNetMqAsync: pass --Diagnostics=true when Engine:Diagnostics:Enabled (opt-in, measurement-only,
     no behaviour change); scrape CBOT|TIMING/CBOT|STOP from ctrader-cli stdout into the run log; fix the
     misleading "logs/profiling/" log message.
```

**Gates after this review:**
- Determinism/golden/journal: **63/63** (was 62/63).
- Integration journal (real sink → DB → query): **7/7**.
- Web build: **0 warnings, 0 errors**.
- cTrader E2E (`RequiresCTrader=true`): **not run** (needs live credentials) — handover's "9/10" is unverified here.

**Nothing committed** — left in the working tree for review.
