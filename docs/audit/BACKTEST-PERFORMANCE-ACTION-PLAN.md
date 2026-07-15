# Backtest Performance — Action Plan (cTrader path)

**Companion to:** `BACKTEST-PERFORMANCE-AUDIT.md` (v2, verified)
**Audience:** OpenCode / DeepSeek implementation agent
**Date:** 2026-06-30
**Owner goal:** the cTrader backtest "takes for ages" — make it materially faster **without** changing backtest results (golden byte-identical) or breaking the cTrader lock-step.

---

## Ground rules (non-negotiable)

1. **Measure before and after every phase.** This is a static audit; impact estimates are hypotheses. Phase 0 builds the measurement harness; every later phase records a before/after wall-clock number in `PROGRESS.md`.
2. **Determinism gate stays byte-identical** after every phase:
   ```powershell
   dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"
   ```
3. **Any cBot/transport change** (Phases 4–5) must also pass the real-cBot E2E:
   ```powershell
   dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"
   ```
4. **One phase per commit.** Phases are ordered cheapest-and-safest first so value lands early and risk is back-loaded. Phases 1–3 are independent and can ship in any order; Phase 4 depends on Phase 0's harness; Phase 5 is last and riskiest.
5. **No behaviour change in Tiers 1–2.** If a "perf" change alters a single golden byte, it's a bug — revert and reconsider.

---

## Owner-profile fast track (H1/H4, 1–3 months ≈ 125–1,500 bars)

The owner's slow run is **H1/H4, 1–3 months** (confirmed 2026-06-30). At that bar count, per-bar engine compute is **not** the bottleneck (see audit "Owner's actual slow run"). The cost is coupled to the **millions of ticks replayed between bars**, all on the cBot thread. So for this profile, **re-order the phases**:

