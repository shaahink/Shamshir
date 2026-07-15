# Backtest Performance Audit — cTrader Path (v2, verified)

**Date:** 2026-06-30
**Scope:** cTrader backtest path (cTrader-cli → cBot → NetMQ → engine kernel → SQLite)
**Methodology:** Static code trace of the entire hot path. **No runtime profiling was done — see the methodology caveat below; it changes how the findings should be ranked.**
**Status:** v2 supersedes the original draft. Every finding was re-read against the source. Each now carries a **Verdict** (Confirmed / Confirmed-with-correction / Overstated / Refuted) and exact line cites. Five new findings (F11–F15) were added.

---

## ⚠️ Methodology caveat (read this first)

This is a **static** audit. It can prove a piece of code *runs on the hot path* and *does redundant work*, but it **cannot rank wall-clock contributors** — that needs measurement. In particular, the original draft asserted "F1 + F2 are the top two bottlenecks" with zero measurements. That conclusion is **not supported by evidence**.

The cTrader path has a wall-clock structure the static trace cannot weigh:

```
total wall clock ≈  Σ_bars [ cTrader-cli tick replay between bars (OUT OF OUR CONTROL)
                            + cBot OnBarClosed round-trip (cBot is BLOCKED here, so tick replay pauses)
                            + engine per-bar compute (F2/F3)
                            + cBot order execution ]
```

The single largest cost may well be **cTrader-cli replaying ticks** (thousands of ticks between two H1 bars), which we cannot optimize. **Phase 0 of the action plan is therefore measurement** — do not optimize blind. See `BACKTEST-PERFORMANCE-ACTION-PLAN.md`.

**Timeframe sensitivity (the dominant variable nobody quantified):** every per-bar cost below is multiplied by *bar count*. A 1-month run is **~500 bars on H1**, **~6,000 on M5**, **~30,000 on M1**. If the user is backtesting lower timeframes, per-bar overhead (indicator recompute, journal serialize, cBot `Print`/`Diag`, round-trips) scales linearly and **dominates**. The original "720 bars" assumption understates the problem by 1–2 orders of magnitude for intraday runs.

---

## Executive Summary

A month-long cTrader backtest streams bars from cTrader-cli through the cBot (NetMQ), into the inner per-run engine host's `KernelBacktestLoop`, through the kernel decision pipeline, with persistence offloaded to background channels.

**The DB is NOT the primary synchronous bottleneck** — all DB writes are backgrounded through bounded channels; no `SaveChangesAsync` blocks the per-bar loop. *However*, the journal channel runs in `Wait` mode, so under high bar counts it **can** apply backpressure and stall the pump (F15).

**Verified hotspot table** (impact column is *static reasoning*, not measured — confirm in Phase 0):

| # | Finding | Verdict | Impact (est.) | Fix difficulty |
|---|---------|---------|---------------|----------------|
| F2 | Indicator recompute: full-series + fresh quote alloc **per indicator per bar** | Confirmed (corrected) | **HIGH** | Medium–Hard |
| F3 | Kernel journal `EventJson`/`EffectsJson` serialized synchronously on the pump thread, every step | Confirmed | **HIGH** | Easy |
| F4/F14 | Per-connection PRAGMAs (`cache_size`/`synchronous`/`mmap`/`temp_store`) never applied; `busy_timeout` set on a throwaway connection | Confirmed (re-framed) | **MEDIUM–HIGH** | Easy |
| F11 | cBot publishes ticks + account on every Nth tick — **drained by nobody during backtest** | New | **MEDIUM** | Easy |
| F12 | cBot `Print` + `Diag` on the cTrader thread, every bar + every exec | New | **MEDIUM** (timeframe-dependent) | Easy |
| F1 | cBot synchronous lock-step round-trip per bar | Confirmed (mechanism corrected) | **MEDIUM** (inherent; hard to remove safely) | Hard |
| F6 | cBot `Serialize()` double-serializes every outbound message | Confirmed | LOW–MEDIUM | Easy |
| F9 | cBot rewrites the **entire** report/events/equity history every 50 bars | Confirmed (understated) | LOW–MEDIUM | Easy |
| F15 | Journal `Wait`-channel can backpressure-stall the pump on long/low-TF runs | New | LOW–MEDIUM | Medium |
| F5 | Trade persistence one `SaveChangesAsync` per trade | Confirmed | **LOW** (downgraded) | Easy |
| F7 | NetMQ sockets use OS defaults (HWM/buffers/Nagle) | Confirmed | LOW | Easy |
| F8 | EngineRunner fires per-bar event-bus publishes | Confirmed (2, not 3) | LOW | Easy |
| F10 | ScopedStepRecordSink creates a DI scope per flush batch | Confirmed (already correct) | NEGLIGIBLE | N/A |

