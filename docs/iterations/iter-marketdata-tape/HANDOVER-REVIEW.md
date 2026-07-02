# Handover Review — independent verification of FULL-HANDOVER.md (iter-marketdata-tape / iter-cache-reads-2)

**Date:** 2026-07-02
**Reviewer:** Claude (Fable 5) — grounded code trace + real build/test runs + a real driven tape run
**Reviews:** `FULL-HANDOVER.md`, `HANDOVER-P0-P4.md`, `VERIFICATION.md` (OpenCode/DeepSeek agent), branch `iter/integration-cache-tape`
**Companion docs written with this review:**
- `docs/iterations/iter-tape-trust/PLAN.md` — the actionable fix plan (phased, for the implementation agent)
- `docs/QUANT-ROADMAP.md` — strategy/calibration/experimentation roadmap (owner + agent)

---

## TL;DR — the verdict

The architecture delivered is **sound and genuinely valuable**: the tape venue works end-to-end (bars feed,
engine evaluates, journal writes, **~531 ms for a full run that costs cTrader 33 s+ of fixed overhead**), the
dual-resolution design has **no look-ahead**, and the cache fixes survived the merge. The handover's *system
description* (sections 1–8) is accurate and a good onboarding doc.

But the headline **"P4 complete" overstates delivery**, and the iteration shipped with a bug that means
**no tape backtest has ever returned a successful result**: the run executes fine and is then reported
`failed — "No bars found"`. I confirmed this live. The "1 pre-existing integration failure" claim is also
wrong — it is a regression introduced by this iteration's own read-path change. Compare mode and the Data
Manager download form exist as UI shells but do not implement what the plan (D6, P4d) specified, and the
**trust gate (V4 reconcile) has never run** — which is the entire epistemic basis for using the fast path.

Bottom line: **one short fix-phase away from being real.** Nothing here needs redesign; it needs finishing,
verifying, and honest labeling. The fix plan is in `iter-tape-trust/PLAN.md`.

---

## 1. Claims verified TRUE (re-run on this machine, 2026-07-02)

