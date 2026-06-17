# Shamshir — Next Steps / Roadmap Backlog

**Logged**: 2026-06-17 (end of iter-31/32 continuation). Owner brain-dump captured verbatim-in-intent
and organized. This is a *roadmap*, not the bug log — correctness bugs live in `docs/OPEN-ISSUES.md`.
Items are grouped by theme with a rough priority (P1 = do soon, P3 = later) and links to related work.

---

## A. Reporting & observability (highest near-term value)

### A1 — One-page, LLM-readable backtest report (P1)
A single page (and/or exportable markdown/JSON) that explains, for one run:
- **What the backtest did**: symbol(s), timeframe(s), period, strategies + effective params, risk
  profile, prop-firm ruleset, initial balance.
- **What happened at each bar**: the per-bar narrative — signal fired (and *why*), signal **rejected
  (and why)**, order, fill, close (reason + costs). The data already exists: `PipelineEvents`
  (SIGNAL/ORDER/FILL/CLOSE with reasons + cost detail, added iter-31 cont.) + `BarEvaluations`
  (per-strategy per-bar indicators + "no signal because…"). **They are not yet stitched into one view.**
- **Final stats**: net/gross, commission/swap totals, win rate, profit factor, max DD, R distribution,
  per-strategy breakdown, rule-breach timeline.
- **Why signals were rejected**: aggregate the `BarEvaluated` reasons + `OrderRejected` violation codes
  into a funnel ("4280 bars: RSI not extreme; 25: warmup; 12 signals; 8 risk-rejected (MAX_EXPOSURE)…").
> Foundation is in place (journal taxonomy + reasons + cost detail). This is mostly a *projection/render*
> task. Consider an LLM-summary endpoint that feeds the journal+stats to a model for prose.

### A2 — Report is missing columns/fields (P1)
Trades table + run KPIs are incomplete. Add/verify: Commission, Swap, Gross, Net columns (data exists
now), R-multiple, MAE/MFE, pips, hold time, entry type (market/limit), strategy, exit reason. Run-level:
cost-inclusive KPIs, commission/swap totals, per-strategy performance breakdown. (Ties to OPEN-ISSUES
OBS-05, and iter-31 31-A3.)

### A3 — Per-bar "why rejected" surfaced in UI (P1)
`BarEvaluations` holds the exhaustive per-bar reasoning but it isn't rendered on the Report (the
"SIGNAL AUDIT" panel sketched in OPEN-ISSUES Part 8 was never built). Build it: paginated per-strategy
bar table + rejection-reason histogram.

### A4 — Progress/UX glitches (P2)
- **Equity curve flicker** on the live Monitor / progress (re-render thrash; throttle or diff updates).
- **Progress bar flicker**.
- **Progress % and elapsed time inconsistent** (percent vs ETA vs wall-elapsed disagree — see
  `BacktestOrchestrator.BuildProgress`; `BarsTotal` ignores weekend/market gaps so % drifts).

### A5 — Venue status info (P2)
Surface venue/connection health in the UI (cTrader connected/disconnected, bars received, last tick,
NetMQ state). Today only buried in logs. Tie into the live Monitor.

---

## B. Strategy / config UX + custom profiles (the big product gap)

### B1 — View strategies & risk setup (P1)
Read-only first: a page to inspect every strategy's effective params (params, SL/TP, breakeven/trailing,
regime filter, order entry, symbols, timeframe, risk profile) and the risk/prop-firm setup. The DB now
holds the seeded configs (iter-32 32-P0) — this is the read side of 32-P4.

### B2 — Choose strategy + params + sizing per run, save as custom profile (P1)
Across **New-Backtest, Run-Report, and Live-Run**, let the user pick: which strategy, with which params,
which position-sizing params, which risk/other related params — and **save the selection as a named
custom profile** to reuse. This is 32-P4 (edit) + 32-P5 (per-run overrides) + a new "profiles" store on
top of `EffectiveConfigResolver` (already merges default ← override ← run plan). Decide: profile =
persisted `StrategyOverride` set + run plan, named and reusable.

### B3 — Edit + validate write-back (P2)
Editing must reuse `ConfigLoader` cross-reference validation (riskProfileId → risk-profiles,
propFirmRuleSetId → prop-firms) before upsert. (32-P4/P5, OPEN-ISSUES RW-01/02.)

---

## C. Architecture / framework

### C1 — Vertical slicing with clean wiring (P2)
Reorganize around feature slices (e.g., Backtest, LiveRun, Strategy, Risk, Reporting) with clean
boundaries, instead of layer-by-layer. Reduce the DI/wiring sprawl. Large, do incrementally.

### C2 — Front-end: SPA (Angular) + SignalR, standard CSS framework (P2)
- Move the richer interactive surfaces to a SPA (Angular or similar), keep/extend the existing SignalR
  push for live runs. Mixed approach is fine.
- **Adopt a standard CSS framework** (Bootstrap/Tailwind/etc.) and **drop the custom styling** — stop
  hand-maintaining the design system.

### C3 — Richer backtest & live framework (P2)
More capable run framework: batch/sweeps (OPEN-ISSUES RW-03), multi-symbol/multi-TF, auto strategy
selection by regime/performance (RW-04), comparison views. Depends on per-run overrides (B2) + data
import.

---

## D. Performance

### D1 — Backtest is slow (P1 — called out twice)
Profile and speed up. Suspects: the cTrader-cli path (subprocess + NetMQ round-trips), per-bar indicator
recompute (`IndicatorSnapshotService`), per-bar DB writes (journal/bar-eval flush), the 5s settle delays
in `RunEngineReplayAsync`, EF change-tracking on bulk inserts. Measure first (bars/sec), then target the
hot path. The credential-free **replay** path is the easiest to profile.

---

## E. Testing — pressure the rules

### E1 — Test inventory + detailed case list (P1)
Review all existing tests and produce a **detailed, named list of test cases** mapping to system rules:
which rule each test pins, and the gaps. (Start from `tests/TradingEngine.Tests.*`.)

### E2 — Rule pressure / edge-case suite (P1)
Adversarial scenarios that try to break the rules and observe behavior:
- **Strategy races**: multiple strategies signaling the same symbol/bar — exposure caps, correlation,
  sizing interplay, ordering determinism.
- **Rule-breaking edge cases**: daily-loss limit, max-DD, max-exposure, force-close-on-breach,
  protection-mode entry/exit, governor states, news/session filters, weekend/rollover (triple-swap),
  partial fills, limit expiry, reconnect/reconcile.
- Assert the engine **enforces** (blocks/flattens) and the journal **explains** each enforcement.
> The cancellation/cost/journal work (iter-31 cont.) added the first of these; expand into a real suite.

---

## Suggested sequencing

1. **A1+A2+A3** (one-page report + missing fields + per-bar "why") — highest signal, mostly rendering on
   data that now exists.
2. **D1** (profile + speed up backtest) — unblocks faster iteration on everything else.
3. **B1→B2** (view → choose/save custom profile) — the core product UX.
4. **E1→E2** (test inventory → rule pressure suite) — confidence before deeper changes.
5. **C2** (CSS framework + SPA) and **C1** (vertical slices) — larger refactors, do once the above
   stabilize requirements.
6. **A4/A5** UX polish + venue status alongside.

See also `docs/OPEN-ISSUES.md` Part 10 (RW-01..05) which overlaps B/C, and the iter-31/32 continuation
section for what already landed.