---

## Hot Path Trace (verified)

```
ctrader-cli.exe  — replays TICK data at max speed; pauses while the cBot blocks (F1)
  OnTick()                     TradingEngineCBot.cs:161  — every tick; every TickEveryN(=10) → Publish("tick") + PublishAccount()  ← F11 (waste in backtest)
  OnBarClosed()                TradingEngineCBot.cs:179
    Print(CBOT|BAR_EVENT)      :189  ← F12 (synchronous cTrader log, every bar)
    Serialize("bar") + send    :201,216  ← F6 (double serialize)
    Diag(BAR_SENT)             :217  ← F12
    BLOCKING WAIT loop         :219–302  ← F1 (lock-step; thread blocked)
      _inbox.TryTake(.,100)    :222  (semaphore-backed; returns immediately on arrival — NOT a 100ms poll)

engine inner host (EngineHostFactory.Create, in-process):
  KernelBacktestLoop.RunFromBrokerAsync   reads _venue.BarStream
    ProcessBarAsync()          KernelBacktestLoop.cs:137
      _advanceVenue / PumpAsync
      ReconcileToVenue (+ JSON serialize)  :163,165  (only when VenueManaged & effects)
      BarEvaluator.EvaluateAsync           :192  → indicatorSnapshot.RecomputeIndicatorsAsync  BarEvaluator.cs:67  ← F2
      PumpAsync → kernel.Decide → _journal.Append(BuildStepRecord(...))  :266
        BuildStepRecord serializes EventJson + EffectsJson  :346,348  ← F3 (synchronous, every step)
      EquityObserved / Trailing / CompleteBarAsync
      _onBarProcessed → EngineRunner.ReportBar  → 2× eventBus.PublishAsync  EngineRunner.cs:256,264  ← F8
  ChannelJournalWriter (background) → ScopedStepRecordSink → SqliteStepRecordSink (batched 500)  ← F10 (fine)
```

---

## Finding F1 — cBot Synchronous Lock-Step Round-Trip (Confirmed; mechanism corrected)

**File:** `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:219–302`

**What is true:** After sending each bar, `OnBarClosed` blocks the cTrader event thread until the engine returns `bar_done` (30 s deadline at :219). While blocked, cTrader-cli cannot replay further ticks. One bar = one full network round-trip + engine compute, serialized.

**Correction to the original draft (which said "wastes up to 100ms of idle-wait per bar"):**
`_inbox.TryTake(out json, 100)` (:222) is **`BlockingCollection.TryTake` with a timeout — semaphore-backed**. It returns the *instant* `OnDealerReceive` enqueues the reply (:607), not after 100ms. The `100` is only the *maximum* sleep when nothing arrives; on a normal bar there is **near-zero** idle waste. So the original "0–100ms wasted per bar" claim is **wrong**. The real cost of F1 is the **inherent per-bar round-trip latency**, not poll granularity.

**Correction to the recommended fix:** the draft's "return immediately, process `bar_done` async" is **unsafe**. Orders from this bar's `bar_done` (`ExecuteMarketOrder`, :334) must reach cTrader **before** the next `OnBarClosed`, because the engine's next-bar reconciliation/exit detection (`KernelBacktestLoop.cs:150–172`) assumes those positions exist at the venue. cTrader also serializes `OnBarClosed` for a single symbol on one thread, so they *cannot* overlap. Removing the lock-step would break determinism and correctness.

