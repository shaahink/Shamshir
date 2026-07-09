# Shamshir — iter-land-fix Tracker

**Plan doc:** `docs/iterations/iter-land-fix/PLAN.md`
**Predecessor:** `docs/iterations/iter-parity-pipeline/TRACKER.md` (P0-P7, all DONE)
**Branch:** `iter/parity-pipeline` (HEAD: `877c120`)

## Handoff  (overwrite this block, ≤12 lines, no history)
last: A1 session 1 — C# default + diagnostic log done. F17 NOT resolved (0 trades persist).
root: OrderEntryOptions default reverted to Market + startup diagnostic confirms all strategies Market.
BUT tape runs still produce 0 Journal entries + 0 TradeResults (kernel events not persisted).
progress counter shows fills during run but DB stays empty — persistence layer gap, not config.
gate: build 0err/5warn · Unit 716/0/6 · Integration 121/0/0 · Sim-fast 144/0/0 · golden clean.
next: trace kernel event flow (BarEvaluator → Kernel → journal/trade persistence). Compare working old runs (2cdba11a, RunPlanJson with explicit strategies) vs broken new runs (empty RunPlanJson).
trap: kill all dotnet before building; old runs used explicit RunPlanJson entries; new runs use empty RunPlanJson causing legacy path — possible StrategyRegistry binding difference.

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path under `docs/iterations/iter-land-fix/evidence/`.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| A1 | Fix F17 — tape/replay zero-trade regression | IN PROGRESS | TBD | a1-f17-session-1.md |
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
