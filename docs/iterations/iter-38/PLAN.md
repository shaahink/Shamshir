# Iter-38 — Strategy Add-ons + Auto-tune + Regime Toggle, `CreatedOn` Audit, and the iter-37 Reality/Observability Fixes

**Branch (suggested):** `iter/38-addons` (cut from `iter/37-frontend-finish`)
**Audience:** the OpenCode/DeepSeek implementation agent. Phased, failing-test-first, machine-checkable gates.
**Companion:** `docs/OPEN-ISSUES.md` (T1–T12 + K-GAP carry-forward + M/H IDs referenced inline).

> **Why this iteration.** Backtesting today is hard because (a) every strategy hard-codes static SL/TP/BE/trail
> numbers that must be hand-tuned per symbol/timeframe, (b) "add-on" behaviours (breakeven, trailing, partial,
> ride) are welded inside each strategy instead of being reusable and individually toggleable, (c) regime
> detection can't be turned off, (d) nothing carries a `CreatedOn`, and (e) the default dev backtest still runs
> through the wall-clock-buggy cTrader path (root of T1/T2/T6/T7/T11/T12). This iteration makes add-ons a
> first-class, reusable, **auto-tuned** concept, journals every add-on decision, stamps `CreatedOn` everywhere,
> and lands the reality/observability fixes so the add-ons can actually be measured.

---

## Build/test round (2026-06-21, after the scaffold landed)

| Suite | Result | Note |
|-------|--------|------|
| Build | 0 errors | Scaffold compiles (`TreatWarningsAsErrors=true`). |
| Unit | 228 / 0 / 5-skip | Green. |
| Simulation (non-cTrader) | 97 / 0 | Green. |
| Integration | 48 / 0 | Green **after the P0-A1 EF regen** (now done — `InitialCreate` regenerated with `AddOnPacks` + `CreatedAtUtc`). |
| **Architecture** | **1 FAILED** | **B0 (new finding)** — `EnginePurityTests.Engine_has_no_ILogger_no_DateTimeNow`: `ResetClock.Crossed` uses `System.DateTime` in `TradingEngine.Engine`. **Pre-existing** (iter-37 `d042a78`); iter-37's signoff gate list omitted Architecture so it went unnoticed. |

> **B0 — repair the Architecture purity gate.** `TradingEngine.Engine` must not reference `DateTime` (arch rule).
> `ResetClock.Crossed(DateTime?, DateTime, ResetConfig)` + `ResetConfig`/`TimeOnly` violate it. **Fix options:**
> (a) move `ResetClock` out of `Engine` into `Host`/`Services` (it's already called from `KernelBacktestLoop` in
> Host); or (b) re-express its inputs as primitive ticks (`long`) and keep the zone logic in the caller. (a) is
> the smaller change. Add Architecture to the iteration's standing gate list so this can't regress silently again.

---

## 0. Owner decisions — LOCKED

| # | Decision |
|---|----------|
| **D1 — Add-on model** | **Packs + per-strategy default.** A strategy carries its own default add-ons, *may carry none*, and a run can override with a named, reusable **Add-on Pack** (`usePack=<id>`, global or per-strategy). **Pack wins** for that run. Packs are reusable across any strategy. |
| **D2 — Auto-tune** | **Auto-tuned, editable.** Enabling an add-on computes its numeric values from **timeframe × symbol × recent volatility (ATR/spread)** via a pure `AddOnAutoTuner`. Values are **resolved once at entry** (deterministic, journaled), shown pre-filled in the UI, and **overridable** (mode = `Auto` \| `Custom`). |
| **D3 — Regime toggle** | **Per-strategy `enabled` flag + run-level master disable.** When off, regime is bypassed (treated as allow-all) and the per-regime filter is ignored. |
| **D4 — Mandatory baseline** | **Every strategy MUST resolve a valid SL and TP.** Add-ons are *optional enrichments only*; they never remove the baseline. A strategy with no add-ons trades baseline-SL/TP-only. |
| **D5 — `CreatedOn`** | **Every persisted entity gets `CreatedAtUtc`** (+ standardized `UpdatedAtUtc`), auto-stamped by an EF interceptor, surfaced in the UI. |
| **D6 — Default venue** | **Flip the default dev backtest venue to `replay`** (credential-free). cTrader becomes explicit opt-in. Kills the symptom of T1/T2/T6/T7/T11/T12 for default runs. |

---

## 1. Concept model (the shared vocabulary)

