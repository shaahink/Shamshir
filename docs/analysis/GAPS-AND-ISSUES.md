# Shamshir — Gap Analysis & Issue Inventory

**Generated:** 2026-06-23
**Branch:** `iter/31-costs-journal` (iter-38 add-ons + iter-39 cleanup)
**Method:** Static analysis across all reference docs, iteration handovers (31–39),
DECISIONS.md (D1–D84), OPEN-ISSUES.md, and full source inventory at the
domain → entity → API → UI layers.

> Items already documented in `docs/OPEN-ISSUES.md` are cross-referenced.
> Items newly discovered from this analysis are marked **[NEW]**.

---

## Critical (4) — Correctness-breaking or Permanent Data Loss

### GAP-C1: Strategy config JSON has zero add-on validation (only packs are validated)

**Severity:** Critical | **Effort:** Medium | **Status:** [NEW]

`AddOnPacksController.ValidatePack` validates add-on fields on packs, but strategy-level
`PositionManagementOptions` — seeded from `config/strategies/*.json` → `StrategyConfigs` table —
has **no validation path at all**. Strategies can carry:

- `Trailing.Enabled=true, Method="None"` → trailing silently disabled (zero log)
- `DynamicSlTp.RrMultipleTp=-5` → null TakeProfit on the intent
- `Ride.Enabled=true` with trailing `Method="StepPips"` → ride does nothing
- `PartialTp.CloseFraction=0` → partial TP never fires
- `DynamicSlTp.AtrMultipleSl=0` → stop placed at entry price

The only gate is `PositionManager.BuildConfig` silently degrading to safe-ish defaults.
**Fix:** Port `ValidatePack` logic to a `PositionManagementOptions.Validate()` method
called by `ConfigLoader` and the strategy upsert path. Log warnings for degraded fields.

| File | Line | Detail |
|------|------|--------|
| `Strategies/*Strategy.cs` | Evaluate() | Config loaded without validation |
| `PositionManager.cs` | BuildConfig() | Silent degradation, no log |
| `AddOnPacksController.cs` | ValidatePack() | Validation exists here but not reused |

---

### GAP-C2: TradeResultEntity missing Timeframe column — data loss for multi-TF runs

**Severity:** Critical | **Effort:** Medium | **Status:** K-GAP-5 (partial fix only)

The `TradeResults` table has `Symbol`, `StrategyId`, `Mode`, but **no `Timeframe`** column.
`BacktestRunEntity` stores `Periods` as a JSON array of all run timeframes. For multi-timeframe
runs there is no way to determine which timeframe a specific trade came from without joining
to the Bar table by timestamps.

The API layer exposes the run's timeframe via `TradeDetailResponse.Timeframe = "H1"` — but for
multi-TF runs every trade reports the same (first?) timeframe.

**Fix:** EF migration to add `Timeframe TEXT` to `TradeResults`. Thread it through close path:
`PublishTradeClosed` effect → `EngineReducer` → `EngineRunner` → `TradeResultEntity`. Set from
the position's bar context or the `OrderProposed` event.

| File | Line | Detail |
|------|------|--------|
| `TradeResultEntity.cs` | — | Column missing |
| `TradeResultMapping.cs` | — | No Timeframe property mapped |
| `EffectExecutor.cs` | PublishTradeClosed | Doesn't carry Timeframe |
| `EngineRunner.cs` | — | Multi-TF ambiguity |

---

### GAP-C3: No pre-flight bar-existence check before backtest engine starts

**Severity:** Critical | **Effort:** Low | **Status:** [NEW]

`RunsController.Start()` validates symbols, periods, dates, and balance — but **never checks
whether bars exist** in the DB for the requested symbol/timeframe/date range.

The check happens only after significant overhead:
1. `BuildLoadedConfigFromDbAsync()` — loads all configs, risk profiles, rules, governor, packs
2. `EngineHostFactory.Create()` — builds full inner `IHost` DI container
3. `innerHost.StartAsync()` — engine begins
4. `BacktestReplayAdapter.ConnectAsync()` → `FeedBarsAsync()`
5. Only then: `if (barCount == 0) { return error; }`

A single `IBarRepository.GetAsync(symbol, tf, from, to, ct)` call before step 1 would catch
this with zero overhead. The repository is already available through DI.

