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
last: **P0 STATIC AUDIT DONE** (s6, commit 305a853) — hardened the spine before P1. Reviewed the whole
      P0 diff (c305a08..HEAD). Found + fixed the F6-R crashed-teardown honesty gap with the tracker's
      recommended option-(b) detection safety net (TRADES_UNRECONSTRUCTABLE), evidence-backed vs all 6
      audit-DB runs (false-positive-free); plus an EngineRunner F1 drift-log accuracy fix. No other
      correctness bugs found — F1/F2/F5/F6 fixes are sound; tests are genuine (not happy-path stubs).
stage: **P0 spine COMPLETE + AUDITED.** Next stage **P1** — start P1.1 (one database, F10).
gate: GREEN — build 0 err/5 warn; Unit 528/0/6; Integration 110/0/0 (+2 F6-R); fast Sim 144/0/0;
      golden 61/61 byte-identical (git diff --stat *golden* = empty; NO rebaseline).
next: **P1.1 (one DB, F10)** — single DB path shared by Web + Host CLI; startup fails loud on pending
      migrations; archive stale root data/trading.db; compute-reference-scales populates 84/84 cells.
F6-R: option (b) detection-only ACCEPTED 2026-07-08 — owner defers economics recovery (option a).
       Detection safety net is false-positive-free, surfaces TRADES_UNRECONSTRUCTABLE honestly.
       Non-blocking for P1.
trap: OWNER-PENDING real cTrader runs (P0.1–P0.4 all creds-gated; not run this audit — proven
      credential-free). One transient Integration failure seen once (not reproduced in 2 subsequent full
      110/0 runs; NOT the 2 new F6-R tests — those use isolated in-memory SQLite); suspected pre-existing
      flakiness — next session watch for it. P2.2 OWNER-GATE; BuildInfo.g.cs re-dirties every build
      (leave it); tsc 2 pre-existing (P5).

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

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| P0.0 | Land the working tree (revert 8 JSONs to Market; 3 deliberate commits, gates pasted) | DONE | 9570ad6, bf74d4b, 9686242 | gates in commit bodies; R5: docs/iterations/iter-parity-pipeline/evidence/P0.0-runtime-strategyconfigs.md |
| P0.1 | ¼-sizing bug (F1): VenueSizingParityTests green + equal lots in a paired mini-run DB | DONE (OWNER-PENDING: paired DB run needs creds) | a6aa08c | docs/iterations/iter-parity-pipeline/evidence/P0.1-sizing-parity.md; VenueSizingParityTests 5/5 (Category=VenueParity) |
| P0.2 | Run-status truth (F5): real ctrader run ends completed; fault→completed-with-warnings | DONE (OWNER-PENDING: real 3× headless ctrader run needs creds) | 6533c7e, de4c8e7 | docs/iterations/iter-parity-pipeline/evidence/P0.2-status-truth.md; RunStatusResolverTests (Unit) + RunStatusTruthTests (Integration); R5: M41 WarningsJson column live in Web DB |
| P0.3 | Trade persistence barrier (F6): BTC-scenario test; count mismatch surfaces + backfill | DONE (OWNER-PENDING: real cTrader BTC-scenario run needs creds) | 3d0c7cc | docs/iterations/iter-parity-pipeline/evidence/P0.3-trade-persistence-barrier.md; TradePersistenceBarrierTests 3/3 (Integration) |
> residual (F6-R, found s5 QA; option-b SAFETY NET landed s6-audit 305a853): P0.3 backfills only from journalled PublishTradeClosed. The audited f7b0538d has 0 of those (7 closes came via OrderFilled close-fill events, no PublishTradeClosed effect) → PublishTradeClosed backfill computes expected=0. **NOW MITIGATED (option b, detection-only):** the barrier also counts close-fills (OrderFilled w/ non-null CloseReason) and flags `Unreconstructable` when persisted+backfilled==0 && closeFills>0 → orchestrator attaches `TRADES_UNRECONSTRUCTABLE:{n}` → completed-with-warnings (no more silent TotalTrades=0). Proven false-positive-free vs all 6 audit-DB runs. **STILL DEFERRED (option a, owner-decision):** RECONSTRUCTING the economics from open+close fill pairs touches VenueManaged reconcile-close mapping (kernel/adapter-adjacent STOP condition) — see HUMAN line below. Evidence: evidence/P0.3-trade-persistence-barrier.md §7.
| P0.4 | Entry-latency instrumentation (F2): entryDelayBars in reconcile output | DONE (OWNER-PENDING: live paired-run confirmation needs creds) | 8277df2 | docs/iterations/iter-parity-pipeline/evidence/P0.4-entry-latency.md; docs/audit/RECONCILE-FINDINGS.md §P0.4 (real numbers: tape 3660s/1.017 bars, cTrader 7200s/2.0 bars, gap 3540s); EntryLatencyAnalyzerTests 6/6 (Unit) + EntryLatencyReconcileTests 1/1 (Integration) |
| P0.5 | Venue-parity test tier (R8): Category=VenueParity wired into the standard gate filter | DONE | a6aa08c | fast-Sim filter 139→144 (5 VenueParity tests ride the standard gate); evidence P0.1 §6 |
| P1.1 | One database (F10): Host CLI verbs run against the Web DB; 84/84 ReferenceScales rows | TODO | | |
| P1.2 | Config propagation + drift (F9,F7): JSON edit reflected in journal; UI edit survives restart | TODO | | |
| P2.1 | Run state machine + tests (F8): cancel/watchdog/orphan-kill transitions green | TODO | | |
| P2.2 | OWNER-GATE: one real compare-both run + committed reconcile verdict (inherited P6.1 gate) | TODO | | |
| P3.1 | TradingEngine.ResearchCli console project (verbs, --json, VERDICT lines, diagnostics) | TODO | | |
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