```
Strategy (entry signal logic)
  └─ BASELINE  (mandatory)         stopLoss + takeProfit        ← always resolves (D4)
  └─ ADD-ONS   (optional enrichments, each independently toggleable)
        • DynamicSlTp   – auto-tuned ATR SL/TP that REPLACES the baseline numbers when on
        • Breakeven     – arm SL to entry(+offset) at trigger R
        • Trailing      – ratchet SL (atr / structure / step / steppedR)
        • PartialTp     – close a fraction at trigger R          ← currently DECLARED-BUT-DEAD, wire it
        • Ride          – relax trailing while ADX > floor       ← currently DECLARED-BUT-DEAD, wire it
        • RegimeDetection – on/off (D3)

Add-on Pack (reusable named bundle of the above, stored once, attachable by id)

Resolution order for a run (handled in EffectiveConfigResolver):
  strategy default add-ons  →  (if run/strategy names a pack) PACK REPLACES them  →  per-run override fields
  Each enabled add-on with mode=Auto  →  AddOnAutoTuner(tf, symbol, volAtEntry)  →  concrete values (journaled)
```

Each add-on block gains two universal fields: `enabled: bool` and `mode: "Auto" | "Custom"`. `Auto` ⇒ the
tuner fills the numbers at entry; `Custom` ⇒ the stored/edited numbers are used verbatim.

---

## 2. Streams & phases

Phases are ordered so each lands green before the next. **P0 first** (it unblocks measurement + bundles the one
EF migration). Streams **A/R/P/U/J** are the new feature; stream **B** is the iter-37 bug backlog (can run in
parallel by a second agent, but P0-B1 is a prerequisite for trustworthy add-on backtests).

### Stream P0 — Prerequisites & single EF migration

| Phase | Goal | Key changes | Gate |
|-------|------|-------------|------|
| **P0-A1** | One disposable EF regen for the whole iteration (matches iter-36/37 convention) | After the schema work in A2/T1/P2 is designed, regenerate a single `InitialCreate` (dev DBs deleted on boot). Do this **once**, last, to avoid migration churn — but reserve the slot here. | `dotnet ef migrations` clean; app boots + reseeds |
| **P0-B1** | **D6** flip default venue to replay | `appsettings.Development.json:23` `UseForBacktest=false` (or remove); `BacktestOrchestrator.cs:296-301` keep `replay` as the `_ =>` fallback; New-Backtest UI default label "Default (replay)". cTrader only when venue=`ctrader` explicitly. | A no-venue backtest runs through `BacktestReplayAdapter` (assert in `BacktestStartGuardTests`/a new `VenueRoutingTests`) |

### Stream T — `CreatedOn` audit (cross-cutting, D5)

| Phase | Goal | Key changes | Gate |
|-------|------|-------------|------|
| **T1** | Audit fields on every entity | Add `CreatedAtUtc` (+ `UpdatedAtUtc` where missing) to **all** entities in `src/TradingEngine.Infrastructure/Persistence/Entities/*` (StrategyConfig, RiskProfile, PropFirmRuleSet, GovernorOptions, TradeResult, BacktestRun, Dataset, ConfigSet, EquitySnapshot, Bar, JournalEntry, Order, Position, Experiment, ExperimentRun, + new AddOnPack). Define `IAuditableEntity { DateTime CreatedAtUtc; DateTime UpdatedAtUtc; }`. | All entities implement `IAuditableEntity`; arch test asserts it |
| **T2** | Auto-stamp | EF `SaveChanges`/`SaveChangesAsync` interceptor in `TradingDbContext`: set `CreatedAtUtc` on Added (if default), `UpdatedAtUtc` always. Use an injected clock so it's testable/deterministic. | `AuditStampInterceptorTests`: insert → CreatedAtUtc set; update → CreatedAtUtc stable, UpdatedAtUtc moves |
| **T3** | Surface in UI | Add "Created" to list/detail views (strategies, risk-profiles, runs, packs, trades) using the existing `DatePipe`. | Playwright: a "Created" value renders on the strategies + runs lists |

> **Note:** roll T1's columns into the single P0-A1 migration. For backtest-domain rows that are reproductions
> (Trades/Bars/EquitySnapshots), `CreatedAtUtc` = persistence time (debug aid), distinct from the sim-time
> business timestamps already present — keep both; don't overwrite `OpenedAtUtc`/`OpenTimeUtc`.

### Stream A — Add-on domain, auto-tune & kernel wiring (backend, the core)

