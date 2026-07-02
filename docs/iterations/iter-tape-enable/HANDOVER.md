# iter-tape-enable — Session Report & Next Plan

**Written:** 2026-07-02
**Branch:** `develop`
**Worktree:** `C:\code\Shamshir-trust`
**Theme:** Make the tape data → tape backtest → experiment loop actually runnable *without cTrader*, fix the download start/end gap, and fix front-end + experiment bugs found on the path.

---

## TL;DR

The core blocker was **data availability**: `marketdata.db` was empty and cTrader is not configured on this machine, so there was **no way to get any tape data in** — which blocks tape backtests and experiments entirely. The download form also had no start/end picker despite the backend supporting it.

This session delivered a **cTrader-free data path (file import)**, wired **start/end date range into the download form**, fixed a **front-end timer leak + silent run-start failure**, and fixed an **EF crash that blocked experiments**. All verified end-to-end by importing a generated dataset and driving a real tape backtest (80 trades, full report data).

**Gates after changes:** Build 0 err · Unit 314/0/6 · Integration 108/0 · Golden/Determinism 63/63 (byte-identical — kernel untouched).

---

## What was applied (in priority order)

### 1. Market-data **import** — the cTrader-free data path ✅ (biggest enabler)
- **New endpoint** `POST /api/data-manager/import` (`DataManagerController.cs`): multipart upload of an **NDJSON** shard (native `MarketDataShardIo` format) *or* a **CSV** export (Dukascopy/MT/broker). Bars dedupe on `(symbol, timeframe, openTime)` via the store, so re-import is idempotent.
  - NDJSON path reuses the tested `MarketDataIngester` (streaming, malformed-line-tolerant).
  - CSV path: flexible header (`time/date/timestamp/opentime[utc]`, `open/high/low/close`, optional `volume`, `symbol`, `timeframe`); `symbol`/`timeframe` fall back to form fields when absent; comma/semicolon/tab delimiters; malformed rows skipped + counted.
  - Limits raised to 500 MB (`RequestSizeLimit` / `RequestFormLimits`) so a year of M1 imports.
- **UI**: new "Import Market Data" panel in Data Manager (`data-manager.component.ts`) — file picker, optional symbol/timeframe/source, result line (bars inserted / skipped). Empty-state text updated (no longer "cTrader required").
- **Why this matters:** the owner can now bring *real* history from any source and run tape backtests + experiments with **zero cTrader dependency**. (No synthetic/seed data is shipped — this imports real files only.)