**Realistic levers (do NOT remove lock-step):**
1. Reduce per-round-trip overhead: confirm `TCP_NODELAY` / disable Nagle on the DEALER/ROUTER pair (ties to F7); loopback request/response can otherwise eat a delayed-ACK penalty.
2. Shrink the engine compute that happens *inside* the round-trip (F2, F3) — this is the part we control and the highest-leverage win that's also safe.
3. Skip the `bar_result` wait entirely when there are **no commands** for that bar (most bars produce none): if `bar_done.commands` is empty, the cBot can return without executing/replying — verify the engine doesn't require a `bar_result` ack for command-less bars before advancing. (Needs a protocol check; gate behind the determinism suite.)

**Verdict:** Confirmed the lock-step exists and blocks the thread; **mechanism and recommended fix both corrected.** Impact downgraded from CRITICAL to MEDIUM and reclassified as *inherent* (attack it via F2/F3/F7, not by going async).

---

## Finding F2 — Per-Bar Indicator Recompute (Confirmed; cost model corrected)

**Files:** `src/TradingEngine.Host/BarEvaluator.cs:67`, `IndicatorSnapshotService.cs:30–101`, `Infrastructure/Indicators/SkenderIndicatorService.cs:11–62`

**What the draft got wrong:** it claimed "~720 passes × 4 strategies = 2,880 Skender calls." Cross-strategy de-dup **already exists**: `RecomputeIndicatorsAsync` keeps a `computed` HashSet (`IndicatorSnapshotService.cs:42,58`) and computes each *unique* indicator signature **once per bar**. Shared EMA50/200/RSI14/ATR14 are not recomputed per strategy. So the multiplier is *unique indicators*, not *strategies*.

**What is actually expensive (and worse than "incremental needed"):**
1. **Full-series recompute, last value discarded.** Each `SkenderIndicatorService` method does `bars.Select(b => new SkenderQuote(b)).ToList()` then `GetXxx(period)` over the **whole ≤500-bar window**, then `.LastOrDefault()` (e.g. `:13–14`). We pay O(window) to keep one number. Per bar that's `unique_indicators × O(500)`.
2. **Redundant quote materialization (missed by the draft).** Every indicator call re-allocates its own `List<SkenderQuote>` of up to 500 elements (`:13,19,25,31,38,44,50,57`). For K unique indicators that's **K × 500 object allocations per bar** of identical data → heavy GC pressure. Converting `bars → quotes` **once per (symbol,tf,bar)** and sharing across indicators removes ~(K−1)/K of that allocation for free.
3. **Repeated 500-element list copies per bar:** `RecomputeIndicatorsAsync` `list.ToList()` (:37), `BuildBarSnapshot` `list.ToList()` per tf (:121), and `BuildStrategyIndicatorValues` allocates a fresh `Dictionary` per active strategy per bar (:152).
4. **Per-strategy redundant work in the loop:** `BarEvaluator.cs:98` computes `barSnapshot.Values.Sum(b => b.Count)` *inside* the strategy loop — recomputed for every active strategy though it never changes within the bar.

**Fix ladder (cheap → structural):**
- **Cheap, safe, byte-identical:** convert bars→quotes once per bar and reuse; hoist the `totalBars` sum out of the loop; reuse buffers. Pure allocation/CPU win, no numeric change → golden stays byte-identical.
- **Medium:** cap the rolling window per indicator to `max(period)×k` instead of a blanket 500 (most indicators converge far sooner) — **numeric-sensitive, must pass the golden gate.**
- **Structural:** incremental/streaming indicators (Skender `BufferList`/increment API, or hand-rolled), making per-bar cost O(1) per indicator instead of O(window). Largest win, largest risk — separate phase, golden-gated.