| Phase | Goal | Key changes | Gate |
|-------|------|-------------|------|
| **A1** | Universal add-on shape | Extend `PositionManagementOptions.cs`: every sub-option (`Breakeven`, `Trailing`, `PartialTp`, `Ride`, new `DynamicSlTp`) gets `Enabled` + `Mode ("Auto"\|"Custom")`. Add `RegimeDetectionOptions { Enabled=true }` to the strategy config (Domain). Keep `SlOptions`/`TpOptions` as the mandatory baseline. | Builds; existing strategy JSON still deserializes (defaults back-compat) |
| **A2** | `AddOnAutoTuner` (pure) | New `src/TradingEngine.Services/AddOns/AddOnAutoTuner.cs`. Input `(Timeframe tf, SymbolInfo sym, VolatilityContext vol)` where `VolatilityContext { AtrPips, TypicalSpreadPips, TfMinutes }`. Output concrete values per add-on. **Pure + deterministic.** Starting heuristics (tune against tests, not gospel): see §3. | `AddOnAutoTunerTests`: monotonic in TF + ATR, clamped, deterministic (same input → same output) |
| **A3** | Resolve-at-entry + journal | In the entry seam (`BarEvaluator.cs:~111` + `SlTpResolver`/`ComposedStrategy`), when an add-on is `Auto`, call the tuner using the entry bar's ATR, stamp the resolved values onto the position's `PositionManagementConfig`, and **journal an `AddOnsResolved` record** (which add-ons active + final numeric values + Auto/Custom). Values are then stable for the position's life (deterministic). | `AddOnResolveAtEntryTests`: an Auto trailing position carries tuner-derived atrMultiple; `AddOnsResolved` appears in the StepRecord journal |
| **A4** | Wire **PartialTp** (dead → live) | `PositionManager.Evaluate` emits a partial-close modification at `TriggerRMultiple` (once); kernel reducer + `EffectExecutor` execute a partial close (reuse `ClosePartialPositionAsync`/`BacktestReplayAdapter` partial path — see C6). Journal kind `PARTIAL`. | `PartialTpKernelTests`: position partially closes at trigger R; one `PARTIAL` journal row; remaining lots trail on |
| **A5** | Wire **Ride** (dead → live) | `PositionManager.ComputeTrail`: when `Ride.Enabled` and ADX (from recent bars) > `AdxFloor`, widen the trailing ATR multiple to `RelaxedAtrMultiple`. Journal the relaxation. | `RideKernelTests`: trailing widens above ADX floor, reverts below |
| **A6** | **DynamicSlTp** add-on | When enabled, `SlTpResolver` uses tuner-derived ATR SL + RR/ATR TP *instead of* the strategy's baseline numbers (baseline still the fallback when off — D4). | `DynamicSlTpTests`: SL/TP distances track the tuner; off ⇒ baseline unchanged |
| **A7** | Journal kinds end-to-end | Ensure BE/trail/partial/ride/dynamic emit StepRecords with explicit `EventKind`/reason (`BREAKEVEN`, `TRAIL`, `PARTIAL`, `RIDE`, `ADDON_RESOLVED`). The SPA F1 filter already lists TRAIL/BREAKEVEN/PARTIAL — backend must produce them. | `JournalAddOnKindsTests`: each add-on action yields its named kind |

### Stream R — Regime detection toggle (D3)

| Phase | Goal | Key changes | Gate |
|-------|------|-------------|------|
| **R1** | Per-strategy + run master | `BarEvaluator.cs:82`: if regime detection disabled for the strategy (its `RegimeDetectionOptions.Enabled == false`) **or** the run master-disable is set, skip `regimeDetector.Detect`, set regime = `Bypassed`, and `strategyBank.GetActive` ignores the filter (allow-all). Thread a run-level `DisableRegime` flag from the run config. Journal regime state per bar incl. "detection: off". | `RegimeToggleTests`: off ⇒ strategy trades in a regime its filter would block; on ⇒ filtered as before |

### Stream P — Add-on Packs (reusable entity, D1)

| Phase | Goal | Key changes | Gate |
|-------|------|-------------|------|
| **PK1** | Pack entity + store | New `AddOnPackEntity { Id, Name, Description, AddOnsJson, IAuditable }`, `IAddOnPackStore` + `SqliteAddOnPackStore`, seeded with 3 starter packs (e.g. `runner-aggressive`, `scalp-tight`, `breakeven-only`). Roll columns into P0-A1 migration. | `AddOnPackStoreTests` (real SQLite `:memory:` per D10) round-trips a pack |
| **PK2** | Pack resolution | `EffectiveConfigResolver.Resolve` gains a pack argument: if a run/strategy names a pack, the pack's add-ons **replace** the strategy's default add-ons (then per-run field overrides apply). A strategy/run naming no pack and having no add-ons ⇒ baseline only. | `PackResolutionTests`: pack overrides strategy default; absent pack ⇒ strategy default; neither ⇒ none |
| **PK3** | Wire into the run | `BacktestOrchestrator.BuildLoadedConfigFromDbAsync` / `ResolveEffectiveConfigJsonAsync` load the named pack and feed it to the resolver; `StartRunRequest`/`DuplicateRunRequest` gain `UsePackId` (+ optional per-strategy pack map) and `DisableRegime`. | `RunWithPackE2ETests`: a run with `usePack` produces add-on behaviour from the pack, journaled |

### Stream U — UI (Angular)

