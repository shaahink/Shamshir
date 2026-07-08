# Shamshir — Conductor-Discovered Debt & Followups

**Generated:** 2026-07-08 by Conductor Baton cross-project audit.
**Read order:** this file → `docs/iterations/iter-parity-pipeline/TRACKER.md` → `PLAN.md` → `AUDIT.md` → `WORKFLOW.md` → relevant handover in `.conductor/handovers/P0.md`.

P0 (parity-truth spine) was completed with a green battery and audited. This file records the one
deferred issue from the P0 audit, the structural followups that P1-P6 must handle, and one transient
test flakiness note.

---

## P2.2 — OWNER-GATE: real compare-both run with creds

**Session size:** medium (~50 min, gate-only — no code changes unless a test fails)
**Files touched:**
- `docs/iterations/iter-parity-pipeline/TRACKER.md` (record run + verdict)
- `docs/audit/RECONCILE-FINDINGS.md` (update with post-P0 run data)
- Potentially: kernel/adapter code if a gate assertion fails

**Background (from P0 handover, `.conductor/handovers/P0.md` §4-7):**
Every P0 checkpoint is proven credential-free (FakeTransport, pure functions, kept audit DB).
**NONE** has been confirmed on a live paired (tape vs. cTrader) run against the post-P0 build. This
is by design — the P2.2 owner-gate is where these get exercised end-to-end. Specifically unconfirmed:

1. **F1 (¼-sizing):** equal lots in a live paired DB run. Verified pure-gate only.
2. **F5 (run-status truth):** 3 consecutive headless cTrader runs ending `completed` with zero
   NetMQPoller messages. Verified via transport teardown unit/integration tests only.
3. **F6/F6-R (trade barrier):** a real BTC-scenario producing `TRADES_LOST` (persistence channel
   loss) and/or `TRADES_UNRECONSTRUCTABLE` (crashed-teardown close-fills) + `completed-with-warnings`.
   Verified via barrier suite + audit-DB SQL detection only.

**These are NOT bugs** — they are unverified-at-scale. The tests prove the logic; the live run
proves the integration. Do NOT attempt live runs during P1 — auto-promote through P1, P2.1, then
run P2.2 as a dedicated gate session.

**Gate:**
- One live paired run against the post-P0 build with creds
- F1: Tape + cTrader lots match (within rounding) in the paired DB
- F5: 3 consecutive cTrader runs in a row end `completed` (zero NetMQPoller crash messages)
- F6: At minimum the barrier firewalls a known scenario. If a BTC-scenario is live, confirm `TRADES_LOST` or `TRADES_UNRECONSTRUCTABLE` surfaces in the reconcile output
- Evidence: `docs/audit/RECONCILE-FINDINGS.md` updated with live-run section, DB run IDs recorded
- Golden tests: `git diff --stat -- **/*golden*.json` = empty (NO rebaseline without investigation)

**Context for the agent — why this wasn't done in P0:**
P0 was credential-free by design. The P0 auditor explicitly wrote: "Do NOT attempt live runs to 'verify' — auto-promote and continue per orchestrator policy." The audit DB is sufficient forensic evidence for the fixes; the live run is the integration gate, not a correctness gate.

**Checkpoint:** per existing tracker row `P2.2`

---

## P3.5 — F6-R economics recovery (deferred, option b accepted)

**Session size:** small (~25 min, if owner decides to pursue)
**Files touched:**
- `src/TradingEngine.Adapters.CTrader/CTraderBrokerAdapter.cs` (reconcile-close path)
- `src/TradingEngine.Core/EffectExecutor.cs` / `TradeResultFactory.cs`
- `tests/TradingEngine.Tests.Integration/TradePersistenceBarrierTests.cs`
- `docs/iterations/iter-parity-pipeline/TRACKER.md` (update F6-R status)

**Background (from P0 audit, `.conductor/handovers/P0.md` §3-4):**
The P0 barrier backfills trades from `PublishTradeClosed` journal effects. When cTrader crashes mid-run, some closes arrive as raw `OrderFilled` events (no `PublishTradeClosed` effect). The P0 audit hardened a **detection safety net** (option b): the barrier now counts close-fills and flags `TRADES_UNRECONSTRUCTABLE:{n}` when no effects match. This is false-positive-free against all 6 audit-DB runs.

**Option (a) — actual recovery — was deferred to owner decision.** The recommended approach: have the `VenueManaged` reconcile-close path emit `PublishTradeClosed` into the journal before teardown, so the existing backfill recovers economics for free. This touches the cTrader adapter's reconcile-close mapping, which is kernel/adapter-adjacent (a STOP condition according to the tracker).

**Current status (2026-07-08):** Owner accepted option (b) detection-only. Recovery is deferred to a later phase or indefinitely. This entry exists so a future session can pick it up if the economics loss becomes material.

**Gate:**
- If pursuing: PublishTradeClosed emitted from reconcile-close path; test proves a simulated crashed-teardown with close-fill-only events produces a backfilled trade with correct economics
- If deferred indefinitely: document the decision date in the tracker and close this checkpoint
- Build 0w/0e, 0 new analyzer warnings

**Checkpoint:** `P3.5 — F6-R economics recovery (deferred: option b accepted)`

---

## P5.1 — RunQueryService status deduplication

**Session size:** small (~20 min)
**Files touched:**
- `src/TradingEngine.Web/Services/RunQueryService.cs` (inline status → shared resolver)
- `src/TradingEngine.Core/Infrastructure/RunStatusResolver.cs` (make EF-translatable or extract)

**Background (from P0 handover, `.conductor/handovers/P0.md` §4):**
`RunQueryService.GetRunsAsync` derives status inline via EF-translatable LINQ because it can't call `RunStatusResolver.Resolve` (a static method that isn't translatable to SQL). Every other reader uses the centralized resolver. Behaviour matches today but is a drift risk — the inline check does not treat the literal string `"null"` as empty, but the resolver does.

**This is already in the tracker** under P5 ("UI truth + targeted Angular refactor"). The handover recommends folding into P5's run-store consolidation.

**Gate:**
- `RunQueryService` uses the same resolver as the rest of the codebase (extracted to an EF-compatible form OR computed post-query)
- Test proves `RunStatusResolver` behaviour matches EF query output for all status variants
- Build 0w/0e, `completed-with-warnings` test in Integration suite passes

**Checkpoint:** `P5.1 — RunQueryService status deduplicated` (exact ID per P5 plan)

---

## Px.0 — Transient Integration test flakiness monitor

**Session size:** N/A (monitoring task, not a code session)
**Files touched:** None (investigation only if recurrence)

**Background (from P0 handover, `.conductor/handovers/P0.md` §6):**
One transient Integration failure was observed a single time during the P0 audit (1/110), then not reproduced across two subsequent full 110/0 runs. It was NOT one of the two new F6-R tests (they use isolated in-memory SQLite). Suspected pre-existing flakiness (shared-fixture/DB timing).

**Action for P1/P2 sessions:**
- When running the gate battery, always run Integration twice if the first run shows a failure
- If the failure recurs, capture the test name via `.trx` logger BEFORE assuming green
- If it recurs >2 times across the P1-P2 run, escalate to a real checkpoint (identify and fix or quarantine)
- If it never recurs through P2, close as "suspected transient, not reproduced"

**Not a checkpoint** — this is a monitoring note for the gate battery preamble of every P1/P2 session.
