# Track C — Features (the backlog, built on the new structure)

**Starts after:** Track B + Track A skeletons exist (slice pattern + SPA shell + generated client).
**Shape:** every feature is **one backend vertical slice** (Track-B pattern) **+ one Angular feature
module** (Track-A pattern). Because each is self-contained, multiple features parallelize across
contributors once the skeletons are up.
**Source:** maps the `docs/NEXT-STEPS.md` roadmap and `docs/OPEN-ISSUES.md` carry-forwards onto the new
architecture so nothing is built twice in the old design.

> Many of these are *rendering* features on data that already exists (MASTER_PLAN F-3). The slice is
> usually a thin query + a SPA view, not new engine work.

---

## C1 — Reporting suite (NEXT-STEPS A1–A7)  — highest near-term value

| Item | Slice (back) | Module (front) | Notes |
|------|--------------|----------------|-------|
| A1 one-page LLM-readable report | `Reporting/ExportReport` (markdown + JSON) | report export button | Stitch RunStats + journal narrative + funnel into one doc. |
| A2 missing columns/KPIs | `GetRunStats`/`GetTradeDetail` (data exists) | report tables | Render Commission/Swap/Gross/Net/R/MAE/MFE/pips/hold/entry-type/strategy/exit. |
| A3 per-bar why-rejected/why-no-signal | `GetBarFunnel` (BarEvaluations ⨝ REJECTED events) | paginated bar table + rejection histogram | Join strategy eval + risk-gate reasons. |
| A4 journal completeness | `GetRunJournal` | unified Orders+Fills view | Fix violations serialization (`[object Object]`→names); ensure commission/swap on CLOSE; verify signal-flooding is expected. |
| A5 trade chart + formatting | `GetTradeChartWindow` (bars ± window + indicators) | trade-detail chart | The flagship; see Track A4. |
| A6 progress/UX glitches | `StreamRunProgress` (typed) | monitor | No flicker, cancel button, accurate %/ETA (skip weekend/gaps), DD timeline. |
| A7 monitor improvements | `GetRunJournal` paged | monitor | Explain counters; lossless journal (drop 30-item ring — 31-B2); no 500-frame sparkline freeze. |

**Gate:** report KPIs equal `RunStats`; per-bar funnel renders; journal violations human-readable;
trade chart populated; monitor lossless + flicker-free.

---

## C2 — Strategy & config UX (NEXT-STEPS E1–E3, carry 32-P4/P5)

- E1 view effective params (read-only) — Track A5.
- E2 choose + save named custom profiles per run across New-Backtest / Report / Live; uses
  `EffectiveConfigResolver`.
- E3 edit + validate write-back through `ConfigLoader` cross-reference validation before upsert.

**Gate:** create/edit/save a custom profile; invalid config rejected with readable errors; a run uses
the chosen profile and the report shows the effective config.

---

## C3 — Verification Layer 2: cTrader parity (NEXT-STEPS B1/B2, carry 31-A2/A3)

> The comparison harness already exists from **Phase 0 P0.1** (built as the early discovery tool). C3
> promotes it from a CLI diagnostic to a first-class, UI-rendered, on-demand diagnosis.

- B1 capture cTrader CLI **JSON + HTML** (wired in P0.1) into the per-run location.
- B2 `CompareToCtrader` slice: reuse the P0.1 diff (cTrader report vs our `RunStats`/Trades — net/gross,
  commission/swap, trade count, max DD) → a discrepancy report. Run on demand (credentialed) — **not** a
  CI gate (doubt #4). Surface results in the SPA.
- 31-A2/A3: cBot emits commission/swap in close EXEC frame; adapter maps to `ExecutionEvent`; report
  shows cost columns for the cTrader path too; delete dead `equityDefinition` string.

**Gate:** a credentialed cTrader run produces a parity diff; known cost/PnL/DD fields compared and
discrepancies listed.

---

## C4 — Live venue & status (NEXT-STEPS C1, B3)

- C1 venue status page/bar from the P0.4 persisted venue-status events: connected/disconnected,
  handshake, stop requested, error, finalized; bars received, last tick, NetMQ port state.
- B3 cTrader venue parameter tracking: surface the CLI args/version/symbol/timeframes/account used per
  run (persisted in P0.3 `VenueParamsJson`) and allow per-call override.

**Gate:** venue status reflects real lifecycle events; per-run cTrader params visible + overridable.

---

## C5 — Rule-pressure testing (NEXT-STEPS G1/G2)

- G1 test inventory: map each test → the system rule it pins; identify gaps. Produce a living matrix.
- G2 adversarial suite: strategy races, daily-loss limit, max-DD, max-exposure, force-close-on-breach,
  protection-mode enter/exit, governor states, news/session filters, weekend/rollover (triple-swap),
  partial fills, limit expiry. Assert the engine **enforces** and the journal **explains** each.

**Gate:** each scenario has a test asserting enforcement + a journal explanation; inventory matrix
committed.

---

## C6 — Batch / multi-run & auto-strategy (OPEN-ISSUES RW-03/RW-04) — tail

- RW-03 batch/multi-run runner (queue several runs, compare).
- RW-04 auto strategy mode (regime/performance-based selection).
- Compliance / Experiments / Governor SPA surfaces (doubt #5) — port here if not pulled forward.

**Gate:** batch runner executes N runs and renders a comparison; (auto-strategy is research-grade,
gate = produces a defensible selection with a journal trail).