**Verdict:** Confirmed it recomputes every bar; **cost model corrected** (de-dup already present; the real costs are full-series recompute + per-indicator quote allocation). Still HIGH impact (verify in Phase 0).

---

## Finding F3 — Kernel Journal JSON Serialized on the Pump Thread (Confirmed)

**File:** `src/TradingEngine.Host/KernelBacktestLoop.cs:266, 318–353` (`EventJson` :346, `EffectsJson` :348); also the Reconcile path `:163,165`.

`PumpAsync` calls `_journal.Append(BuildStepRecord(...))` for **every dequeued kernel event** (:266). `BuildStepRecord` serializes the full event object and the full effects list to JSON **synchronously on the pump thread** — which is exactly the thread the cBot is blocked waiting on (F1). For a strategy-heavy bar that's 15–30 serializations per bar, scaling with bar count.

**Confirmed-good context:** `ChannelJournalWriter.Append` (`ChannelJournalWriter.cs:44`) is only a channel write, and the *other* journal fields (`EffectKinds`, `RiskJson`, `VerdictsJson`) are already serialized in the **background** sink (`SqliteStepRecordSink.cs:29,31,34`). So the codebase already has the pattern; only `EventJson`/`EffectsJson` are on the hot thread.

**Fix:** carry the raw `evt` (an immutable record) and `decision.Effects` on `StepRecord` and serialize them in `SqliteStepRecordSink.Map` (background), matching the existing fields. This moves ~all journal serialization off the pump thread. (Bonus: a config knob to journal only sampled/non-bar steps for throwaway runs.)

**Verdict:** Confirmed. HIGH and **cleanly fixable** with the existing background-serialize pattern.

---

## Finding F4 / F14 — SQLite PRAGMAs Not Applied to Query Connections (Confirmed; re-framed)

**Files:** `src/TradingEngine.Web/Configuration/ServiceRegistration.cs:77–84`; `src/TradingEngine.Host/EngineServiceCollectionExtensions.cs:51–55`.

**The draft's framing was partly wrong.** Two corrections from tracing the actual run:

1. **WAL is already active for the inner engine host.** The per-run inner host is built in-process with `DbPath = Persistence:DbPath` — the **same file** the Web host already switched to WAL at startup (`BacktestOrchestrator.cs:743,856`). `journal_mode=WAL` is **persistent in the database file header**, so every connection to that file is in WAL mode regardless of who opens it. The draft's "the engine runs in DELETE mode on a separate `trading-backtest.db`" applies only to the *standalone* Host process (`TradingEngine.Host/appsettings.json`), **not** the default UI path. So WAL is **not** the gap.

2. **The real gap — per-connection PRAGMAs are never effective:**
   - `cache_size`, `synchronous`, `temp_store`, `mmap_size` are **never set anywhere**. SQLite therefore runs with a 2 MB page cache and (in WAL) **`synchronous=FULL`** — every commit fsyncs. With one `SaveChangesAsync` per 500-record journal batch plus equity/bar/trade writes, FULL adds real fsync latency.
   - `busy_timeout=5000` and `journal_mode=WAL` are run on a **throwaway init connection** (`ServiceRegistration.cs:79–84`) that is opened, used, and closed. `busy_timeout` is a **per-connection** pragma — it does **not** propagate to the pooled EF connections that run actual queries. (`journal_mode` happens to persist in the file, so that one "sticks" by luck.) So the H21 busy_timeout fix is effectively a **no-op for real queries.**
   - The inner per-run host — **where the journal write volume actually is** — sets **zero** pragmas (`EngineServiceCollectionExtensions.cs:53`).

**Fix (per-connection, both paths):** add an EF Core `IDbConnectionInterceptor` (or `SqliteConnection` open hook) that runs on **every** opened connection:
```sql
PRAGMA cache_size=-65536;     -- 64 MB
PRAGMA synchronous=NORMAL;    -- safe under WAL; big commit-latency win
PRAGMA temp_store=MEMORY;
PRAGMA mmap_size=268435456;   -- 256 MB
PRAGMA busy_timeout=5000;     -- now actually applied per-connection
```
Register it in **both** `ServiceRegistration.AddPersistence` (Web) and `EngineServiceCollectionExtensions.AddPersistence` (inner host). The throwaway-init approach should be removed/replaced.

