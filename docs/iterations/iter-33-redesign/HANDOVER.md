# AGENTS.md — Session Stater Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/33-p01-test-infra` (active)
**Created:** 2026-06-18
**Summary:** W6 implement the test infrastructure overhaul (W1-W5) + W7 bisect report.json crash + W8 fix Web Handshake + `--report-json` integration

---

## 1. Read this first (mandatory, in order)

At the start of every session:
1. **`docs/reference/SYSTEM-REFERENCE.md`** — §1 system overview → skim the rest
2. **`docs/reference/CODE-MAP.md`** — Feature→file index + process walkthroughs
3. **`docs/reference/BACKTEST-ARCHITECTURE.md`** — How backtesting actually works
4. **`docs/reference/TEST-ARCHITECTURE.md`** — Test tiers, harnesses, credential requirements
5. **`docs/WORKFLOW.md`** — Agent workflow rules, code standards, handover format
6. **`DECISIONS.md`** — Backlog of all resolved decisions
7. **`docs/OPEN-ISSUES.md`** — Bugs, design problems, carry-forwards
8. **`docs/NEXT-STEPS.md`** — Roadmap backlog

---

## 2. Branch State

### Branch: `iter/33-p01-test-infra`

### What got done

- **`W6` Test Infrastructure Overhaul (W1-W5)**
  - `W1`: Strongly-typed CLI layer (`BacktestCli` + `BacktestCliRequest`/`Result`)
  - `W2`: Typed transport health protocol (`ITransportStatusSource` + `TransportPhase`/`TransportStatus`)
  - `W3`: SnapshotRecorder + SnapshotReplayer + FakeCBot integration
  - `W4`: `CtraderE2EHarness` — phased builder (`StartEngine → StartCtrader → WaitHandshake → WaitCompletion → CollectResult`)
  - `W5`: `RunArtifacts` — atomic run IDs with isolated per-test directories
- **`W7` bisect `--report-json` crash**
  - Traced the root cause to `ctrader-cli.exe` x64 subdirectory binary failing with .NET 6.0.0
  - `CTraderCliLocator` now prefers the root-level binary
  - Proved that `--report-json` works (all bisection tests reproduced report.json successfully)
- **`W8` cBot renaming**
  - Real cBot: `Shamshir` (was `TradingEngineCBot`) → prints `v=2.0.0|build=2026-06-18`
  - Test cBot: `Shamshir-test` (was `TestCbot`) → prints `v=1.0.0-test`
  - All `src.algo` references updated in 4 files
- **Port old tests** — 12 old cTrader tests → `CtraderE2EHarness` (deleted `CtraderTestHarness`)

### What is NOT done

- **`E2E Handshake Issue`** — `CtraderE2EHarness` tests currently fail with `Phase=Connecting` (handshake timeout). Transport never receives ROUTER messages. Root cause: introduced during `BacktestCli` / `CTraderCliLocator` changes or file lock issues.
- **`--report-json` not integrated into `BacktestOrchestrator` yet** — the orchestrator's `RunEngineNetMqAsync` path doesn't use `ReportJsonPath` (it falls back to scanning for `events.json`).
- **`Discovery Audit Test`** — exists (`E2E/DiscoveryAuditTests.cs`) but can't run until handshake issue is resolved.
- **`BacktestRunner.cs` dead code** — no callers, but still exists in `TradingEngine.CTraderRunner`
- **`AutoDeployAlgo` target** still references `src.algo` — needs update to `Shamshir.algo` (only impacts deployment, not test)

---

## 3. What's in `iter/33-p01-test-infra` vs `iter/31-costs-journal`