| Phase | Goal | Key changes | Gate |
|-------|------|-------------|------|
| **U1** | Add-on Packs feature | New `features/addon-packs/` (list + detail editor): per add-on `enabled` + `Auto/Custom` + editable values, with an **auto-preview** (calls `/api/addons/preview?tf=&symbol=` → tuner values). CRUD via new `AddOnPacksController`. Show `CreatedOn`. | Playwright: create a pack, toggle an add-on, see auto-filled values |
| **U2** | Strategy-detail → "Add-ons" | Re-skin the existing PM editor in `strategy-detail.component.ts` as **Add-ons**: baseline SL/TP shown as *mandatory*; each enrichment gets enable + Auto/Custom; add the **Regime Detection on/off** toggle. Remove the impression that BE/trail are always-on. | Playwright: strategy shows baseline + togglable add-ons + regime switch |
| **U3** | New-Backtest selection | `new-backtest.component.ts`: per chosen strategy, pick **No add-ons / Strategy default / Pack…**; a **master "Regime detection" toggle**; resolved-config preview shows the effective add-ons + auto-tuned numbers. Replaces the raw F8 JSON-override textareas with a structured control (keep JSON as "advanced"). | Playwright: select a pack + disable regime, preview reflects it; run starts |
| **U4** | `CreatedOn` display | Add "Created" to strategies/risk-profiles/runs/trades/packs lists (T3). | covered by T3 gate |

### Stream B — iter-37 reality & observability backlog (from OPEN-ISSUES; parallelizable)

| Phase | Issue(s) | Goal |
|-------|----------|------|
| **B1** | **O1 / T7a / Finding-4 / OBS-02/03** | Feed live monitor counters (Signals/Orders/Fills/Rejections) from the StepRecord journal / kernel decisions, not the deleted `"SIGNAL"/"ORDER"/"EXEC"` progress producers. Currently structurally always-0 on **both** replay + cTrader. |
| **B2** | **T4 / T5 / T2** | Server-side paged per-bar "why" endpoint over `BarClosed` verdicts (not a client 200-row slice); de-noise `EquityObserved` from the decision-journal view. |
| **B3** | **Finding-2 / M18** | Consolidate the two `WireRiskRules` twins (`EngineHostFactory` vs `EngineHostWireExtensions`); apply the T8 `&& govOptions.Enabled` + H25 `Interlocked` fixes to **both** paths. |
| **B4** | **T12 / Finding-3 / K-GAP-2** | Make the cTrader path await `EngineRunner` completion before `StopAsync` so `FlushBacktestEquityAsync` reliably writes `EquitySnapshots` (the replay path already does). |
| **B5** | **T10 / K6 / F3** | `Duplicate` replays the saved `DatasetId` bars deterministically (replay venue) + opens New-Backtest prefilled & editable (now incl. pack/regime selection from Stream U). |
| **B6** | **T1 root / T2 / T6 / R2-R5 (cTrader fidelity)** | cBot `.algo` rebuild for `Server.TimeInUtc`; `CTraderBrokerAdapter` backtest-authoritative sim-time; cTrader cost reporting; SL/TP-vs-force-close detection. *(Needs the cTrader platform; gate behind the `ctrader-e2e` skill / owner-verify. Lower priority once D6 makes replay the default.)* |
| **B7** | **Finding-5 / Finding-6** | T9 `finally` broadcasts `cancelled` (not `completed`) for user-cancel; reconcile the HANDOVER vs OPEN-ISSUES integration-test count drift. |

---

## 3. `AddOnAutoTuner` — starting heuristics (the agent tunes against tests)

Pure function of `(tf, symbol, vol)`. These are *starting points*; calibrate to keep golden/characterization
tests sane. All outputs clamped.

```
tfBase(tf):   M1..M15 → 2.0   M30/H1 → 2.5   H4 → 3.0   D1 → 3.5      (trailing ATR multiple base)
volFactor   = clamp(vol.AtrPips / referenceAtrFor(tf, symbol), 0.7, 1.5)

Trailing.AtrMultiple   = clamp(tfBase(tf) * volFactor, 1.5, 4.0)
Trailing.StepPips      = clamp(0.5 * vol.AtrPips, minPip, ...)         (StepPips method only)
Breakeven.TriggerR     = clamp(1.0 * (1 / volFactor), 0.6, 1.6)        (calmer vol ⇒ arm sooner)
Breakeven.OffsetPips   = ceil(vol.TypicalSpreadPips * 1.5) + 1
PartialTp.TriggerR     = 1.0     PartialTp.CloseFraction = 0.5
DynamicSl.AtrMultiple  = clamp(1.2 * tfBase-ish, 1.0, 2.5)
DynamicTp.RrMultiple   = clamp(1.5 + 0.25*tfTier, 1.5, 3.0)
Ride.AdxFloor          = 25     Ride.RelaxedAtrMultiple = Trailing.AtrMultiple * 1.4
```

