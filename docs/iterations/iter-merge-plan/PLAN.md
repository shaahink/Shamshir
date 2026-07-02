# iter-merge-plan — Consolidated plan after iter-tape-trust + master-plan

**Written:** 2026-07-02
**Branch:** `iter/tape-trust` (active)
**Based on:** `docs/iterations/iter-master-plan/PLAN.md` (7 tracks A–G) + `docs/iterations/iter-tape-trust/PLAN.md` (T0–T5 completed)
**Priority order:** UI/UX front-end → Engine/server fixings → cTrader-dependent (owner verifies)

iter-tape-trust (T0–T5) delivered the foundation: honest tape venue, data pipeline, reconcile infrastructure,
sweep runner. The master plan defines what comes next. This doc re-sequences it per owner priority: **UI/UX
and existing-feature polish first, then engine hardening, with cTrader items last for owner verification.**

Scope guard unchanged: golden 63/63 byte-identical after every phase. Do NOT touch kernel/strategy/risk math.
Gates: Unit 314/0/6 · Integration 91/0 · golden 63/63 · `RequiresCTrader` E2E after any cBot change.

---

## Phase M1 — UI/UX: Nav + Settings + DB reset (Track D1, D5, E1)

### M1.1 — Nav consolidation (D1)
- 6 top-level areas: Live · Runs · Strategies · Risk (hub: profiles + FTMO + governor + packs) · Data · Settings
- Runs gets sub-nav: All runs · Compare · Trades · cTrader sessions
- Risk hub = one page with sub-tabs mounting existing standalone components
- Old paths redirect; routerLinkActive with child routes so refresh works

### M1.2 — Settings page (D5)
- `/api/system/info` endpoint returning version, branch, build stamp, data paths, cache stats
- Settings page shows system info + three reset actions (E1) with type-the-word confirm modals
- Replace hardcoded branch label in `settings.component.ts:44`

### M1.3 — DB reset API (E1)
- `POST /api/system/reset` with `{ scope: "runs" | "config" | "all", confirm: "delete-everything" }`
- **runs:** DELETE BacktestRuns/Trades/JournalEntries/EquitySnapshots/etc in FK-safe order; VACUUM; evict caches
- **config:** DELETE StrategyConfigs/RiskProfiles/PropFirmRuleSets/GovernorOptions/AddOnPacks; re-seed from JSON
- **all:** `SqliteConnection.ClearAllPools()`, rename trading.db* to .bak, recreate via `db.Database.Migrate()` + seeders
- 409 while any run is active
- **NEVER touch marketdata.db**

**Gate M1:** each reset scope works from Settings UI; active run blocks reset; app usable after reset without restart

---

## Phase M2 — UI/UX: New-Backtest + Monitor + Report (Track D2, D3, D4 + C1, C2, C3)

### M2.1 — New-Backtest redesign (D2)
- Full-width two-pane layout: LEFT = strategy rows, RIGHT = sticky summary panel (date span, venue, coverage check, start)
- Inline data-coverage check: per (symbol, TF) ✓/✗ coverage + m1-overlap before start; block with "download it" link when missing
- Protections as compact toggle-chip row with "all on/off" master; one-line tooltip per chip
- Field hygiene: grouped sections (Data & venue / Money / Protections); numeric inputs with sensible widths + unit suffixes
- Validation: date sanity, balance>0, at least one enabled row; start button shows disabled reason
- Saved setups get names ("Save as…") instead of timestamps

### M2.2 — Monitor redesign (D3)
- Top: progress + pass + ETA (keep)
- 2×2 grid below: (1) equity+DD live chart (C3), (2) risk tiles (equity/balance/daily-DD bar, max-DD, governor, distance-to-limit), (3) live narrative journal (B2, severity coloring), (4) open positions table
- Timeline events from narrative categories (entry/exit/breach/roll) instead of 6-kind ring
- Terminal state: "View report" CTA, final reconcile badges

### M2.3 — Report tabs (D4) + Journal UX (B4)
- Tabs: Overview · Trades · Journal · Costs & Risk
- Trades table: DEFAULT 9 columns (Sym/Dir/Entry→Exit/Net/R/Pips/Exit reason/Strategy/Hold); column-chooser for other 10
- Row expand = trade chart (C1) + trade narrative (B3)
- Journal: one `<app-journal>` component (kind chips + severity filter + search + cursor "load more") used by BOTH monitor and report
- Header: Duplicate · Export ▾ · Monitor · All runs
- Lazy-load per-tab API calls

### M2.4 — Charts (C1, C2, C3)
- **C1:** Entry/exit arrows at bar times (above/below candles) + SL/TP step-lines from trail events; in-trade region shading
- **C2:** Daily DD bar chart (22:00 UTC roll, NOT calendar date) with 5% limit line; underwater area chart; tiles (worst day, days >50% budget)
- **C3:** Unified equity+balance+DD chart for monitor AND report; live monitor append-only updates

