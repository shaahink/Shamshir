# iter-tape-trust — Make the fast path truthful, trusted, and usable at scale

**Written:** 2026-07-02
**For:** the implementation agent (OpenCode / DeepSeek). Self-contained; read top to bottom.
**Prereq reading:** `docs/iterations/iter-marketdata-tape/HANDOVER-REVIEW.md` (bug/gap IDs **B1–B11**, **F1–F8**
used below are defined there), `docs/iterations/iter-marketdata-tape/PLAN.md` (the original iteration; its
V2–V5 are absorbed into this plan), `docs/audit/RECONCILE-FINDINGS.md`.
**Branch base:** `iter/integration-cache-tape`.
**Scope guard (unchanged):** NO decision-kernel / strategy / risk-math changes. Golden must stay 63/63
byte-identical after every phase. Venue models, orchestration, web, tooling only.

**The one-line goal:** today the tape venue *runs* but (a) reports every run as failed, (b) has never been
reconciled against cTrader, and (c) the data pipeline can't fill it at scale. After this iteration the owner
can download months of data from the UI, run tape backtests that report honest results, and point at a
committed reconcile artifact that says exactly how far tape is from cTrader and why.

---

## Phase T0 — Tape runs report the truth (blocking everything; small)

1. **B1 — run result.** Give both replay venues a common way to report bar throughput. Preferred: a tiny
   interface in Domain, e.g. `IReplayVenue { int BarCount { get; } }`, implemented by `BacktestReplayAdapter`
   and `TapeReplayAdapter`; `BacktestOrchestrator.RunEngineReplayAsync` casts to that instead of the concrete
   `BacktestReplayAdapter` (`BacktestOrchestrator.cs:929`). Do NOT pattern-match two concrete types — the next
   venue would re-introduce the bug.
2. **B1b — progress totals.** The pre-query at `BacktestOrchestrator.cs:860-867` must count bars from
   `IMarketDataStore` when `useTape` (decision TF, run window), `IBarRepository` otherwise.
3. **B2 — memory-served run detail.** `RunQueryService.BuildRunDetailFromState` must carry `Venue`,
   `RiskProfileId`, `GovernorEnabled`, `RegimeEnabled`, `InitialBalance`, `BacktestFrom/To` (extend
   `BacktestRunState` with the missing fields — it already has Venue/toggles; add balance + range at `Start`).
4. **F8 — no silent fidelity downgrade.** When `TapeReplayAdapter.ConnectAsync` finds zero exit-TF bars, emit a
   `LogWarning` (survives `MinLogLevel=Warning`) AND write a run-journal line (e.g. progress event
   `TAPE|EXIT_RESOLUTION|fallback=decision-tf`) so the run itself records which resolution actually ran.
   Surface effective exit resolution on the run detail (one string field, e.g. `ExitResolution: "M1" | "decision-TF (fallback)"`).
5. **B9 — no silent event loss.** Execution-channel writes in `TapeReplayAdapter`: check `TryWrite`'s return;
   on false, log an error with orderId (do not await on the engine thread). Same in `BacktestReplayAdapter`
   if it shares the pattern.

**Gate T0:** Integration **91/91** (the venue-null test now green); golden 63/63; a driven tape run over the
seeded day (EURUSD M1, 2025-05-30→06-02) returns `completed`, `totalBars=170`, `venue="tape"`, sane
`barsPerSec`, and the journal contains the exit-resolution line.

## Phase T1 — Data acquisition that works at scale (unblocks V2)

1. **B3 — record the full cross-product.** Change `TradingEngineCBot.StartRecording` to subscribe
   **symbols × periods** (every symbol gets every listed period). Keep the positional behaviour behind
   nothing — cross-product is the only sane semantic. Rebuild `.algo`, confirm `AlgoHash` changes,
   `RequiresCTrader` E2E after the cBot change.