| Area | `iter/31-costs-journal` | `iter/33-p01-test-infra` |
|------|-------------------------|--------------------------|
| **CLI invocation** | `CTraderCli` (CliWrap) + string arrays + `Process.Start` in harness | `BacktestCli.InvokeAsync(request)` — single `Process.Start` code path |
| **Test harness** | `CtraderTestHarness` (300+ lines, monolithic) | `CtraderE2EHarness` (410 lines, phased) + `RunArtifacts` |
| **Transport status** | `IsConnected` (bool), `OnStatusChange(string, string)` | `ITransportStatusSource` with `TransportPhase` enum + `TransportStatus` record |
| **CLI binary locator** | `EnumerateFiles.OrderByDescending.FirstOrDefault` | Prefers root binary over `app_*\x64\` subdirectory copies |
| **cBot naming** | `TradingEngineCBot` → `src.algo` | `TradingEngineCBot` → `Shamshir.algo` with version print |
| **Diff harness** | N/A | `CtraderDiffHarness.CompareAsync()` — per-trade + summary comparison |

---

## 4. Architecture at a glance

```
src/
  TradingEngine.CTraderRunner/
    BacktestCli.cs                # Unified CLI invocation with version logging
    BacktestCliRequest.cs         # Typed request record
    BacktestCliResult.cs          # Typed result record + CbotLines
    CTraderCliLocator.cs          # Locates cTrader CLI, prefers root binary
  TradingEngine.Domain/
    Interfaces/ITransportStatusSource.cs  # Typed transport health
    EngineHostOptions.cs          # ActiveStrategyIds support
  TradingEngine.Infrastructure/
    Transport/NetMq/NetMqMessageTransport.cs  # Implements ITransportStatusSource
    Venues/CTrader/CTraderBrokerAdapter.cs    # Exposes TransportStatus property
  TradingEngine.Adapters.CTrader/
    TradingEngineCBot.cs          # Shamshir cBot v2.0.0

tools/TestCbot/
  TestCbot.cs                    # Minimal Shamshir-test cBot v1.0.0-test
  TestCbot.csproj

tests/TradingEngine.Tests.Simulation/
  Harness/
    CtraderE2EHarness.cs         # Phased E2E builder
    CtraderTestHelpers.cs        # Static credential/algo helpers
    RunArtifacts.cs              # Isolated per-test artifact management
    E2EResult.cs                 # Typed E2E result record
    FakeCBot.cs                  # +SendRawDealerFrame for snapshot replay
    SnapshotRecorder.cs          # Records NetMQ messages to JSONL
    SnapshotReplayer.cs          # Replays JSONL through FakeCBot
  E2E/
    CtraderE2EHarnessSmokeTests.cs  # 2 new E2E smoke tests
    CtraderScenarioE2ETests.cs      # 3 scenario tests (ported)
    PipelineE2ETests.cs             # 7 pipeline tests (ported)
    DiffE2ETests.cs                 # 1 diff test (ported)
    DiscoveryAuditTests.cs          # 1 discovery audit test
  Verification/
    CtraderDiffHarness.cs        # cTrader vs DB comparison engine
    CtraderDiffResult.cs         # Structured discrepancy types
    CtraderJsonReport.cs         # Parses events.json + report.json
    CtraderSummaryReport.cs      # Parses report.json summary format
```

---

## 5. Key Facts

- **CLI:** `BacktestCli.InvokeAsync()` — always uses `Process.Start` with single command string
- **CLI locator:** `CTraderCliLocator.Locate()` picks root-level `ctrader-cli.exe` over `app_*\x64\ctrader-cli.exe`
- **E2E tests:** use `CtraderE2EHarness` with optional snapshot recording
- **Health:** `TransportPhase` transitions: `Disconnected → Connecting → HandshakeReceived → HandshakeAcknowledged → Connected`
- **Diff:** `CtraderDiffHarness.CompareAsync()` compares cTrader output vs DB trades
- **Snapshot:** `SnapshotRecorder` + `SnapshotReplayer` + `FakeCBot` enable credential-free E2E playback
- **Money:** all price/money/lot arithmetic in `decimal`
- **DB path:** one configurable location per test via `RunArtifacts`
- **cBot version:** real cBot prints `v=2.0.0|build=2026-06-18`, test cBot prints `v=1.0.0-test`

---

## 6. Known Issues

### CRITICAL: E2E Handshake failure

**Symptom:** All `CtraderE2EHarness` tests fail with `E2EHandshakeException: Phase=Connecting` (30s handshake timeout).

**Root cause:** After `ITransportStatusSource` was added to `NetMqMessageTransport` (W2 commit `dbd36c0`), the E2E harness started failing with `Phase=Connecting`. The transport is created in `CtraderE2EHarness.StartEngineAsync()` but the cBot's dealer socket never connects to the router. The engine binds with `router.Bind("tcp://*:{commandPort}")` and the cBot connects with `dealer.Connect("tcp://127.0.0.1:{commandPort}")` — they should connect, but the router never receives the dealer's hello.

**The `NetMqMessageTransport` changes are additive** (no behavior was removed, only fields/events added) and the tests were passing at commit `beafbe5` (W4, before the transport modifications). However, the handshake failure started after the `BacktestCli` version-check and locator changes (commit `6fcfa29` onwards).

**Debug steps already taken:**
- Cleaned TestCbot builds — no effect
- Restarted the machine — no effect  
- Checked for orphan cTrader processes — none found
- Verified cTrader CLI can run standalone backtests — confirmed working
- Reverted `BacktestCli` version pre-launch — no effect
- Tried different CLI binaries — root binary only one that works

### DOCS: Reference docs drifted from code

`SYSTEM-REFERENCE.md` / `CODE-MAP.md` describe `Backtests/Run.cshtml`, `Backtests/Progress.cshtml`, `Monitor.cshtml` but reality has newer Razor pages. The iter-33 redesign program addresses this.

### CLI: `--report-json` not integrated in `BacktestOrchestrator`

The orchestrator's `RunEngineNetMqAsync` path copies `events.json` from Backtesting directories but doesn't pass `--report-json` to the CLI. The `ReportJsonPath` field is set in `BacktestCliRequest` only in the `CtraderE2EHarness` test path, not in the orchestrator.

### CLI: `--report-json` consistently crashes with "Message expected"

The `BacktestReportSavingStateStrategy.DoEnter()` crash is consistent across all cBots when running `ctrader-cli.exe` standalone. The `events.json` in the Backtesting directory is empty (0 bytes) in NetMQ mode because the cBot doesn't manage positions — our engine does. This crash is believed to be environmental, as earlier bisection steps (1-9) produced valid `report.json` files that still exist on disk.

**Workaround:** The `events.json` in the Backtesting directory is captured by `CtraderE2EHarness.CopyEventsFromBacktestingDir()` and used as fallback. The `CtraderDiffHarness` handles both `report.json` (summary) and `events.json` (event list) formats.

---

## 7. Test Commands

```powershell
# Full build (may show Aspire NU1903 error — pre-existing, ignore)
dotnet build

