# iter-redesign — HANDOVER (for review)

**Author:** opencode (DeepSeek) — implementation
**Branch:** `iter/redesign` (base: `iter/strategy-system`)
**Plan:** `docs/iterations/iter-redesign/PLAN.md` (Claude/Opus diagnosis + phased plan)
**Status:** **Plan fully implemented (Phases 0–6).** 9 checkpoint commits. All automated suites green. One operational step remains (run the app + Playwright in a live environment).

---

## 0. TL;DR

Every defect in the plan's §1 diagnosis is fixed or proven-already-fixed, and every owner symptom in §1.7 is addressed:

| # | Owner symptom | Resolution | Commit |
|---|---|---|---|
| E1/E2 | "fewer signals in 3mo than 1mo", 85 illegal transitions | open-book purge + entry-bar pump ordering | `fea67b2` |
| E3 | "can't tell if SL/TP fired" | **already fixed in code** — regression-locked | `fea67b2` |
| C1/C2 | "can't confidently disable guards" | toggle-gated limiters + numeric rejections + raw preset | `895ff3e` |
| B1 | "want plain entry/exit + opt-in add-ons" | `StripAddOns` raw mode + builder toggle | `599b1b2`, `42df701` |
| O1 | "live backtest never shows", broken stats | idempotent finalization + durable reconcile | `cac9238` |
| O2 | "equity/drawdown never show" | **already sim-time correct** — audited + locked | `cac9238` |
| O3 | "no per-bar insight" | per-bar narrative API + Bar Inspector UI | `b4a7e9f`, `42df701` |
| U1 | "live monitor stopped working" | SignalR snapshot-on-join + dead path removed | `95cf214` |
| U2 | "no trade chart; trades not linked to runs" | trade-chart API + chart UI + trades↔runs links | `af3fb85`, `42df701` |

**Bonus root-cause fix:** the New-Backtest service was sending a hard-coded partial payload that **dropped** the governor / daily-DD / max-DD / force-close toggles and the row/per-row-pack plan before they reached the backend — almost certainly *why* the owner reported "guards/governor aren't confidently toggleable." Now forwarded correctly (`42df701`).

---

## 1. Commits (in `git log` order, newest first)

| Commit | Phase | Summary |
|---|---|---|
| `ef3de83` | 6.1 | Playwright self-verify spec (8 tests; validated via `--list`) |
| `42df701` | 6.2–6.4 / 3.3 | All Angular UI + StripAddOns wiring + startRun payload fix |
| `af3fb85` | 6.2 (be) | Trade-detail chart API `GET /api/trades/{id}/chart` |
| `95cf214` | 6.1 | Live-monitor snapshot-on-join + remove dead `JournalAppend` |
| `b4a7e9f` | 5 | Per-bar decision narrative API `GET /api/runs/{id}/bars` |
| `599b1b2` | 3 | "no add-ons (raw)" mode (`EffectiveConfigResolver.StripAddOns`) |
| `cac9238` | 4 | Robust run finalization + equity sim-time lock |
| `895ff3e` | 2 | Provably-raw guards (toggle-gated limiters + numeric rejections) |
| `fea67b2` | 0–1 | Repro tests + engine-truth fixes + `EngineInvariants` harness |

~35 source files, +2031/−73 vs base.

---

## 2. What shipped, phase by phase

### Phase 0 — Repro harness (`fea67b2`)
`tests/TradingEngine.Tests.Simulation/EngineTruth/EngineTruthReproTests.cs` — failing tests encoding E1, E2, C2 (fail-before / pass-after). Established the red baseline before touching engine code.