`referenceAtrFor(tf, symbol)` = a per-symbol/TF typical ATR (seed a small lookup, or derive from
`SymbolInfo.TypicalSpread` × a TF factor). The point: **same strategy, different symbol/TF ⇒ different but
sensible add-on numbers, with zero hand-tuning.**

---

## 4. Determinism & journal (non-negotiables)

- Auto-tuned values are resolved **once at entry** from that bar's volatility and frozen on the position →
  reproducible under K6 replay (same `(Dataset, ConfigSet, Seed)` ⇒ identical add-on numbers). Do **not**
  re-tune per bar.
- Pack id + the resolved effective add-ons participate in the `ConfigSetId` hash
  (`BacktestOrchestrator.WriteStartRecordAsync` config identity) so a different pack ⇒ a genuinely different
  run identity (K6).
- Every add-on action is a first-class StepRecord with a named `EventKind` (§A7) — the F1 unified journal and
  the F2 per-bar "why" must render them without `[object Object]`.

---

## 5. Test plan (failing-test-first)

- **Unit/pure:** `AddOnAutoTunerTests`, `PackResolutionTests`, `RegimeToggleTests`, `AuditStampInterceptorTests`,
  `DynamicSlTpTests`.
- **Kernel (Simulation, golden-adjacent):** `AddOnResolveAtEntryTests`, `PartialTpKernelTests`, `RideKernelTests`,
  `JournalAddOnKindsTests`, `RunWithPackE2ETests`, a **determinism** test (same pack+seed ⇒ identical journal).
- **Characterization:** extend `StrategyCharacterizationTests` — a strategy with `breakeven-only` pack vs no pack
  yields different exits on the same fixture.
- **Repo (real SQLite `:memory:`, per D10):** `AddOnPackStoreTests`.
- **HTTP contract:** `AddOnPacksController` CRUD + `/api/addons/preview` + run-with-pack in `RunEndpointsTests`.
- **Playwright:** packs CRUD, strategy add-on toggles + regime switch, New-Backtest pack/regime selection,
  `CreatedOn` visible.
- **Regression:** the existing golden/oracle suites stay byte-identical for strategies with **no** add-ons and
  regime-on (the default path must not move).

---

## 6. Master delivery sequence — one slice at a time

The whole iteration (backend feature + iter-37 backlog + both frontend audits) ships as ordered, independently
mergeable slices. Each slice ends green on **build + Unit + Simulation + Integration + Architecture + SPA build**
(B0 adds Architecture to the standing gate). IDs map to the stream tables in this file and the two audit streams
(§9 Stream W = `web-ui-functional-audit.md`; §10 Stream NG = `web-ui-angular-audit.md`).

| # | Slice | Contents (IDs) | Gate highlight |
|---|-------|----------------|----------------|
| **S0** | Gate repair & prereqs | **B0** (ResetClock → out of Engine), **P0-B1** (default venue → replay), EF regen ✅done | Architecture green; no-venue run uses replay |
| **S1** | `CreatedOn` audit complete | **T1–T3** (retrofit remaining ~14 entities, register `AuditStampInterceptor`, UI "Created") | `AuditStampInterceptorTests`; arch "all entities are `IAuditableEntity`" |
| **S2** | Add-on core | **A1–A3** (wire `AddOnResolver` into entry seam, journal `ADDON_RESOLVED`) | `AddOnAutoTunerTests`, `AddOnResolveAtEntryTests` |
| **S3** | Add-on behaviours + regime | **A4–A7, R1** (PartialTp/Ride/DynamicSlTp in `PositionManager`, regime bypass in `BarEvaluator`, journal kinds) | per-add-on kernel tests; golden unchanged for no-add-on strategies |
| **S4** | Packs | **PK1–PK3** (seed 3 packs, run wiring, pack→`ConfigSetId`) | `PackResolutionTests`, `RunWithPackE2ETests` determinism |
| **S5** | Backend data/endpoint correctness | **W-B1…B10, W-C1…C6, W-A7, W-B8** (regime stub, experiment report, holding-time clip, pass-prob rules, decimal PnL, streak/PF sentinels, CSV escaping, paging+totals, bars cap, export from/to, governor snapshot map, UTC-`Z` serialization) | endpoint contract tests; CSV-injection test |
| **S6** | Venue & observability backlog | **B1–B7** (live counters from journal, paged "why" endpoint, `WireRiskRules` twins, cTrader equity-flush race, duplicate deterministic replay, T9 terminal status, doc drift) | per-item; B6 cTrader gated behind `ctrader-e2e` |
| **S7** | Angular foundation | **NG-R1–R4, R10–R11** (flat ESLint+Prettier+Stylelint, environments + HTTP error interceptor, typed DTOs, one service per domain, kill component `HttpClient` + `any`) | lint clean (`no-explicit-any` warn), SPA build |
| **S8** | Angular reactivity & charts | **NG-R5–R9, R13–R14 + W-A2/A3/A4/A5/A6 + W-D1** (OnPush, `takeUntilDestroyed`, chart `ngOnDestroy` disposal + `ResizeObserver`, time-unit/axis fixes, computed templates, error surfacing) | chart-mapper units; navigate-repeatedly leak check |
| **S9** | Web functional UI fixes | **W-A1, W-D3, W-D2** (SL/TP markers via DTO field align + real `TradeDetail` type; `prompt()` → dialogs) | trade-detail SL/TP renders (e2e) |
| **S10** | Add-on UI | **U1–U4** (packs feature, strategy "Add-ons" re-skin, New-Backtest pack/regime selection, `CreatedOn` display) — built on S7's typed services | Playwright add-on flows |
| **S11** | Tests + CI gate | **NG-R12 + §5** (service/chart-mapper units, e2e backtest→report→trade-detail, flip lint to **error** + gate CI) | coverage up; happy-path e2e green |

