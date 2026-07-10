# Shamshir — iter-land-fix Tracker

**Plan doc:** `docs/iterations/iter-land-fix/PLAN.md`
**Predecessor:** `docs/iterations/iter-parity-pipeline/TRACKER.md` (P0-P7, all DONE)
**Branch:** `iter/parity-pipeline` (HEAD: `877c120`)

## Handoff  (overwrite this block, ≤12 lines, no history)
last: A1 DONE — F17 root-caused + fix VERIFIED by live run (owner session 2026-07-10). Root cause: 9454878 removed Persistence:DbPath; orchestrator inner-host fallback wrote journal/trades/equity to phantom repo-root data/trading.db while run rows went to canonical DB. Fix 9962432 verified: run 8bd9cedb = 3 trades / 272 journal / 244 equity, NetProfit byte-identical to known-good 2cdba11a. Evidence: a1-f17-verified-20260710.md.
next: A2 (F18 compare-both + P2.2 headline gate). New findings F19–F23 recorded in the A1 evidence file — F19 (false TRADES_PARTIALLY_UNRECONSTRUCTABLE warning on healthy tape runs) and F20 (CTraderListenService stale dbPath fallback) should land with A2/B1.
do-not: re-investigate kernel persistence / RunPlanJson-shape / list-vs-detail theories — all disproved with evidence in a1-f17-verified-20260710.md.
⚠ port: dev Web app listens on http://localhost:5134 (NOT 5000 as quickstart says). Never `Stop-Process` all dotnet — other repos run builds/tests on this machine.
⚠ STALL PREVENTION: never run web app / backtests as blocking foreground commands — background with output redirected to a file, poll the API, heartbeat every 3m.

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path under `docs/iterations/iter-land-fix/evidence/`.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| A1 | Fix F17 — tape/replay zero-trade regression | DONE | 9962432 | a1-f17-verified-20260710.md |
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