### 2. Download form: **start/end date range** ✅ (explicit ask)
- `data-manager.component.ts`: added a **"Last N days ↔ Date range"** toggle with `From`/`To` date inputs; wires `from`/`to` into the existing `POST /api/data-manager/download` body (backend `DownloadRequest.From/To` already supported it — the UI just never sent them). Added a 365-day preset. Client-side validation (from < to).
- Note: the **download itself still requires cTrader** (unchanged). This only fixes the *options*; use **Import** (#1) when cTrader isn't available.

### 3. Front-end bug fixes ✅
- **Timer leak (real):** `DataManagerComponent` started a 1.5 s job-poll `setInterval` but never cleared it on destroy → leaked interval + HTTP calls when navigating away mid-download. Now implements `OnDestroy` → `stopPoll()`.
- **Silent run-start failure (real, critical path):** `new-backtest.start()` called `store.startBacktest()` with no try/catch. On a **409** (overlap protection) or **400** (validation) the user saw *nothing* — button did nothing, no error. Now wrapped: surfaces `err.error.error` and guards a null runId before navigating.
- Audited every `setInterval/setTimeout` in the SPA: `run-monitor` and `ctrader-sessions` already clean up correctly; data-manager was the only leaker.

### 4. Experiments: **EF crash fixed** ✅ (partial — see gaps)
- `SqliteExperimentRepository.UpdateAsync` threw *"another instance with the same key value is already being tracked"* because `RunAsync` calls `CreateAsync` (tracks the entity) then `UpdateAsync` with a **fresh instance of the same Id** in the same scoped `DbContext`. Fixed by loading the tracked entity (`FindAsync`) and copying values (`CurrentValues.SetValues`) instead of attaching a duplicate.
- **Result:** `POST /api/experiments` now completes instead of 500-ing. (It still produces 0 trades over tape data — that's a deeper design gap, see below.)

### 5. Housekeeping
- Fixed malformed `.claude/settings.local.json` (leftover git merge-conflict markers) — reported by `/doctor`.

---

## Verification (end-to-end, real run)

Generated a realistic EURUSD dataset (480 H1 + 28,800 M1 bars, 2026-06-01→06-20; random walk + slow trend) and drove the whole loop against the running app on `:5137`:

| Step | Result |
|------|--------|
| `POST /import` NDJSON | 29,280 lines → 29,280 bars, 0 parse errors |
| `POST /import` CSV (same window) | 48 rows parsed, 0 inserted (correctly **deduped**) |
| `GET /inventory` | EURUSD H1 (480) + M1 (28,800), `m1Overlap:true`, spread 1.0 |
| `POST /runs` venue=**tape**, trend-breakout, H1 | run reached **completed**, 80 trades, net +61,040, 409 equity points, DD computed |
| `GET /runs/{id}/trades` | 80 trades with entry narrative ("Break of 20-bar high…") + exit reason (SL/TP) |
| `POST /experiments` | **completes** (was 500 before the fix) |
| `POST /runs/delete`, `/data-manager/delete` | cascade delete works; DB restored to clean/empty |

> The 91% win rate / huge profit is purely an artifact of the synthetic sine-trend data (trivially exploitable) — it was a **pipeline-verification** dataset, not a strategy result. All synthetic data + the test run were deleted afterward; `marketdata.db` and runs are back to empty. The generator lives at `scratchpad/gen-tape-data.mjs` if you want to re-demo.

---

## Review findings — bugs / gaps (not fixed this session)

### Experiments are architecturally half-wired (top priority for "run experiments smoothly")
Beyond the crash (now fixed), `ExperimentRunner` has interlocking gaps that make results wrong/empty:
1. **Ignores `spec.Strategies`** — the requested strategies are used only for bar-validation; the actual run executes whatever is enabled in the **base JSON config** (`ConfigLoader.Load()` from solution-root JSON), not the requested set. → 0 or wrong trades.
2. **Wrong data source** — runs `BacktestReplayAdapter` over `IBarRepository` (per-run/catalog `Bars` in `trading.db`), **not** the tape `IMarketDataStore` (`marketdata.db`). So imported/downloaded tape data is invisible to experiments.
3. **Config source divergence** — experiments use JSON config; the web backtest path uses **DB** config. Same nominal strategy can behave differently.
4. **Run records never persist** — `RunSingleAsync` writes the backtest to a **temp db** (`exp_{id}.db`), then `_backtestRunRepo.GetByIdAsync(runId)` reads the **main** db → returns null → `AddRunAsync` skipped. `Experiment.Runs` stays empty.
5. **No UI** — experiments are API/CLI only; there is no Angular feature. "Run experiments smoothly" from the app isn't reachable.
6. **No tests** — `tests/**/*xperiment*` = none.

### Known correctness gaps (pre-existing, documented)
- **C1 — short entries miss half-spread cost** in both `TapeReplayAdapter` and `BacktestReplayAdapter` (systematic optimistic bias for shorts). Deferred because the fix changes fill prices → **golden must be re-baselined**. See `docs/audit/PROGRESS.md` Tier 4.
- **F5 — commission half-at-open** — same golden-rebaseline constraint.
- `exitResolution` is computed by `TapeReplayAdapter` but comes back `null` on the run-detail DTO — likely not threaded to the summary. Low priority; verify wiring.

### Data path hygiene (Tier 1 from prior audit, still open)
- **D1** — multiple `trading.db` paths (`src/TradingEngine.Web/data`, `data/`, test temp dirs). Unify behind one env var.
- **D2** — hardcoded `EURUSD`/`H1`/`10000` defaults scattered in UI→engine config path.

---

## Next plan (prioritized, progressable)

**Tier 1 — finish "experiments smoothly" (the remaining goal), no cTrader**
1. **Wire `ExperimentRunner` to the tape venue + requested strategies.** Inject `IMarketDataStore`; in `RunSingleAsync` build a `TapeReplayAdapter` (decisionTf + M1 exit) reading the store; validate bars against the store; enable exactly `spec.Strategies` in the resolved config. This makes experiments run over imported tape data and produce real trades. *(Golden-safe: experiment runner is outside the kernel reducer.)*
2. **Persist experiment run records.** Read the run summary from the same DB the run wrote (or point the run at the main DB), so `Experiment.Runs` populates and the report resolves.
3. **Minimal Experiments UI** — list + "new experiment" form (name, symbols, TFs, strategies, date range, variants) + results table (variant composite / P(pass) / trades) + report view. Backend endpoints already exist (`/api/experiments`).
4. Add the first experiment integration test (spec → completes → run rows + scores).

**Tier 2 — data path polish**
5. Verify `download` start/end round-trips against real cTrader (owner machine); confirm the `To` date is inclusive as expected.
6. D1 DB-path unification; D2 hardcoded-defaults audit.
7. Thread `exitResolution` onto the run-detail DTO so the report shows M1 vs fallback.

**Tier 3 — golden-sensitive (needs owner sign-off + re-baseline)**
8. C1 short half-spread; F5 commission half-at-open.

---

## Tier 1 — done (branch `iter/experiments-tape-tier1`)

All four Tier 1 items above are implemented and verified. Not committed to `develop` yet — merge when reviewed.

1. **Venue + strategies** (`ExperimentRunner.cs`): swapped `IBarRepository`/`BacktestReplayAdapter` for `IMarketDataStore`/`TapeReplayAdapter` (decision TF + M1 exit, same as the web tape path). `ValidateBarsAsync` now checks the tape store. `EngineHostOptions.ActiveStrategyIds = spec.Strategies` so the run honours exactly the requested strategies instead of "whatever's enabled in the base JSON config" — plus a `ForceEnableStrategies` pass so a requested strategy runs even if its config file has `enabled: false`. `ExperimentCli.cs` updated to match (registers `IMarketDataStore`/`SqliteMarketDataStore` instead of the bar-repo trio; unused `IBacktestRunRepository`/`ITradeRepository`/`IEquityRepository` ctor params dropped from `ExperimentRunner` — they were dead fields).
2. **Run persistence**: `PersistRunAsync` now writes an `ExperimentRunEntity` (with real `ScoreJson` = the fold's `FoldScore`) right where each fold's score is computed in `RunAsync`, instead of `RunSingleAsync` gating on `_backtestRunRepo.GetByIdAsync(runId)` against a temp per-fold DB that gets deleted before that lookup could ever succeed. `Experiment.Runs` now populates.
3. **Experiments UI** (`web-ui/src/app/features/experiments/`): list (`experiment-list.component.ts`), new-experiment form (`experiment-new.component.ts` — strategy/symbol/timeframe pills, optional walk-forward folds, multi-variant with raw JSON config overrides), detail (`experiment-detail.component.ts` — variant results table re-aggregated client-side from persisted fold rows using the *same* weighting formula as `VariantScorer.ScoreVariant`, plus a report viewer). Wired into `app.routes.ts` / nav (`app.component.ts`). **Gotcha fixed along the way:** `specJson`/`scoreJson` are written with a bare `JsonSerializer.Serialize(...)` inside `ExperimentRunner` (no ASP.NET Core camelCase policy), so they come back **PascalCase** — unlike every other API response body. `experiment-detail.component.ts` has a `camelizeKeys()` helper to normalize both blobs before reading them; if either payload changes shape, keep using it rather than hand-parsing.
4. **Test**: `tests/TradingEngine.Tests.Integration/Api/ExperimentEndpointsTests.cs` — two tests hitting the real `/api/experiments` endpoints through `WebApplicationFactory<Program>`, seeding `IMarketDataStore` directly (never `IBarRepository`) so a regression back to the catalog path fails loudly instead of silently passing.

**Verified end-to-end against the real running app** (not just tests): imported 500 synthetic EURUSD H1 bars via `/api/data-manager/import`, POSTed a `trend-breakout` experiment via `/api/experiments`, confirmed `status: completed` and a populated `runs[]` with real `scoreJson`, and hit the new `/experiments` and `/experiments/new` SPA routes (200s). Cleaned up the smoke-test `docs/experiments/...` report dir and temp DB afterward — no leftovers.

**Gates**: Unit 314/0/6 · Integration 110/0 (108 baseline + 2 new) · full `dotnet build` + `npm run build` clean.

**Not done in this branch** (unchanged from the plan above): Tier 2 (#5–7) and Tier 3 (#8). Also out of scope: multi-symbol/multi-timeframe experiments still only run the first `(Symbols[0], Timeframes[0])` pair (pre-existing, not a Tier1-flagged bug); recomputing the exact variant composite client-side duplicates `VariantScorer.ScoreVariant`'s formula — if that formula changes server-side, update `summarizeVariant()` in `experiment-detail.component.ts` to match.

---

## Changed files this session

```
src/TradingEngine.Web/Api/DataManagerController.cs                 # + import endpoint (NDJSON/CSV) + CSV parser
src/TradingEngine.Infrastructure/Persistence/Repositories/
    SqliteExperimentRepository.cs                                 # UpdateAsync: fix EF double-tracking crash
web-ui/src/app/features/data-manager/data-manager.component.ts    # start/end range, import panel, OnDestroy (timer leak)
web-ui/src/app/features/runs/new-backtest/new-backtest.component.ts # start() error handling (409/400 no longer silent)
.claude/settings.local.json                                       # /doctor: removed merge-conflict markers
```
(Auto-generated `BuildInfo.g.cs` / `build-info.ts` also show as modified — build stamps, ignore.)

**Not committed** — changes are in the working tree for review. Verification data was cleaned up; DBs are empty.

## How to re-demo the tape loop (no cTrader)
```bash
# 1. build + serve
cd web-ui && npm run build && cd ..
dotnet build src/TradingEngine.Web -c Debug
(cd src/TradingEngine.Web && ASPNETCORE_URLS=http://localhost:5137 ASPNETCORE_ENVIRONMENT=Development \
   dotnet bin/Debug/net10.0/TradingEngine.Web.dll)
# 2. generate + import data, then use the UI (Data Manager → Import; New Backtest → venue=Tape)
node <scratchpad>/gen-tape-data.mjs eurusd-tape.ndjson eurusd-h1-sample.csv
curl -s -X POST http://localhost:5137/api/data-manager/import -F "file=@eurusd-tape.ndjson" -F "source=synthetic"
```