**Dedup / cross-links (one fix, referenced from both audits):** W-A2 ≡ NG-R7 (chart disposal, done in S8) · W-D1 ≡
NG-R3 (silent catch ↔ error interceptor, S7→S8) · W-A1/W-D3 (trade-detail SL/TP + type, S9) · W-A7 (governor
blank) is the backend `ApplySnapshot` gap, also touched by iter-37 T11 · W-B8 (DateTime-`Z`) is the same wall-clock
theme as iter-37 T1/T2 — fix once at the serialization boundary in S5.

---

## 8. Backbone scaffold already landed (fill these in)

A compile-oriented contract scaffold is on the branch. **Build verified 0 err; EF migration regenerated
(P0-A1 done); Unit/Simulation/Integration green.** Delete the dev DB before first app run so the regenerated
`InitialCreate` (now incl. `AddOnPacks` + `CreatedAtUtc`) applies clean.

| File | Phase | State / what to fill |
|------|-------|----------------------|
| `Domain/AddOns/AddOnMode.cs` | A1 | Done — `Auto`/`Custom`. |
| `Domain/AddOns/DynamicSlTpOptions.cs` | A6 | Done — option record. **Fill:** wire into `SlTpResolver` (replace baseline when enabled). |
| `Domain/AddOns/AddOnPack.cs` | PK1 | Done — domain record (payload reuses `PositionManagementOptions`). |
| `Domain/AddOns/AddOnJournalKinds.cs` | A7 | Done — kind constants. **Fill:** emit them from the kernel (BE/trail/partial/ride/resolved). |
| `Domain/PositionManagement/PositionManagementOptions.cs` | A1 | Edited — `Enabled`/`Mode` on add-ons + `DynamicSlTp`. **Fill:** seed JSON sets `enabled` on strategies that already trail. |
| `Domain/StrategyBank/RegimeFilterOptions.cs` | R1 | Edited — `DetectionEnabled` + `Allows` short-circuit. **Fill:** skip `regimeDetector.Detect` in `BarEvaluator` + thread run-level master. |
| `Services/AddOns/AddOnAutoTuner.cs` | A2 | Done — pure tuner w/ starting heuristics. **Fill:** calibrate constants vs tests. |
| `Services/AddOns/AddOnResolver.cs` | A3 | Done — resolve-at-entry (Auto ⇒ tuner). **Fill:** call from the entry seam + journal `Raw` as `ADDON_RESOLVED`. |
| `Services/EffectiveConfigResolver.cs` | A1/PK2 | Edited — carries `Mode`/`Enabled`/`DynamicSlTp` through merge; `ApplyPack(...)` added. **Fill:** call `ApplyPack` in the run path. |
| `Infrastructure/.../IAuditableEntity.cs` + `AuditStampInterceptor.cs` | T1/T2 | Done — interface + interceptor. **Fill:** retrofit the other ~14 entities; register the interceptor on `AddDbContext`. |
| `Infrastructure/.../Entities/AddOnPackEntity.cs` | PK1 | Done — entity (auditable). |
| `Infrastructure/.../Entities/{StrategyConfig,RiskProfile}Entity.cs` | T1 | Edited — reference `IAuditableEntity` retrofits. **Fill:** the rest. |
| `Infrastructure/.../Repositories/IAddOnPackStore.cs` + `SqliteAddOnPackStore.cs` | PK1 | Done — CRUD store. **Fill:** seed 3 starter packs. |
| `Infrastructure/.../TradingDbContext.cs` | PK1 | Edited — `AddOnPacks` DbSet + mapping. |
| `Host/EngineServiceCollectionExtensions.cs` + `Web/.../ServiceRegistration.cs` | PK1 | Edited — pack store DI registered (both roots). |
| `Web/Api/AddOnPacksController.cs` | PK/U1 | Done — `GET/PUT/DELETE /api/addons/packs`, `GET /api/addons/preview`. **Fill:** validation. |
| `Web/Dtos/Runs/{StartRunRequest,DuplicateRunRequest}.cs` | PK3/R1 | Edited — `UsePackId`/`PerStrategyPackIds`/`DisableRegime`. **Fill:** consume in `BacktestOrchestrator`. |