| Claim | Result |
|---|---|
| Build clean | ✅ 0 errors (net6 cBot NuGet warnings benign) |
| Unit suite | ✅ 314 passed / 0 failed / 6 skipped |
| Golden determinism | ✅ 63/63 byte-identical (handover's "3 golden" figure was wrong; the suite is 63 tests — all pass) |
| Market-data + tape integration tests | ✅ 11/11 (resolves the docs' own "3/3" vs "3/7" contradiction) |
| Cache fixes (iter-cache-reads-2) survived the merge | ✅ append invalidates snapshots; `MarkCompleted` called in `BacktestOrchestrator.cs:460` + `CTraderListenService.cs:229`; `CacheEvictionSweeper` registered (60 s grace, cap 8); dead `AppendBar` gone |
| Recorder V1 real | ✅ `marketdata.db` holds EURUSD M1×170 + H1×95 from the recorded shards; ingest idempotent |
| Tape venue is fast | ✅ driven run: 170 bars end-to-end in **531 ms** wall (incl. inner-host spin-up) vs the measured ~33 s cTrader-cli floor (`docs/audit/PROGRESS.md`) |
| TapeReplayAdapter core design | ✅ no look-ahead (exits for bar N processed before N is evaluated, entries fill at N's close, first exit exposure is N+1); SL-before-TP conservative same-bar rule via `EngineReducer.DetectSlTpExit`; sim-time (not wall-clock) on fills; venue advance wired through the `IBrokerAdapter.OnBarObserved` interface (no type-sniffing) |
| Kernel untouched | ✅ golden byte-identical; no `TradingEngine.Engine` changes in the venue work |

## 2. Claims verified FALSE or overstated

| Handover claim | Reality |
|---|---|
| "P0–P4 iteration delivered", "P4 complete: Compare mode UI + Data Manager download form + tape venue" | Tape venue **runs but always reports failure** (Bug B1). Compare mode is an aggregate-only diff of two *existing* runs — no per-trade ledger, no run-both flow, `LedgerReconciler` is referenced **nowhere** in `TradingEngine.Web` (D6-A not delivered). Download form exists but silently drops all but the first timeframe (Bug B3) and runs the CLI inside the HTTP request with no job status (plan required "ingestion/job status"). |
| "90 integration (1 pre-existing)" | 90/91, but the failure (`RunMetadataTests.RowRun_persists_and_surfaces_full_selection`, venue=null) is a **regression from this iteration's** memory-first read path (Bug B2), not pre-existing. |
| `BacktestReplayAdapter` "models spread … on exits via directional bid/ask half-spread" (§2, PERF audit §5 phrasing carried over) | Spread is applied to **floating PnL only** (`ComputeFloatingPnL`, H16). Entry fills and SL/TP exit fills are spread-free in BOTH replay venues — a systematic optimistic bias (Gap F1) that V4 reconcile will surface as a per-trade money diff. |
| Recorder "resumable: skip shards already written" (plan D3) | `GetShardWriter` opens with `append: false` — a re-run **truncates** existing shards (Bug B7). |
| "All file paths and line numbers verified" | Mostly true; the substantive errors are the ones above. |

**Not verified by anyone yet (correctly flagged in VERIFICATION.md, but worth repeating because FULL-HANDOVER's
headline buries it):** V2 (bulk download), V3 (tape speed baseline — now done informally by this review),
V4 (tape-vs-cTrader reconcile — **the trust gate**), V5 (engine-DB-vs-cTrader reconcile — the owner's original
pain). Until V4 runs, the fast path has no verified fidelity.

---

## 3. Bugs found (new, with evidence)

### B1 — P0 — Every tape run is reported FAILED after running successfully
`BacktestOrchestrator.cs:929`: `var barCount = (adapter as BacktestReplayAdapter)?.BarCount ?? 0;`
`TapeReplayAdapter` is a sealed sibling, not a subclass → cast is null → `barCount=0` → `anyBars=false` →
after all passes: `ExitCode=1, "No bars found for any symbol/timeframe combination."`
**Confirmed live:** run `6660341a` (EURUSD M1, tape) — journal full of real `BarClosed` records, result
`failed / "No bars found" / totalBars=0 / barsPerSec=0` in 531 ms.
Same root cause: the progress pre-query (`:860-867`) counts bars via `IBarRepository` (per-RunId `Bars` table
in trading.db) even for tape runs → `BarsTotal` is wrong/0 on the tape path.

### B2 — P0 — Memory-served run detail drops run metadata (the failing integration test)
`RunQueryService.BuildRunDetailFromState` (`:136-187`) — the new cache-era path serving **running** runs —
omits `Venue`, `RiskProfileId`, `InitialBalance` (=0), `BacktestFrom/To` (=0001-01-01), `GovernorEnabled`,
`RegimeEnabled`, `RunPlanJson`, `EffectiveConfigJson`, even though `BacktestRunState` carries most of them
(`BacktestOrchestrator.cs:261-272`). Breaks `RowRun_persists_and_surfaces_full_selection` and feeds the live
monitor wrong data (balance 0, venue unknown) for every running run.

### B3 — P1 — Multi-timeframe download records only the first timeframe
Recorder pairs positionally: `TradingEngineCBot.StartRecording` (`:757-767`) loops `for i < symbols.Length`
with `period = periods[i]`. `DataManagerController.StartDownload` (`:71,89`) sends ONE symbol and
`Periods=["m1,h1"]` (comma-joined) → only `m1` is subscribed. A user downloading "EURUSD H1+M1" gets M1 only,
silently. (This is why V1 needed two separate cTrader runs.)

### B4 — P1 — Download endpoint is a synchronous fire-and-pray
`DataManagerController.StartDownload` runs the full cTrader CLI (~minutes) inside the HTTP request: no job id,
no status/progress endpoint (plan P4 required it), client timeout kills nothing, `finally` **deletes the
shards even when ingest failed** (`:117`), NetMQ ports hardcoded 15562/3 (collides with a concurrent backtest
using the same pair), and only "last N days" is supported (no explicit date range).

### B5 — P1 — Limit-order expiry unit changes meaning in dual-resolution mode
`TapeReplayAdapter.ProcessPendingLimits` decrements `BarsRemaining` per **exit-TF** bar (`:263`); config
`LimitOrderExpiryBars=3` means 3 **hours** on the replay venue but 3 **minutes** on the tape venue with m1
exits. Guaranteed tape-vs-replay divergence for any `LimitOffset` strategy. (The code comment acknowledges
and rationalizes it; it is still a cross-venue semantic break.)

### B6 — P2 — `GetAccountStateAsync` always returns the initial balance
Both replay venues (`TapeReplayAdapter.cs:207`, same in `BacktestReplayAdapter`) return
`new AccountState(_initialBalance, _initialBalance, [])` regardless of `_balance`. Benign today (called at
startup) but a landmine for any future mid-run caller.

### B7 — P2 — Recorder shards are not resumable (plan D3 promised resumable)
`append:false` truncation, see §2 table. For a 5-year pull, a crash at month 55 restarts from zero, and a
re-run with a shorter range silently *loses* previously recorded data in that file.

### B8 — P2 — Ingester won't scale to the 5-year pull
`MarketDataIngester.IngestFileAsync` materializes the whole shard, and `SqliteMarketDataStore.WriteBarsAsync`
puts the entire batch through one EF change-tracker + one `SaveChangesAsync`. ~1.8 M tracked entities for a
5-yr m1 shard will crawl/blow memory. Needs chunking (e.g., 5–10 k rows per transaction +
`ChangeTracker.Clear()`), or raw `INSERT OR IGNORE` batches.

### B9 — P2 — Execution events written with `TryWrite` on a Wait-mode bounded channel
`TapeReplayAdapter` execution channel is `Bounded(1_000, FullMode.Wait)` but all writes are `TryWrite` —
when full, events are **silently dropped** (a lost fill = engine/venue book desync). 1000/bar is unlikely to
overflow, but the failure mode is silent corruption; either `await WriteAsync` (it's the engine thread —
would deadlock; so instead) assert/throw on false, or log loudly.

### B10 — P3 — Synthetic tick spread hardcoded
`FeedBarsAsync` publishes `ask = close + 0.0001m` — wrong scale for JPY pairs, wrong for anything non-FX.
Harmless today (ticks unused in backtest); wrong the day something reads it.

### B11 — P3 — Repo hygiene
`.git-rewrite/` (leftover from the history rewrite/squash mentioned in HANDOVER-P0-P4 §4) should be deleted;
`src/TradingEngine.Web/data/` (marketdata.db + a 32 MB trading.db + stale `test_migrate.db`) is untracked —
decide gitignore policy before someone commits 30 MB of SQLite.

---

## 4. Fidelity gaps (tape/replay venue vs cTrader) — name them BEFORE V4 runs

These are **not bugs**; they are model gaps that WILL appear in the V4 reconcile. Naming them now prevents
mis-triage (RECONCILE-FINDINGS.md says "RawMoney divergence = bug" — F1 below is a known modelling gap that
will masquerade as exactly that).

| # | Gap | Effect | Sizing |
|---|---|---|---|
| **F1** | **No spread cost on entry/exit fills** (both replay venues; spread only in floating PnL). cTrader fills longs at ask, detects long exits on bid, etc. | Tape is optimistic by ≈ spread × pipValue × lots per round turn, every trade | EURUSD ~1 pip ≈ $10/lot/trade; on a 30-pip stop, ~3 % of risk per trade — decisive for tight-TP configs |
| **F2** | **Intrabar floating equity not snapshotted** — `EmitAccountUpdate` fires per decision bar + on exits, not per exit-TF bar; intrabar equity troughs invisible | Tape MaxDD still understates cTrader's floating DD — the ORIGINAL "DB MaxDD=0 vs venue 4.6 %" pain survives on the fast path | Was the #1 predicted divergence in RECONCILE-FINDINGS.md |
| **F3** | **Trailing/breakeven/partial cadence** — `KernelTrailingEvaluator` runs once per decision bar (by design, end-of-bar, next-bar effect); cTrader trails per tick. Dual-res detects hits of the *last-set* stop on m1, but the stop only *moves* per H1 | Trailing exits systematically later/looser than cTrader | The audit called this out (§5); measure in V4 before deciding to fix |
| **F4** | **Gap-through fills at exact stop price** — a bar opening beyond SL fills at SL, not at the (worse) open | Optimistic on weekend/news gaps | Rare but fat-tailed; FTMO cares about exactly these |
| **F5** | Commission charged wholly at close (round-turn) vs cTrader per side | Intrabar equity slightly optimistic while a position is open | Minor |
| **F6** | A limit that fills on fine bar k can be SL-checked on the same bar k | Intrabar entry+exit possible with unknowable intra-bar ordering | Minor at m1; document |
| **F7** | Fine bars falling inside decision-TF **gaps** (weekend, missing H1) are consumed by the warmup skip and never exit-checked | Exits can't trigger during decision-bar holes | Only matters with patchy decision data |
| **F8** | **Silent single-resolution fallback**: when no exit-TF bars exist for the window, the venue logs Information and quietly degrades — but the inner host runs `MinLogLevel=Warning`, so the fallback is **invisible**. Current store state makes this live: M1 covers May 30–Jun 1, H1 covers Jun 1–Jun 5 — almost zero overlap, so an "H1 decisions / m1 exits" run today silently runs single-res | The owner thinks they got wick-fidelity and didn't | Fix: journal a warning + surface exit-resolution on the run detail |

---

## 5. Smaller design notes (no action needed, recorded for posterity)

- **Loop ordering is right:** advance venue (exits for the elapsed window) → pump → reconcile book to venue →
  day/week/month rolls → evaluate → pump entry fills before `BarClosed` → equity → trailing at end.
  The dual-resolution adapter slots into this cleanly without kernel changes — good work.
- **RunDataCache while running re-sorts whole collections per read** (equity bag sort per poll). Correct but
  wasteful; the planned cursor/incremental reads (cache-plan P6) remain the right follow-up.
- `DownsampleEquityIfNeeded` drains and re-fills the ConcurrentBag non-atomically — safe only under the current
  single-writer usage; fragile if a second writer ever appears.
- `GetJournal` caches the first caller's `maxEntries` after completion; a later larger request gets the smaller
  snapshot. Cosmetic.
- `SqliteMarketDataStore.ReadBarsAsync` materializes the full range (fine ≤ ~1 M rows; the plan said
  "streamed/paged" — revisit only if 5-yr multi-symbol windows hurt).
- `GetOpenPositionIds()` allocates a fresh HashSet per bar on all venues. Noise today; batch it if sweeps
  ever show it.
- Tests target `net10.0` — the ".NET 10 upgrade is orthogonal" note in the plan is already stale.

## 6. What the handover got RIGHT (credit where due)

- The §0 mental model (re-simulate INPUTS, oracle verifies) is preserved correctly in code — the venue does
  NOT replay recorded fills.
- Store schema (separate `marketdata.db`, (symbol, tf, openTime) unique key, REAL prices, provenance columns,
  weekend-aware gap detection) matches the plan's storage design.
- The system-overview + strategy/risk/add-on documentation (FULL-HANDOVER §1–§8) checks out against the code
  and is the best single onboarding doc in the repo right now.
- MAE/MFE auto-tuning direction (§9) is directionally sensible — but the specific statistical recipe has
  methodological problems; see `docs/QUANT-ROADMAP.md` §4 for the corrected method.

---

*Verification artifacts: build/test runs 2026-07-02 on this machine; driven run `6660341a` (tape, EURUSD M1,
2025-05-30→06-02) — journal shows BarClosed stream, result shows the B1 failure. App torn down after.*
