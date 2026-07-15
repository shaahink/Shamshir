# Test Audit — iter-36 Kernel Cutover

**Date:** 2026-06-20 · **Branch:** `iter/36-kernel-cutover`
**Scope:** test-suite health + the improvements the finished kernel design now enables. Companion to
`TEST-ARCHITECTURE.md` (the tiers/harnesses) — this doc is the audit findings + backlog.

---

## 1. Issues found this audit

| # | Issue | Severity | Status |
|---|-------|----------|--------|
| A | **`ConfigSetId` ignored the risk profile** — it hashed only the strategy effective-config, so two runs with different risk profiles got the *same* config identity (breaks K6 "duplicate with changes" lineage/determinism). | High | ✅ **FIXED** — `BacktestOrchestrator.WriteStartRecordAsync` now hashes the full behavior set (effective config + `RiskProfileId` + `StrategyIds` + `StrategyOverrides`). Caught by the new `DuplicateRunE2ETests`. |
| B | **cTrader E2E harness completion polled the now-empty `BarEvaluations`** — iter-36 K5 stopped writing that table, so `CtraderE2EHarness.WaitForCompletionAsync`/`CollectResult` would hang to timeout (→ `E2ECompletionException`) even *with* credentials. | High | ✅ **FIXED** — repointed to the single StepRecord journal (`JournalEntries`). |
| C | **cTrader E2E tests silently skip** when credentials are absent (a bare `return` → reported as **PASS**), masking that the only live cBot+NetMQ+cTrader-CLI coverage is not running. | High | ⚠ **OPEN — see OPEN-ISSUES CT-1.** Real fix = the cTrader env (creds + built cBot algo); see the `ctrader-e2e` skill. xUnit v2 has no `Assert.Skip`; revisit with `[SkippableFact]`. **These tests must RUN, not skip.** |
| D | **Funnel / report readers still read the unwritten tables** — `RunFunnel`, `RunProjection`, `BacktestQueryService` (and `RunQueryService.GetRunJournalAsync`) point at `PipelineEvents`/`BarEvaluations` → return empty under the kernel. | Medium | 🔜 **iter-37 F2/F4** — repoint to the StepRecord journal, then drop the tables. |
| E | **2 pre-existing Simulation failures** — `InProcessEngineSmokeTests.NetMQEngine_InnerHost_StartsAndStopsCleanly` (`EntryPlanner` not registered in that test's own DI) + `NetMQBridgeTest.EngineReceivesBarAndTickOverNetMQ` (NetMQ transport unavailable in sandbox). | Low | Pre-existing (PLAN K0 STATUS); not iter-36 regressions. The `EntryPlanner` one is a real test-harness DI gap worth fixing. |

---

## 2. Test improvements the kernel design now enables

The engine is now a pure `(state, event) → (state', effects)` kernel with one journal — so coverage can move
*down* (cheap, deterministic) and *converge* on one observation point.

1. **Push coverage to pure event-script tests.** Most behavior is now assertable directly against
   `Kernel.Decide` / `EngineReducer.Apply` (no I/O, no harness). Added this round: per-profile sizing,
   trailing apply, equity-snapshot mapping, in-host journal. **Backlog:** migrate the `EngineHarnessBuilder`
   (oracle) scenario tests that only assert *decisions* (not venue I/O) to event-script tests; keep the
   oracle suite purely as the golden regression backstop.
2. **Determinism is the strongest oracle.** Add a **"duplicate is bit-identical"** test: `POST /duplicate`
   with no changes → the new run's StepRecord journal is byte-identical to the source's. Locks K6 replay.
3. **One observation point.** Assertions should read the StepRecord stream (`JournalEntries`) — the in-host
   `BacktestReplayTests` + `DuplicateRunE2ETests` already do. Retire assertions that scrape `PipelineEvents`/
   `BarEvaluations`.
4. **Kernel risk invariants as unit tests.** Now that `PreTradeGate`/`KernelSizing`/`Kernel.DecideEquity`
   are the single authority, the FTMO rule scenarios (daily/max/weekly/monthly DD, protection enter/exit,
   budget downsizing) can be fast event-scripts rather than full sim runs.

---

## 3. cTrader E2E — relevance + recommended improvements

The cTrader E2E suite (`tests/.../E2E/`, `[RequiresCTrader]`, `[Collection("CtraderSerial")]`) is the **only**
coverage that exercises the *real* stack: the compiled cBot (`src.algo`) running under the cTrader CLI,
NetMQ transport framing, the full kernel engine, and **cTrader-report-vs-DB reconciliation**
(`CtraderDiffHarness` — trade count + per-trade cost integrity). It is what proves the cutover live. See the
`ctrader-e2e` skill for how to run it. Recommended additions (for a cTrader-equipped run):

- **Reconcile the StepRecord journal + ClientOrderId** (not just the trade table) — assert the live run wrote
  `JournalEntries` and each `ClientOrderId` on the journal joins to a cTrader ledger fill (validates K2 id
  unification + K5 single-journal live).
- **Live trailing assertion** — a winning-trade scenario where the stop ratchets, verifying WP2
  (`StopLossModifyRequested` → venue `ModifyOrder`) end-to-end live.
- **Make the skip visible** (issue C) — `[SkippableFact]` (Xunit.SkippableFact) instead of a silent `return`,
  and a stricter `HasCredentials` that also checks the cBot algo + CLI are actually present, so partial-cred
  environments *skip* rather than *fail*. **But the primary fix is configuring the env so they RUN.**

---

## 4. Current suite state (iter-36 close)

- build 0 errors · Unit **208/4-skip** · Simulation non-cTrader **82/2** (the 2 = issue E, pre-existing) ·
  Integration `DuplicateRunE2ETests` green · in-host replay writes the StepRecord journal ·
  `run-shamshir` driver **11/11** · `npm run build` green.
- cTrader E2E: **skipped** in this sandbox (no usable cTrader env) — issue C: they must run in a real env.