**Fix:** Call `IBarRepository.GetAsync` (with a small limit, e.g. 1) in `RunsController.Start()`
before orchestrator invocation. Return 400 with a clear message if no bars exist.

| File | Line | Detail |
|------|------|--------|
| `RunsController.cs` | 52–113 | No bar check in Start() |
| `BacktestOrchestrator.cs` | 566–666 | Check at barCount==0, too late |
| `BacktestReplayAdapter.cs` | 77–131 | Silent when bars empty |

---

### GAP-C4: `symbolRegistry.Get()` unguarded in BarEvaluator — crash for unknown symbols

**Severity:** Critical | **Effort:** Low | **Status:** [NEW]

`BarEvaluator.cs:135`:
```csharp
var symbolInfo = symbolRegistry.Get(intent.Symbol);
```
No try/catch. If a strategy fires on a symbol not in the registry, this throws an unhandled
exception and **terminates the entire bar evaluation loop** for all remaining strategies on that
bar. Events for other strategies on the same bar are silently lost.

Compare `ResolveHalfSpread` (line 248) which wraps the same call in try/catch and falls back
to a hardcoded half-spread with a warning log.

**Fix:** Wrap in try/catch. On failure: log warning, skip that strategy on this bar (continue
to next strategy), and emit a `StrategyVerdict` with reason "Unknown symbol: {symbol}".

| File | Line | Detail |
|------|------|--------|
| `BarEvaluator.cs` | 135 | Unguarded Get() |
| `BarEvaluator.cs` | 248 | Guarded pattern exists in same file |

---

## Serious (8) — Significant Impact on Reliability, UX, or Data Integrity

### GAP-S1: No guard against simultaneous backtests — SQLite corruption risk

**Severity:** Serious | **Effort:** Low | **Status:** [NEW]

`BacktestOrchestrator.Start()` adds to `_runs` (ConcurrentDictionary) and launches `RunAsync()`
as fire-and-forget. No semaphore, no capacity limit, no interlock. Two rapid POSTs to
`/api/runs` launch two inner `IHost` instances simultaneously competing for the same SQLite
database. SQLite serializes writers — concurrent writes produce `SQLITE_BUSY` errors, potentially
corrupting both runs. WAL mode helps reads but doesn't eliminate write serialization.

**Fix:** `SemaphoreSlim(1, 1)` or an `Interlocked` flag. Return 409 Conflict if a run is
already in progress. Or use a named mutex scoped to the DB file path.

| File | Line | Detail |
|------|------|--------|
| `BacktestOrchestrator.cs` | 205–224 | No concurrency guard on Start() |
| `BacktestOrchestrator.cs` | 608–609 | 30-min CTS timeout is not a concurrency limiter |

---

### GAP-S2: Duplicate from cTrader silently becomes replay — venue not preserved

**Severity:** Serious | **Effort:** Low-Medium | **Status:** [NEW] (related to T10)

Neither duplicate path propagates the source run's venue:
- **SPA modal:** `run-report.component.ts:648-653` passes `sourceRunId`, `usePackId`, and
  `disableRegime` — but **not** `venue`. The New Backtest form loads with `venue=''` (replay).
- **API duplicate:** `POST /api/runs/{id}/duplicate` receives empty body → no Venue in
  CustomParams → `ResolveUseCtrader(null) = false` → replay.

The stored `BacktestRunSummary` has **no `Venue` column**, so even the API can't recover
what the source used. A cTrader source run duplicated without explicit venue selection silently
runs as replay — different fills, different costs, different results.

**Fix:** Add `Venue` column to `BacktestRunEntity` / `BacktestRunSummary`. Read it in
`RunsController.Duplicate()` and propagate to `cfg.CustomParams["Venue"]`. Pass from SPA
duplicate modal as a query param.

| File | Line | Detail |
|------|------|--------|
| `BacktestRunEntity.cs` | — | No Venue column |
| `run-report.component.ts` | 648–653 | Duplicate params missing venue |
| `RunsController.cs` | 150–151 | Duplicate doesn't read source venue |
| `StartRunRequest.cs` | 20–22 | Stale XML comment mentions UseForBacktest |

---

### GAP-S3: `RegimeDetectionEnabled` flips to `false` under JSON deserialization

**Severity:** Serious | **Effort:** Low | **Status:** [NEW]