2. **B4 — download as a background job.** `POST /api/data-manager/download` returns a job id immediately;
   job state (queued/recording/ingesting/done/failed + counts) held like `BacktestRunState` and polled via
   `GET /api/data-manager/jobs/{id}` (+ list endpoint). UI shows job progress. Keep shards on ingest failure
   (move the delete inside the success path; log the temp dir on failure). Allocate NetMQ ports from the
   existing port-manager instead of hardcoded 15562/3. Accept an explicit `{from,to}` range as well as `days`.
3. **B7 — resumable shards.** Recorder writes `<sym>_<tf>.ndjson.partial` and renames on `OnStop`, OR opens
   with `append:true` and the ingester's dedupe absorbs overlaps (simplest: append + dedupe — document it).
4. **B8 — chunked ingest.** `SqliteMarketDataStore.WriteBarsAsync`: insert in chunks (5–10 k rows per
   transaction, `ChangeTracker.Clear()` between; or drop to raw SQL `INSERT OR IGNORE` batches). Stream shard
   lines instead of materializing the file when the file exceeds ~100 k lines.
5. **Coverage view.** Data Manager inventory: show per (symbol, decision-TF) whether m1 coverage overlaps the
   same window (drives F8 away at the source). A red "no m1 overlap" chip is enough.

**Gate T1:** from the UI, one download job for EURUSD **H1+M1, same 2-week window** lands BOTH timeframes
(inventory shows overlapping ranges); re-running the job inserts 0 new rows; job status transitions visible;
`RequiresCTrader` E2E green after the cBot rebuild.

## Phase T2 — The trust loop (V2–V5 from the original plan; the point of everything)

1. **V2 — owner's working set.** Download EURUSD H1+M1 for the owner's real profile (1–6 months) + any other
   symbols in active use. Record inventory in `VERIFICATION.md`.
2. **V3 — speed baseline.** Same strategy/symbol/range through (a) cTrader path, (b) tape. Record wall-clock,
   bars/sec in `docs/audit/PROGRESS.md`. (Informal datum from review: 170 bars in 531 ms incl. host spin-up.)
3. **V4 — tape vs cTrader reconcile (trust gate).** Finish the engine-side ledger mapper
   (`BacktestRunSummary`+`Trades` → `ReconcileLedger`) in Web + `scripts/reconcile-run.ps1`; run the same short
   config both ways; `LedgerReconciler.Compare`; commit the diff artifact.
   **Before running it, update `RECONCILE-FINDINGS.md` to pre-register F1** (spread-free fills) as an EXPECTED
   per-trade money divergence ≈ spread×pipValue×lots per round turn — otherwise the "RawMoney = bug" rule
   mis-triages it. Also pre-register F2 (MaxDD), F3 (trailing cadence), F4 (gap fills).
