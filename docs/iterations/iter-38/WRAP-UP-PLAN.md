# Iter-38/39 — WRAP-UP PLAN (for the finishing agent)

**Branch:** `iter/38-addons`
**Author of this pass:** static audit + targeted implementation (Claude Code, 2026-06-23). Code written **and now built + tested green** (see §0 — the full standing gate passes, golden 61/61 byte-identical). Angular built (0 errors).
**Read first:** `docs/iterations/iter-38/PLAN.md`, `docs/iterations/iter-38/HANDOVER.md`, then this file.

> **Why this file exists.** A deep static audit of iter-38/39 found the feature is mostly delivered and *correctly plugged into the kernel*, but with (a) a real correctness bug the original agent never finished, (b) a dead-twin regime mechanism, (c) missing pack/test coverage, and (d) a pile of honestly-deferred items. This pass implemented the load-bearing fixes + tests. You **verify, finish the parked work, and re-baseline**.

---

## 0. Gate status — VERIFIED GREEN this pass (2026-06-23)

The full standing gate was run after the §1 changes and **passes**:

| Suite | Result |
|-------|--------|
| `dotnet build` | 0 errors (1 IDE0011 brace fix applied in `AddOnPacksController`) |
| Unit | 267 passed / 5 skip (incl. the 7 new add-on/regime tests) |
| Architecture | 5 passed |
| Golden / Characterization / Determinism / Equivalence / Journal subset | **61 / 61 — byte-identical** (proves §1 didn't move the golden path) |
| Integration | 67 passed (incl. the 6 new packs-API / run-with-pack / validation tests) |
| `web-ui` `npm run build` | 0 errors |

Re-run it after your own changes (the full commands below). The golden/characterization subset is the contract for every change in §1 — if it goes red, the diff is in one of those four changes.

```powershell
dotnet build                                                  # TreatWarningsAsErrors=true
dotnet test tests\TradingEngine.Tests.Unit
dotnet test tests\TradingEngine.Tests.Architecture
dotnet test tests\TradingEngine.Tests.Integration
# Determinism / credential-free Simulation subset — MUST stay green (golden byte-identical):
dotnet test tests\TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"
cd web-ui ; npm run build      # already verified 0 errors this pass
```

**The golden/characterization subset is the contract for every change in §1.** All §1 changes were designed to be byte-identical on the golden path; if that subset goes red, the diff is in one of the four changes in §1 — start there.

After any SPA build: `git checkout -- src/TradingEngine.Web/wwwroot ; git clean -fdq src/TradingEngine.Web/wwwroot` (the bundle is regenerated; don't commit intermediate bundles).

---

## 1. What this pass CHANGED (verify these, then build on them)

### 1.1 🔴 BUG FIX — `Trailing.Enabled` toggle was a no-op (the auto-tuner never ran on seeded trailing)

**Root cause.** `PositionManager.BuildConfig` mapped `Trailing.Method` onto an active trail **regardless of `Trailing.Enabled`**. Every seeded strategy JSON trailed via `"method": "AtrMultiple"` with **no `"enabled"`**, so:
- the add-on `Enabled` toggle (UI / pack) was a **lie** for Trailing — flipping it off did nothing; and
- `AddOnResolver.ResolveAtEntry` only tunes trailing when `Enabled && Mode==Auto`, so **the headline auto-tuner never touched any seeded strategy's trailing**. The PLAN §8 "fill: seed JSON sets enabled on strategies that already trail" was never done.

**Fix (byte-identical, conservative):**
- `src/TradingEngine.Services/PositionManager.cs` — `BuildConfig` now gates the method: `var trailingMethod = tr.Enabled ? ParseTrailingMethod(tr.Method) : TrailingMethod.None;`. Breakeven already worked this way; trailing is now consistent.
- The 7 trailing strategy JSONs (`config/strategies/{trend-breakout,macd-momentum,super-trend,ema-alignment,bb-squeeze,session-breakout,mtf-trend}.json`) now carry `"enabled": true, "mode": "Custom"` on `trailing`. **`Custom` is deliberate** — it preserves the stored numbers (the resolver skips Custom), so backtests stay byte-identical. (`Auto` would re-tune them — see §3.1.)
- `tests/.../PositionManagement/TrailingStopE2ETests.cs` — its inline strategy now sets `Enabled = true, Mode = Custom` (it relied on Method-without-Enabled).
- New pin: `tests/TradingEngine.Tests.Unit/AddOns/TrailingEnabledToggleTests.cs`.

**Verify:** golden/characterization subset stays green (it must — golden's `AlwaysSignalStrategy` has no trailing). Then a real replay backtest of e.g. `trend-breakout` still trails exactly as before.

### 1.2 🟠 CONSISTENCY — regime run-master was a dead twin

`BarEvaluator` had a `disableRegime` constructor flag that was **never passed** (always `false`) — `EngineRunner.cs:78` constructs it without the arg. The run-master `DisableRegime` actually works only because `BacktestOrchestrator` mutates each strategy's `RegimeFilter.DetectionEnabled=false` (consumed by `StrategyBankService.GetActive` via `RegimeFilter.Allows`). Two mechanisms; the "intended" one dead; the PLAN R1 "journal detection: off" was never delivered.

**Fix:** `src/TradingEngine.Host/BarEvaluator.cs` — removed the dead param; `RegimeFilter.DetectionEnabled` is now the single source of truth. `Detect` still runs exactly as before (golden-safe), and the per-bar journal label is `"Bypassed"` when **every** active strategy has detection off (the run-master / regime-off-pack state). Deepened `tests/.../Regime/RegimeToggleTests.cs`.

**Verify:** golden subset green (detection still runs; default strategies are detect-on). A run with `disableRegime: true` should show `"Bypassed"` in the per-bar "why" / monitor regime field.

### 1.3 🟡 MISSING — pack validation + stale skeleton docs

- `src/TradingEngine.Web/Api/AddOnPacksController.cs` — `Upsert` now validates the bundle (the `TODO(iter-38 U1)` fill): enabled add-on needs a real method, `closeFraction ∈ (0,1]`, non-negative triggers/offsets, positive ATR/RR. Returns `400 { errors }`.
- Cleaned the stale "skeleton — agent should seed" comments in `SqliteAddOnPackStore.cs` and `AddOnPacksController.cs` (both were actually done).

### 1.4 Angular A4 — dedicated `TradeDetail` type (ends the type-lie)

`/api/trades/{id}` returns `TradeDetailResponse` but the SPA typed it as `TradeSummary`. Added `TradeDetail` (`web-ui/src/app/models/api.types.ts`) matching the DTO; `TradesApiService.getById` + `trade-detail.component.ts` now use it. **Built: 0 errors.**

### 1.5 New tests added (run + passing this pass)

| File | Proves |
|------|--------|
| `tests/.../Unit/AddOns/TrailingEnabledToggleTests.cs` | `BuildConfig` honors `Trailing.Enabled` |
| `tests/.../Unit/AddOns/AddOnPackConfigFlowTests.cs` | pack → `ApplyPack` → `BuildConfig` → runtime config (the PK3 seam that had no e2e coverage) + ApplyPack determinism |
| `tests/.../Integration/Api/AddOnPacksApiTests.cs` | packs API contract: 3 seeded packs listed, preview returns numbers, **invalid bundle → 400**, upsert roundtrip, **run started with `usePackId` → 200** |
| `tests/.../Unit/Regime/RegimeToggleTests.cs` (extended) | detection-off allows every regime |

> `AddOnPacksApiTests` passed against real startup seeding this pass; if it ever flakes on seeding timing, gate the seeded-pack assertions on a short retry.

---

## 2. Carry-forward BUGS / ISSUES still open (prioritized)

| # | Sev | Item | Where | Notes |
|---|-----|------|-------|-------|
| B1 | 🔴 HIGH | **cTrader reconciliation** cTrader=17 vs DB=16 | `KernelBacktestLoop.PumpAsync`, `BacktestOrchestrator.cs:646/809/1034`, `CTraderBrokerAdapter.cs:340-358` | Pump-drain race: last close exec not consumed before stop. iter-39 added cBot logging (C1) — now diagnosable. Needs cTrader platform to verify. |
| B2 | 🟠 MED | **No `totalCount` on trades API** (W-C1) | `TradesController.cs`, `RunsController.cs` | `/api/trades` + `/runs/{id}/trades` page without a total → pagers can't show counts. |
| B3 | 🟠 MED | **Histogram bins cast to UTCTimestamp** (W-A5) | `run-analyzer`, scatter/histogram charts | Bin index `i` used as epoch time → wrong axis. Use a category/index axis. |
| B4 | 🟡 LOW | **Live-equity time discontinuity** (W-A6) | `run-monitor.component.ts:~220` | `Date.now()` fallback when `simTimeUtc` missing → axis jump. Use sim-time only. |
| B5 | 🟡 LOW | **Duplicate daily-pnl/analytics endpoints** (W-C5) | `BacktestAnalyticsController` vs `RunsController` | One path is likely dead. Consolidate. |
| B6 | 🟡 LOW | **DynamicSlTp double tuner path** | `BarEvaluator.cs:147-158` | DynamicSlTp recomputes `AddOnAutoTuner.Tune` inline instead of via `AddOnResolver`. Deterministic but duplicated; and the DynamicSlTp resolved in `KernelTrailingEvaluator` at registration is dead (BuildConfig ignores it). Unify when convenient. |

---

## 3. IMPROVEMENTS / decisions for the owner

### 3.1 ⚖️ OWNER DECISION — auto-tune the seeded strategies' trailing?

§1.1 set the 7 trailing strategies to `mode: "Custom"` (byte-identical, hand-tuned numbers preserved). The **whole point of iter-38 (D2)** is auto-tuned add-ons. Flipping those 7 to `"mode": "Auto"` makes their trailing auto-tune per symbol/timeframe — the headline feature — **but changes backtest results** (must re-baseline). Recommend: confirm with owner, flip to `Auto`, re-run baselines, eyeball that the tuned numbers are sane (`AddOnAutoTuner` / `/api/addons/preview`).

### 3.2 Angular quality leftovers (frontend-only, deferred by iter-39 §4/§8)

| Item | File | ~Effort |
|------|------|---------|
| A3 `takeUntilDestroyed` | `run-monitor.component.ts` | replace manual Subscription+setInterval+queueMicrotask, ~40 lines |
| A5 chart time helpers | 4 chart comps | extract `toUtcTimestamp`/`fromUtcTimestamp`, kill raw `*1000`/`/1000`, ~30 lines |
| Duplicate-component extraction | multiple components | larger refactor, tagged BLOCKER in HANDOVER §8 |
| NG8107 warning | `new-backtest.component.ts:176` | `s.stats?.totalTrades` → `s.stats.totalTrades` (type is non-null) |

### 3.3 S11 / S12 — CI gate + owner runtime review (PARKED)

- **S11:** Angular unit tests (NG-R12 near-zero coverage), flip ESLint `no-explicit-any` → error, wire CI, final `wwwroot` rebuild.
- **S12:** drive `run-shamshir` and owner-verify live: governor state populated, DateTime-`Z`, kernel monitor counters, packs CRUD, **the new "Bypassed" regime label**, **trailing toggle** in the strategy editor.

---

## 4. Kernel-consistency verdict (the owner's specific concern)

The add-on feature **is plugged into the kernel correctly** and does not undermine the iter-36 redesign:
- Per-bar management flows impure-evaluator → **event** → pure reducer → effect → venue → feedback → reducer (PartialTp: `PositionManager.Evaluate` → `PartialCloseRequested` → `ClosePartialOpenPosition` → venue partial fill → `OrderFilled(FilledLots<Lots)` → reduce-keep-open). 
- The 3 new events (`StopLossModifyRequested`, `PartialCloseRequested`, `AddOnsResolved`) are in `EngineReducer.Reduce` **and** pinned in `EngineReducerWiringTests`.
- Add-ons resolve **once at entry** and freeze on the position (determinism / K6). Pack id + regime fold into `ConfigSetId`.
- Journal kinds route through `KernelBacktestLoop.EventKindFor` and only remap events that never fire on the golden path.

The only consistency defect found was the regime dead-twin (§1.2, now fixed). No other twin/drift introduced by iter-38.

---

## 5. Definition of done for this wrap-up

- [ ] §0 gate fully green (esp. golden/characterization/journal subset — proves §1 is byte-identical).
- [ ] New tests in §1.5 pass (fix any harness/global-using mismatch; they were not compiled here).
- [ ] Owner decision on §3.1 (trailing `Auto` vs `Custom`); re-baseline if flipped.
- [ ] B1 cTrader reconciliation investigated (needs platform) or explicitly re-parked with the new cBot logs attached.
- [ ] S11/S12 scheduled or executed.
- [ ] `wwwroot` rebuilt + cleaned before commit.
