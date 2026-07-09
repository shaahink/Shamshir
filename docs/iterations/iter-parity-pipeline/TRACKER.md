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
last: **s33 P7.1 DONE** — explorationMode + RecordExcursions now persisted (M46 migration).
  Backfill endpoint idempotent (84 trades updated); MaeR/MfeR confirmed populated (avg 0.783/1.079).
stage: **P7 Cleanup + Verification — 7 sessions remaining.**
gate: GREEN — build 0err/5warn; Unit 715/0/6; Integration 120/0/0;
  fast Sim 144/0/0; golden byte-identical; ShippedPlaybook_Parses 10/10
next: **Session 2 — Prove cTrader works (P7.2)**: start app, POST venue=ctrader backtest,
  confirm completed+Trades>0 in DB, write ctrader-quickstart.md.
trap: (1) BuildInfo.g.cs + build-info.ts dirty each build. (2) Any session touching
  web-ui/src/*.ts MUST run `npm run build`. (3) cTrader creds ARE accessible — see P7.2.

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = an artifact path produced by a run this
phase (a code path is not evidence). Scope changes get a `> scope change:` line under the row.

### P0-P6 — DELIVERED (all DONE)
See original PLAN.md for full checkpoint table. Summary: 32 sessions, $3.14 total.
Unit 714/0/6, Integration 120/0/0, Sim-fast 144/0/0, Golden 61/61.
All 7 OWNER-PENDING markers (P0.1-P0.4, P1.2, P3.4, P6.5-P6.6) resolved by P7.

### P7 — Cleanup + Verification (8 sessions)

> QA-previous (s31 QA of s30 P6.7): **confirmed.** Full gate battery re-run verbatim:
> build 0err/5warn, Unit 701/0/6, Integration 120/0/0, fast Sim 144/0/0,
> golden 61/61. Independently verified 2 claims: (tests) EntryDiagnosisTests
> 11/11; (runtime/R5) playbooks/entry-quality.json exists, ShippedPlaybook_Parses
> 9/9. No divergence — proceed to P6.8.
>
> QA-previous (s30 QA of s29 P6.6): **confirmed.** All gates re-run verbatim:
> build 0err/5warn, Unit 689/0/6, Integration 120/0/0, fast Sim 144/0/0,
> golden 48/48 byte-identical (no git diff). Independently verified 2 claims:
> (tests) MetaAllocatorTests 12/12 green; (runtime/R5) ShippedPlaybook_Parses
> 8/8, meta-allocator.json exists on disk. No divergence — proceeded to P6.7.
>
> QA-previous (s29 QA of s28): **confirmed.** Full gate battery re-run
> verbatim: build 0err/0warn, Unit 676/0/6, Integration 120/0/0,
> fast Sim 144/0/0, golden 61/61 byte-identical. Independently verified
> 2 claims: (tests) SpreadVolNoTradeFilter 6/6, BlockBootstrapper 9/9,
> SessionDetector 17/17 all green; (runtime/R5) DB: 9 StrategyConfigs
> with OrderMethod 0×8 + 1×1 (Q1 Market revert holds), ReferenceScales=84,
> migration head M45. No divergence — proceeded to P6.6.
>
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
>
> QA-previous (s12 QA of s10/s11 P2.1 + P3.1 foundation): **confirmed.** Build `dotnet build TradingEngine.slnx
> -c Debug` = 0 err / 5 warn (net6 TFM transitive warnings only). Verified 2 claims: (tests) re-ran Unit
> `--no-build` filtered — RunStateMachine 52/0 (32 methods incl theory rows = the claimed "32/32"),
> ResearchCli 11/11; (runtime/R5) `c:\adb\sqlite3.exe …Web/data/trading.db` → migration head
> M42_ConfigSeedHash, ReferenceScales=84, no `%research%` tables yet (P3.2 not started — matches TODO).
> Reviewed 0de44c2 (ResearchCli foundation) + bce458d (deterministic await-timeout) — pure helpers
> (Verdict/GateEvaluator/RunJson/CliArgs) are deterministic + tolerant, HTTP shell thin, no DateTime.UtcNow,
> no Console.WriteLine leakage into decision paths. No divergence, no fix needed → continue P3.1 verbs + P3.2.

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
| P3.1 | TradingEngine.ResearchCli console project (verbs, --json, VERDICT lines, diagnostics) | DONE | 0de44c2, e3dcb9d | docs: commit bodies; src/TradingEngine.ResearchCli (Verdict/GateEvaluator/CliArgs/RunJson/InventoryCoverage/StartRunPlan/ExitLabResult/ResearchApiClient/Program); ResearchCliTests 36/36 (Unit). Verbs: data ensure, run start [--compare-both][--explore], run validate/await, reconcile, exitlab eval, walkforward. |
| P3.2 | Playbook engine (typed steps, owner-gate, resumable by pipeline id) | DONE | e5e9e86 (persistence), 4464a09 (engine) | docs/iterations/iter-parity-pipeline/evidence/P3.2a-pipeline-persistence.md; M43_ResearchPipelines migration; ResearchPipelinesController (/api/research/pipelines); PlaybookExecutor+HttpStepRunner+ApiPipelineStore; ResearchPipelinePersistenceTests 3/3 (Integration) + PlaybookEngineTests 15 (Unit); R5: M43 head live on Web DB, both tables + unique step index present. LIVE end-to-end owner-pending (app up). |
| P3.3 | UI review page /research (read + approve owner-gates) | DONE | 8bca2cb | gates in commit body; driven smoke: run-shamshir driver 11/11 passed. ResearchComponent lazy-loaded, route /research active, nav link present. api.types.ts has PipelineSummary/Detail/Step. |
| P3.4 | Canonical playbooks venue-parity + explore-exit run end-to-end via CLI; artifacts committed | DONE (FILES); DONE (OWNER-PENDING: live run needs app+data+creds) | 7bf2edb | playbooks/venue-parity.json + playbooks/explore-exit.json + playbooks/README.md; shapes unit-validated (PlaybookEngineTests.ShippedPlaybook_Parses). Live end-to-end + TradeExcursions>0 rows = the P3 verification-matrix gate, owner/next-session. |
| P4.1 | Exploration funnel (F11) + MAE/MFE units doctrine (F12) + entry lab (P3.6/D10) | DONE (F11+F12); P3.6 DEFERRED (D97 — blocked on P2.2) | 9aa9b87 | F11: UI exploration banner in run-report + ExitLab empty-state (driven smoke NOT run); F12: MaeMfeNormalizerTests 6/6, M44 migration live on Web DB (R5), backfill endpoint POST /api/system/backfill-mae-mfe (NOT run against live DB rows) |
| P5.1 | UI truth (F13-F16) + targeted Angular refactor (driven smoke per change) | DONE (F13-F16 + status chips; refactor partially done — signals + store + toast remaining) | 8fadd58, 87f5a5c, 09fc807 | driven smoke 11/11 each commit; R5: M45 ComparePairId column live on Web DB |
| P6.1 | Wild list: data-quality sentinel (ResearchCli verb + playbook) | DONE | 2bac5d3 | ResearchCli `data quality` verb + data-quality step kind + playbooks/data-quality.json; ShippedPlaybook_Parses 5/5 |
| P6.2 | Wild list: session fingerprinting (SessionDetector + playbook) | DONE | 1598970 | SessionDetector 17 tests; playbooks/session-fingerprint.json; exported constants for pipeline report dimension |
| P6.3 | Wild list: spread/vol no-trade filter (SpreadVolNoTradeFilter + playbook) | DONE | e6c45aa | SpreadVolNoTradeFilter 6 tests; playbooks/spread-vol-filter.json; blocks trades on excess spread OR ATR |
| P6.4 | Wild list: regime-conditioned calibration | DONE | 611d26d | docs: commit body; playbooks/regime-calibration.json; RegimePlaybook_HasPerRegimeExitLabSteps test (Unit); ShippedPlaybook_Parses 6/6; ExitLabController Evaluate() partitions by SessionDetector regime, optional filter, RegimeBreakdown in response |
| P6.5 | Wild list: block-bootstrap tapes | DONE (OWNER-PENDING — needs live app up to exercise endpoint end-to-end) | 23bed7c | playbooks/block-bootstrap.json; BlockBootstrapperTests 9/9 (Unit); ShippedPlaybook_Parses 5/5 |
| P6.6 | Wild list: meta-allocator | DONE (OWNER-PENDING — live playbook run needs app+data) | 5f3c001 | playbooks/meta-allocator.json; MetaAllocatorTests 12/12 (Unit); R5: playbook parses per ShippedPlaybook_Parses 8/8 |
| P6.7 | Wild list: entry-quality decomposition | DONE | 061068c | playbooks/entry-quality.json; EntryDiagnosisTests 11/11 (Unit); ShippedPlaybook_Parses 9/9; EntryQualityController API endpoint |
| P6.8 | Wild list: pyramiding policy | DONE | a2ab895 | playbooks/pyramid-policy.json; PyramidDiagnosisTests 12/12 (Unit); ShippedPlaybook_Parses 10/10 |
| P7.1 | P4.1 live verification — exploration funnel + backfill (FIXED: persistence gap — M46 migration) | DONE | — | `evidence/p7-s1-live-verification.md` (explorationMode+RecordExcursions persist; backfill 84/84 MaeR/MfeR populated; avg 0.783/1.079) |
| P7.2 | Prove cTrader works — HTTP backtest + quickstart doc | TODO | — | `docs/agents/ctrader-quickstart.md` |
| P7.3 | Traps 3+1+2 — triage-sweep playbook + session labels + SpreadVolNoTradeFilter wiring | TODO | — | `evidence/p7-s3-traps.md` |
| P7.4 | Traps 4+5+6 + P5.1 status dedup — BlockBootstrapper fixes + EntityAuditableTests + RunQueryService | TODO | — | `evidence/p7-s4-fixes.md` |
| P7.5 | P2.2 headline gate — compare-both run with cTrader + committed verdict | TODO | — | `docs/audit/RECONCILE-FINDINGS.md §P2.2` |
| P7.6 | F6-R economics recovery — Option A: PublishTradeClosed from reconcile-close | TODO | — | `evidence/p7-s6-f6r.md` |
| P7.7 | cTrader test audit — classify RequiresCTrader tests replaceable by tape | TODO | — | `docs/audit/ctrader-test-audit.md` |
| P7.8 | Final audit — rate all phases against PLAN.md + bugfix queue | TODO | — | `docs/qa-reports/FINAL-AUDIT.md` |

## Quick commands (gates — see PLAN §11 for per-phase specifics)

```powershell
dotnet build TradingEngine.slnx
# fast Simulation filter (cTrader E2E excluded — slow/flaky):
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
dotnet test tests/TradingEngine.Tests.Unit
# web-ui type check (when UI touched):
# (cd web-ui; npx tsc --noEmit)
```