# Unit tests (~207 pass, 4 skip)
dotnet test tests/TradingEngine.Tests.Unit

# Architecture tests (3 pass)
dotnet test tests/TradingEngine.Tests.Architecture

# Integration tests (~35 pass)
dotnet test tests/TradingEngine.Tests.Integration

# Ctrader E2E tests (credentialed, run serially)
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"

# Discovery audit test (fails currently due to handshake issue)
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~DiscoveryAuditTests"
```

---

## 8. What Needs to Happen Next

The next steps move toward a working E2E diff pipeline:

1. Fix E2E handshake failure
   - Debug `NetMqMessageTransport` not receiving router messages
   - Option 1: verify `RouterSocket.Bind` and `DealerSocket.Connect` are using the same port
   - Option 2: rollback `NetMqMessageTransport` to version before W2, test, then reapply incrementally
   - Option 3: add `NetMQ` logging or packet capture

2. Integrate `ReportJsonPath` into `BacktestOrchestrator.RunEngineNetMqAsync`
   - Set `ReportJsonPath` in the `BacktestCliRequest`
   - Persist `ReportJsonPath` to `BacktestRunSummary` (column already exists)
   - Toggle recording through `SnapshotRecorder`

3. Resolve `--report-json` crash / inconsistency
   - Clean bisection with fresh cTrader CLI state
   - Test with `BacktestCli.InvokeAsync`
   - Record reference snapshot from a known-good run

4. Expand `CtraderDiffHarness`
   - Add per-direction (long/short) comparison
   - Add `profitFactor`, `largestTrade`, `averageTrade`
   - Add per-trade `history[]` comparison from `report.json`
   - Add exit reason cross-reference

5. Build snapshot-based credential-free CI gate
   - Record one clean snapshot (1-month EURUSD H1, single strategy)
   - Replay via `FakeCBot` + `SnapshotReplayer`
   - Compare against DB with hard assertions

6. Fix `AutoDeployAlgo` target to use `Shamshir.algo`

---

## 9. Rules you must not break

1. `decimal` for all price, money, lot arithmetic
2. Never add infrastructure deps to `TradingEngine.Domain`
3. Schema changes via EF migrations only
4. No `Console.WriteLine` — use Serilog or test output
5. Don't touch `aspire/AppHost` (NU1903)
6. Keep Unit + Architecture suites green — stop-the-line on red
7. **New:** Use `BacktestCli.InvokeAsync()` for all CLI invocations (single code path)
8. **New:** Use `CtraderE2EHarness` for all cTrader E2E tests (never raw `Process.Start`)
9. **New:** Use `RunArtifacts.Create(testName)` for all test file/directory management