`AddOnPack` is a record: `public bool RegimeDetectionEnabled = true`. When deserialized from
JSON via `System.Text.Json` in `SqliteAddOnPackStore`, if the JSON payload **omits** the
`regimeDetectionEnabled` field, the serializer assigns `default(bool) = false` — **bypassing**
the record's `= true` default.

```json
// This deserializes RegimeDetectionEnabled = false (silently!)
{"id":"p1","name":"Test","addOns":{...}}
```

This is a well-known System.Text.Json behavior: property-level initializers on settable
properties are ignored during deserialization.

**Impact:** A pack saved without the toggle in the JSON body silently disables regime
detection, causing strategies to trade in all market regimes when they shouldn't.

**Fix:** Option A: Use `[JsonConstructor]` with a parameter default. Option B: Custom
converter. Option C: Set the property as `init`-only and not settable (but then it's
read-only after construction).

| File | Line | Detail |
|------|------|--------|
| `AddOnPack.cs` | — | Record default bypassed by STJ |
| `SqliteAddOnPackStore.cs` | 18 | PropertyNameCaseInsensitive = true |

---

### GAP-S4: JSON config seed files drift silently from DB — no detection

**Severity:** Serious | **Effort:** Medium | **Status:** [NEW]

All five seeders (`StrategyConfigSeeder`, `RiskProfileSeeder`, `PropFirmRuleSetSeeder`,
`GovernorOptionsSeeder`, `AddOnPackSeeder`) are strictly idempotent on first launch.
Once the DB has entries, the JSON files in `config/` are **never consulted again**.

There is no:
- Content hash on entities compared against JSON files
- Timestamp-based freshness check (JSON `lastWriteTimeUtc` vs DB `UpdatedAtUtc`)
- Version number in JSON or entity
- UI/API action to "re-seed from JSON"
- Startup warning/log when JSON was modified but DB unchanged

A developer editing `config/strategies/trend-breakout.json` expecting it to take effect on the
next launch will silently get the stale DB values.

**Fix:** Store a `SourceHash` on each config entity at seed time. On startup, compare JSON
hash to DB hash. Log a warning if they differ. Add a `POST /api/admin/re-seed` endpoint or
a `--re-seed` CLI flag. Consider a `"version"` field in JSON files.

| File | Line | Detail |
|------|------|--------|
| `StrategyConfigSeeder.cs` | — | One-shot, no drift detection |
| `RiskProfileSeeder.cs` | — | Same pattern |
| `MiddlewarePipeline.cs` | 17–41 | All 5 seeders run, no hash check |

---

### GAP-S5: DynamicSlTp Custom mode with zero/negative multiplier → null TakeProfit

**Severity:** Serious | **Effort:** Low | **Status:** [NEW]

In `BarEvaluator.cs`, when `DynamicSlTp.Mode == Custom` and `RrMultipleTp <= 0`:
```csharp
tpRr = dyn.RrMultipleTp;   // could be 0 or negative
// ...
var tp = SlTpHelpers.RRMultiple(entryPrice, stopLoss, direction, tpRr);  // returns null
intent = intent with { StopLoss = dynSl, TakeProfit = dynTp };  // dynTp = null
```
`SlTpHelpers.RRMultiple` returns `null` when `rrRatio <= 0`.

**Impact:** The trade is dispatched with a null TakeProfit — the take-profit is silently
disabled with zero logging. The position stays open until SL hit, force-close, or run end.

The `AddOnPacksController.ValidatePack` catches `RrMultipleTp > 0` for packs, but strategy-level
configs skip this validation entirely (see GAP-C1).

**Fix:** Guard in `BarEvaluator.cs`: `if (tpRr <= 0) { log warning; use baseline TP; }`.
Add validation to `ConfigLoader` for strategy-level `DynamicSlTp`.

| File | Line | Detail |
|------|------|--------|
| `BarEvaluator.cs` | 161–169 | No guard on RrMultipleTp |
| `SlTpHelpers.cs` | 40–41 | Returns null for rrRatio <= 0 |
| `AddOnPacksController.cs` | 83–84 | Validation exists for packs only |

---

### GAP-S6: Journal table grows unboundedly — no archiving, purging, or TTL

**Severity:** Serious | **Effort:** Medium | **Status:** [NEW]

The `Journal` table uses `ChannelJournalWriter` (Wait mode, 50K capacity, 500-item batches,
5 retries) — solid write integrity. But there is **zero lifecycle management**:

