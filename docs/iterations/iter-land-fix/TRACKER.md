# Shamshir — iter-land-fix Tracker

**Plan doc:** `docs/iterations/iter-land-fix/PLAN.md`
**Predecessor:** `docs/iterations/iter-parity-pipeline/TRACKER.md` (P0-P7, all DONE)
**Branch:** `iter/parity-pipeline` (HEAD: `877c120`)

## Handoff  (overwrite this block, ≤12 lines, no history)
last: A1 session5 — reconciliation gap fixed (RunQueryService). Gates all green.
fix: RunQueryService.GetRunsAsync() read _db.BacktestRuns.TotalTrades directly, bypassing ReconcileAsync(). Detail endpoint used GetByIdAsync()→ReconcileAsync(). Added FixStaleTradeCounts() batch reconciliation.
finding: No current DB discrepancy (all TotalTrades match TradeResults). Fix is defense-in-depth. Jul 9 tape runs genuinely have 0 TradeResults — reconciliation can't fix that. The 0-trade generation on NEW runs remains uninvestigated.
gate: build 0err/5warn · Unit 716/0/6 · Integration 121/0/0 · Sim-fast 144/0/0 · golden clean
path: If next session wants tape trades: trace WHY Jul 9 bar-processing produces 0 proposals/fills despite Market default (f0855ed). Check BarEvaluator→EntryPlanner→Kernel path with RunPlanJson. Compare working 2cdba11a (Jul 7, explicit RunPlan) vs broken Jul 9 runs (empty RunPlan).

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path under `docs/iterations/iter-land-fix/evidence/`.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| A1 | Fix F17 — tape/replay zero-trade regression | IN PROGRESS | f0855ed | a1-f17-session-5.md |
| A2 | Fix F18 + rerun P2.2 headline gate | TODO | | |
| B1 | Pipeline completion + UI gaps + P6 playbook validation | TODO | | |
| C1 | P3.6 data model + recording hooks | TODO | | |
| C2 | P3.6 counterfactuals + API + Angular UI | TODO | | |
| D1 | Final audit + fidelity gaps | TODO | | |

## Quick commands (gates)

```powershell
dotnet build TradingEngine.slnx
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
git diff --stat -- **/*golden*.json
```
