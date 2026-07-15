# Track E — Engine ↔ System Decoupling (DEFERRED)

**Worktree:** `git worktree add ../shamshir-decouple iter/33-track-e-decouple`
**Priority:** **LOW / deferred** — owner: "would be good to decouple them but not the biggest priority."
**Starts after:** the core program (Phase 0/1 + Tracks A/B/C) is stable.
**Prerequisite already met by Phase 0:** P0.5 establishes the *lossless, non-blocking emit→consume seam*.
Track E turns that seam into a clean architecture; it is not required for correctness.

See `MASTER_PLAN.md` §2.5 for the engine-vs-system definition.

---

## The goal

Today the **engine** (per-bar decision+execution core) and the **system** (run lifecycle, persistence,
journaling, reporting, scheduling, governor/rotation/experiments, host composition) are entangled —
`EngineWorker`/`Host` drive bars *and* own persistence handlers + run context. Decouple so:

- The engine is a self-contained unit that **emits domain events** through a published port
  (`IEngineEventSink` / channel) and depends on nothing in the system layer.
- The system **subscribes** as independent consumers (persistence, journaling, equity, reporting,
  broadcasting) — each replaceable without touching the engine.
- Ideally the engine lives in its own assembly with no reference to Host/Infrastructure/persistence;
  the host wires engine + consumers together (composition root only).

This is the architectural completion of P0.5's lossless seam.

---

## Phases

### E1 — Define the published engine-event port
Formalize the event surface the engine emits (bar processed, signal, order, fill, close+cost,
rejected, breach, venue-status, engine-state, equity). Reuse the typed events from P0.7. The engine
depends only on this port, not on concrete handlers.

**Gate:** engine project references only Domain (+ the event port); architecture test enforces it.

### E2 — Move system consumers behind subscriptions
Persistence/journaling/equity/broadcasting become subscribers to the port, registered by the host.
Remove their direct ownership from the engine worker. Bar loop = compute + emit only.

**Gate:** all suites + reconciliation gate + lossless stress test green; no behaviour change.

### E3 — Assembly boundary
Split the engine into its own assembly (or harden the existing `TradingEngine.Engine`) with the
architecture test asserting zero references to Host/Infrastructure/persistence. Host wires the two.

**Gate:** architecture boundary test green; full suite green.

---

## Why deferred (not now)

- The lossless/non-blocking *correctness* requirement is satisfied by P0.5 without the full split.
- It touches the kernel, which is the part that works (MASTER_PLAN F-6) — lowest-risk to do last, once
  the reconciliation gate, contract tests, and rule-pressure suite (Track C5) give maximum safety net.
- It delivers architectural cleanliness, not user-visible value — sequence it after the UI/data wins.
