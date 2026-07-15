# Iter-33b — UI Wiring & Data-Path Completion — HANDOVER

**Branch:** `iter/33-angular-spa`
**Date:** 2026-06-19
**Predecessor:** `HANDOVER.md` (iter-33 Angular SPA, feature-complete-but-buggy)
**State:** Backend + serving fixes **verified** headlessly; cTrader-dependent paths **unverified** (no cTrader in this environment — see §4). **Nothing committed.**

> This session was a fix/wiring pass over the iter-33 Angular SPA, driven by the owner's bug list and
> two clarifications made mid-session (see §0). It did **not** restructure the backend into vertical
> slices (Track B) — that remains future work. It made the existing app actually run end-to-end.

---

## 0. Owner clarifications that shaped this pass

1. **"cTrader sends bars via NetMQ, we consume/eval/process. CSV/replay is secondary, never used
   thoroughly."** → The real data path is cTrader→NetMQ. Replay exists for credential-free smoke tests.
2. **"Do NOT seed bars. Bars come from the stream; we store them separately. No bars after a real
   backtest = a storage bug, not missing seed data. For now all tests are against actual cTrader."**
   → I reverted an initial bar-catalog-seeder idea. The remaining bar work is to **verify/repair the
   storage pipeline** on a real cTrader run, not to seed.

Consequence: I could verify everything **up to the bar source** (serving, API, DB config, run
lifecycle, strategy/risk wiring) but **not** a run that actually produces trades — that needs cTrader.

---

## 1. What was fixed (with verification status)

