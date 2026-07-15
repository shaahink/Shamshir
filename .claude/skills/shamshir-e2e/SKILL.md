# Skill: shamshir-e2e

# Shamshir cTrader E2E Test Infrastructure

Run and verify cTrader backtest E2E tests that exercise the full NetMQ bridge:
engine + cBot + cTrader CLI → trade ledger reconciliation.

## Prerequisites

- cTrader credentials in `src/TradingEngine.Web/appsettings.Development.json` under `CTrader.CtId` / `CTrader.PwdFile` / `CTrader.Account` OR environment variables `CTrader__CtId` / `CTrader__PwdFile` / `CTrader__Account`
- cTrader CLI binary (auto-located via `CTraderCliLocator`)

## Run E2E tests

```powershell
# Fast smoke (300s timeout each)
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true&Category!=Slow"

# All E2E including diff/comparison
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"
```

The test suite uses `[Collection("CtraderSerial")]` so tests run one at a time
(one cTrader CLI instance at a time).

## Test hierarchy

| Test class | What it verifies |
|------------|-----------------|
| `CtraderE2EHarnessSmokeTests` | Engine starts, cTrader CLI runs, handshake completes, trades produced |
| `CtraderScenarioE2ETests` | Trade ledger integrity (no zero-entry-price, no $0-PnL real-movers), weekend edge cases, orphan process cleanup |
| `DiffE2ETests` | Per-trade cost reconciliation (cBot `shamshir-report.json` ↔ DB `TradeResults`), gross/net/commission/swap/exit-price match |
| `PipelineE2ETests` | Multi-symbol, multi-timeframe, pipeline data flow |
| `DiscoveryAuditTests` | 1-month full audit with strategy |

## Architecture

```
Test Layer
├── CtraderE2EHarness  ── orchestrates engine host + cTrader CLI
│   ├── NetMqMessageTransport (real NetMQ ROUTER/SUB)
│   ├── CTraderBrokerAdapter (engine-side)
│   ├── BacktestCli.InvokeAsync → ctrader-cli.exe subprocess
│   └── CtraderReportHarvester (post-run artifact collection)
├── CtraderDiffHarness ── joins cBot ShamshirTradeLogger ↔ DB TradeResults
│   └── CompareAsync(db, runId, reportJsonPath) → CtraderDiffResult
└── FakeCBot           ── simulated cBot for replay/unit testing
```

## Logging chain (critical for reconciliation)

1. **cBot (`TradingEngineCBot`)** writes via `ShamshirTradeLogger`:
   - `shamshir-report.json` — summary + history[] with `clientOrderId`, gross/net/commission/swap
   - `shamshir-events.json` — events[] with per-event costs
   - Written to `CbotReportDir` (passed as `--ReportPath` to cTrader CLI)

2. **Harness (`CtraderE2EHarness`)** calls `CollectCbotReports()`:
   - Copies `shamshir-report.json` → `Artifacts.ReportJsonPath`
   - Copies `shamshir-events.json` → `Artifacts.EventsJsonPath`

3. **Diff harness (`CtraderDiffHarness.CompareAsync`)**:
   - Parses `shamshir-report.json` (has `clientOrderId` in history[])
   - Joins to DB `TradeResultEntity.OrderId` (== `clientOrderId`)
   - Compares per-trade: Net, Gross, Commission, Swap, ExitPrice
   - Reports discrepancies by severity (Error/Warning/Info) and kind (Structural/Numeric)

## Known gotchas

- **cTrader CLI's native `--report-json` crashes** — `CtraderReportHarvester` reads the default
  `report.html` (embedded JSON) + `events.json` instead. Our cBot's `ShamshirTradeLogger` writes
  `shamshir-report.json` / `shamshir-events.json` INDEPENDENTLY — these are the primary source.
- **No bars = "failed" terminal state is EXPECTED in non-cTrader containers** — bars come from
  cTrader/NetMQ only. The replay path has 0 bars seeded by policy.
- **Dedup signature includes cost fields** (`CTraderBrokerAdapter.TryWriteExec` at line 550):
  `$"{OrderId}|{State}|{Price}|{Lots}|{GrossProfit}|{NetProfit}|{Commission}|{Swap}"` — cost
  corrections are NOT silently dropped.
- **E2E tests require `[Collection("CtraderSerial")]`** — parallel cTrader CLI instances
  would conflict on ports and the `Backtesting` output directory.

## Adding a new E2E test

```csharp
[Trait("Category", "E2E")]
[Trait("RequiresCTrader", "true")]
[Collection("CtraderSerial")]
public async Task MyNewTest()
{
    if (!HasCredentials) return;  // skip if no creds

    await using var harness = new CtraderE2EHarness("my-test-label")
        .WithSymbol("EURUSD", "H1")
        .WithDateRange(new DateTime(2024, 1, 15), new DateTime(2024, 1, 18));

    var result = await harness.RunAsync();
    result.Trades.Should().BeGreaterThan(0);
}
```

Base directory for this skill: file:///C:/Code/Shamshir/.claude/skills/shamshir-e2e