- No archiving by run age
- No purging of old runs' journal entries
- No row cap, TTL, or scheduled cleanup
- No API endpoint to delete journal entries for a run

**Growth rate:** For a 30-day H1 single-symbol backtest: ~720 bars × ~5 events/bar = ~3,600 rows.
Multi-symbol × multi-year: easily millions of rows. The composite index `(RunId, SimTimeUtc)`
works but the sheer volume eventually degrades query performance.

**Fix:** Option A: Add a configurable retention policy (keep last N runs, delete older
journal rows on new run start). Option B: Add `DELETE /api/runs/{id}/journal` endpoint.
Option C: Make journal retention opt-in with a `--journal-mode=summary` flag that only
writes trades/breaches/governor events, not every bar.

| File | Line | Detail |
|------|------|--------|
| `JournalEntryEntity.cs` | — | No TTL, no partition |
| `ChannelJournalWriter.cs` | — | Write-only, no lifecycle |
| `RunsController.cs` | — | No delete-journal endpoint |

---

### GAP-S7: BacktestReplayAdapter doesn't fail fast when bars are empty

**Severity:** Serious | **Effort:** Low | **Status:** [NEW]

`ConnectAsync()` unconditionally sets `IsConnected = true` and launches `FeedBarsAsync()`.
When `bars.Count == 0`, `FeedBarsAsync` logs a warning but:
- Doesn't throw an exception
- Doesn't set `IsConnected = false`
- Doesn't write a terminal/error event to channels
- Doesn't cancel the feed CTS

The channels complete gracefully, so `BarStream.Completion` resolves normally. Any code path
using this adapter directly would silently produce an empty backtest with zero trades.

**Fix:** After the warning log, throw `InvalidOperationException("No bars found for {symbol}
{tf} in [{from}–{to}]")` or set an error flag. The orchestrator already checks `barCount == 0`
but direct adapter consumers have no guard.

| File | Line | Detail |
|------|------|--------|
| `BacktestReplayAdapter.cs` | 99–101 | Warning only, no error propagation |
| `BacktestReplayAdapter.cs` | 77 | IsConnected set to true unconditionally |

---

### GAP-S8: Dead `CTrader:UseForBacktest` config key + stale XML doc comment

**Severity:** Serious | **Effort:** Low | **Status:** [NEW]

`VenueResolution` is now a pure function in `BacktestOrchestrator.ResolveUseCtrader` that
ignores `IConfiguration` entirely (fixed in iter-38 D6). However:

1. `appsettings.Development.json:23` still has `"UseForBacktest": "false"` — dead config,
   zero code reads this key (confirmed: grep across all `.cs` files returns 0).

2. `StartRunRequest.cs:20-22` has a stale XML comment:
   ```csharp
   /// Absent = the configured default (CTrader:UseForBacktest)
   ```
   The real behavior is: absent → `ResolveUseCtrader(null)` → `false` → replay.
   `CTrader:UseForBacktest` is never read.

**Fix:** Remove the dead key from `appsettings.Development.json`. Update the XML comment.

| File | Line | Detail |
|------|------|--------|
| `appsettings.Development.json` | 23 | Dead config key |
| `StartRunRequest.cs` | 20–22 | Stale XML comment |

---

## Medium (13) — UX, Reliability, and Feature Completeness

### GAP-M1: Missing validation for Trailing numeric fields in packs

**Severity:** Medium | **Effort:** Low | **Status:** [NEW]

`AddOnPacksController.ValidatePack` checks that `Method` is in the valid set, but doesn't
validate numeric parameters. Accepted without error:
- `AtrMultiple = -5` for Method=AtrMultiple → negative offset → stop on wrong side
- `StepPips = 0` for Method=StepPips → no movement
- `StructureLookbackBars = 0` for Method=Structure → empty lookback

**Fix:** Add numeric range validation: `AtrMultiple > 0`, `StepPips > 0`, `StructureLookbackBars > 0`.

| File | Line | Detail |
|------|------|--------|
| `AddOnPacksController.cs` | 56–59 | Method validated, params not |
| `AddOnPacksController.cs` | 62–86 | Other validations exist as pattern |

---

### GAP-M2: `Trailing.Enabled=true` with `Method="None"` silently disables trailing