**Gate M2:** owner drives tape run from redesigned UI; monitor + report show consistent narrative; charts answer "what happened"

---

## Phase M3 — Engine/Server: Narrative + trade detail (Track B1, B2, B3)

### M3.1 — Narrative projection (B1)
- `RunNarrativeService`: projects StepRecords → `NarrativeEvent { seq, simTime, severity, category, headline, detail }`
- Headlines built server-side in C#: human sentences for OrderProposed, OrderFilled (SL/TP/close), StopLossModifyRequested (trail), gate rejections
- `GET /api/runs/{id}/narrative?afterSeq=&kinds=&severity=` — cursor-paged, BarClosed/EquityObserved excluded by default
- Unit tests for each headline builder; project from `EventJson`/`EffectsJson` (stable, golden-locked)

### M3.2 — Live monitor feeds off real journal (B2)
- Switch monitor from `state.RecentJournal` ring → `GET /api/runs/{id}/narrative` polling every 1–2s
- SignalR envelope carries `latestSeq` and counters only; client fetches gap from narrative endpoint
- Delete `TallyEvent` journal branch once switched; `BacktestJournal`/`LogLines` become error/system only
- Gate: during live tape run, monitor journal = report journal (same seq, same lines)

### M3.3 — Trade narrative columns (B3)
- Migration: nullable `EntryReason`, `EntryRegime`, `EntrySnapshotJson`, `ExitDetailJson` on `Trades`
- Stamp at open (from OrderProposed verdict + BarEvaluator regime) and close (from position state) in effect executor
- Surface on trade detail + expandable report/monitor row: "Why entered" + "Why exited"
- Golden untouched (executor stamping is outside kernel reducer)

**Gate M3:** driven run produces trades with why-entered/exited; golden 63/63

---

## Phase M4 — Engine/Server: Housekeeping + remaining gaps

### M4.1 — Runs housekeeping (E2)
- Multi-select delete runs (FK-safe cascade) from runs list
- Auto-prune option in Settings ("keep last N runs", default off)

### M4.2 — Data Manager enhancements
- Per (symbol, TF) delete range in Data Manager
- Storage size per symbol; last-verified fidelity chip (A2 when built)

### M4.3 — Remaining gaps from iter-tape-trust
- F5: Commission half-at-open (split commission in ComputeCosts)
- F6: Document limit+SL same-fine-bar ordering
- F7: Handle fine bars in decision-TF gaps
- Coverage view: T1's promised per-(symbol, TF) m1 overlap badge (feeds M2.1's pre-start check)
- Journal thinning: verify `SkipJournal` in EngineHostOptions actually skips StepRecord writes correctly

**Gate M4:** delete 5 runs from list → 0 rows in all related tables; F5 applied identically both venues

---

## Phase M5 — cTrader-dependent (OWNER VERIFIES)

**Do NOT implement these — infrastructure is ready, owner runs and commits artifacts.**

### M5.1 — Oracle set + trust gate (Track A1 + V2-V5 from iter-tape-trust)
- Owner downloads EURUSD H1+M1 for working set (V2)
- Owner runs speed baseline: tape vs cTrader same config (V3)
- Owner drives compare-both: run tape + cTrader same config → reconcile endpoint (V4)
- Owner records oracle configs with committed `shamshir-report.json` artifacts (A1)
- Define tolerance contract in `RECONCILE-FINDINGS.md` with per-divergence sizing

### M5.2 — Continuous verification (Track A2)
- Build committed-artifact test: reads oracle artifacts, runs tape vs stored report, asserts within contract
- `[Trait("RequiresCTrader","false")]` — runs in CI without cTrader creds
- UI fidelity chip on Data page + tape run report header

### M5.3 — Per-bar recorded spread (Track A3)
- cBot captures `Symbol.Spread` at bar close → NDJSON schema extension
- Tape venue uses per-bar spread when present; reconcile A1 set again
- cBot rebuild + `RequiresCTrader` E2E

---

## Sequencing summary

```
M1 (UI: nav + settings + DB reset)     ← start here, no dependencies
M2 (UI: backtest + monitor + report)    ← needs M1 nav for the pages to live under
M3 (Engine: narrative + trade detail)   ← needs M2 journal component to consume narrative
M4 (Engine: housekeeping + gaps)        ← can run in parallel with M3
M5 (cTrader: owner verifies)            ← needs M3 foundation + owner's cTrader access
```

**Every gate from iter-tape-trust still applies:** golden 63/63, Unit 314/0/6, Integration 91/0.
Numbers → `docs/audit/PROGRESS.md` per phase.

---

## What NOT to do

- Do NOT change strategy/risk/kernel math — golden stays 63/63
- Do NOT group anything "daily" by calendar date — 22:00 UTC prop-firm roll
- Do NOT create new persisted journal tables — project over StepRecords (D-B1)
- Do NOT touch `marketdata.db` from any reset path
- Do NOT implement M5 (owner's job — infrastructure is ready)
- Do NOT auto-tune, auto-rotate symbols, or add ML in v1
