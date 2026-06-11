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
Implementing agent reads PLAN.md + docs/agents/HOW-TO-WORK.md
        ↓
Agent implements phases, runs verification steps
        ↓
Agent writes HANDOVER.md
        ↓
Human reviews HANDOVER.md + checks DB state
        ↓
Commit + merge → move to completed/
```

---

## Status

| Iter | Title | Status | Branch |
|------|-------|--------|--------|
| 01–09 | Prior iterations | ✅ Completed | (see git log) |
| 10 | Observability, metadata, bar tracing | ✅ Completed | `phase/8b-bar-tracing` |
| 11 | Replay adapter + E2E test | 📋 Planned | — |
| 12 | Wire replay to UI + correct metrics | 📋 Planned | — |
| 13 | Observability pass | 📋 Planned | — |
| 14 | UI rewrite (Blazor Server) | 📋 Planned | — |
| 15 | Architecture cleanup | 📋 Parallel with 14 | — |
| 16 | cTrader in-process engine | ⚠ Implemented — orders never reach cBot from UI path (diagnosed in iter-17 PLAN) | `iter/16-ctrader-inproc` |
| 17 | Deterministic pipeline: transport fix, lock-step protocol, single composition root, journal observability | 🔧 Implemented — ready for verification | `iter/17-deterministic-pipeline` |

---

## Completed iteration handovers (pre-folder-structure era)

These exist at `docs/ITERATION-N-HANDOVER.md` (old location, kept for reference):
- `docs/ITERATION-6-HANDOVER.md`
- `docs/ITERATION-8-HANDOVER.md`
- `docs/ITERATION-9-HANDOVER.md`
- `docs/ITERATION-10-HANDOVER.md`

---

## Dependency graph

```
iter-11 (fix replay adapter + E2E test)
    ↓
iter-12 (wire to UI + metrics)
    ↓
iter-13 (observability)
    ↓ ↓ (can fan out here)
iter-14  iter-15
(UI)     (cleanup)
```

Iter-14 and iter-15 are independent of each other and can run in parallel worktrees.
Iters 11→12→13 must be sequential — each one's E2E test is the gate for the next.