1. **Phase 0 (measure)** — but specifically split the cBot's *blocked* round-trip window from total run time, and count tick-publish calls. This tells you instantly whether F11 (tick PUB) or cTrader's own replay dominates.
2. **Phase 3, F11 first** (suppress tick/account PUB in backtest) — highest expected win for this profile; scales with tick count.
3. **Phase 5b, the TCP_NODELAY part only** (pull forward from Phase 5 — it's low-risk) — addresses per-bar round-trip latency.
4. Then Phase 1 (PRAGMAs) and the rest as written.
5. **Defer Phases 2 & 4** (journal serialization, indicator allocation) — they help low-TF/high-bar-count runs, not this one. Still correct to do eventually; just not the lever here.

The full phase list below stays the canonical plan; this fast track is the ordering for the owner's specific complaint.

---

## Phase 0 — Measure (do this first; blocks ranking, not the Tier-1 fixes)

**Why:** we do not know whether wall-clock is dominated by (a) cTrader-cli tick replay, (b) per-bar engine compute, (c) the round-trip, or (d) journal/DB. Optimizing blind risks spending effort on a non-bottleneck.

**Do:**
1. Add lightweight, opt-in stage timers (behind an env var / run flag, default off so production is untouched):
   - **cBot side** (`TradingEngineCBot.cs`): per-bar wall-time spent *blocked* in the `OnBarClosed` wait loop (:219–302) — record `Stopwatch` from frame-sent to `bar_done` received; emit a single aggregate line in `OnStop` (count, total, mean, p95). This isolates "round-trip + engine compute" from cTrader's tick replay.
   - **Engine side** (`KernelBacktestLoop.ProcessBarAsync`): accumulate time in `EvaluateAsync` (F2), in `PumpAsync` journal serialization (F3), and in `CompleteBarAsync`. Emit an aggregate at run end (gate behind `MinLogLevel`/a flag).
2. Capture **two baselines** with the existing app (use the `run-shamshir` skill or a scripted run): one H1 month, one M5 or M1 month (the low-TF case is where the pain is). Record total wall-clock, bars processed, and the stage breakdown in `docs/audit/PROGRESS.md`.
3. From the breakdown, **confirm or re-rank** the Tier 1–3 ordering in the audit.

**Files:** `TradingEngineCBot.cs`, `KernelBacktestLoop.cs` (timers only; no logic change).
**Gate:** determinism suite green (timers must not alter the journal). Timers off by default.
**Output:** baseline numbers + a confirmed bottleneck ranking in `PROGRESS.md`.

---

## Phase 1 — SQLite per-connection PRAGMAs (F4/F14)

**Goal:** make every DB commit cheap; fix the ineffective `busy_timeout`.

**Do:**
1. Add an EF Core `IDbConnectionInterceptor` (e.g. `SqlitePragmaInterceptor`) whose `ConnectionOpened`/`ConnectionOpenedAsync` runs:
   ```sql
   PRAGMA cache_size=-65536;   PRAGMA synchronous=NORMAL;   PRAGMA temp_store=MEMORY;
   PRAGMA mmap_size=268435456; PRAGMA busy_timeout=5000;
   ```
2. Register it on **both** DbContexts in `ServiceRegistration.AddPersistence` (Web) **and** in `EngineServiceCollectionExtensions.AddPersistence` (inner per-run host — this is where the journal volume is).
3. Keep the one-time `journal_mode=WAL` init (it's persistent and harmless) but remove the throwaway `busy_timeout` line (now handled per-connection).

**Files:** new `Infrastructure/Persistence/SqlitePragmaInterceptor.cs`; `Web/Configuration/ServiceRegistration.cs:67–84`; `Host/EngineServiceCollectionExtensions.cs:51–55`.
**Risk:** very low. `synchronous=NORMAL` is the standard, safe WAL setting.
**Gate:** determinism suite green; integration tests that hit SQLite green.
**Expected:** lower commit latency across journal/equity/bar/trade writes; also de-risks F15 backpressure.

---

## Phase 2 — Defer kernel journal serialization off the pump thread (F3)

**Goal:** stop serializing `EventJson`/`EffectsJson` on the thread the cBot is blocked on.

**Do:**
1. Change `StepRecord` to optionally carry the raw `EngineEvent evt` and `IReadOnlyList<EngineEffect> effects` instead of pre-serialized `EventJson`/`EffectsJson` (keep the existing strings as a fallback path if any other sink needs them).
2. Move the `JsonSerializer.Serialize(evt …)` / `Serialize(effects …)` calls from `KernelBacktestLoop.BuildStepRecord` (:346,348) into `SqliteStepRecordSink.Map` (`:22–35`), alongside the already-backgrounded `EffectKinds`/`RiskJson`/`VerdictsJson`. Use the same `Json` options object to keep output identical.
3. Do the same for the Reconcile step record (`KernelBacktestLoop.cs:158–170`).
4. Optional knob: a run flag to journal only non-`BarClosed`/sampled steps for disposable perf runs.

**Files:** `Domain/Kernel` `StepRecord`; `Host/KernelBacktestLoop.cs`; `Infrastructure/Persistence/Repositories/SqliteStepRecordSink.cs`.
**Risk:** medium — the serialized JSON must be byte-identical (same options, same field order). The **Journal** golden tests are the guard.
**Gate:** determinism + Journal tests byte-identical.
**Expected:** removes ~all journal CPU from the per-bar pump; biggest safe engine-side win.

---

## Phase 3 — cBot streaming/logging diet (F11, F12, F6, F9)

**Goal:** stop the cBot doing throwaway work on the cTrader thread during backtest. All cBot-side, so the **cTrader E2E gate applies**.

**Do (each independently committable):**
1. **F11 — suppress tick/account streaming in backtest.** Add a cBot `Verbose`/`StreamTicks` parameter (or have the engine set "suppress streaming" in `hello_ack`). When off, `OnTick` only maintains its counter and `PublishAccount`/`Publish("tick")` are skipped. Account is still reported in `bar`/`bar_result` payloads, so the engine loses nothing for a backtest.
2. **F12 — gate `Print`/`Diag`.** Behind the same `Verbose` flag (default off for backtest). Keep error + `OnStop` stat prints.
3. **F6 — single-pass `Serialize()`** (`TradingEngineCBot.cs:765–773`) via `SerializeToNode` (snippet in the audit). Compounds with F11.
4. **F9 — stop full-history rewrites.** Raise `ReportCheckpointEveryNBars` (50 → e.g. 500) and/or only `Write()` on `OnStop`; or make the checkpoint append-only. Keep the atomic write-temp-then-move.

**Files:** `Adapters.CTrader/TradingEngineCBot.cs`, `Adapters.CTrader/ShamshirTradeLogger.cs`.
**Risk:** medium — the cBot is the real compiled bot; the report.json/events.json schema must stay parseable by `CtraderReportHarvester`. **The cTrader E2E + ledger-reconciliation tests are the guard.**
**Gate:** cTrader E2E green (incl. report-vs-DB reconciliation); determinism green.
**Expected:** removes per-tick serialize/send and per-bar synchronous logging — scales with the *whole dataset*, so this is the lever that helps low-TF runs most.

---

## Phase 4 — Indicator allocation/CPU diet, cheap layer (F2 Tier-2, F5, F8)

**Goal:** cut per-bar allocations and redundant work **without changing any indicator output** → golden byte-identical.

**Do:**
1. **Convert bars→`SkenderQuote` once per (symbol,tf,bar)** and reuse across all indicator calls, instead of each `SkenderIndicatorService` method re-materializing its own list. Cleanest: have `IndicatorSnapshotService.RecomputeIndicatorsAsync` build the quote list once and pass it down (add quote-accepting overloads, or precompute and cache the converted list for the current bar).
2. **Hoist `barSnapshot.Values.Sum(b => b.Count)`** out of the per-strategy loop in `BarEvaluator.cs:98` (compute once per bar).
3. **Reuse buffers** where `list.ToList()` is called purely to snapshot under lock — evaluate whether a reusable array/pooled buffer is safe given the lock discipline.
4. **F5** — batch trade persistence in `TradePersistenceHandler` (accumulate + `AddRange` + one `SaveChangesAsync` on drain/threshold).
5. **F8** — optional: have `ReportBar` write directly to the persistence channels instead of via the event bus.

**Files:** `Infrastructure/Indicators/SkenderIndicatorService.cs`, `Host/IndicatorSnapshotService.cs`, `Host/BarEvaluator.cs`, `Infrastructure/Persistence/TradePersistenceHandler.cs`, `Host/EngineRunner.cs`.
**Risk:** medium — pure refactor; **any** numeric drift means a bug. Golden is the guard.
**Gate:** determinism byte-identical; re-measure with Phase-0 timers.
**Expected:** large GC-pressure reduction; meaningful if Phase 0 confirms `EvaluateAsync` is hot.

---

## Phase 5 — Structural / latency (F2 incremental, F1/F7) — last, riskiest

Only attempt the sub-items Phase 0 proves are bottlenecks.

**5a — Incremental indicators (F2 structural).** Replace full-series recompute with incremental/streaming computation (Skender `BufferList`/increment API or hand-rolled), so per-bar cost is O(1) per indicator. Highest CPU win, highest risk of numeric drift. **Golden byte-identical gate is mandatory**; if any indicator can't match bit-for-bit, leave it on the full-recompute path.

**5b — Round-trip latency (F1/F7).**
- Set `TcpNoDelay`/disable Nagle explicitly on the DEALER (cBot) and ROUTER (engine) sockets; set explicit HWM/buffers for the PUB/SUB streaming path.
- Investigate a **command-less-bar fast path**: when `bar_done.commands` is empty (the common case), let the cBot skip executing/replying and return — *iff* the engine doesn't require a `bar_result` ack before advancing. Verify against the protocol; this removes a half-round-trip on most bars.
- **Do NOT** convert the lock-step to fire-and-forget async (breaks the next-bar reconciliation invariant and determinism — see audit F1).

**Files:** `Infrastructure/Indicators/*`, `Host/IndicatorSnapshotService.cs`, `Infrastructure/Transport/NetMq/NetMqMessageTransport.cs`, `Adapters.CTrader/TradingEngineCBot.cs`, kernel feedback/protocol.
**Risk:** high. Determinism + cTrader E2E both mandatory after every change.
**Gate:** determinism byte-identical; cTrader E2E green (incl. reconciliation).

---

## Summary table

| Phase | Findings | Risk | Determinism-sensitive | cTrader-E2E-sensitive |
|-------|----------|------|-----------------------|-----------------------|
| 0 Measure | — | none | timers only | recommended |
| 1 PRAGMAs | F4/F14 | low | no | no |
| 2 Journal defer | F3, F15 | medium | **yes (Journal golden)** | no |
| 3 cBot diet | F11,F12,F6,F9 | medium | no | **yes** |
| 4 Indicator cheap | F2(cheap),F5,F8 | medium | **yes (golden)** | no |
| 5 Structural | F2(incr.),F1,F7 | high | **yes** | **yes** |

**Land Phases 0–3 first** — they're either zero-determinism-risk (1) or guarded by a single suite, and they target the costs that scale with dataset size (the low-TF pain). Phases 4–5 are the deeper wins but carry golden-drift risk; do them only after Phase 0 confirms they're worth it.