**Verdict:** Confirmed PRAGMAs are missing; **diagnosis re-framed** — WAL is fine, but the perf pragmas must be applied *per connection*, and the existing `busy_timeout` is ineffective. MEDIUM–HIGH, easy.

---

## Finding F5 — Trade Persistence One-at-a-Time (Confirmed; downgraded to LOW)

**File:** `src/TradingEngine.Infrastructure/Persistence/TradePersistenceHandler.cs:31–44` — one `SaveTradeAsync` (→ `SaveChangesAsync`) per closed trade, backgrounded via a `Wait` channel (:10–15).

**Why LOW, not MEDIUM:** trades close on the order of **tens** per run, versus hundreds–to–tens-of-thousands of bars. Even un-batched and even if each `SaveChangesAsync` were 5–10 ms, the total is a fraction of a second and it's off the hot thread. Batch it (accumulate + `AddRange` + one `SaveChangesAsync` on drain) for consistency with the journal path, but it is **not** a meaningful wall-clock lever.

**Verdict:** Confirmed; impact downgraded MEDIUM → **LOW**.

---

## Finding F6 — cBot Double Serialization (Confirmed)

**File:** `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:765–773`

`Serialize()` serializes the payload to a string, **re-parses** it into a `JsonDocument`, copies every property into a `Dictionary`, then serializes **again**. Called for every bar, every `bar_result`, every `tick`/`acct` publish (so it compounds F11), and stats. Single-pass replacement:
```csharp
private static string Serialize(string type, object payload)
{
    var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), JsonOpts)!.AsObject();
    node["type"] = type;
    return node.ToJsonString(JsonOpts);
}
```
**Verdict:** Confirmed. LOW–MEDIUM (matters more because it's on the per-tick path via F11). Easy.

---

## Finding F7 — NetMQ Sockets Use OS Defaults (Confirmed)

**Files:** `Infrastructure/Transport/NetMq/NetMqMessageTransport.cs:61–74`; `TradingEngineCBot.cs:81–90`.

No `SendHighWatermark`/`ReceiveHighWatermark`, send/receive buffers, or **TCP_NODELAY** configured on either side. For lock-step request/response over loopback (F1), **Nagle + delayed-ACK is the more likely culprit than buffer size** — verify NetMQ's default for `TcpNoDelay` and set it explicitly on the DEALER/ROUTER pair. HWM/buffer tuning matters only for the multi-symbol streaming (PUB/SUB) case.

**Verdict:** Confirmed. LOW in general, but the **TCP_NODELAY angle directly affects F1's round-trip latency** and was not called out in the draft. Easy.

---

## Finding F8 — Per-Bar EventBus Publishes (Confirmed; count corrected)

**File:** `src/TradingEngine.Host/EngineRunner.cs:235–269`

`ReportBar` fires **2** `eventBus.PublishAsync` (EquityUpdated :256, BarIngested :264) — not 3 as the draft said — plus `_equitySink.Observe` (:251) and `_progress.Report` (:238). `TypedEventBus.PublishAsync` locks, snapshots the handler list, and awaits handlers sequentially (handlers themselves just enqueue). Minor per-bar overhead; could write directly to the persistence channels.

**Verdict:** Confirmed; count corrected (2, not 3). LOW.

---

## Finding F9 — cBot Rewrites Entire History Every 50 Bars (Confirmed; understated)

**Files:** `TradingEngineCBot.cs:198–199` → `ShamshirTradeLogger.Write:154–168`.

Every 50 bars, `Write()` serializes the **entire** accumulating `_events`, `_history`, and `_equity` lists. **Understatement in the draft:** `_equity` grows **per bar** (`RecordEquity` at `TradingEngineCBot.cs:197`), not per trade — so for an M1 month (~30k bars) the final checkpoints serialize tens of thousands of equity points, and `File.WriteAllText` runs synchronously on the cTrader thread (blocking tick replay, compounding F1). Append-only events, or checkpoint far less often (e.g. every 500 bars) / only on stop.

**Verdict:** Confirmed and **understated**. LOW–MEDIUM, scales badly with bar count. Easy.

---

## Finding F10 — ScopedStepRecordSink Scope-Per-Flush (Confirmed; already correct)

**Files:** `ScopedStepRecordSink.cs:16–21`; `SqliteStepRecordSink.cs:15–19`.

One DI scope + `TradingDbContext` per **batch** (≤500 records), and each batch is a single `AddRange` + one `SaveChangesAsync` — i.e. already batched. ~20 scope creations for 10k entries is negligible.

**Verdict:** Confirmed; this is **correct architecture**, NEGLIGIBLE impact. No action.

---

## NEW — Finding F11 — cBot Tick/Account PUB Stream Is Pure Waste in Backtest (New)

**File:** `TradingEngineCBot.cs:161–177` (`OnTick`), plus `PublishAccount` (:754–763) called from `OnTick`, `OnBarClosed` indirectly, and every order exec.

`OnTick` fires for **every tick** cTrader replays (millions over a month). Every `TickEveryN`(=10) it does `Publish("tick", …)` **and** `PublishAccount()` — each going through `Serialize()` (F6's double-serialize) and a 2-frame PUB send on the cTrader thread.

**The kicker:** the kernel backtest loop (`KernelBacktestLoop.RunFromBrokerAsync`) consumes **only `_venue.BarStream`**. Ticks land in the adapter's bounded tick channel and — during a headless backtest — are **drained by nobody** (DropOldest). So we pay serialize + send + transport-receive + dispatch for data that is **immediately discarded**. Account updates are needed for the live monitor, not for a backtest result.

**Fix:** suppress (or heavily throttle) `tick`/`acct` publishing in backtest. Options: raise `TickEveryN` dramatically when backtesting; or have the engine signal "backtest, suppress streaming" in `hello_ack` and gate `OnTick`/`PublishAccount`. Pure removal of waste; no effect on the backtest result.

**Verdict:** New, MEDIUM (scales with tick count — i.e. with the whole dataset, not just bars), easy.

---

## NEW — Finding F12 — cBot `Print` + `Diag` on the cTrader Thread Every Bar/Exec (New)

**File:** `TradingEngineCBot.cs:189–193` (`Print` every bar), `:217,283,315,…` (`Diag` sends a PUB frame), plus per-exec `Diag`/`Print`.

`Print(CBOT|BAR_EVENT|…)` runs a `string.Format` with 8 fields and writes to cTrader-cli's log **synchronously, every bar**. `Diag(…)` sends a 2-frame PUB message per call even if nobody subscribes. Both are on the cTrader thread inside the blocking window (F1). On H1 this is minor; on M1 (~30k bars) it is a real, removable cost.

**Fix:** gate verbose `Print`/`Diag` behind a `Verbose` parameter (default off for backtest); keep only error/stat prints.

**Verdict:** New, MEDIUM (timeframe-dependent), easy.

---

## NEW — Finding F15 — Journal `Wait`-Channel Can Backpressure-Stall the Pump (New)

**Files:** `ChannelJournalWriter.cs:35–53` (capacity 50,000, `FullMode = Wait`), `KernelBacktestLoop.cs:266`.

The journal channel is **lossless `Wait` mode** (correctly, to avoid the old silent-drop bug). But that means if the SQLite sink can't keep up (slow disk, FULL fsync per F4, large `EventJson` per F3), the channel fills to 50,000 and `Append` **blocks the pump thread** — which is the thread the cBot is waiting on (F1). So under high bar/step counts the "safely backgrounded" journal can **directly stall bar processing**. The draft's "DB is not blocking the hot path" is true *until the channel saturates*.

**Fix:** primarily mitigated by F3 (smaller/deferred serialization) + F4 (faster commits). Additionally consider: a larger batch size, a sampling mode for disposable runs, or an explicit "journal disabled" fast path for perf runs. Monitor `ChannelJournalWriter.DroppedBatches` and channel depth.

**Verdict:** New, LOW–MEDIUM, becomes the gating factor precisely on the long/low-TF runs the user is complaining about.

---

## What We Confirmed Is Fine (don't touch)

1. **No synchronous DB writes in the per-bar pump** — persistence is channel-backgrounded (subject to F15 backpressure).
2. **Channel backpressure modes are deliberate** — `Wait` on critical streams (exec/router/journal), `DropOldest` on analytics (bars/equity/ticks).
3. **Journal losslessness** — `ChannelJournalWriter` has bounded retry + observable `DroppedBatches`; old silent-drop bugs are gone.
4. **WAL is active for both hosts** (shared DB file; persistent in header) — F4 is about the *other* pragmas.
5. **Trade batching aside, the sink (F10) is correct** scope-per-batch architecture.

---

## Owner's actual slow run (2026-06-30): H1/H4, 1–3 months — re-ranks the tiers

The owner confirmed the slow case is **H1/H4 over 1–3 months ≈ 125–1,500 bars**. That is a small bar count, which **rules out per-bar engine compute as the cause**: even a pessimistic 10 ms/bar × 1,500 bars = ~15 s, not "ages." So F2/F3 are *not* the headline for this profile.

What "ages" with so few bars points to is cost coupled to the **millions of ticks cTrader-cli replays between those bars** (all on the cBot thread):

1. **F11 (tick/account PUB) is the prime suspect.** `OnTick` publishes every `TickEveryN`(=10) ticks → for 1–3 months of tick data that's **hundreds of thousands to millions** of double-serialized (F6) PUB sends, on the cTrader thread, **discarded by the backtest loop.** This scales with tick count, not bar count — exactly the shape of "few bars but takes forever."
2. **F1/F7 round-trip latency × bars + Nagle.** 125–1,500 lock-step round-trips; if each carries a delayed-ACK/Nagle penalty (~tens of ms), that alone is seconds-to-minutes. The TCP_NODELAY check (F7) is cheap and should be pulled forward.
3. **cTrader-cli's own tick replay** (uncontrollable) — Phase 0 measures this floor by timing the cBot's *blocked* window vs total run.

For this profile, **F2/F3/F12 drop in priority** (bar-count-bound, and F12's `Print`/`Diag` are per-bar so only hundreds–thousands of calls), and **F11 + the F7 TCP_NODELAY check rise to the top.** The action plan's "Owner-profile fast track" reflects this.

## Revised Priority (pending Phase-0 measurement)

**Tier 1 — safe, no determinism risk, do regardless of measurement:**
- F4/F14 per-connection PRAGMAs (both hosts)
- F3 defer `EventJson`/`EffectsJson` to the background sink
- F11 suppress tick/account PUB in backtest
- F12 gate cBot `Print`/`Diag`
- F6 single-pass `Serialize()`

**Tier 2 — safe, golden-gated allocation/CPU wins:**
- F2 cheap layer (convert quotes once/bar, hoist sums, reuse buffers)
- F5 batch trade persistence; F9 stop full-history rewrites; F8 direct-channel publishes

**Tier 3 — structural, needs care + full gates:**
- F2 incremental indicators (largest win, golden byte-identical gate is the guard)
- F1/F7 round-trip latency (TCP_NODELAY; command-less-bar fast path) — must keep lock-step + pass cTrader E2E + determinism

See `BACKTEST-PERFORMANCE-ACTION-PLAN.md` for the phased plan, file targets, and gates.

---

## Verification gates (every phase must keep these green)

Determinism / golden (must stay byte-identical):
```powershell
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"
```
cTrader E2E (the only coverage that exercises the real compiled cBot over NetMQ — required after any cBot/transport change):
```powershell
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"
```
