# Iter-38 + Iter-39 — HANDOVER (comprehensive, all sessions)

**Branch:** `iter/38-addons` (cut from `iter/37-frontend-finish`)
**Current HEAD:** `d3da582` — 82 commits total since branch cut
**State:** working tree clean, all gates green
**Companion docs:** `docs/iterations/iter-38/PLAN.md`, `AGENTS.md`, `docs/OPEN-ISSUES.md`, `.claude/skills/ctrader-e2e/SKILL.md`

> **Read this whole file before touching code.**

---

## 0. 30-second orientation

This branch delivers the **Strategy Add-ons** feature (Breakeven, Trailing, DynamicSlTp, Ride, PartialTp) as first-class, auto-tuned, frozen-at-entry, journaled add-ons; makes regime detection toggleable; adds reusable **Add-on Packs**; stamps `CreatedAtUtc`/`UpdatedAtUtc` on every entity; flips the default backtest venue to replay; lands the Stream-W backend audit fixes, the Angular foundation refactor, and the add-on packs UI. **Iter-39** (this session) closed the remaining gaps: SPA build blocker, auto-tuner lookup, regime pack wiring, bare catch fixes, missing tests, clock injection, OnPush on all components, and the Duplicate replay dialog.

**The golden rule:** *with all add-ons OFF and regime ON (the default), the engine output is byte-identical to before.* Every behavioural change is gated behind an `Enabled`/pack/flag that defaults off.

---

## 1. Standing gate (current as of iter-39 cleanup)

```powershell
dotnet build                                                  # 0 errors (TreatWarningsAsErrors=true)
dotnet test tests\TradingEngine.Tests.Unit                    # 260 pass / 5 skip
dotnet test tests\TradingEngine.Tests.Architecture            # 5 pass
dotnet test tests\TradingEngine.Tests.Integration             # 61 pass / 0 skip
# Determinism / credential-free Simulation subset — MUST stay 61/61:
dotnet test tests\TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"
# SPA build — MUST be 0 errors:
cd web-ui ; npm run build
```

- The 4 credential-gated cTrader tests run with `[SkippableFact]` + `Skip.IfNot(HasCredentials)`. Credentials ARE configured in `appsettings.Development.json` on this machine. With creds and the cTrader CLI installed, 2 of 3 smoke tests pass; `TradeLedger_ClientOrderIdReconciliation_NoMissingTrades` shows cTrader=17 DB=16 (known pump-drain reconciliation bug — see §7).
- After `npm run build`, run `git checkout -- src/TradingEngine.Web/wwwroot; git clean -fdq src/TradingEngine.Web/wwwroot` to avoid committing intermediate SPA bundles.

---

## 2. What's DONE — 82 commits across all sessions

### Session 1 — S0–S5 (original iter-38, 24 commits)
Scaffold, venue flip, `CreatedOn` audit, add-on core + auto-tuner + resolve-at-entry, Regime toggle + DynamicSlTp + Ride + PartialTp + journal kinds, Packs (seed 3, resolution, run wiring, ConfigSetId), Stream-W backend audit fixes (holding times, decimal PnL, CSV, regime stub, streak/PF, bars cap, governor W-A7, pass-prob W-B4, experiment W-B2, DateTime-UTC W-B8).

### Session 2 — S6–S10 + Audit (original iter-38, 20 commits)
CT-1 SkippableFact, InProcessEngineSmokeTests DI fix, WireRiskRules consolidation + T8, terminal cancelled-status B7/T9, kernel live-monitor counters B1, per-bar decisions endpoint B2/W-C2, cTrader equity-flush B4, Angular foundation (Prettier + Stylelint + environments + error interceptor + typed services for all 6 domains), Angular reactivity (OnPush on 9 components, chart disposal, ResizeObserver, stable track keys, downloadBlob helper, queryHost helper), Web functional fixes (SL/TP markers W-A1, prompt→dialog W-D2), Add-on packs UI (list + detail CRUD U1, strategy re-skin U2, New-Backtest pack/regime U3), audit fixes (ResizeObserver, HttpClient elimination, template arithmetic, unlogged catches).

