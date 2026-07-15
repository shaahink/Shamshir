# Iter-34 — Finish the working backtest system (UI + data path)

**Branch base:** `iter/33-angular-spa` (commit iter-33b first — nothing is committed yet)
**Author:** continuation plan after the iter-33b wiring pass (see `HANDOVER-iter33b-ui-wiring.md`)
**Audience:** the implementing agent. Phases are dependency-ordered; do not advance past a red gate.

> **Read first:** `HANDOVER-iter33b-ui-wiring.md` (what was just fixed + verification gaps) and the two
> owner clarifications in its §0. The single most important one: **bars come from the cTrader→NetMQ
> stream and are stored separately; do NOT seed bars; "no bars after a real backtest" is a storage bug
> to find, not seed around; all real tests are against actual cTrader.**

## Where things stand (entry state)

- The app now **runs from one `dotnet run`** (SPA + Scalar + API, single origin). ✅
- Strategies, strategy detail, and risk profiles are **served from / evaluated against the DB**
  (strategies) or the same config the engine uses (risk profiles), and are **selectable per run**
  (strategy set, risk profile, venue). ✅
- The full run lifecycle works up to the **bar source**; a replay run with no seeded bars fails
  gracefully. The cTrader path is **unverified here** (no cTrader in the dev sandbox).
- Storage flush fixes (equity/bars) and the SignalR live-monitor fix are **implemented but unverified**
  against a real run.

---

## Phase 1 — Prove (or fix) the storage path on a real cTrader run  ← TOP PRIORITY

**Why first:** this is the owner's core complaint ("no bars / equity didn't work / journal not wiring")
and the one thing that could not be verified without cTrader. Everything visual depends on it.

1. Run a real cTrader backtest from the UI (New Backtest → Venue=cTrader, one symbol, ~1 month) and
   watch the Monitor.