4. **V5 — engine-DB vs cTrader report** (the owner's original "DB ≠ cTrader" pain) on a cTrader-path run.
   Fix clear bugs; log modelling gaps as T3 items with measured sizes.

**Gate T2:** committed reconcile artifacts for V4+V5 with every divergence either (a) fixed, or (b) named,
sized, and mapped to a T3 item. No unexplained rows.

## Phase T3 — Fidelity hardening (driven by T2 numbers, not speculation)

Order by measured impact from V4; expected order:

1. **F1 — spread on fills.** Long entry fills at `close + halfSpread`; short entry at `close`; exit detection
   stays on bid bars for longs (bar OHLC = bid), shorts' SL/TP detection and fills shift by spread
   (`trigger when high + spread ≥ SL` ⇔ detect on ask). Apply identically in BOTH replay venues so replay and
   tape stay comparable. Use `SymbolInfo.TypicalSpread` now; per-bar recorded spread is a later refinement
   (see QUANT-ROADMAP §6 data items).
2. **F2 — intrabar equity envelope.** Track a per-decision-bar min/max floating-equity watermark inside the
   venue while scanning exit-TF bars; either emit one worst-case `AccountUpdate` per decision bar or expose
   the watermark so drawdown sees the trough. Target: tape MaxDD within tolerance of cTrader's on the V4 set.
3. **B5 — limit expiry in decision bars.** Convert `LimitOrderExpiryBars` to decision-bar units inside the
   tape venue (count-down only when the decision-bar window rolls, not per fine bar).
4. **F4 — gap-through slippage.** If a fine bar OPENS beyond the stop, fill at the open, not the stop price.
5. **F3 — trailing cadence:** do NOT silently change it. Measure its share of V4 divergence; if material,
   propose a venue-side exit-TF trailing mode as its own reviewed change (it shifts behaviour of every
   trailing config).

**Gate T3:** each item lands with a re-run V4 reconcile that is strictly greener (or the item's divergence is
formally accepted with the number recorded); golden 63/63 untouched throughout.

## Phase T4 — Compare mode, as designed (D6-A)

1. **Run-both:** New-Backtest "Compare both" → orchestrator starts a cTrader run and a tape run from one
   config, tagged with a shared `ComparePairId` (new column or reuse `ParentRunId` semantics — pick one and
   document).
2. **Server-side verdict:** `GET /api/reconcile?left={runId}&right={runId}` builds both ledgers and returns
   `LedgerReconciler.Compare` output (per-trade rows + classified aggregates + verdict). The compare page
   consumes THIS instead of client-side subtraction, and renders the per-trade ledger side-by-side with
   per-field classification (RawMoney/Aggregation/TradeSet).
3. Keep the current lightweight two-run compare as the entry point (checkbox flow already exists).

**Gate T4:** a driven "compare both" run on the T2 dataset renders two ledgers + a verdict; WebSmoke green.

## Phase T5 — Experiment throughput (feeds `docs/QUANT-ROADMAP.md`; do after T2)

1. **Sweep runner:** accept a run-matrix (strategy × parameter-grid × symbol × TF) and execute N tape runs
   with a bounded parallel pool of inner hosts (start N=4). All share the read-only `IMarketDataStore`;
   per-run writes stay run-scoped. Content-addressing (`DatasetId`,`ConfigSetId`,`Seed`) already exists —
   reuse it to skip already-computed cells.
2. **Sweep mode journal thinning:** per-run flag to skip `StepRecord` persistence (keep trades + summary +
   equity). The journal is the dominant in-process per-bar cost; sweeps don't need per-bar narration.
   Determinism unaffected (journal is an output, not an input).
3. **Results surface:** one grid (params × metric) API + minimal UI table; CSV export is fine v1.
4. Measure first per PROGRESS.md discipline: record bars/sec single-run vs 4-way parallel before/after
   thinning. No optimization without a number.

**Gate T5:** a 13-cell ATR-multiplier sweep (QUANT-ROADMAP Q1's first experiment) completes on the T2 dataset,
produces a results grid, re-running it is a no-op (content-address hit), numbers in PROGRESS.md.

## Housekeeping (any phase)

- Delete `.git-rewrite/` (leftover history-rewrite scratch — confirm with owner first).
- gitignore `src/TradingEngine.Web/data/` (or at least `*.db*` there); remove stale `test_migrate.db*`.
- Fix B6 (`GetAccountStateAsync` → return current `_balance`/equity) and B10 (tick ask offset from
  `SymbolInfo.PipSize`) opportunistically when touching the adapters.
- Reconcile the stale claims in `FULL-HANDOVER.md` §11/§13 with a pointer to `HANDOVER-REVIEW.md`.

## Verification gates (every phase)

```powershell
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration          # 91/91 after T0
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"   # 63/63 byte-identical
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"   # after ANY cBot change
```
Plus each phase's own gate. Numbers (not adjectives) into `docs/audit/PROGRESS.md`.

## What NOT to do

- Do NOT touch the kernel/strategy/risk math; do NOT let golden drift a byte.
- Do NOT "fix" F3 (trailing cadence) as a side effect of anything — it is its own decision.
- Do NOT tune/calibrate any strategy before T2+T3(F1) are done — tuning on a spread-free simulator
  optimizes for a world that doesn't exist (see QUANT-ROADMAP §2).
- Do NOT re-run the V-phases without committing the artifacts — an unrecorded reconcile didn't happen.