**Not scaffolded (agent builds):** the kernel wiring of PartialTp/Ride/DynamicSlTp/regime-bypass (behaviour in
`PositionManager.Evaluate` + `BarEvaluator`), the EF regen + interceptor registration, the seed packs, the
Angular `addon-packs` feature + strategy-detail "Add-ons" re-skin + New-Backtest selection, and all tests (§5).

---

## 7. Static-audit findings folded in (for traceability)

From the iter-37 HANDOVER static review (see conversation): the "all gates green / signed off" claim holds for
the **replay/kernel** path only; the owner's manual T1–T12 pass ran on the **cTrader-in-process default**, which
has no automated coverage. D6 (venue flip) + Stream B address the symptom and the gap. The verified-correct
iter-37 fixes (K-GAP-1/2/3/6, T1-frontend, T3, T8, T9) are **not** re-opened here — only their cTrader-path tails
(B-stream) and the two `WireRiskRules` twins (B3) remain.

---

## 9. Stream W — Web functional audit (`docs/audit/web-ui-functional-audit.md`)

Severity from the audit (HIGH 5 · MED 11 · LOW 6). Mostly **backend data/endpoint** fixes (slice S5) the UI reads,
plus chart/UX items that ride the Angular refactor (S8/S9). Verification legend: ✅ code-read · 🔎 exploration.

**A — Charts & live data**

| ID | Sev | Issue (location) | Fix | Slice |
|----|-----|------------------|-----|-------|
| W-A1 | HIGH ✅ | SL/TP markers never render — `trade-detail` reads `slPrice`/`tpPrice`, DTO serializes `stopLoss`/`takeProfit` (`TradeDetailResponse.cs:13-14`); response mis-typed as `TradeSummary` | Align field names + a real `TradeDetail` interface | S9 |
| W-A2 | HIGH ✅ | Charts never disposed (leak) — candle/equity/scatter/histogram have no `ngOnDestroy`/`chart.remove()` | `ngOnDestroy → chart.remove()`, null series (**≡ NG-R7**) | S8 |
| W-A3 | MED ✅ | No responsive resize (`candle-chart:40` one-shot width) | `ResizeObserver`/autoSize | S8 |
| W-A4 | MED ✅ | Fragile time-unit round-trip (`×1000` then `/1000`, untyped) | one unit end-to-end; brand/validate | S8 |
| W-A5 | MED 🔎 | Histogram/scatter use bin indices as epoch ts (`run-analyzer:40-42`) | category/index axis, not time | S8 |
| W-A6 | LOW 🔎 | Live-equity time discontinuity (wall-clock fallback then sim jump, `run-monitor:126`) | consistent sim-time | S8 |
| W-A7 | MED 🔎 | Governor state always blank — `BacktestOrchestrator.ApplySnapshot:1041` never set Governor/DistanceToDailyLimit | map governor onto the snapshot (also iter-37 T11) | S5 ✅ |

**B — Data correctness**

| ID | Sev | Issue (location) | Fix | Slice |
|----|-----|------------------|-----|-------|
| W-B1 | HIGH ✅ | regime-history is a hardcoded `"Unknown"`/bar stub (`BacktestAnalyticsController:135`) | implement from journal regime, or remove endpoint | S5 |
| W-B2 | HIGH ✅ | Experiments report ignores `id`, returns first `REPORT.md` (`ExperimentsController:83-97`) | resolve by experiment id | S5 |
| W-B3 | MED ✅ | Holding-time histogram clipped at 1h (`Math.Min(dur,3600)`, `:99`) | real buckets / open-ended top bin | S5 |
| W-B4 | MED ✅ | Pass-prob hardcodes 10/5/10/30, ignores run ruleset (`:58-61`) | read the run's `PropFirmRuleSet` | S5 |
| W-B5 | MED ✅ | decimal→double PnL precision loss (`:101,103`) | keep `decimal` to the boundary (repo money rule) | S5 |
| W-B6 | MED 🔎 | Strategy stats win/loss-streak always 0; PF sentinel 999 (`StrategiesController:188-191`) | compute real streaks; null PF when undefined | S5 |
| W-B7 | MED ✅ | CSV not escaped → corruption + injection (`ExportController:21`) | RFC-4180 quoting + `'`-guard | S5 |
| W-B8 | MED 🔎 | `DateTime` serialized without `Z` → viewer-TZ shift | UTC `Z` (ISO round-trip) or `DateTimeOffset` (same theme as T1/T2) | S5 |
| W-B9 | LOW ✅ | pnl-by-hour/day are UTC buckets (assumption, not defect) | document | S5 (doc) |
| W-B10 | LOW ✅ | daily-pnl grouping depends on stored `Kind=Utc` | verify EF/SQLite materializes UTC | S5 (verify) |

