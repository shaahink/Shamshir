# Shamshir — iter-parity-pipeline Tracker (resume here)

**This is the machine-readable progress source Conductor parses.** The narrative docs
(`PLAN.md`, `AUDIT.md`) stay the human authority — this file is the strict checkpoint table +
handoff that Conductor verifies against. Read `PLAN.md` first (it is the durable spec), then `AUDIT.md`
(the F-findings each phase fixes), then this file for live state.

**Read order for a fresh session:** this file → `../../../conductor-DEBT.md` (audit followups —
unresolved bugs + deferred work from P0 audit) → `PLAN.md` → `AUDIT.md` → `../../WORKFLOW.md` →
`../../reference/SYSTEM-REFERENCE.md` (+ CODE-MAP, BACKTEST-ARCHITECTURE, TEST-ARCHITECTURE) →
`../../../DECISIONS.md`.
Branch: `iter/parity-pipeline` off `iter/quant-model--p1-tf-agnostic`.
Convention: one subphase = one commit, gate output pasted in the body (PLAN §10). Do not batch.

> NOTE ON IDS: `PLAN.md` calls the first phase "P-0"; this tracker uses **P0.0** for it (the
> current Conductor build's stage-id parser does not accept a hyphen). P0.0 = "land the working
> tree"; P0.1–P0.5 = the parity-truth spine. Stages are P0…P6.

## Handoff  (overwrite this block, ≤12 lines, no history)
last: **P2.1 DONE** (ccf6aa4, F8): RunStateMachine (Domain, pure) enumerates queued→starting→running→
      finalizing→terminal; single guarded writer TransitionRun in orchestrator (grep `.Status =`→1 hit);
      happy path forced through `finalizing`; Cancel idempotent+truthful (CancelRequested intent, no lying
      mid-finalize cancelled) + best-effort ctrader-cli tree kill; RunStateMachineTests 32/32.
      **P2.2 auto-promoted DONE (OWNER-PENDING — needs creds).** **P3.1 foundation** (0de44c2): ResearchCli
      console (`research`) — Verdict/GateEvaluator/CliArgs/RunJson/ApiClient + verbs run validate/await,
      reconcile; ResearchCliTests 11/11.
stage: **P2 COMPLETE** (P2.1 done; P2.2 owner-pending). P3 STARTED (P3.1 foundation).
gate: GREEN (documented battery) — build 0err/5warn; Unit 577/0/6; Integration 117/0/0; fast Sim 144/0/0;
      golden byte-identical (git diff --stat **/*golden*.json = empty; NO rebaseline).
next: **Finish P3.1** (verbs: data ensure, run start [--compare-both], exitlab eval, walkforward, report,
      pipeline) then **P3.2 playbook engine** (ResearchPipelines/Steps DB, Q6) + **P3.3 UI /research**.
      Live end-to-end of ResearchCli against a running app is owner/next-session (needs app up).
QA-prev: s8/s9 P1 → **confirmed** (build 0/5, Unit 534/6, Integration 117, fast Sim 144, golden empty;
      R5 DB: ReferenceScales=84, StrategyConfigs Method:0×8+MR:1, head M42). No divergence, no fix.
trap: (1) Tests.Architecture has **2 PRE-EXISTING fails** (EnginePurity; ExitCalibrationEntity !IAuditable)
      — NOT in the gate battery, Engine/Infra untouched this session; do not attribute to P2/P3.
      (2) `finalizing` is transient in-memory ONLY — never persisted (end-record writes after terminal
      transition); don't add it to RunStatusResolver/DB. (3) live cancel/watchdog/orphan-kill + all P0
      cTrader runs are creds-gated → OWNER-PENDING (P2.2). (4) BuildInfo.g.cs re-dirties each build (leave);
      .conductor/ orchestrator-managed. (5) commit via `git commit -F <file>`.

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = an artifact path produced by a run this
phase (a code path is not evidence). Scope changes get a `> scope change:` line under the row.

> QA-previous (s2 QA of P0.0): **confirmed**. Re-ran the full P0.0 gate battery verbatim — build
> 0err/5warn, Unit 508/0/6, fast Sim 139/0/0, golden 61/61. Independently verified 2 claims: (runtime/R5)
> `sqlite3 …Web/data/trading.db` StrategyConfigs = Method:0 ×8 + Method:1 mean-reversion (matches JSON —
> F9 no longer diverges); (tests) golden 61/61 byte-identical re-run. No divergence; no fix needed.
>
> QA-previous (s3 QA of P0.1/P0.5): **confirmed**. Full gate battery re-run verbatim: build 0err/5warn,
> Unit 508/0/6, fast Sim 144/0/0, golden 61/61 byte-identical, Integration 101/0/0. Independently verified
> 2 claims: (runtime/R5) `sqlite3 …Web/data/trading.db` StrategyConfigs OrderEntryJson = `Method:0` ×8 +
> `Method:1` mean-reversion (still Market per Q1); (tests) `Category=VenueParity` 5/5. Reviewed a6aa08c
> diff: `EngineRunner.ResolveInitialBalance` is pure + handles all mode/fallback branches correctly. No
> divergence; no fix needed.
>
> QA-previous (s4 QA of P0.2): **confirmed**. Full gate battery re-run verbatim: build 0err/5warn, Unit
> 522/0/6, Integration 104/0/0, fast Sim 144/0/0, golden 61/61 byte-identical. Independently verified 2
> claims: (runtime/R5) `sqlite3 …Web/data/trading.db` PRAGMA table_info(BacktestRuns) → `WarningsJson TEXT`
> present, `__EFMigrationsHistory` head = `20260708050224_M41_RunWarnings`; (tests) RunStatusResolver +
> RunStatusTruth 12/0. No divergence; no fix needed.
>
> QA-previous (s5 QA of P0.3): **confirmed for delivered scope; diverged on F6 closure.** Full gate battery
> re-run verbatim: build 0err/5warn, Unit 522/0/6, Integration 107/0/0, fast Sim 144/0/0, golden 61/61
> byte-identical. Verified 2 claims: (tests) `TradePersistenceBarrier` 3/3; (runtime/R5) `sqlite3
> …Web/data/trading.db` — the audited F6 run `f7b0538d` has **0** journalled PublishTradeClosed effects
> (7 closes came via Reconcile events), 0 TradeResults, TotalTrades=0; the *successful* cTrader runs DO
> journal PublishTradeClosed (44175d3e=3, 817af3f5=24, 81729685=7). **Divergence:** P0.3's barrier backfills
> only from PublishTradeClosed, so for `f7b0538d` it computes expected=0 → no TRADES_LOST warning → still
> TotalTrades=0. P0.3's synthetic test is correct + green, but the audited crashed-teardown case is neither
> recovered nor flagged → new residual **F6-R** (below). NOT fixed this session (out of P0.4 stage + STOP:
> touches cTrader reconcile-close/kernel semantics; needs owner decision on approach).
>
> QA-previous (s8 QA of s7/P1-attempt-1): **confirmed (s7 was a no-op).** s7 committed only conductor
> bookkeeping + a benign TRACKER F6-R reword; P1.1/P1.2 stayed TODO, no P1 source landed. Re-ran the full P0
> gate battery verbatim: build 0err/5warn, Unit 528/0/6, Integration 110/0/0, golden 61/61 byte-identical,
> fast Sim 144/0/0. Verified 2 claims: (runtime/R5) `sqlite3 …Web/data/trading.db` StrategyConfigs = Method:0
> ×8 + mean-reversion Method:1 (F9 no longer diverges), migration head M41, ReferenceScales=0; (tests) golden
> + Integration + fast Sim re-run green. No divergence, no fix needed → proceeded to deliver P1.1 + P1.2.
>
> QA-previous (s10 QA of s8/s9 P1): **confirmed.** Full gate battery re-run verbatim: build 0err/5warn,
> Unit 534/0/6, Integration 117/0/0, fast Sim 144/0/0, golden byte-identical (`git diff --stat **/*golden*.json`
> = empty). Verified 2 claims: (runtime/R5) `c:\adb\sqlite3.exe …Web/data/trading.db` → ReferenceScales=84
> [P1.1], StrategyConfigs Method:0 ×8 + mean-reversion Method:1 [P1.2/Q1], migration head M42_ConfigSeedHash;
> (tests) full battery re-run green incl golden. s9 audit commit 795807f (lint-config repo-root unify + 2
> ConfigSync tests) reviewed — ratchet-only, no regression. No divergence, no fix needed → proceed to P2.1.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| P0.0 | Land the working tree (revert 8 JSONs to Market; 3 deliberate commits, gates pasted) | DONE | 9570ad6, bf74d4b, 9686242 | gates in commit bodies; R5: docs/iterations/iter-parity-pipeline/evidence/P0.0-runtime-strategyconfigs.md |
| P0.1 | ¼-sizing bug (F1): VenueSizingParityTests green + equal lots in a paired mini-run DB | DONE (OWNER-PENDING: paired DB run needs creds) | a6aa08c | docs/iterations/iter-parity-pipeline/evidence/P0.1-sizing-parity.md; VenueSizingParityTests 5/5 (Category=VenueParity) |
| P0.2 | Run-status truth (F5): real ctrader run ends completed; fault→completed-with-warnings | DONE (OWNER-PENDING: real 3× headless ctrader run needs creds) | 6533c7e, de4c8e7 | docs/iterations/iter-parity-pipeline/evidence/P0.2-status-truth.md; RunStatusResolverTests (Unit) + RunStatusTruthTests (Integration); R5: M41 WarningsJson column live in Web DB |
| P0.3 | Trade persistence barrier (F6): BTC-scenario test; count mismatch surfaces + backfill | DONE (OWNER-PENDING: real cTrader BTC-scenario run needs creds) | 3d0c7cc | docs/iterations/iter-parity-pipeline/evidence/P0.3-trade-persistence-barrier.md; TradePersistenceBarrierTests 3/3 (Integration) |
> residual (F6-R, found s5 QA; option-b SAFETY NET landed s6-audit 305a853): P0.3 backfills only from journalled PublishTradeClosed. The audited f7b0538d has 0 of those (7 closes came via OrderFilled close-fill events, no PublishTradeClosed effect) → PublishTradeClosed backfill computes expected=0. **NOW MITIGATED (option b, detection-only):** the barrier also counts close-fills (OrderFilled w/ non-null CloseReason) and flags `Unreconstructable` when persisted+backfilled==0 && closeFills>0 → orchestrator attaches `TRADES_UNRECONSTRUCTABLE:{n}` → completed-with-warnings (no more silent TotalTrades=0). Proven false-positive-free vs all 6 audit-DB runs. **STILL DEFERRED (option a, owner-decision):** RECONSTRUCTING the economics from open+close fill pairs touches VenueManaged reconcile-close mapping (kernel/adapter-adjacent STOP condition) — see HUMAN line below. Evidence: evidence/P0.3-trade-persistence-barrier.md §7.
| P0.4 | Entry-latency instrumentation (F2): entryDelayBars in reconcile output | DONE (OWNER-PENDING: live paired-run confirmation needs creds) | 8277df2 | docs/iterations/iter-parity-pipeline/evidence/P0.4-entry-latency.md; docs/audit/RECONCILE-FINDINGS.md §P0.4 (real numbers: tape 3660s/1.017 bars, cTrader 7200s/2.0 bars, gap 3540s); EntryLatencyAnalyzerTests 6/6 (Unit) + EntryLatencyReconcileTests 1/1 (Integration) |
| P0.5 | Venue-parity test tier (R8): Category=VenueParity wired into the standard gate filter | DONE | a6aa08c | fast-Sim filter 139→144 (5 VenueParity tests ride the standard gate); evidence P0.1 §6 |
| P1.1 | One database (F10): Host CLI verbs run against the Web DB; 84/84 ReferenceScales rows | DONE | f364102 | docs/iterations/iter-parity-pipeline/evidence/P1.1-one-database.md; DbPathResolverTests 6/6 (Unit) + MigrationTests 3/3 (Integration); R5: sqlite COUNT(ReferenceScales)=84 on the unified Web DB; fail-loud repro exit 2 |
| P1.2 | Config propagation + drift (F9,F7): JSON edit reflected in journal; UI edit survives restart | DONE (OWNER-PENDING: journal-in-a-real-run + StrategyParamsJson-in-a-persisted-run are bars/cTrader-gated) | d36f491 | docs/iterations/iter-parity-pipeline/evidence/P1.2-config-propagation-drift.md; ConfigSyncServiceTests 3/3 (Integration); R5 LIVE: edit trend-breakout Market→LimitOffset + restart → DB OrderEntryJson Method:1 Version 1→2; GET /api/system/config-drift 200 |
| P2.1 | Run state machine + tests (F8): cancel/watchdog/orphan-kill transitions green | DONE | ccf6aa4 | docs/iterations/iter-parity-pipeline/evidence/P2.1-run-state-machine.md; RunStateMachineTests 32/32 (Unit); single guarded writer TransitionRun (grep `.Status = ` → 1 hit) |
| P2.2 | OWNER-GATE: one real compare-both run + committed reconcile verdict (inherited P6.1 gate) | DONE (OWNER-PENDING — needs cTrader creds) | — | Verifiable-now: P0.1/P0.2/P0.3 fixes + P0.4 instrumentation + P2.1 state machine (32/32) all green credential-free. Needs owner+creds: one live paired compare-both (EURUSD H1 1mo) on post-P0 build → equal lots (F1), 3× consecutive `completed` no NetMQPoller (F5), TRADES_LOST/UNRECONSTRUCTABLE surfaces (F6), committed reconcile verdict in docs/audit/RECONCILE-FINDINGS.md §P2.2 (template stubbed). Auto-promoted per run policy. |
| P3.1 | TradingEngine.ResearchCli console project (verbs, --json, VERDICT lines, diagnostics) | IN PROGRESS (foundation landed) | 0de44c2 | docs: commit body; src/TradingEngine.ResearchCli (Verdict/GateEvaluator/CliArgs/RunJson/ResearchApiClient/Program); ResearchCliTests 11/11 (Unit). Verbs landed: run validate/await, reconcile. TODO: data ensure, run start, exitlab, walkforward, report, pipeline + live end-to-end. |
| P3.2 | Playbook engine (typed steps, owner-gate, resumable by pipeline id) | TODO | | |
| P3.3 | UI review page /research (read + approve owner-gates) | TODO | | |
| P3.4 | Canonical playbooks venue-parity + explore-exit run end-to-end via CLI; artifacts committed | TODO | | |
| P4.1 | Exploration funnel (F11) + MAE/MFE units doctrine (F12) + entry lab (P3.6/D10) | TODO | | |
| P5.1 | UI truth (F13-F16) + targeted Angular refactor (driven smoke per change) | TODO | | |
| P6.1 | Wild list (pipeline-gated; each feature ships with a measuring playbook) | TODO | | |

## Quick commands (gates — see PLAN §11 for per-phase specifics)

```powershell
dotnet build TradingEngine.slnx
# fast Simulation filter (cTrader E2E excluded — slow/flaky):
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
dotnet test tests/TradingEngine.Tests.Unit
# web-ui type check (when UI touched):
# (cd web-ui; npx tsc --noEmit)
```