**Severity:** Medium | **Effort:** Low | **Status:** Fix landed in iter-38 but gap remains for strategy-level configs

`PositionManager.BuildConfig` now gates on `Enabled` (iter-38 wrap-up fix). But strategy-level
config JSON can carry `Trailing.Enabled=true, Method="None"` with zero logging when it degrades.
The `AddOnPacksController` catches this for packs, but strategy configs skip all validation.

**Fix:** See GAP-C1 — port validation to `ConfigLoader` for strategy-level configs.

| File | Line | Detail |
|------|------|--------|
| `AddOnPacksController.cs` | 58 | Rejects Method="None" when Enabled |
| `ConfigLoader.cs` | — | No equivalent validation |

---

### GAP-M3: No PositionId index or unique constraint on TradeResults

**Severity:** Medium | **Effort:** Low | **Status:** M15 in OPEN-ISSUES.md

`TradeResultMapping` has indexes on `ClosedAtUtc` and `StrategyId`, but `PositionId` is a plain
column: no index, no unique constraint, no foreign key. If `PublishTradeClosed` is ever invoked
twice for the same position, the DB silently accepts duplicate rows. Querying by PositionId
requires a full table scan.

**Fix:** Add unique index on `PositionId`. Add foreign key to `Positions` table (optional, but
adds referential integrity).

| File | Line | Detail |
|------|------|--------|
| `TradeResultMapping.cs` | — | No PositionId index |
| `TradingDbContextModelSnapshot.cs` | 810–811 | Plain TEXT column only |

---

### GAP-M4: cTrader E2E pump-drain race — cTrader=17 trades, DB=16

**Severity:** Medium | **Effort:** Medium | **Status:** OPEN-ISSUES.md, T1/T7 area

`TradeLedger_ClientOrderIdReconciliation_NoMissingTrades` shows cTrader reporting 17 trades
but only 16 persisted to DB. Engine stops processing before the last execution frames drain
from the NetMQ channel. The K5 tail drain logic was added for the replay path but the cTrader
path may have a timing gap between `CompleteBarAsync` and the final execution drainage.

**Fix:** Add a final pump after the last bar in the cTrader path. Ensure the `shutdown` command
is sent only after all execution frames are drained. CBOT logging added in iter-39 enables
diagnosis.

| File | Line | Detail |
|------|------|--------|
| `CTraderBrokerAdapter.cs` | — | Tail drain may be incomplete |
| `KernelBacktestLoop.cs` | — | Final pump after bar loop |

---

### GAP-M5: Missing per-trade Timeframe column — deferred from K-GAP-5

**Severity:** Medium | **Effort:** Medium | **Status:** K-GAP-5 (partial, deferred)

The SPA side is fixed (reads run timeframe). The entity side is not: `TradeResultEntity` has
no `Timeframe` column. For multi-TF runs, every trade from that run reports the same
(declarative run-level) timeframe. See GAP-C2 for full resolution.

---

### GAP-M6: Auto-mode add-on validation rejects unused placeholder values

**Severity:** Medium | **Effort:** Low | **Status:** [NEW]

When `Mode = Auto`, the tuner overwrites stored values at runtime. But `AddOnPacksController`
validates stored values unconditionally. Example: a pack with `Breakeven.TriggerRMultiple=-1,
Mode=Auto` is **rejected** with "must be >= 0" even though the tuner would compute and
override it. Users must store syntactically valid placeholder values for dead fields.

**Fix:** Skip numeric validation when `Mode == Auto && Enabled`. Only validate structural
fields (Enabled, Method) for Auto-mode add-ons.

| File | Line | Detail |
|------|------|--------|
| `AddOnPacksController.cs` | 62–86 | Validates all numeric fields regardless of Mode |

---

### GAP-M7: Signal gate unaware of add-on pack changes

**Severity:** Medium | **Effort:** Low | **Status:** [NEW]

`ISignalGate.Check` gates on per-strategy reentry cooldowns from `ReentryOptions`. But add-on
packs can enable `PartialTp` (which splits one close into two fills) or modify trailing behavior
(which changes exit timing). The signal gate's cooldown tracking assumes one-open/one-close
per signal and may miscount open positions or cooldown bars when add-ons change close behavior.

**Fix:** Review `SignalGateService.OnPositionClosed` for consistency when PartialTp splits one
position close into two execution events. Add test coverage for add-on + cooldown interaction.