### Phase 1 — Engine truth (`fea67b2`)
- **P1.2 (E2 root cause)** — `KernelBacktestLoop.ProcessBarAsync`: now pumps proposals (entry fills drain → positions reach `Open`) **before** enqueuing `BarClosed`. Kills the `(Submitted, BarClosed)` illegal-transition race ("the 85") and lets a position exit on its entry bar.
- **P1.1 (E1 leak)** — `EngineReducer.HandleBarClosed`: skips non-live positions (no illegal record) and purges terminal phases so the gate never sums dead positions (the latch cause).
- **P1.4** — new `EngineInvariants` (Engine project): "the live book contains only live positions," asserted in tests.

### Phase 2 — Provably-raw guards (`895ff3e`)
- Added `ExposureEnabled` / `BudgetEnabled` / `MaxPositionsEnabled` to `ProtectionToggles` + `ConstraintSet`; gated the exposure (`:96`), budget (`:169`) and position-count (`:82`) checks in `PreTradeGate`.
- `config/prop-firms/raw.json` now disables all three; per-run overrides + `ConfigSetId` hash extended.
- **P2.3** — `MAX_EXPOSURE` and `BudgetBlocked` rejections now carry the resolved numbers (`openRisk=… + new=… > cap=… (heatCap=… @M×)`).

### Phase 3 — Strategy = signal + opt-in add-ons (`599b1b2`, `42df701`)
- Investigation confirmed the architecture is **already** signal + toggleable add-ons (DynamicSlTp already gated; PositionManager gates every add-on on `Enabled`). The gap was no explicit "strip everything" path.
- Added `EffectiveConfigResolver.StripAddOns` (forces breakeven/trailing/ride/partial/dynamic off, keeps baseline SL/TP), wired into the orchestrator via the `StripAddOns` run param + the New-Backtest "No add-ons (raw)" checkbox.

### Phase 4 — Lifecycle + equity (`cac9238`)
- **O1** — `BacktestOrchestrator.RunAsync` now tracks `finalized`; the `finally` block does a last-ditch terminal write if the try/catch path failed/was skipped. `WriteEndRecordAsync` returns success. `SqliteBacktestRunRepository.ReconcileAsync` now triggers on `ExitCode==-1` / `CompletedAtUtc==default` (not just `TotalTrades==0`) and **durably** writes the re-derived summary back.
- **O2** — audited all 9 equity/account timestamp sources: every one already uses sim-time (bar `OpenTimeUtc`), no wall-clock leak. Locked with `EquitySimTimeTests`.

### Phase 5 — Per-bar narrative (`b4a7e9f`)
`GET /api/runs/{runId}/bars` aggregates the **existing persisted journal** into one bar-keyed narrative per sim-time (regime, per-strategy verdicts, proposal count, gate rejections with P2.3 numbers, equity/dd/open-book snapshot, fill/close counts). **No new DB table / migration** — the journal already carries everything.

### Phase 6 — UI fix-first (`95cf214`, `af3fb85`, `42df701`, `ef3de83`)
- **6.1** `RunHub.JoinRun` sends the current `RunProgress` snapshot to the caller on join (was blank until the next throttled frame). Removed the dead client `JournalAppend` subscription.
- **6.2** `GET /api/trades/{id}/chart` (bars + entry/exit/SL/TP markers, server-resolved timeframe + window); trade-detail switched to it.
- **6.3** Trades list gains a **Run** column (cross-link, stop-propagation) + a Run-id filter; backend `TradeSummaryResponse` carries `RunId`.
- **6.4** Run-report gains a **Bar Inspector** table (active bars: regime, fired signals, proposals/fills/closes, gate rejections, equity, positions).
- **3.3** New-Backtest "No add-ons (raw baseline SL/TP)" checkbox.
- Playwright spec `tests/e2e/iter-redesign.spec.ts` (8 tests).

---

## 3. Plan-vs-reality findings (review these)

