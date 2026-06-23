---
name: ctrader-e2e
description: Run and reason about the Shamshir cTrader CLI end-to-end tests — the only coverage that exercises the real compiled cBot under the cTrader CLI over NetMQ with the full kernel engine, including cTrader-report-vs-DB ledger reconciliation. Load when asked to run, set up, debug, or extend the cTrader E2E suite (RequiresCTrader), configure cTrader credentials/algo for tests, or investigate why these tests skip/fail.
---

# Skill: ctrader-e2e

---

## Session Warmup (read FIRST — current state as of iter-39)

### Credentials: are they configured?

**Yes — on this machine, creds are in `appsettings.Development.json`.**

```
src/TradingEngine.Web/appsettings.Development.json → CTrader section
  CtId:    "seankiaa"
  PwdFile: "C:\\Users\\shahi\\Documents\\ctrader.pwd"
  Account: "5834367"
```

The resolution chain (in `CtraderTestHelpers.ResolveCredential`):
1. Read `src/TradingEngine.Web/appsettings.Development.json` → `CTrader:{key}`
2. Fallback: environment variable `CTrader__{key}`
3. Fallback: empty string (→ `HasCredentials` returns false → tests SKIP)

Do **not** waste time checking env vars if the JSON file has values.

### Current test status (iter-39, HEAD `d3da582`)

| Test | Status | Duration | Notes |
|------|--------|----------|-------|
| `EurUsd_H1_3Days_ProducesTrades_UsingPhasedHarness` | ✅ PASS | ~60s | |
| `EurUsd_H1_3Days_ProducesTrades_UsingRunAsync` | ✅ PASS | ~30s | |
| `TradeLedger_ClientOrderIdReconciliation_NoMissingTrades` | ❌ FAIL | ~30s | cTrader=17 DB=16 — known bug (see Investigation) |
| `DiffE2ETests.CostIntegrity` | ⚠️ Not re-run this session | — | Likely PASS (was gated by old silent-skip bug) |
| `PipelineE2ETests.EurUsd` | ⚠️ Not re-run this session | — | Likely PASS |
| `NetMQBridgeTest` | ⚠️ Not re-run this session | — | Likely PASS |

**Key insight:** Don't assume tests "silently skip." The CT-1 silent-skip bug was fixed in iter-38 (`[SkippableFact]` + `Skip.IfNot`). With creds configured, these tests **RUN** (not skip). If they fail, it's a real bug.

### Quick run

```powershell
# All cTrader E2E (serial — never parallelize)
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"

# Just the smoke tests (fastest feedback)
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~CtraderE2EHarnessSmokeTests"

# Determinism gate (credential-free — the standing gate, must stay 61/61)
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"
```

**Always run the determinism gate BEFORE and AFTER any cBot or engine change.** It must stay 61/61.

### cBot rebuild

The cBot targets **net6.0** (cTrader platform constraint). After ANY change to `TradingEngineCBot.cs`:

```powershell
dotnet build src\TradingEngine.Adapters.CTrader
```

This produces:
- `src/TradingEngine.Adapters.CTrader/bin/Debug/net6.0/src.algo` (used by tests via `CtraderTestHelpers.ResolveAlgo()`)
- `C:\Users\shahi\OneDrive\Documents\cAlgo\Sources\Robots\src.algo` (cTrader desktop)

The test harness resolves the algo from the bin path. If the cBot hasn't been rebuilt after a code change, the test runs the STALE `.algo`. The 5 warnings about net6.0 support are benign.

---

## Why these tests matter

Every other tier is credential-free and stubs the venue. The **cTrader E2E suite is the ONLY coverage that exercises the real stack end-to-end**:

- the **compiled cBot** (`src.algo`) running inside the **cTrader CLI** backtester,
- **NetMQ** PUB/SUB + ROUTER/DEALER transport framing over loopback,
- the **full kernel engine** (`EngineHostFactory` → `KernelBacktestLoop`, post iter-36),
- and **ledger reconciliation** — `CtraderDiffHarness` compares the cBot's own `shamshir-report.json` against the engine's DB trades (trade count + per-trade cost integrity).

> Files: `tests/TradingEngine.Tests.Simulation/E2E/*` (`CtraderE2EHarnessSmokeTests`, `CtraderScenarioE2ETests`, `DiffE2ETests`, `PipelineE2ETests`, `DiscoveryAuditTests`), harness `tests/.../Harness/CtraderE2EHarness.cs` + `CtraderTestHelpers.cs`, recon `tests/.../Verification/CtraderDiffHarness.cs`. All tagged `[Trait("RequiresCTrader","true")]` `[Collection("CtraderSerial")]`.

---

## Prerequisites (the env that makes them RUN)

1. **cTrader CLI** installed + on PATH / resolvable by `BacktestCli.InvokeAsync`. Real market data comes from cTrader — there is no offline substitute.
2. **The compiled cBot algo** — see **cBot rebuild** section above.
3. **Credentials** — see **Credentials** section in Warmup. Resolved by `CtraderTestHelpers.ResolveCredential(key, envKey)` from `appsettings.Development.json` first, then env vars. `HasCredentials` gates on `CtId` being non-empty.
4. **`--full-access`** — the CLI is invoked with `FullAccess = true` (D76).

---

## Harness phases