| File | Line | Detail |
|------|------|--------|
| `SignalGateService.cs` | OnPositionClosed | Assumes one close per signal |
| `SignalGateService.cs` | OnPositionOpened | May miscount with partial closes |

---

### GAP-M8: Auto-tuner PartialTp uses static values — not timeframe/volatility-based

**Severity:** Medium | **Effort:** Low | **Status:** [NEW]

`AddOnAutoTuner.Tune` returns `partialTpTriggerR = 1.0` and `partialTpCloseFraction = 0.5`
regardless of timeframe, symbol, or volatility. The source comment says "STARTING heuristics...
the agent calibrates the constants against AddOnAutoTunerTests." In Auto mode, PartialTp
is functionally identical to Custom with defaults — a user expecting timeframe-adjusted
partials (e.g., higher R trigger on D1 vs M1) would be surprised.

**Fix:** Calibrate `partialTpTriggerR` against timeframe tier (similar to `tfBase` for
trailing). E.g., M1→0.8, M5→0.9, M15→1.0, H1→1.2, H4→1.5, D1→2.0.

| File | Line | Detail |
|------|------|--------|
| `AddOnAutoTuner.cs` | 35 | Static values, comment acknowledges |

---

### GAP-M9: `TrailingBaseFor` and `TfTier` have asymmetric W1 handling

**Severity:** Medium | **Effort:** Low | **Status:** [NEW]

Both functions lack an explicit `W1` case but fall to different defaults:
- `TrailingBaseFor` → `default: 2.5` (W1 gets 2.5)
- `TfTier` → `default: 3` (W1 gets tier 3)

Inconsistent default values for the same timeframe across two related functions. W1 is rarely
used but the asymmetry is a latent inconsistency.

**Fix:** Add explicit `Timeframe.W1` cases with intentional values, or unify the tier/base
into a single lookup.

| File | Line | Detail |
|------|------|--------|
| `AddOnAutoTuner.cs` | 74 | TrailingBaseFor missing W1 |
| `AddOnAutoTuner.cs` | — | TfTier missing W1 |

---

### GAP-M10: Ride enabled with StepPips trailing silently does nothing

**Severity:** Medium | **Effort:** Low | **Status:** [NEW]

`EffectiveAtrMultiple` widens ATR multiples for Ride. If the trailing method is `StepPips`,
the ATR multiple is unused — `ComputeTrail` for StepPips ignores ATR entirely. Ride shows as
enabled in config, is journaled as `"RIDE"` based on ADX, but the stop value does not change.
No cross-add-on consistency check exists.

**Fix:** In `AddOnPacksController.ValidatePack`: if `Ride.Enabled && Trailing.Enabled` and
Trailing Method is not ATR-based, reject with "Ride requires an ATR-based trailing method."
Same check in strategy config validation (see GAP-C1).

| File | Line | Detail |
|------|------|--------|
| `PositionManager.cs` | 142–143 | StepPips ignores ATR |
| `PositionManager.cs` | 252–257 | EffectiveAtrMultiple unused by StepPips |
| `PositionManager.cs` | 262–266 | Ride journal tag fires regardless |

---

### GAP-M11: Strategy JSON edit has no transaction wrapping

**Severity:** Medium | **Effort:** Low | **Status:** [NEW]

The strategy detail page patches individual JSON fields. `PUT /api/strategies/{id}/config`
accepts a partial JSON body, merges it into the existing entity, and upserts. No transaction
wrapping the read-merge-write cycle. A concurrent edit could silently overwrite.

**Fix:** Use EF Core's concurrency token (`[ConcurrencyCheck]` or `IsRowVersion()`) on the
entity, or wrap in a serializable transaction with retry.

| File | Line | Detail |
|------|------|--------|
| `StrategiesController.cs` | — | No concurrency guard on upsert |

---

### GAP-M12: No dedup guard on `TradeResults.PositionId`

**Severity:** Medium | **Effort:** Low | **Status:** M15 in OPEN-ISSUES.md

If `PublishTradeClosed` is ever retried or duplicated, two `TradeResultEntity` rows with
different `Id` but same `PositionId` would be inserted. No unique constraint, no upsert, no
application-level dedup on the persist path.

