# P7.3 Evidence — Traps 3+1+2

**Date:** 2026-07-09
**Session:** #47 (P7.3 deliver)

## Gate Battery (fresh, this session)

| Gate | Result |
|------|--------|
| Build (`dotnet build TradingEngine.slnx`) | 0 errors, 5 warnings (pre-existing net6.0 TFMs) |
| Unit (`dotnet test TradingEngine.Tests.Unit`) | 716 passed, 0 failed, 6 skipped |
| Integration (`dotnet test TradingEngine.Tests.Integration`) | 120 passed, 0 failed, 0 skipped |
| Sim-fast (`dotnet test TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"`) | 144 passed, 0 failed, 0 skipped |
| Golden (`git diff --stat **/*golden*.json`) | empty (byte-identical) |
| ShippedPlaybook_Parses | 11/11 (includes new triage-sweep.json) |

## Trap 3 — Triage-Sweep Playbook

- **File:** `playbooks/triage-sweep.json`
- **Shape:** Sweeps EURUSD+GBPUSD × H1+M15 (4 cells) as exploration runs, awaits all, assert-gates each, reports.
- **Step kinds:** All existing (`ensure-data`, `start-run`, `await-run`, `assert-gates`, `report`) — no new step kind needed.
- **Parse test:** Added `[InlineData("triage-sweep.json")]` → `ShippedPlaybook_Parses` now 11/11 green.

## Trap 1 — Session Labels

- **Entity:** `TradeExcursionEntity.SessionLabel` (TEXT, nullable) added
- **Mapping:** `TradeExcursionMapping` maps `.HasColumnType("TEXT")`
- **Repository:** `IExcursionRepository.SaveAsync` now accepts `string? sessionLabel`
- **Repository impl:** `SqliteExcursionRepository.SaveAsync` stores `SessionLabel`
- **Service:** `PersistenceService.SaveExcursionAsync` passes `sessionLabel` through
- **Handler:** `TradePersistenceHandler.DrainAsync` calls `SessionDetector.Detect(trade.OpenedAtUtc)` and passes to `SaveExcursionAsync`
- **Migration:** `M47_SessionLabelAndEntryFilter` adds `SessionLabel` column to `TradeExcursions` (confirmed: schema shows `SessionLabel|TEXT|0`)

## Trap 2 — SpreadVolNoTradeFilter Wiring

- **Domain type:** `EntryFilterOptions` (record: `Enabled`, `MaxSpreadPips`, `MaxAtrPips`, `AtrIndicatorKey`)
- **Config entry:** `StrategyConfigEntry.EntryFilter` (nullable)
- **IStrategyConfig:** `EntryFilterOptions? EntryFilter => null;` (default null — existing strategies unchanged)
- **Entity:** `StrategyConfigEntity.EntryFilterJson` (TEXT, nullable)
- **DbContext:** Column mapping `.HasColumnType("TEXT")`
- **Store:** `SqliteStrategyConfigStore` ToEntity/ToEntry/UpsertAsync all handle `EntryFilterJson`
- **Loader:** `ConfigLoader` parses `entryFilter` from strategy JSON
- **Seeder:** `StrategyConfigSeeder.ParseFile` parses `entryFilter` from strategy JSON
- **Strategy wiring (TrendBreakout only):** `TrendBreakoutConfig` has `EntryFilter`. `Create()` instantiates `SpreadVolNoTradeFilter` when `Enabled=true`. `Evaluate()` checks `_entryFilters` before signal logic. Other 8 strategies unchanged (default null in IStrategyConfig).
- **Migration:** `M47_SessionLabelAndEntryFilter` adds `EntryFilterJson` column to `StrategyConfigs` (confirmed: schema shows `EntryFilterJson|TEXT|0`)

## DB State

```
Migration head: 20260709040707_M47_SessionLabelAndEntryFilter

TradeExcursions columns: Id, CreatedAtUtc, UpdatedAtUtc, RunId, PositionId, PathJson, SessionLabel (new)
StrategyConfigs columns: ..., EntryFilterJson (new)
```
