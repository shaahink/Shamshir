# Iteration Index

Each iteration lives in `iter-NN/`. Two files per iteration:
- `PLAN.md` — written before implementation (the spec)
- `HANDOVER.md` — written after implementation (what was actually done)

The plan drives the agent. The handover is the audit trail.

---

## Lifecycle

```
Human/Opus writes PLAN.md
        ↓
Implementing agent reads PLAN.md + docs/agents/HOW-TO-WORK.md + AGENTS.md
        ↓
Agent implements phases, runs verification steps
        ↓
Agent writes HANDOVER.md
        ↓
Human reviews HANDOVER.md + checks DB state
        ↓
Commit + merge
```

---

## Status

| Iter | Title | Status | Branch |
|------|-------|--------|--------|
| 01–09 | Prior iterations | ✅ Completed | (see git log) |
| 10 | Observability, metadata, bar tracing | ✅ Completed | `phase/8b-bar-tracing` |
| 11 | Replay adapter + E2E test | ✅ Completed | — |
| 12 | Wire replay to UI + correct metrics | ✅ Completed | — |
| 13 | Observability pass | ✅ Completed | — |
| 14 | Engine core architecture (kernel) | ✅ Completed | — |
| 15 | Architecture cleanup | ✅ Completed | — |
| 16 | cTrader in-process engine | ✅ Completed | `iter/16-ctrader-inproc` |
| 17 | Deterministic pipeline (NetMQ, lock-step) | ✅ Completed | `iter/17-deterministic-pipeline` |
| 18 | EF migrations, schema cleanup | ✅ Completed | — |
| 19 | Audit, fixes, breach watchdog | ✅ Completed | — |
| 20 | Kernel: EngineState, EngineReducer | ✅ Completed | — |
| 21 | Kernel: PositionLifecycle | ✅ Completed | — |
| 22 | Kernel: DrawdownReducer | ✅ Completed | — |
| 23 | Kernel: GovernorMachine | ✅ Completed | — |
| 24 | Unify: concurrency, venue decoupling | ✅ Completed | — |
| 25–27 | Web UI fixes, Monitor, signal fixes | ✅ Completed | — |
| 28 | Trade metrics (MAE/MFE, R-multiple) | ✅ Completed | — |
| 29 | Indicator/regime correctness | ✅ Completed | — |
| 30 | Breakeven/trailing wiring | ✅ Completed | — |
| 31 | Costs + Journal + Limit orders | ✅ Completed | `iter/31-costs-journal` |
| 32 | Config as editable data (in-progress) | 🟡 Partial | `iter/31-costs-journal` |

---

## Old iteration files (pre-folder-structure era)

Archived in `docs/iterations/archive/`:
- `ITERATION-2.md` through `ITERATION-10.md` (root-level iteration specs)
- `ITERATION-*-HANDOVER.md` (old handovers from docs/)
- `ITERATION-10-REFACTOR-PLAN.md`
- Old handovers moved from `docs/`

---

## Active handover

Current: `docs/iterations/iter-31-32-combined/HANDOVER.md` — lists what shipped and what's carried forward.