| # | Area | Change | Verified? |
|---|------|--------|-----------|
| 1 | **Run-the-app / serving** | ASP.NET now serves the built Angular SPA + Scalar + API from **one origin**. `UseDefaultFiles`/`UseStaticFiles`/`MapFallbackToFile("index.html")` in `MiddlewarePipeline`; Angular `outputPath` flattened into `wwwroot/` (`{base, browser:""}`); `Dev:NgServe` defaulted **off**; launch profiles open `/` (added an opt-in `ng-serve` profile). | ✅ `/`, `/runs/abc` (SPA fallback), JS assets, `/scalar/v1`, `/api/*` all 200 from a single `dotnet run`. |
| 2 | **Strategies page + picker empty** | `StrategiesController` rewritten to read the **DB** (`IStrategyConfigStore`) instead of the lazy in-memory `StrategyRegistry` cache (which is only populated during a run). List + detail now return full config (timeframe, symbols, riskProfileId, parameters, position-management, order-entry, regime, reentry). Stats aggregated from the Trades table. Enable/disable/config **persist to DB**. | ✅ `/api/strategies` returns 9 strategies; `/api/strategies/{id}` returns full config JSON. |
| 3 | **Engine evaluates JSON, not DB** | `BacktestOrchestrator.BuildLoadedConfigFromDbAsync` builds the engine's `LoadedConfig` from the **DB** strategy store (params/symbols/TF/PM/OE/regime), passed as `EngineHostOptions.PreloadedConfig` in **both** run paths. So what the UI shows/edits is what the engine evaluates. | ✅ A run instantiated strategies from DB with no DI errors (failed only on "no bars", as expected). |
| 4 | **Risk profile / venue not selectable** | `StartRunRequest` gained `RiskProfileId` + `Venue`; `RunsController` threads them into `CustomParams`; orchestrator picks venue per-run (`ctrader`/`replay`, else config default) and applies the chosen risk profile to every strategy in `BuildLoadedConfigFromDbAsync`. New `RiskProfilesController` (`/api/risk-profiles`) feeds the picker from the **same** profiles the engine evaluates. | ✅ `/api/risk-profiles` returns standard/conservative/aggressive; run accepted both params. |
| 5 | **Live monitor dead (SignalR)** | **Root cause:** backend sent `onProgress`/`onDone`; Angular listened for `RunProgress`/`RunCompleted`. Renamed broadcaster methods to `RunProgress`/`RunCompleted`. Monitor now derives its journal from the embedded `recentJournal` (DecisionRecordView: `event`→kind, `detailJson`→detail) — there is **no** separate `JournalAppend` message. | ⚠️ Static only — needs a live run (cTrader) to confirm frames arrive. |
| 6 | **Equity chart empty (report + live)** | **Root cause:** `EquityPersistenceHandler` flushed every 5s and on `DisposeAsync` it **cancelled without a final drain** → a short run lost its buffered equity. Added `FlushAsync()` (drains+persists, grouped by run); called from the orchestrator at run end and from `DisposeAsync`. | ⚠️ Static only — needs a run with trades. |
| 7 | **Bar tail loss / host disposal** | Orchestrator now `FlushRunPersistenceAsync` (BufferedBarWriter + EquityPersistenceHandler) **before** stop, and `DisposeHostAsync` (async dispose so `IAsyncDisposable` engine singletons flush properly) instead of sync `Dispose()`. | ⚠️ Static only. |
| 8 | **Trade detail "no bars"** | `BarQueryService` made **case-insensitive** (`h1` vs stored `H1`) and de-dups by timestamp (lightweight-charts rejects duplicate times; catalog+run bars can overlap). `TradeDetailResponse` gained `Timeframe` (resolved from the run's period); `trade-detail.component` queries that TF instead of hardcoded `h1`. | ✅ endpoint shapes; chart needs a real run with bars. |
| 9 | **New-Backtest UX** | Risk-profile `<select>` now loads from `/api/risk-profiles` (was hardcoded standard/conservative/aggressive); added a **Data Venue** selector (Default/cTrader/Replay); strategy picker already reads `/api/strategies` (now populated). Multi symbol/TF checkboxes already existed and are passed through. | ✅ build + endpoints. |
| 10 | **Funnel logic lost in Razor scrap** | `ReportModel.BuildFunnel` (deleted with Razor) rehomed to `Web/Services/RunFunnel.cs` (+ `FunnelRow`). Fixed the Integration project (it referenced `TradingEngine.Web.Pages`). | ✅ compiles; unit logic test re-pointed. |
| 11 | **Obsolete Razor smoke tests** | `WebSmokeTests.IndexPage_HasLayout` / `NewBacktestPage_*` asserted Razor server-HTML (`sidebar`, `Data source`). Re-pointed at the SPA shell (`app-root`). | ⚠️ edited; **final re-run was interrupted — re-run to confirm** (see §4). |

---

## 2. Files touched

```
src/TradingEngine.Web/
  Configuration/MiddlewarePipeline.cs      # static files + SPA fallback
  Api/StrategiesController.cs              # DB-backed (rewrite)
  Api/RiskProfilesController.cs            # NEW — /api/risk-profiles
  Api/RunsController.cs                    # riskProfileId + venue → CustomParams
  Api/TradesController.cs                  # TradeDetailResponse.Timeframe from run period
  Dtos/Runs/StartRunRequest.cs            # + RiskProfileId, Venue
  Dtos/Trades/TradeDetailResponse.cs      # + Timeframe
  Services/BacktestOrchestrator.cs        # BuildLoadedConfigFromDbAsync, venue switch, flush+async-dispose
  Services/BarQueryService.cs             # case-insensitive + dedupe
  Services/RunProgressBroadcaster.cs      # onProgress/onDone → RunProgress/RunCompleted
  Services/RunFunnel.cs                    # NEW — rehomed BuildFunnel
  Properties/launchSettings.json          # open "/", add ng-serve profile
  appsettings.Development.json            # Dev:NgServe=false
src/TradingEngine.Infrastructure/
  Persistence/EquityPersistenceHandler.cs # FlushAsync() final drain
web-ui/
  angular.json                            # outputPath flatten {base, browser:""}
  src/app/models/api.types.ts             # + venue, RiskProfile
  src/app/features/runs/runs.service.ts   # send riskProfileId + venue
  src/app/features/runs/new-backtest/...  # risk-profile + venue selectors
  src/app/features/runs/run-monitor/...   # journal from recentJournal
  src/app/features/trades/trade-detail/...# query run TF
tests/TradingEngine.Tests.Integration/
  Iter27/Iter27FixTests.cs                # ReportModel→RunFunnel
  WebSmokeTests.cs                        # Razor asserts → SPA shell
```

`src/TradingEngine.Web/wwwroot/**` is the Angular build output (regenerated by `npm run build`).

---

## 3. How to run (new single-origin model)

```powershell
# 1. Build the SPA (once, and after any web-ui change):
cd web-ui; npm run build          # → emits into src/TradingEngine.Web/wwwroot

# 2. Run the API + SPA together (single process):
dotnet run --project src/TradingEngine.Web --launch-profile https
#   SPA:    http://localhost:5134/   (or https://localhost:7108/)
#   Scalar: http://localhost:5134/scalar/v1
#   API:    http://localhost:5134/api/*

# For Angular HMR instead: run the `ng-serve` launch profile (spawns ng serve on :4200, proxied),
# or run `npm start` in web-ui separately.
```

`Dev:NgServe` is **off** by default now — the app no longer tries to spawn `ng serve` (the source of the
"only the API runs / no Scalar" problem). It serves the pre-built SPA from `wwwroot`.

---

## 4. Verification performed & gaps

**Verified (headless):**
- `dotnet build` Web — 0 errors.
- `dotnet test` Unit (207 pass / 4 skip) and Architecture (3 pass).
- Integration **compiles** and 33 tests passed; the 2 obsolete Razor smoke tests were then edited to
  assert the SPA shell — **the confirming re-run was interrupted**. ⚠️ **Re-run Integration first thing.**
- Live Web run (replay venue): `/`, SPA fallback, assets, `/scalar/v1`, `/api/strategies`,
  `/api/strategies/{id}`, `/api/risk-profiles`, `/api/governor/state`, `/api/equity`, `/api/runs` all 200.
- Run lifecycle: `POST /api/runs` → engine started → strategies built from DB → venue=replay →
  graceful "No bars found" (expected) → run record persisted (status=failed, exitCode=1).

**NOT verified (no cTrader available here; owner: "all tests are against actual cTrader"):**
- A real run producing **trades / journal / equity / bars**. The storage flush fixes (#6, #7) and the
  SignalR live monitor (#5) are **static-only** and must be confirmed against a real cTrader backtest.
- Whether the owner's "no bars after a real backtest" is now resolved — see PLAN P1.

---

## 5. Known remaining gaps → see `PLAN-iter34-ui-completion.md`

Short list (full detail + gates in the plan):
1. **Bar/equity/journal storage verified on a real cTrader run** (owner's core complaint). Flush fixes
   here are unverified.
2. **Risk/money config in DB** — profiles are now *selectable + applied + engine-evaluated*, but still
   *loaded from JSON*, not stored in a DB table. Owner wants them in the DB as saveable profiles
   (+ sizing/governor/prop-firms).
3. **Multi-symbol/TF replay** (cTrader path already multi; replay adapter is single).
4. **Faithful run-plan capture/display** (run row stores one Symbol/Period).
5. **Per-strategy funnel + per-bar "why-rejected" endpoint** (`RunFunnel` rehomed but not wired to an API).
6. **Journal kind mapping** report-side (NormalizedKind vs the UI's SIGNAL/ORDER/FILL/CLOSE/… filter).
7. **Credential-free storage test** to guard #1 without cTrader.
8. **Perf**: 5s `Task.Delay` in replay; 5s equity flush interval.
9. **Commit** the branch (nothing is committed).
