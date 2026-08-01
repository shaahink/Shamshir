---
name: ctrader-e2e
description: Run and reason about the Shamshir cTrader CLI end-to-end tests — the only coverage that exercises the real compiled cBot under the cTrader CLI over NetMQ with the full kernel engine, including cTrader-report-vs-DB ledger reconciliation. Load when asked to run, set up, debug, or extend the cTrader E2E suite (RequiresCTrader), configure cTrader credentials/algo for tests, or investigate why these tests skip/fail. Explains the credential + cBot-algo setup, the harness phases, what each test proves, and the iter-36 "tests silently skip" bug.
---

# Skill: ctrader-e2e

## Why these tests matter (read first)

Every other tier is credential-free and stubs the venue. The **cTrader E2E suite is the ONLY coverage that
exercises the real stack end-to-end**:

- the **compiled cBot** (`src.algo`) running inside the **cTrader CLI** backtester,
- **NetMQ** PUB/SUB + ROUTER/DEALER transport framing over loopback,
- the **full kernel engine** (`EngineHostFactory` → `KernelBacktestLoop`, post iter-36),
- and **ledger reconciliation** — `CtraderDiffHarness` compares the cBot's own `shamshir-report.json`
  against the engine's DB trades (trade count + per-trade cost integrity).

So this suite is what proves the kernel cutover is correct **live** (real fills, real costs, real process
lifecycle), not just in the synthetic harness. It is high-value and **must run** in a cTrader-equipped env.

> Files: `tests/TradingEngine.Tests.Simulation/E2E/*` (`CtraderE2EHarnessSmokeTests`,
> `CtraderScenarioE2ETests`, `DiffE2ETests`, `PipelineE2ETests`, `DiscoveryAuditTests`),
> harness `tests/.../Harness/CtraderE2EHarness.cs` + `CtraderTestHelpers.cs`, recon
> `tests/.../Verification/CtraderDiffHarness.cs`. All tagged `[Trait("RequiresCTrader","true")]`
> `[Collection("CtraderSerial")]` (run serially — they spawn the cTrader CLI).

## Prerequisites (the env that makes them RUN, not skip)

1. **cTrader CLI** installed + on PATH / resolvable by `BacktestCli.InvokeAsync` (the cTrader desktop
   backtester CLI). Real market data comes from cTrader — there is no offline substitute.
2. **The compiled cBot algo** — build the cBot so `src.algo` exists where `CtraderTestHelpers.ResolveAlgo()`
   looks:
   ```
   src/TradingEngine.Adapters.CTrader/bin/{Debug,Release}/net6.0/src.algo
   ```
   (Build `TradingEngine.Adapters.CTrader`; note it targets **net6.0** — the cTrader platform constraint.)
3. **Credentials** — resolved by `CtraderTestHelpers.ResolveCredential(key, envKey)` from EITHER
   `src/TradingEngine.Web/appsettings.Development.json`:
   ```json
   { "CTrader": { "CtId": "...", "PwdFile": "C:\\path\\to\\pwd.txt", "Account": "..." } }
   ```
   OR environment variables: `CTrader__CtId`, `CTrader__PwdFile`, `CTrader__Account`.
   `HasCredentials` gates on `CtId` being non-empty.
4. **`--full-access`** — the CLI is invoked with `FullAccess = true` (D76): the cTrader-cli sandbox
   intercepts both .NET managed sockets AND NetMQ native sockets without it.

## Run

```bash
# All cTrader E2E (serial). Requires the env above.
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"

# A single suite
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~CtraderE2EHarnessSmokeTests"
```

The harness (`CtraderE2EHarness`) runs in phases: `StartEngineAsync` (NetMQ + kernel engine on loopback
ports) → `StartCtraderAsync` (launches the cTrader CLI with the cBot) → `WaitForHandshakeAsync` →
`WaitForCompletionAsync` (polls the **StepRecord `JournalEntries`** — iter-36 K5; was `BarEvaluations`) →
`CollectResult` → `CtraderDiffHarness.CompareAsync` (report-vs-DB recon). Artifacts (DB, report.json,
events.json, CLI log) land under the per-run `RunArtifacts` dir.

## What each test proves

- **`CtraderE2EHarnessSmokeTests`** — 3 days EURUSD H1 produces real trades (phased + `RunAsync`); the
  **ClientOrderId ledger reconciliation** (`TradeLedger_…_NoMissingTrades`) joins cTrader fills to DB trades
  with no missing/extra trades and no error-severity cost discrepancies. This is the K2 id-unification +
  K5 journal proof live.
- **`CtraderScenarioE2ETests`** — ledger integrity (entry/exit/lots > 0; real movers have non-zero PnL),
  a weekend-range edge (no garbage), and **no orphan cTrader processes** after the run (clean lifecycle).
- **`DiffE2ETests` / `PipelineE2ETests`** — multi-symbol/timeframe + cTrader-vs-DB diff + cost-integrity.

## ⚠ Known bug — they SILENTLY skip (OPEN-ISSUES CT-1)

When credentials are absent the tests do a bare `return` and report as **PASS** — masking that this
critical live coverage never ran. **They should not skip silently.** The real fix is **configuring the env**
(creds + built `src.algo` + cTrader CLI) so they actually execute. Secondary: switch to `[SkippableFact]`
(xUnit v2 has no `Assert.Skip`) so a genuine no-env skip is *visible*, and harden `HasCredentials` to also
check the algo/CLI are present (so a *partial* cred env skips instead of hard-failing mid-run).

## Gotchas

- **Serial only** (`[Collection("CtraderSerial")]`) — they spawn the cTrader CLI; never parallelize.
- **net6.0 cBot** — the algo targets net6.0 (platform constraint); the rest of the solution is net10.0.
- **Orphan processes** — `CtraderProcessGuard.StrayCount()` must be 0 after a run; the cBot's `Stop()` is
  called from the NetMQ poller thread (M3, owner-verify).
- **iter-36:** the harness now polls/reports off `JournalEntries` (the single StepRecord journal), not the
  deleted `BarEvaluations` table — don't reintroduce `BarEvaluations` reads.