### Session 3 — Post-HANDOVER SPA fixes (3 commits, between sessions)
The SPA build had Angular 19 template literal compilation issues. `c122d78` extracted run-report export logic to `report-export.helper.ts` (to avoid JS template literals in .ts files that Angular 19 mis-parses). `e5ba0d1` fixed 13 remaining template literals across badge, data-table, trade-detail, run-monitor, new-backtest, addon-pack-list, plus import paths + GovernorOptions interface additions. **BUT:** introduced a new bug — the closing backtick on all 5 run component inline templates was escaped as `\`` instead of bare `` ` `` (see §6 footgun NEW-3). The HEAD at this point was `e5ba0d1` — SPA build was BROKEN.

### Session 4 — Iter-39 cleanup (this session, 10 commits)

#### Phase 1 — SPA Build (BLOCKER)
| Commit | Fix | What |
|--------|-----|------|
| `4933fa6` | **F0** | Fixed 5 escaped closing backticks (`\`` → `` ` ``) in new-backtest, run-analyzer, run-list, run-monitor, run-report. Also fixed pre-existing bugs unmasked by the fix: orphan `</div>` in new-backtest, missing `DatePipe` import in addon-pack-list, unnecessary nullish coalescing on non-nullable number fields, dead `rp.profiles` fallback. |

#### Phase 2 — Backend Bugs
| Commit | Fix | What |
|--------|-----|------|
| `2b28828` | **F1** | Implemented `ReferenceAtrPips(Timeframe tf, double typicalSpreadPips)` on `AddOnAutoTuner` — the lookup the PLAN §3 required but never built. Both callers (`KernelTrailingEvaluator.BuildVolatility()` and `BarEvaluator`) now supply non-zero `ReferenceAtrPips`, so the auto-tuner produces different add-on numbers per symbol/TF. 7 new tests. |
| `935e6b7` | **F2** | Wired `RegimeDetectionEnabled` from pack at runtime in `BacktestOrchestrator`. Previously the field was stored and round-tripped via DB but never read — the engine always used the strategy's own regime setting regardless of which pack was selected. The run-level `disableRegime` master flag still applies on top (AND semantics). |
| `67a0003` | **F15** | Fixed `SqliteGovernorOptionsStore` bare `catch {}` — replaced with service-locator `IServiceProvider` pattern that lazily resolves `ILogger<T>`. Uses `NullLogger` fallback so Integration tests don't break. Previous `ILogger` injection attempt (`c11db32`) was reverted because it broke DI; the service-locator pattern works because `IServiceProvider` is always registered. |
| `1b28368` | **F5** | Completed the TODO on `AuditStampInterceptor` — added `IEngineClock` constructor, registered interceptor as scoped service in DI with `GetService` fallback (not `GetRequiredService`), updated Web + Host `AddDbContext` registrations to use factory overload `(sp, o)`. Infrastructure extension method keeps parameterless constructor. |

#### Phase 3 — Missing Tests
| Commit | Fix | What |
|--------|-----|------|
| `3ec84c0` | **F3** | Wrote `AddOnJournalKindsTests` (7 tests) — was required by PLAN §A7 but never written. Verifies all 5 journal kind constants are distinct and that `PositionManager` reason strings match the `AddOnJournalKinds` constants the SPA unified-journal filter keys on. |

#### Phase 4 — Angular Quality
| Commit | Fix | What |
|--------|-----|------|
| `91bb0c3` | **F13** | Added `ChangeDetectionStrategy.OnPush` to all 17 remaining components. All 26 components now use OnPush (was only 9). |
| `1e47ed9` | **A1** | Implemented S6 B5 — Duplicate replay dialog. The Duplicate button on `run-report` now opens a modal with pack dropdown (loaded from `AddOnPacksApiService`) and regime detection checkbox. On confirm, sends `usePackId`/`disableRegime` in the body. Quick duplicate (no modal) remains available. |
| `d3da582` | **A2** | Fixed the 4 `edit: any = {}` instances + remaining `any` signals/callbacks. Updated `api.types.ts` interfaces (GovernorOptions, RiskProfile, PropFirmRule, StrategyDetail) with all missing fields from backend DTOs — each was missing 5-14 fields the backend actually sends. Changed `edit: any` → `Record<string, any>` for clearer intent. Typed `data` signals, subscribe callbacks, `onTradeClick`, `strategies`/`riskProfiles` signals. |

#### Phase 5 — cTrader
| Commit | Fix | What |
|--------|-----|------|
| `72d92f6` | **C1+C2** | Logged all 4 bare `catch {}` blocks in `TradingEngineCBot.cs` (ModifyPosition failure, dealer receive error, exec send failure, stats send failure). Added retry logic (3 attempts with 100ms delays) to the close exec frame send at line 661 — the most likely cause of the 17-vs-16 trade reconciliation mismatch. cBot rebuilt (`.algo`). |

The reconciliation test still fails (cTrader=17 DB=16) — the CBOT logging now enables diagnosis. Root cause is likely an engine pump-drain race where the kernel stops processing before the last execs are consumed from the channel. See §7.

---

## 3. Conventions / patterns — FOLLOW THESE

1. **Off-by-default = byte-identical.** New behaviour gates behind `Enabled`/`Mode`/pack/flag defaulting off. Golden snapshot unchanged.
2. **Add-on resolution at entry.** `AddOnResolver.ResolveAtEntry(...)` runs ONCE at position register, frozen for the life of the position.
3. **Journal kinds.** `KernelBacktestLoop.EventKindFor(evt)` maps add-on events to canonical kinds (`TRAIL`, `BREAKEVEN`, `PARTIAL`, `RIDE`, `ADDON_RESOLVED`). Only events that never fire in golden are remapped.
4. **New kernel events → reducer + wiring test.** Must be added to BOTH `EngineReducer.Reduce` AND `EngineReducerWiringTests`.
5. **Packs.** Payload reuses `PositionManagementOptions`. `ApplyPack` REPLACES enrichments, keeps baseline SL/TP. Pack id + regime fold into `ConfigSetId` hash.
6. **EF migrations.** TWO `DbContext`s — always `--context TradingDbContext`. One disposable `InitialCreate`. Dev DBs deleted on boot.
7. **Money = `decimal`**, lot sizing = `Math.Floor`, `CancellationToken` last, Serilog message templates, no infra deps in `TradingEngine.Domain`.
8. **Angular typed services.** `@Injectable({ providedIn: 'root' })` per feature domain. All HttpClient calls go through services — **no component injects HttpClient directly**.
9. **OnPush** is now on **all 26 components**. Add to any new component.
10. **ResizeObserver** is wired on all 4 chart components. Don't remove it.
11. **TS interface sync.** The `api.types.ts` interfaces are now complete (updated in this session). When backend DTOs change, sync the TS interfaces.
12. **Auto-tuner `ReferenceAtrPips`.** The `AddOnAutoTuner.ReferenceAtrPips()` method computes a TF-dependent reference ATR from `TypicalSpread`. Any new caller that builds a `VolatilityContext` must supply non-zero `ReferenceAtrPips`.
13. **Pack `RegimeDetectionEnabled`** now participates in the runtime config — pack's setting overrides strategy default, run master disables on top.

---

## 4. LEFTOVERS — remaining items for next iteration

### S6 B6 — cTrader reconciliation root cause (HIGH)

The `TradeLedger_ClientOrderIdReconciliation_NoMissingTrades` test shows cTrader=17 DB=16. The CBOT logging (C1) will now report any exec frame send failures. The remaining likely cause: the engine pump stops consuming execs from the channel before the last close exec is processed (pump-drain race).

**Investigation path:**
- `KernelBacktestLoop.PumpAsync` — when the bar stream ends, does it drain remaining execs from `_effectExecutor.ExecutionStream`?
- `BacktestOrchestrator.RunEngineReplayAsync` — the B4 3-second delay may need to be followed by an explicit drain of the exec channel.
- Check the RECONCILE log line from `CTraderBrokerAdapter.HandleStats` (`:348-357`) in test output — it reports execs sent vs received vs deduped.

**Files:** `KernelBacktestLoop.cs`, `BacktestOrchestrator.cs:646,809,1034`, `CTraderBrokerAdapter.cs:340-358`

### Angular — remaining quality items

| Item | File | Approach |
|------|------|----------|
| **A3: takeUntilDestroyed** | `run-monitor.component.ts` | Replace manual `Subscription` + `setInterval` + `queueMicrotask` with `takeUntilDestroyed()`. The operator is not imported anywhere in the project yet — add from `@angular/core/rxjs-interop`. ~40 lines. |
| **A4: TradeDetail type** | `api.types.ts`, `trade-detail.component.ts` | The backend returns `TradeDetailResponse` (29 fields) but TypeScript uses `TradeSummary` for the detail view. Add a separate `TradeDetail` interface. ~20 lines. |
| **A5: Chart time helpers** | 4 chart components | Extract `toUtcTimestamp(ms: number)` / `fromUtcTimestamp(s: number)` to eliminate 8 raw `/ 1000` / `* 1000` conversions. ~30 lines. |
| **W-A5: Histogram axis** | `run-analyzer`, scatter/histogram charts | Bin indices `i` are cast to `UTCTimestamp` — use category/index axis instead. ~20 lines. |
| **W-A6: Live-equity time** | `run-monitor.component.ts:220` | `Date.now()` fallback when `simTimeUtc` missing causes time-axis discontinuity. ~5 lines. |
| **W-C1: totalCount** | `TradesController.cs` | `/api/trades` and `/runs/{id}/trades` have no `totalCount` for pagination. Add response wrapper. ~30 lines API + type change. |

### PARKED — needs cTrader platform or environment setup

| Item | What | Blocked by |
|------|------|-----------|
| **S6 B6 (full)** | cBot `.algo` rebuild for `Server.TimeInUtc`; `CTraderBrokerAdapter` backtest-authoritative sim-time; cTrader cost reporting | Platform rebuild required (T1/T2/T6 root fixes) |
| **S11** | CI gate + Angular unit tests + ESLint `no-explicit-any` → error + `wwwroot` final rebuild | Environment setup |
| **S12** | Owner review round — runtime verify governor + DateTime-Z + kernel counters + add-on packs CRUD | Needs `run-shamshir` |

---

## 5. cTrader E2E status

Credentials are configured in `appsettings.Development.json`:
```json
{ "CTrader": { "CtId": "seankiaa", "PwdFile": "C:\\Users\\shahi\\Documents\\ctrader.pwd", "Account": "5834367" } }
```

The cTrader CLI IS installed and functional. The cBot `.algo` is at `src/TradingEngine.Adapters.CTrader/bin/Debug/net6.0/src.algo`.

| Test | Status | Notes |
|------|--------|-------|
| `EurUsd_H1_3Days_ProducesTrades_UsingPhasedHarness` | ✅ PASS | |
| `EurUsd_H1_3Days_ProducesTrades_UsingRunAsync` | ✅ PASS | |
| `TradeLedger_ClientOrderIdReconciliation_NoMissingTrades` | ❌ FAIL | cTrader=17 DB=16 — pump-drain race (see §4) |
| Other credential-gated tests (`DiffE2ETests`, `PipelineE2ETests`, `NetMQBridgeTest`) | Not re-run this session | `[SkippableFact]`-gated |

**To run all cTrader tests:**
```powershell
dotnet test tests\TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"
```

The 4 `TradingEngineCBot` unlogged catches are now logged (C1). Rebuild the cBot with `dotnet build src\TradingEngine.Adapters.CTrader` after any cBot changes.

---

## 6. Repo footguns (will bite a cold session)

- **NEW-1 — Angular 19 template literal compiler bug.** The Angular 19 compiler mis-parses JavaScript template literals (backtick strings with `${...}`) inside `.component.ts` files as Angular template expressions. **Never write JS template literals inside inline component templates.** Use string concatenation or extract to helper `.ts` files. The `report-export.helper.ts` pattern is the reference (extracted from `run-report`).
- **NEW-2 — `IsExternalInit`/PowerShell here-strings.** PowerShell here-strings `@"…"@` interpolate `${…}` as PowerShell variables and **destroy JavaScript template literals**. Use the **Write tool** for any `.ts` file containing backtick strings. This is separate from NEW-1 — both issues exist.
- **NEW-3 — Escaped closing backtick `\``.** In TypeScript, `\`` is an escape sequence that inserts a literal backtick character into a string — it does NOT terminate a template literal. If you see `\`` at the end of an inline template, change it to `` ` ``. This was introduced by the post-HANDOVER SPA fix session and broken on HEAD for the entire iter-39 session. The fix is 5 one-character changes.
- **EF:** always `--context TradingDbContext` (dual context). One disposable `InitialCreate`.
- **`wwwroot`:** the built Angular bundle is git-tracked. After every SPA build, run `git checkout -- src/TradingEngine.Web/wwwroot; git clean -fdq src/TradingEngine.Web/wwwroot` to avoid committing intermediate bundles.
- **PowerShell `Select-String -Path "src\**\*.cs"` does NOT recurse** — it silently misses files. Use the **Grep tool** for code search. `rg` (ripgrep) is **not installed**.
- **LF→CRLF** warnings on commit are benign.
- **Golden snapshot** holds no journal kinds → journal `EventKind` remaps are byte-safe; trade/risk changes are not.
- **Full Simulation suite ≠ credential-free gate** — use the §1 filter for the determinism gate.
- **Pre-existing Stylelint warnings** (`19 rules skipped due to selector errors`) — benign, pre-existing in SPA build output.
- **`SqliteGovernorOptionsStore` now has service-locator logging.** When adding an `ILogger<T>` dependency to a class that's registered as a singleton/scoped service used by Integration tests, use the `IServiceProvider` service-locator pattern with `NullLogger` fallback — don't add a required `ILogger<T>` constructor parameter (it breaks the Integration test's DI container).

---

## 7. Key files by feature (quick reference)

### Add-on system
- `src/TradingEngine.Services/AddOns/AddOnAutoTuner.cs` — pure tuner + `ReferenceAtrPips()` lookup
- `src/TradingEngine.Services/AddOns/AddOnResolver.cs` — resolve Auto vs Custom at entry
- `src/TradingEngine.Services/PositionManager.cs` — PartialTp (lines 74-91), Ride (lines 112-118), Breakeven/Trail (lines 94-106)
- `src/TradingEngine.Host/KernelTrailingEvaluator.cs` — per-bar add-on evaluation for kernel path
- `src/TradingEngine.Host/BarEvaluator.cs:137-168` — DynamicSlTp at entry evaluation
- `src/TradingEngine.Domain/AddOns/AddOnJournalKinds.cs` — canonical journal kind strings
- `src/TradingEngine.Host/KernelBacktestLoop.cs:317-323` — `EventKindFor` mapping
- `src/TradingEngine.Services/EffectiveConfigResolver.cs:103-116` — `ApplyPack` (replaces enrichments, keeps baseline)

### Packs
- `src/TradingEngine.Domain/AddOns/AddOnPack.cs` — domain record with `RegimeDetectionEnabled`
- `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteAddOnPackStore.cs` — CRUD + seed
- `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:491-525` — pack resolution + regime wiring at runtime
- `web-ui/src/app/features/addon-packs/` — list + detail CRUD UI

### Audit / infrastructure
- `src/TradingEngine.Infrastructure/Persistence/AuditStampInterceptor.cs` — `IEngineClock` injected, stamps CreatedAtUtc/UpdatedAtUtc
- `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteGovernorOptionsStore.cs` — service-locator logger
- `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:248-253` — `ResolveUseCtrader` (default → replay)

### cTrader / cBot
- `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` — 4 catches now logged (lines 355, 602, 661, 707)
- `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` — RECONCILE logging at line 348-357
- `tests/TradingEngine.Tests.Simulation/E2E/CtraderE2EHarnessSmokeTests.cs` — 3 tests, 2 pass, reconciliation fails
- `tests/TradingEngine.Tests.Simulation/Harness/CtraderTestHelpers.cs` — credential resolution

### Angular
- `web-ui/src/app/models/api.types.ts` — all interfaces now complete (GovernorOptions, RiskProfile, PropFirmRule, StrategyDetail)
- `web-ui/src/app/features/runs/run-report/run-report.component.ts` — Duplicate dialog at line 552+
- `web-ui/src/app/features/runs/run-monitor/run-monitor.component.ts` — typed `RunProgressEnvelope`/`RunCompletedEnvelope`

---

## 8. Audit findings NOT fixed (carry-forward)

| Finding | Severity | File | Approach |
|---------|----------|------|----------|
| Duplicate component code extraction | BLOCKER | Multiple Angular components | Large refactor — deferred |
| `takeUntilDestroyed` | MEDIUM | `run-monitor.component.ts` | See §4 A3 |
| Chart time helpers (`/1000` / `*1000`) | MEDIUM | 4 chart components | See §4 A5 |
| Histogram bin indices as timestamps | MEDIUM | `run-analyzer`, scatter/histogram charts | See §4 W-A5 |
| Dedicated `TradeDetail` TS interface | LOW | `api.types.ts`, `trade-detail.component.ts` | See §4 A4 |
| Live-equity time discontinuity | LOW | `run-monitor.component.ts:220` | See §4 W-A6 |
| `totalCount` on trades API | MEDIUM | `TradesController.cs`, `RunsController.cs` | See §4 W-C1 |
| Dedup `daily-pnl`/`analytics` endpoints | LOW | `BacktestAnalyticsController`, `RunsController` | Duplicate endpoints, `BacktestAnalyticsController` path likely dead |
| cTrader pump-drain race | HIGH | Kernel loop + cTrader path | See §4 S6 B6 |

---

## 9. Definition of done

| Phase | Status |
|-------|--------|
| S0–S5 (backend feature) | ✅ Complete |
| S6 (venue + observability) | ⬜ B5 done (A1), B6 deferred, reconciliation pending |
| S7–S10 (Angular) | ✅ Foundation + services + OnPush + add-on UI complete; A3-A5 deferred |
| S11 (CI gate) | ⬜ PARKED |
| S12 (review round) | ⬜ PARKED |
| F0–F3, F5, F13, F15 (iter-39 backend bugs) | ✅ Complete |
| A1–A2 (iter-39 Angular quality) | ✅ Complete |
| C1–C2 (iter-39 cTrader logging) | ✅ Complete |
| Full §1 gate | ✅ 260/5 · 5 · 61 · 61 · SPA 0 errors |