**Fix:** Add unique index on `PositionId` (or composite on `(RunId, PositionId)`). Add
application-level `TryAdd` check in `TradePersistenceHandler`.

| File | Line | Detail |
|------|------|--------|
| `TradeResultMapping.cs` | — | No unique constraint on PositionId |
| `TradePersistenceHandler.cs` | — | No dedup before insert |

---

### GAP-M13: Run list page has no search, filter, or pagination

**Severity:** Medium | **Effort:** Low | **Status:** [NEW]

`GET /api/runs` returns all runs. The table has no search bar, no date range filter, no
strategy/symbol filter, no pagination control. All SQLite runs are loaded into memory for
every page load. With dozens of backtest runs, this becomes slow.

**Fix:** Add query params to `GET /api/runs`: `?search=&from=&to=&strategyId=&symbol=&skip=&take=`.
Server-side: filter in SQL. Client-side: search bar + pagination controls.

| File | Line | Detail |
|------|------|--------|
| `RunsController.cs` | — | No query params |
| `run-list.component.ts` | — | No search/filter/pagination UI |

---

## Low (8) — Cosmetic, Edge Cases, and Documentation

### GAP-L1: Tuner breakeven offset has no upper clamp on extreme spread

`beOffset = Math.Ceiling(vol.TypicalSpreadPips * 1.5) + 1`. For a 200-pip spread (exotic):
`beOffset = 301 pips` — breakeven would need 301 pip movement beyond trigger R. No clamp.
**File:** `AddOnAutoTuner.cs`.

### GAP-L2: Inconsistent spread fallback between tick and volatility context on unknown symbols

`KernelTrailingEvaluator.BuildVolatility` sets `spreadPips = 0` on unknown symbol. But
`ResolveHalfSpread` returns `0.00005m` (~0.5 pip) for the same symbol. The tick uses ~1-pip
spread while the tuner sees zero spread — breakeven offsets are inconsistent.
**Files:** `KernelTrailingEvaluator.cs:100-112`, `KernelTrailingEvaluator.cs:124-128`.

### GAP-L3: DynamicSlTp with zero ATR silently bypasses with no log

When `atrPrice <= 0`, the entire DynamicSlTp block in `BarEvaluator` is skipped. Baseline SL/TP
used instead. Zero logging of this fallback.
**File:** `BarEvaluator.cs:145-146`.

### GAP-L4: `InitialRiskAmount` stored in `PositionManagementConfig` but never read

`BuildConfig` stores it; `PositionManager.Evaluate` never reads it. In the kernel path it's
`0m`; in the old `PositionTracker` path it carries the real risk amount. Latent inconsistency
if the field is ever used.
**File:** `PositionManager.cs:222`, `KernelTrailingEvaluator.cs:71`.

### GAP-L5: `RotationMode.Disabled` — dead config

`config/rotation.json` exists with `"mode": "Disabled"`. No code implements strategy rotation.
The `StrategyRotationOptions` record exists in Domain but is never wired into production.
**File:** `config/rotation.json`, `StrategyRotationOptions.cs`.

### GAP-L6: Legacy `BacktestController` diverged from main `RunsController`

`POST /api/backtest/start` lacks `StrategyOverrides`, `UsePackId`, `DisableRegime`,
`PerStrategyPackIds`. Any client hitting the old endpoint gets a restricted feature set.
**File:** `BacktestController.cs:44-78`.

### GAP-L7: `AddOnAutoTuner.Tune` invoked eagerly even when no add-ons are enabled

Tuner runs on every position registration. On the default/golden path (zero add-ons enabled),
output is computed and immediately discarded. Negligible perf cost but wasteful.
**File:** `KernelTrailingEvaluator.cs:70`, `AddOnResolver.cs:18`.

### GAP-L8: Static `ResolveUseCtrader` — no DI wrapper over venue resolution

No abstraction layer. If someone bypasses this and reads `IConfiguration["CTrader:UseForBacktest"]`
directly (the old pattern), the footgun returns. Mitigated by being the only resolution point
today, but fragile.
**File:** `BacktestOrchestrator.cs:248-253`.

---

## Cross-Cutting Gaps

### CC1: Two-tier architecture for add-on config — declarative vs runtime split