**C — Endpoint contract**

| ID | Sev | Issue | Fix | Slice |
|----|-----|-------|-----|-------|
| W-C1 | MED 🔎 | `/api/trades` pages without `totalCount` | return total for pagers | S5 |
| W-C2 | MED 🔎 | `/runs/{id}/trades` unpaged; journal kind filter applied post-DB-limit | page server-side; filter before limit | S5 |
| W-C3 | MED 🔎 | `/api/bars` unbounded | row cap + paging | S5 |
| W-C4 | MED ✅ | `/export/trades.csv` ignores `from/to` (`ExportController:9,14`) | honour the range | S5 |
| W-C5 | LOW 🔎 | Duplicate daily-pnl/analytics on two controllers; two start endpoints | consolidate | S6 |
| W-C6 | LOW 🔎 | No auth (acceptable single-user) | document explicitly | doc |

**D — Error handling & UX**

| ID | Sev | Issue | Fix | Slice |
|----|-----|-------|-----|-------|
| W-D1 | MED 🔎 | Pervasive silent `catch {}` → empty states hide 500s | surface via error interceptor (**≡ NG-R3**) | S8 |
| W-D2 | LOW 🔎 | `prompt()` for create flows (risk-profile/prop-firm) | real dialog + validation | S9 |
| W-D3 | LOW | Trade-detail typed as `TradeSummary` (type lie) | dedicated `TradeDetail` (ties W-A1) | S9 |

> **F — runtime checklist (⏳):** during S5/S8/S9, drive `run-shamshir` and confirm live monitor stream, W-A7
> governor, W-A1 SL/TP + W-A4 candles, W-A5 analyzer axes, W-C4/W-B7 CSV, and W-D1 error states.

---

## 10. Stream NG — Angular refactor (`docs/audit/web-ui-angular-audit.md`)

Severity (HIGH 3 · MED 7 · LOW 4). Frontend-only — runnable in a worktree parallel to the backend slices. The
audit's 5-phase roadmap maps onto slices **S7** (foundation + types/services), **S8** (reactivity + templates +
charts), **S11** (tests + flip lint to error / gate CI).

| ID | Sev | Issue | Fix | Slice |
|----|-----|-------|-----|-------|
| NG-R1 | HIGH | Ubiquitous `any`, `no-explicit-any` disabled (~15 spots) | type signals/HTTP generics/chart series; re-enable rule (warn→error) | S7→S11 |
| NG-R2 | HIGH | No API-service layer — 12 components inject `HttpClient` (only `RunsApiService` exists) | one typed service per domain; components depend on services | S7 |
| NG-R3 | MED | No base-URL/env config, no HTTP error interceptor | `environments` + error interceptor (feeds W-D1) | S7 |
| NG-R4 | MED | All DTOs in one `api.types.ts`; chart types inline | split per feature; shared chart contracts | S7 |
| NG-R5 | MED | No `ChangeDetectionStrategy.OnPush` anywhere | OnPush on all components (signals make it default) | S8 |
| NG-R6 | MED | Manual lifecycle (Subscription + setInterval + queueMicrotask in run-monitor) | `takeUntilDestroyed`, RxJS `interval`, `afterNextRender` | S8 |
| NG-R7 | MED | Chart disposal missing (≡ W-A2) | `ngOnDestroy` disposal | S8 |
| NG-R8 | MED | Logic in templates; unused `NgClass` import (`run-report:3`) | move to `computed()` | S8 |
| NG-R9 | MED | `track $index` on dynamic lists (`data-table:24`, `run-report:133`) | track stable keys | S8 |
| NG-R10 | MED | Lint/format gaps (legacy `.eslintrc`, no Prettier/Stylelint) | flat config + Prettier + Stylelint + tightened rules | S7 |
| NG-R11 | LOW | `no-console`/`no-debugger` only warn | promote `no-console` to error for prod | S7→S11 |
| NG-R12 | HIGH | Near-zero coverage (2 unit + 1 smoke) | service + chart-mapper units; e2e backtest→report→trade-detail | S11 |
| NG-R13 | LOW | Direct DOM via `ElementRef.querySelector` (4 chart comps) | isolate behind a small wrapper | S8 |
| NG-R14 | LOW | Manual `document.createElement('a')` download (`run-report:316`) | small download helper | S8 |