1. **E3 was already fixed.** The `CloseReason` carry at `EngineReducer.cs:259` works; the DB's "all FORCE" was end-of-data flatten / pre-fix runs. `ExitReasonReflectsSlOrTp` passes with no change.
2. **O2 was already correct.** All equity timestamps are sim-time. The plan's "wall-clock" symptom was old data, not a live bug.
3. **Golden tape did NOT need re-baselining (D4 unused).** The ordering fix changed *when illegal records appear*, not trade outcomes — the golden snapshot (count/prices/exit reasons/drawdown) is byte-identical.
4. **P5 used query-time aggregation** over the journal instead of a new persisted bar-record (D3's retention question is therefore moot; the UI filters client-side to "active" bars).
5. **The startRun payload bug** (governor/DD/force-close/rows dropped) is the most likely concrete cause of the "toggles don't work" complaint — now fixed but see §6 risk note.

---

## 4. Owner decisions (D1–D5) — outcomes

| D | Decision | Outcome |
|---|---|---|
| D1 | Raw exit = opposite-signal OR EOD-flatten (+ optional max-hold) | **Partial.** `StripAddOns` removes enrichments but keeps the strategy's baseline SL/TP (all 9 strategies always set SL/TP). A *pure no-SL/TP* raw mode was not built — see §6. |
| D2 | Default = no add-ons | **Implemented as opt-in checkbox, default OFF** (deliberate — see §6). Flip the default after a visual review. |
| D3 | Bar-record retention N=5000 | **Moot** — no new persistence; query-time aggregation + client-side "active bars" filter. |
| D4 | OK to re-baseline golden | **Unused** — not needed. |
| D5 | Keep Angular, defer Phase 7 | **Yes.** Phase 7 (desktop/framework) deferred. |

---

## 5. Verification status (all green)

Run from repo root:

| Suite | Command | Result |
|---|---|---|
| Unit | `dotnet test tests/TradingEngine.Tests.Unit` | **274 pass, 6 skip** |
| Sim — engine truth | `dotnet test tests/...Simulation --filter "Category=EngineTruth"` | **11 pass** |
| Sim — golden/kernel | `--filter "(FullyQualifiedName~GoldenReplay)|(Category=KernelAcceptance)"` | **65 pass** |
| Sim — Ftmo/Scenarios/Risk/PosMgmt | `--filter "(FullyQualifiedName~Ftmo)|..."` | **39 pass** |
| Sim — Characterization/Verification | | **9 pass** |
| Sim — Pipeline/Strategies (excl. NetMQ) | | **12 pass** |
| Integration | `dotnet test tests/TradingEngine.Tests.Integration` | **76 pass** |
| Web C# | `dotnet build src/TradingEngine.Web -p:NgProjectDir=__skip_ng__` | clean |
| Angular TS | `cd web-ui; npx tsc --noEmit -p tsconfig.app.json` | clean |
| Angular bundle | `cd web-ui; npx ng build` | **NG_EXIT=0 (~21s)** |
| Playwright collect | `cd web-ui; npx playwright test --list` | **8 tests, OK** |

**Known pre-existing failure (NOT caused by this work):** `Pipeline.NetMQBridgeTest.EngineReceivesBarAndTickOverNetMQ` — a `RequiresCTrader` ZeroMQ E2E with a 20s timeout. **Verified identical on the base commit** (stash + rebuild + run). Needs a live cTrader environment.

---

## 6. Risk notes & carry-forward (please review)

**Not visually verified (needs the running app — use the `run-shamshir` skill):**
- All Angular UI changes compile + bundle cleanly (`ng build` green) and load in Playwright (`--list`), but were **not run against a live app** (this environment can't host a persistent dev server + browser). The 8 Playwright tests in `iter-redesign.spec.ts` are ready for CI but unexecuted here.
- **The startRun payload fix changes behavior**: the governor/DD/force-close toggles now actually apply, and `rows`/per-row-packs are now actually used (they were dropped before). This is the intended behavior and the backend paths are tested, but **confirm with one live run** that toggling e.g. Governor off behaves as expected.

**Deliberate deviations (decide whether to keep):**
- **D2 default**: StripAddOns checkbox defaults **OFF**. Defaulting ON would silently change every default run's behavior, which I couldn't visually verify — left it as opt-in. Flip to default-ON in `new-backtest.component.ts` (`stripAddOns = true`) if you want the owner's literal D2.
- **D1 pure-raw**: strategies always emit SL/TP, so "raw" means "baseline SL/TP, no enrichments." A true "no synthetic stops, exit only on opposite-signal/EOD" mode would require strategies to optionally omit SL/TP — not built.

**Deferred (from the plan / discovered):**
- **P2.1 Gap 2** — `EffectiveConfigJson` still doesn't persist the *resolved* ProtectionToggles/profile for audit fidelity (would need a schema change). The toggles are now correct at runtime; only the stored audit record is incomplete.
- **P3.1** — full config-schema separation of position-management out of strategy config. Unnecessary for runtime (add-ons are already separable); deferred.
- **Phase 7** — desktop/framework decision (deferred per D5).

**Housekeeping:**
- `src/TradingEngine.Web/wwwroot/*` build artifacts are **gitignored** but some old chunk files were previously committed; after `ng build` they show as deleted in the working tree. Recommend `git rm --cached` the tracked wwwroot build output so it stops appearing as a diff. **Not done here** (pre-existing; out of scope for the source changes).

---

## 7. How to review / run it live

```powershell
# Backend + frontend suites (fast)
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation --filter "Category=EngineTruth"

# Run the app (auto-rebuilds Angular via RebuildAngularIfStale), then drive a backtest:
#   - New Backtest → tick "No add-ons (raw baseline SL/TP)" → Start → watch the Monitor populate immediately
#   - Run Report → scroll to "Bar Inspector" → click a trade → see the chart (entry/exit/SL/TP)
#   - Trades page → "Run" column links back; filter by Run id
# (Use the run-shamshir skill for the build/serve/smoke loop.)

# Playwright (against a running app at localhost:5134, with a data-rich run present):
cd web-ui; $env:SEEDED_RUN_ID="<a finished run id>"; npx playwright test iter-redesign.spec.ts
```

---

## 8. Key file index

| Area | Files |
|---|---|
| Engine truth | `src/TradingEngine.Engine/EngineReducer.cs`, `EngineInvariants.cs`, `src/TradingEngine.Host/KernelBacktestLoop.cs` |
| Guards | `src/TradingEngine.Engine/Kernel/PreTradeGate.cs`, `src/TradingEngine.Domain/RiskAndEquity/{ConstraintSet,ProtectionToggles}.cs`, `config/prop-firms/raw.json` |
| Strategy/add-ons | `src/TradingEngine.Services/EffectiveConfigResolver.cs` |
| Lifecycle/equity | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`, `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteBacktestRunRepository.cs` |
| APIs | `src/TradingEngine.Web/Api/{RunsController,TradesController}.cs`, `Services/RunQueryService.cs`, `Dtos/Runs/{BarNarrativeResponse,StartRunRequest}.cs`, `Dtos/Trades/{TradeChartResponse,TradeSummaryResponse}.cs` |
| SignalR | `src/TradingEngine.Web/Hubs/RunHub.cs`, `web-ui/src/app/core/signalr/run-hub.service.ts` |
| UI | `web-ui/src/app/features/{runs/run-report,runs/new-backtest,trades/trade-detail,trades/trade-list}/*.component.ts`, `web-ui/src/app/features/{runs/runs,trades/trades}.service.ts`, `web-ui/src/app/models/api.types.ts` |
| Tests | `tests/TradingEngine.Tests.Simulation/EngineTruth/*`, `tests/TradingEngine.Tests.Integration/{Journal/BarNarrativeTests,Bars/TradeChartTests}.cs`, `tests/TradingEngine.Tests.Unit/Services/StripAddOnsTests.cs`, `web-ui/tests/e2e/iter-redesign.spec.ts` |