`PositionManagementOptions` (declarative, from strategy JSON/packs) and `PositionManagementConfig`
(runtime, used by PositionManager) are two different config objects. `DynamicSlTp` lives only
in the declarative tier and is consumed directly by `BarEvaluator`, bypassing `PositionManager`
entirely. To understand all position-management behavior, one must look at both config objects
and two evaluator components. No clear pattern for which add-ons belong to which tier.

**Risk:** Future developers adding a new add-on must decide which tier to implement in without
clear guidance, potentially picking the wrong tier and creating dead code or silent no-ops.

### CC2: Strategy-centric vs run-centric entity model split

The entity model has a split: `StrategyConfigEntity` stores strategy definitions (durable across
runs), while `BacktestRunEntity` stores per-run snapshots. But the trade/journal/equity
entities have thin run linkage (`RunId` string) without referential integrity (no FKs).
Cross-run queries (e.g., "show all trades by this strategy across all runs") require full
table scans on `StrategyId`.

### CC3: Config resolution is at startup only — no live reload

Run config is frozen at `EngineHostFactory.Create()` time. If a strategy config is edited
mid-run (via the Strategy Detail page), the running backtest never sees it. This is by design
for deterministic replay but creates a UX expectation gap: users editing configs during a run
may expect them to take effect.

---

## Items Already Tracked in OPEN-ISSUES.md (Cross-Reference)

The following are already documented in `docs/OPEN-ISSUES.md` and are not duplicated here.
See that file for full details.

| OPEN-ISSUES Ref | Description | Current Status |
|----------------|-------------|----------------|
| T1 | cBot wall-clock vs sim-time for entry timestamps | Partial fix (algo needs rebuild) |
| T6 | Trades show no commission/swap (cTrader path) | Open |
| T7 | Live journal only shows CLOSE (no SIGNAL/ORDER/FILL) | Open |
| T8 | Governor disable via UI doesn't take effect | Fixed in iter-38 |
| T10 | Duplicate is pointless (re-runs via engine, not replay) | Open (see GAP-S2) |
| T11 | Live equity chart DD line broken | Open |
| T12 | No DD timeline on run report | Open |
| H11 | Race on RiskManager.CurrentState in live path | Open |
| H13 | NetMQ counter semantics wrong | Open |
| H17 | Bar-range vs tick-based SL/TP divergence | Open |
| M3 | cBot Stop() from NetMQ poller thread | Owner verify |
| M6 | ProfitTarget uses balance not equity | Open |
| M8 | DrawdownVelocity stale between resets | Open |
| M9 | IndicatorSnapshotService CancellationToken never checked | Open |
| M13 | EntryPlanner no bounds check on SL/TP prices | Open |
| CT-1 | cTrader E2E silently skip when env not configured | Fixed in iter-36 |
| 31-A2 | cBot cost itemization | Open |
| 31-C2 | Live limit path verification | Blocked |
| 31-B2 | Monitor lossless journal | Open |
| 32-P4 | Strategy browse/edit UI | Partial (landed in iter-34) |
| 32-P5 | New-Backtest per-run override UI | Partial (landed in iter-37/38) |

---

## Summary By Fix Effort

| Effort | Gaps | Cumulative Impact |
|--------|------|-----------------|
| **Low** (≤ 2 hours) | C3, C4, S1, S3, S8, M1, M2, M6, M9, M10, M12, L1-L8 | Fixes the most acute crashes, data loss risks, and UX footguns |
| **Medium** (≤ 1 day) | C1, C2, S2, S4, M3, M4, M5, M7, M8, M11, M13 | Closes validation gaps, entity completeness, puzzle pieces |
| **Large** (multi-day) | S6 (journal lifecycle design) | Infrastructure design decision needed first |

---

## Priority Sequencing (Suggested)

1. **C3 + S1** — Pre-flight bar check + concurrent backtest guard (safety rails)
2. **C4 + S3** — Crash fix + boolean deserialization fix (correctness)
3. **C1 + S5** — Strategy add-on validation (correctness + UX)
4. **S8 + S7** — Dead config removal + adapter fast-fail (hygiene)
5. **M1-M3-M12** — Validation and constraint fixes (data integrity)
6. **S2 + C2** — Venue preservation + Timeframe column (completeness)
7. **S4** — Config drift detection (operational)
8. **M4-M5-M7-M8-M9-M10-M11-M13** — Remaining medium items
9. **L1-L8** — Edge cases and cosmetic
10. **S6** — Journal lifecycle (design decision first)
