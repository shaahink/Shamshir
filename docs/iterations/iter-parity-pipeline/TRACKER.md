# Shamshir — iter-parity-pipeline Tracker (resume here)

**This is the machine-readable progress source Conductor parses.** The narrative docs
(`PLAN.md`, `AUDIT.md`) stay the human authority — this file is the strict checkpoint table +
handoff that Conductor verifies against. Read `PLAN.md` first (it is the durable spec), then `AUDIT.md`
(the F-findings each phase fixes), then this file for live state.

**Read order for a fresh session:** this file → `PLAN.md` → `AUDIT.md` → `../../WORKFLOW.md` →
`../../reference/SYSTEM-REFERENCE.md` (+ CODE-MAP, BACKTEST-ARCHITECTURE, TEST-ARCHITECTURE) →
`../../../DECISIONS.md`.
Branch: `iter/parity-pipeline` off `iter/quant-model--p1-tf-agnostic`.
Convention: one subphase = one commit, gate output pasted in the body (PLAN §10). Do not batch.

> NOTE ON IDS: `PLAN.md` calls the first phase "P-0"; this tracker uses **P0.0** for it (the
> current Conductor build's stage-id parser does not accept a hyphen). P0.0 = "land the working
> tree"; P0.1–P0.5 = the parity-truth spine. Stages are P0…P6.

## Handoff  (overwrite this block, ≤12 lines, no history)
last: **P0.0 DONE** — tree landed in 3 deliberate commits: (a) 9570ad6 fix(F5) kernel Entry thread +
      isLimit-from-request.Type +2 tests; (b) bf74d4b test(P7,P3.3) 14 tests; (c) 9686242 feat(ui)
      compare-both toggle + Q1 revert (8 JSONs→Market) + iteration docs. (conductor b57d913 interleaved.)
stage: **P0 IN PROGRESS** — P0.0 complete; P0.1 (¼-sizing F1) is next and NOT started.
gate: GREEN — build 0 err/5 warn (pre-existing net6.0 cBot); Unit 508/0/6; fast Sim 139/0/0;
      golden 61/61 byte-identical; R5 DB verify Method=Market (evidence file, see P0.0 row).
next: **P0.1** — instrument OrderSubmitted DetailJson with sizing inputs (Kernel.DecideProposed),
      write VenueSizingParityTests (FakeTransport, NO creds), prove ×0.25 mechanism, fix, then a
      SEPARATE golden REBASELINE commit (DetailJson WILL move — do not fold into the fix commit).
trap: tsc has 2 PRE-EXISTING spec/e2e errors (runs.service.spec.ts, ui-smoke.spec.ts) — not P0.0,
      flagged for P5. cTrader E2E slow/flaky → gate with fast Sim filter. P2.2 is an OWNER-GATE.
      BuildInfo.g.cs (cBot) regenerates every build → will re-dirty; it is committed generated metadata.

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = an artifact path produced by a run this
phase (a code path is not evidence). Scope changes get a `> scope change:` line under the row.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| P0.0 | Land the working tree (revert 8 JSONs to Market; 3 deliberate commits, gates pasted) | DONE | 9570ad6, bf74d4b, 9686242 | gates in commit bodies; R5: docs/iterations/iter-parity-pipeline/evidence/P0.0-runtime-strategyconfigs.md |
| P0.1 | ¼-sizing bug (F1): VenueSizingParityTests green + equal lots in a paired mini-run DB | TODO | | |
| P0.2 | Run-status truth (F5): real ctrader run ends completed; fault→completed-with-warnings | TODO | | |
| P0.3 | Trade persistence barrier (F6): BTC-scenario test; count mismatch surfaces + backfill | TODO | | |
| P0.4 | Entry-latency instrumentation (F2): entryDelayBars in reconcile output | TODO | | |
| P0.5 | Venue-parity test tier (R8): Category=VenueParity wired into the standard gate filter | TODO | | |
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
