# Next Session — Continuation Handover

**Written:** 2026-07-03 (session close)
**Branch:** `iter/tape-trust` (active) / `develop` (authoritative, merged)
**Worktree:** `C:\Code\Shamshir` (`iter/tape-trust`) — only one
**Gates:** Unit 314/0/6 · Integration 109/0 (develop) · Golden 63/63 · build 0 · npm 0

---

## What happened this session

1. **Audit** — found 3 critical bugs (RunNarrativeService schema, VenueSessions orphan, DD calendar roll) + 4 Angular bugs (overlap guard, trade-detail crash, gateRejections null, chart reload)
2. **Branch decision** — chose `iter/tape-trust` over sibling `origin/iter/merge-plan`; sibling's RunNarrativeService was broken
3. **W1+W2 port** — ported sibling's valuable additions manually (M3.3 real data, M4.2 delete, prune, NaN guards, download expansion)
4. **Merged to develop** — theirs strategy, resolved 3 merge duplicates, pushed
5. **Cleanup** — deleted 5 stale branches, 2 stale worktrees, archived fixed issues

## What's still open

**Read `docs/OPEN-ISSUES.md`** — the single source of truth. Quick summary:

| # | Item | Priority | Golden risk |
|---|------|----------|-------------|
| C1 | Short entries miss half-spread cost (2 lines) | Critical | Yes — needs re-baseline |
| D1 | DB fragmentation — single `TRADING_DB_PATH` | Medium | No |
| D2 | Hardcoded defaults audit | Low | No |
| Angular race | `RebuildAngularIfStale` pin | Low | No |
| V2–V5 | cTrader reconcile (owner only) | Owner | No |
| Q1–Q2 | Quant roadmap | Future | No |

## What to read first (mandatory, in order)

1. **`docs/OPEN-ISSUES.md`** — all remaining work
2. **`docs/audit/PROGRESS.md`** — what's been done, gate numbers
3. **`AGENTS.md`** — build commands, architecture, current branch state
4. **`docs/reference/SYSTEM-REFERENCE.md`** — system overview

## What NOT to do

- Do NOT touch kernel/strategy/risk math — golden must stay byte-identical (63/63)
- Do NOT implement M5 (owner-only, needs cTrader CLI)
- Do NOT group anything "daily" by calendar date — 22:00 UTC prop-firm roll
- Do NOT add comments to code (convention)
- Do NOT merge `origin/iter/merge-plan` — it's already ported
- Decimal for all price/money/lot arithmetic

## Build commands

```powershell
dotnet build -p:NgProjectDir=C:/nonexistent-skip   # skip Angular rebuild
npm run build --prefix web-ui                        # Angular build
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration     # 109/0
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"
```