2. After it completes, query the DB and assert each table populated for that `runId`:
   - `Bars` (RunId = the run) — if empty, the `BarIngested → BarPersistenceHandler → BufferedBarWriter`
     path is broken. The writer's `FlushAsync()` is now called at run end (`BacktestOrchestrator.
     FlushRunPersistenceAsync`); confirm it actually runs for the NetMQ path and that `BarIngested` is
     emitted (`TradingLoop.cs` publishes it with `runContext.RunId`).
   - `Trades`, `EquitySnapshots` (the equity drain fix — `EquityPersistenceHandler.FlushAsync`),
     `PipelineEvents` (journal).
3. In the UI, confirm: Report equity chart renders, Trades table populated, Trade Detail shows a
   **candlestick chart** (the `BarQueryService` case + `TradeDetailResponse.Timeframe` fixes), Report
   Journal lists events, Monitor was live during the run.

**Gate:** a real cTrader run leaves non-empty `Bars/Trades/EquitySnapshots/PipelineEvents` for its
`runId`, and the Report + Trade Detail + Monitor render real data. If a table is empty, fix the
producer/flush, don't seed.

### P1.x — Credential-free regression test (do alongside P1)

Add an Integration test that exercises storage **without** cTrader: build the engine host with a fake
adapter (or directly publish `BarIngested`/`EquityUpdated`/`TradeClosed` on the `IEventBus`), run the
orchestrator's flush, and assert the rows land in a temp DB. This pins the flush fixes so the storage
bug can't silently regress. **Gate:** test green in `tests/TradingEngine.Tests.Integration`.

---

## Phase 2 — Risk & money-management config in the DB (owner-requested)

**Current:** risk profiles are *selectable + applied + engine-evaluated*, but still **loaded from JSON**
(`ConfigLoader.LoadBase()`), not stored in the DB. Owner wants them **in the DB as saveable profiles**
so they can be browsed/edited/chosen for the next run, same as strategies already are.

1. New entity `RiskProfileEntity { Id, DisplayName, Json, UpdatedAtUtc }` (store the whole `RiskProfile`
   as a JSON blob — avoids a 18-column mapping) + `IRiskProfileStore` + `SqliteRiskProfileStore`.
   Same pattern for prop-firm rule sets and sizing/governor/regime if you want full coverage (start with
   risk profiles + prop-firms; they're the "money management" the owner means).
2. **EF migration** (a design-time factory does not exist — generate with
   `dotnet ef migrations add AddConfigProfiles --project src/TradingEngine.Infrastructure
   --startup-project src/TradingEngine.Web`; it applies via the existing `MigrateAsync`). The DB is
   fresh, so this is low-risk; verify `MigrateAsync` + the `EnsureCreatedAsync` in the seeders still
   co-exist.
3. Seed from JSON at startup (idempotent), like `StrategyConfigSeeder`.
4. `BacktestOrchestrator.BuildLoadedConfigFromDbAsync`: build `LoadedConfig.RiskProfiles`/`PropFirms`
   from the **DB store** instead of `LoadBase()`. The per-run override already works.
5. Point `RiskProfilesController` at the DB store. Add upsert + a "save as new profile" path so the UI
   can persist custom profiles (NEXT-STEPS E2).
6. (Optional UI) A Risk-Profiles library page mirroring Strategies (browse/edit/clone).

**Gate:** delete `config/risk-profiles/*.json` after seeding and a run still resolves the chosen profile
from the DB; a custom profile saved via the API is selectable on the next New Backtest and the Report
shows it in the effective config.

---

## Phase 3 — Reporting completeness

1. **Per-strategy funnel endpoint.** `RunFunnel.BuildFunnel` is rehomed but unused. Add
   `GET /api/runs/{id}/funnel` that loads the run's decision timeline from `PipelineEvents` (raw `Event`
   + `Reason`, not just `NormalizedKind`) and returns `FunnelRow[]`; render it on the Report.
   Also surface the per-bar **"why rejected / why no signal"** view from `BarEvaluations` ⨝ REJECTED
   events (NEXT-STEPS A3).
2. **Journal kind mapping.** Verify the Report journal filter (`SIGNAL/ORDER/FILL/CLOSE/REJECTED/BREACH/
   BAR`) matches the persisted `NormalizedKind` values from `PipelineEventWriter`; fix any mismatch and
   make `violations`/`detail` human-readable (NEXT-STEPS A4 — the `[object Object]` issue).
3. **LLM-readable report export** (NEXT-STEPS A1) — markdown/JSON download stitching RunStats + journal
   narrative + funnel.

**Gate:** Report shows a per-strategy funnel whose `Closes` sum equals the trade count; the per-bar view
renders; journal kinds all filter correctly.

---

## Phase 4 — Multi-symbol / multi-timeframe fidelity

- The **cTrader path already streams multiple symbols/timeframes** (CLI `--SymbolString`/`--Periods`);
  confirm a multi-symbol cTrader run evaluates all of them.
- The **run record stores a single `Symbol`/`Period`** → multi-symbol runs display only the primary.
  Capture the full run plan (symbols × timeframes × strategy ids × risk profile × venue) — reuse the
  spare `BacktestRunEntity.StrategyParamsJson` column (currently `"{}"`) — and surface it on the Report
  and Runs list.
- The **replay adapter (`BacktestReplayAdapter`) is single-symbol/TF.** Either implement a multi-symbol
  replay (merge per-symbol bar streams by time, track per-symbol open book + last close + costs), or
  explicitly document replay as single-symbol. Lower priority than the cTrader path.

**Gate:** a multi-symbol cTrader run shows all symbols in the Report; the run plan round-trips.

---

## Phase 5 — Live monitor & dashboard

1. Confirm the Monitor receives `RunProgress`/`RunCompleted` during a live run (the method-name fix) and
   that counters/equity sparkline/journal update without flicker.
2. The **Dashboard (`/`)** is a one-shot fetch (governor + global equity). Optionally subscribe it to a
   broadcast channel so it's a live engine view, or leave it as a summary (owner's call — it's not the
   primary "live" surface; the per-run Monitor is).

**Gate:** Monitor is demonstrably live on a real run.

---

## Phase 6 — Perf, cleanup, commit

- Replace the `await Task.Delay(5_000)` settle in `BacktestOrchestrator.RunEngineReplayAsync` with a
  deterministic drain (the engine-thread completion), and shorten the 5s equity flush interval.
- Decide whether `src/TradingEngine.Web/wwwroot/**` (Angular build output) is committed or gitignored
  with a build step. (An MSBuild target that runs `npm run build` when `wwwroot/index.html` is missing
  makes a fresh clone "just work"; gate it so `dotnet test` doesn't trigger npm.)
- **Commit the branch** — iter-33b changes are uncommitted.

---

## Owner decisions to confirm (don't block Phase 1)

1. **Config storage scope (P2):** risk profiles only, or also prop-firms + sizing + governor + regime in
   the DB? (Default: risk profiles + prop-firms first.)
2. **Replay multi-symbol (P4):** build it, or document replay as single-symbol since cTrader is the real
   path? (Default: document; build later.)
3. **wwwroot in git (P6):** commit the build output for "clone-and-run", or gitignore + MSBuild build
   step? (Default: gitignore + build step.)
4. **Dashboard (P5):** make it live, or keep as a summary? (Default: keep as summary.)