`CtraderE2EHarness` runs in phases:
1. `StartEngineAsync` — NetMQ + kernel engine on loopback ports
2. `StartCtraderAsync` — launches cTrader CLI with the cBot
3. `WaitForHandshakeAsync` — HELLO_ACK from cBot
4. `WaitForCompletionAsync` — polls **StepRecord `JournalEntries`** (iter-36 K5; was `BarEvaluations`)
5. `CollectResult` — trades, barEvals, signals, orders, execs
6. `CtraderDiffHarness.CompareAsync` — report-vs-DB reconciliation

Artifacts (DB, report.json, events.json, CLI log) land under the per-run `RunArtifacts` dir.

---

## What each test proves

- **`CtraderE2EHarnessSmokeTests`** — 3 days EURUSD H1 produces real trades (phased + `RunAsync`); the **ClientOrderId ledger reconciliation** (`TradeLedger_…_NoMissingTrades`) joins cTrader fills to DB trades with no missing/extra trades and no error-severity cost discrepancies.
- **`CtraderScenarioE2ETests`** — ledger integrity (entry/exit/lots > 0; real movers have non-zero PnL), a weekend-range edge (no garbage), and **no orphan cTrader processes** after the run.
- **`DiffE2ETests` / `PipelineE2ETests`** — multi-symbol/timeframe + cTrader-vs-DB diff + cost-integrity.

---

## Investigation playbook

### When `TradeLedger_ClientOrderIdReconciliation_NoMissingTrades` fails

The test asserts `cTrader trade count == DB trade count`. If they differ:

1. **Check the CBOT output.** The cBot now logs exec frame send failures (iter-39 C1):
   - `CBOT|EXEC_SEND_FAIL|clientOrderId=...` — a close exec was lost at the NetMQ level
   - `CBOT|MODIFY_FAIL|...` — SL/TP modify failed (less critical)
   - `CBOT|DEALER_RECV_ERR|...` — engine command was lost
   - `CBOT|STATS_SEND_FAIL|...` — final stats lost (less critical)

2. **Check the RECONCILE line.** `CTraderBrokerAdapter.HandleStats` (`:348-357`) logs:
   ```
   CTRADER|RECONCILE| bars: sent=X recv=Y v/x | cmds: sent=X recv=Y v/x | execs: sent=X recv=Y dedup=Z unique=W v/x
   ```
   If `execsOk = "x"`, the cBot sent more execs than the engine received (minus deduped). This pinpoints the problem to transport (NetMQ) vs processing (engine).

3. **If execs match but trades differ** — the engine received all execs but didn't produce a trade for one. Check the dedup logic in `TryWriteExec` (`CTraderBrokerAdapter:550-567`).

4. **If execs DON'T match** — the problem is NetMQ transport. Check:
   - `_dealer?.SendFrame(execJson)` failures (now logged)
   - Socket teardown timing (the `Linger = 2s` on line 706-707)
   - B4 delay (3 seconds after CLI exit — `BacktestOrchestrator:818`)

5. **Pump-drain race (most likely cause of 17 vs 16).** When the cTrader CLI exits, the engine's `KernelBacktestLoop.PumpAsync` may stop consuming from the `_execChannel` before the last close exec is processed. The B4 3-second delay is supposed to allow draining, but the pump may have already exited. Check whether the pump explicitly drains the exec channel after the bar stream ends.

### When tests "skip" (genuine skip)

If `Skip.IfNot(HasCredentials, ...)` fires, check:
- `appsettings.Development.json` → `CTrader:CtId` is non-empty
- The password file at `CTrader:PwdFile` exists
- `src.algo` exists at `src/TradingEngine.Adapters.CTrader/bin/Debug/net6.0/src.algo`

### When tests "silently pass" (old CT-1 bug)

The old `return`-based skip was fixed in iter-38 (`[SkippableFact]` + `Skip.IfNot`). If a test reports PASS but you suspect it didn't actually run, check the test output for `"No cTrader credentials"` — that's the skip message. No skip message = it actually ran.

---

## Gotchas

- **Serial only** (`[Collection("CtraderSerial")]`) — they spawn the cTrader CLI; never parallelize.
- **net6.0 cBot** — the algo targets net6.0 (platform constraint); the rest of the solution is net10.0. The 5 build warnings about `System.Collections.Immutable` + net6.0 are benign.
- **Orphan processes** — `CtraderProcessGuard.StrayCount()` must be 0 after a run.
- **iter-36:** the harness now polls/reports off `JournalEntries` (the single StepRecord journal), not the deleted `BarEvaluations` table.
- **cBot rebuild needed after ANY `TradingEngineCBot.cs` change.** The `.algo` file is a pre-compiled binary — code changes don't take effect until you `dotnet build src\TradingEngine.Adapters.CTrader`.
- **Two test filter modes:**
  - `RequiresCTrader=true` → cTrader E2E tests (need creds + CLI + algo)
  - `RequiresCTrader!=true` → credential-free determinism gate (must stay 61/61)
  - Running the unfiltered Simulation suite runs BOTH, producing confusing mixed results
- **The CBOT exec frame send now retries** (iter-39 C2): 3 attempts with 100ms delays. If `CBOT|EXEC_SEND_FAIL` still appears, the NetMQ socket is in a terminal bad state — the pump-drain race is the root cause.
- **`appsettings.Development.json` values are committed** (unlike most apps). Don't add real production credentials to this file.
