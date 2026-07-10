# Shamshir — Conductor-Discovered Debt & Followups

**Generated:** 2026-07-08 by Conductor Baton cross-project audit.
**Updated:** 2026-07-09 — reordered by P7 session.

**Read order:** this file → `docs/iterations/iter-parity-pipeline/TRACKER.md` → `docs/workflows/shamshir-post-p6-workflow.md`.

Items ordered by P7 session. Each entry cross-refs the handover or log that identified it.
All 11 OWNER-PENDING tracker markers are covered here — most are agent-doable (see P7.2 for cTrader proof).

---

## P7.5 — P2.2: Compare-both run + committed reconcile verdict 🟡 TODO

**P7 Session:** 5. **Effort:** ~60 min. **cTrader:** ✅ (proven in P7.2)

**Background (from P0 handover §4-7, TRACKER row P2.2):**
Every P0 checkpoint is proven credential-free. NONE has been confirmed on a live paired run against
the post-P0 build. This is the headline gate: one real compare-both proving F1 (equal lots), F5
(truthful status), F6 (trade persistence). The historic "needs creds" belief is outdated —
credentials are in appsettings.Development.json and the deadlock bugs (B1-B3) are fixed.

**Files:** `docs/iterations/iter-parity-pipeline/TRACKER.md`, `docs/audit/RECONCILE-FINDINGS.md`

**Gate:**
- One live paired run (EURUSD H1, 1 month)
- F1: Tape + cTrader lots match (within rounding)
- F5: 3 consecutive cTrader runs end `completed` (zero NetMQPoller crash messages)
- F6: Barrier firewalls known scenario; `TRADES_LOST`/`TRADES_UNRECONSTRUCTABLE` surfaces if triggered
- Evidence: `docs/audit/RECONCILE-FINDINGS.md` updated with live-run verdict

---

## P7.6 — P3.5: F6-R economics recovery (Option A) 🟡 TODO

**P7 Session:** 6. **Effort:** ~40 min. **cTrader:** No (code change)

**Background (from P0 audit §3-4, P0 handover §4):**
The P0 barrier backfills trades from `PublishTradeClosed` journal effects. When cTrader crashes
mid-run, closes arrive as raw `OrderFilled` events. Option A: have the VenueManaged reconcile-close
path emit `PublishTradeClosed` before teardown so the existing backfill recovers economics for free.
Currently on Option B (detection-only via `TRADES_UNRECONSTRUCTABLE:{n}` flag).

**Files:** `src/TradingEngine.Adapters.CTrader/CTraderBrokerAdapter.cs` (reconcile-close path)

**Gate:** PublishTradeClosed emitted from reconcile-close path. Test proves crashed-teardown with
close-fill-only events produces a backfilled trade with correct economics.

---

## P7.4 — P5.1: RunQueryService status deduplication 🟡 TODO

**P7 Session:** 4. **Effort:** ~20 min. **cTrader:** No

**Background (from P0 handover §4):**
`RunQueryService.GetRunsAsync` derives status inline via EF-translatable LINQ while every other
reader uses `RunStatusResolver.Resolve`. Behaviour matches today but is a drift risk.

**Files:** `src/TradingEngine.Web/Services/RunQueryService.cs`, `src/TradingEngine.Core/Infrastructure/RunStatusResolver.cs`

**Gate:** `RunQueryService` uses the same resolver (EF-compatible or post-query). Test proves
`RunStatusResolver` behaviour matches EF output for all status variants.

---

## P7.7 — cTrader test audit: replaceable-with-tape analysis 🟡 TODO

**P7 Session:** 7. **Effort:** ~30 min. **cTrader:** No (review only)

**Background (from P0 handover §6, CTRADER-TEST-POLICY.md):**
~20+ simulation tests carry `[Trait("RequiresCTrader", "true")]`. Each takes 1-5 min and depends
on CLI. Some test transport/connection (genuine cTrader need), others test trading logic that could
use tape/FakeTransport. Classify each.

**Files:** `tests/TradingEngine.Tests.Simulation/E2E/*.cs`

**Gate:** `docs/audit/ctrader-test-audit.md` with full classification table + effort estimates.

---

## Px.0 — Transient Integration test flakiness monitor 📊

**Not a P7 session item** — ongoing observation.

**Background (from P0 handover §6):**
One transient Integration failure observed once (1/110), not reproduced. If it recurs >2 times
across this phase, escalate to a real checkpoint.
