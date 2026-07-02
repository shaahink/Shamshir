# MASTER PLAN ΓÇö the multi-iteration program after iter-tape-trust

**Written:** 2026-07-02 (Claude / Fable 5, at owner request)
**For:** the implementation agent (OpenCode / DeepSeek), executed as MULTIPLE iterations over time. Each track
below is sized so one-or-two track phases Γëê one agent iteration. This document is the map; each iteration
still gets its own short `docs/iterations/<name>/PROGRESS.md` as it lands.
**Relationship to other plans:** this is a CONTINUATION of `docs/iterations/iter-tape-trust/PLAN.md`
(in flight on branch `iter/tape-trust` ΓÇö do NOT modify that plan or duplicate its work) and the research
sequencing in `docs/QUANT-ROADMAP.md`. Bug/gap IDs (B*, F*) come from
`docs/iterations/iter-marketdata-tape/HANDOVER-REVIEW.md`.
**Branch bases:** start every new iteration from the latest integrated tip (today: `iter/tape-trust` after
its T-phases merge). Never work in the main worktree while another agent is active there ΓÇö `git worktree add`
a fresh one.

---

## ┬º0 ΓÇö Session warm-up (read this first, every fresh agent session)

**What Shamshir is:** a prop-firm algo-trading engine for cTrader. Deterministic event-sourced kernel
(`TradingEngine.Engine`) + risk gate/governor/prop-firm compliance + 9 test strategies + Angular 19 SPA
served single-origin by `TradingEngine.Web`. Three backtest venues: `tape` (fast in-process, canonical
`marketdata.db`), `replay` (in-process, per-run bars), `ctrader` (3-process NetMQ lock-step; the ORACLE and
source of truth for fills/economics).

**Read in order (Γëê20 min):**
1. `docs/iterations/iter-marketdata-tape/FULL-HANDOVER.md` ΓÇö system tour (sections 1ΓÇô8 accurate).
2. `docs/iterations/iter-marketdata-tape/HANDOVER-REVIEW.md` ΓÇö verified reality + bug/gap vocabulary.
3. `docs/iterations/iter-tape-trust/PLAN.md` + its PROGRESS/VERIFICATION ΓÇö what's already fixed (T0ΓÇôT5).
4. `docs/QUANT-ROADMAP.md` ΓÇö research methodology; Tracks F/G below implement its ┬º5ΓÇô┬º6.
5. This file.

**Hard rules (unchanged from every prior iteration):**
- NO decision-kernel / strategy-math / risk-math changes unless a track explicitly says so; golden suite
  stays **63/63 byte-identical** after every phase.
- Gates: `dotnet test tests/TradingEngine.Tests.Unit` ┬╖ Integration (91/91) ┬╖ golden filter (see
  iter-tape-trust ┬ºgates) ┬╖ `RequiresCTrader` E2E after any cBot change. Numbers ΓåÆ `docs/audit/PROGRESS.md`.
- One commit per phase; update the iteration's PROGRESS.md in the same commit.
- Owner reviews visually after UI phases ΓÇö put a one-line "how to see it" note in PROGRESS.md per phase.

**State of the world (2026-07-02):** tape venue works and reports honestly (T0 landed: `IReplayVenue`,
ExitResolution surfaced); recorder/download fixed at scale (T1); reconcile mapper + endpoint exist (T2);
F1 spread-on-fills, F2 intrabar-equity watermark, F4 gap-through, B5 limit expiry landed (T3); sweep runner
(T5) in flight. V2 bulk data download + V4/V5 real reconcile artifacts may still be pending ΓÇö check
`iter-tape-trust` PROGRESS before assuming.

---

## ┬º1 ΓÇö Owner decision block (vote before the affected track starts; Γ£à = default)

- **D-A1 Oracle cadence.** Γ£à **A.** Weekly scheduled reconcile (one short cTrader run vs same-config tape run,
  artifact committed) + on-demand after any venue change. ┬╖ B. Only on venue changes (cheaper, drift found late).
- **D-B1 Journal consolidation shape.** Γ£à **A.** StepRecords (`JournalEntries` table) stay the ONE persisted
  journal; everything user-facing (monitor feed, report views, trade narrative) is DERIVED read-side from it;
  the web-side `RecentJournal`/`BacktestJournal` become transport-only (no third vocabulary). ┬╖ B. New
  "narrative" table written in parallel (risks divergence ΓÇö rejected unless read-side proves too slow).
- **D-B2 Trade narrative persistence.** Γ£à **A.** Add nullable columns to `Trades`
  (`EntryReason`, `EntryRegime`, `EntrySnapshotJson`, `ExitDetailJson`) stamped at open/close by the effect
  executor ΓÇö cheap to query, survives journal pruning. ┬╖ B. Join from journal at read time (no migration, but
  slow + breaks if journal thinned in sweep mode).
- **D-C1 Report layout.** Γ£à **A.** Tabs (Overview ┬╖ Trades ┬╖ Journal ┬╖ Bars/Why ┬╖ Costs&Risk) replacing the
  single 12-section scroll. ┬╖ B. Collapsible accordion sections (keeps one-page feel; still one giant DOM).
- **D-D1 Nav consolidation.** Γ£à **A.** 6 top-level areas: **Live ┬╖ Runs ┬╖ Strategies ┬╖ Risk (hub:
  profiles + FTMO rules + governor + packs) ┬╖ Data ┬╖ Settings**; Trades/Compare/cTrader-sessions move under
  Runs; API-docs link stays. ┬╖ B. 8 areas (keep Trades top-level). Owner may re-letter freely ΓÇö the hub idea
  is the point, not the exact grouping.
- **D-E1 DB reset semantics.** Γ£à **A.** Three scoped, confirm-gated actions in Settings: *Clear runs*
  (runs/trades/equity/journal/orders/positions/events/sessions; config+marketdata survive), *Reseed config*
  (drop strategy/risk/pack/governor tables ΓåÆ reseed from JSON), *Wipe all* (recreate trading.db; marketdata.db
  NEVER touched by any reset). ┬╖ B. Single "factory reset". *(Marketdata gets its own separate "delete symbol/tf
  range" in Data Manager instead.)*
- **D-F1 Portfolio mechanism.** Γ£à **A.** A named **Portfolio** config entity (strategy rows + per-row risk
  budget + activation rule) that New-Backtest can run as one unit; static allocation first, regime-based
  activation as the only "smart" rule in v1. ┬╖ B. Free-form per-run row builder only (status quo ΓÇö no reusable
  portfolio object).
- **D-G1 Symbol program scope.** Γ£à **A.** Build the *scorecard + nomination report* (rank candidate symbols on
  measured cost/volatility/data quality); the OWNER picks the trading set (recommend 2ΓÇô4 symbols); the system
  does not auto-rotate symbols. ┬╖ B. Auto-selection in the engine (rejected for now: adds a silent degree of
  freedom that invalidates comparisons).

---

## Track A ΓÇö Venue-fidelity assurance program ("not experimenting on wrong calcs")

**Goal:** the tape venue's economics are *provably* close to cTrader and STAY that way. T2/T3 built the
mechanics; this track makes fidelity a standing property with numbers, not a one-off check.

### A1 ΓÇö Establish the oracle set + tolerance contract
- Curate 3ΓÇô5 **oracle configs** (short cTrader runs with committed `shamshir-report.json`): at minimum
  (a) market-entry trend strategy H1/1wk, (b) limit-entry config, (c) a config with trailing+partial add-ons,
  (d) a multi-day run crossing a weekend (gap + swap exposure), (e) a breach/force-close run.
- Define the tolerance contract in `docs/audit/RECONCILE-FINDINGS.md`: per-trade net within X (start: 1├ù spread
  cost), aggregate NetProfit within Y, MaxDD within Z pts, TradeSet exact. Every number JUSTIFIED by a named
  residual gap (F3 trailing cadence, F5 commission timing, ΓÇª) ΓÇö no vibes tolerances.
- **Gate:** `scripts/reconcile-run.ps1` over all oracle configs ΓåÆ committed artifact per config, every row
  within contract or mapped to a named residual.

**Tricky bits + guidance:**
- *Bid/ask semantics.* cTrader recorder bars are BID bars. After T3/F1, verify the convention is applied
  symmetrically: longs enter at ask (= bid + spread), long SL/TP trigger on bid (raw bar), shorts enter at bid,
  short SL/TP trigger on ask (bar ┬▒ spread offset). Write ONE table of the four cases in code comments +
  a unit test per case ΓÇö this is where sign errors hide. Γ£à Keep the spread constant per-symbol
  (`SymbolInfo.TypicalSpread`) until A3.
- *Swap calibration.* Don't derive swap rates from formulas; read cTrader's own per-trade swap from an oracle
  run crossing 2+ nights (incl. the triple-swap day) and back-solve the per-lot-per-night rate into
  `symbols.json`. Verify `TradeCostCalculator.CountNightsHeld` boundary is 22:00 UTC ┬▒ the venue's actual
  rollover from the oracle timestamps, not an assumption.
- *Don't chase zero.* F3 (per-tick trailing) will leave a residual on trailing-heavy configs. Measure it,
  record it in the contract, and only escalate to a venue-side exit-TF trailing mode (T3 note) if the residual
  changes a GO/NO-GO decision on a real strategy.

### A2 ΓÇö Continuous verification (the drift alarm)
- A small runner (script or `SweepRunnerService` sibling) that executes: 1 tape run per oracle config +
  reconcile vs the committed oracle report ΓåÆ PASS/FAIL per the tolerance contract. Wire as (a) an opt-in test
  `[Trait("RequiresCTrader","false")]` reading committed oracle artifacts (fast, runs in CI/gates), and (b) a
  weekly OWNER habit refreshing one oracle from a live cTrader run (D-A1).
- Surface the latest verdict in the UI: a "Fidelity: verified <date> Γ£ô / drift Γ£ù" chip on the Data page and on
  every tape run's report header.
- **Gate:** deleting one committed oracle trade (tamper test) flips the chip to drift.

**Guidance:** the committed-artifact test is the trick ΓÇö it makes fidelity regression-testable WITHOUT cTrader
creds in the loop. Store oracle artifacts under `tests/data/oracle/<config-id>/` with the config JSON beside
them so the tape side can re-run the exact config forever.

### A3 ΓÇö Per-bar recorded spread (upgrade the cost model's weakest constant)
- Recorder cBot: capture `Symbol.Spread` at bar close into the NDJSON schema (new nullable `spreadPips` field;
  `MarketDataShardIo` version-tolerant parse). Store column on `MarketDataBar` (nullable REAL).
- Tape venue: use per-bar spread when present, else `TypicalSpread`. Reconcile A1 set again ΓÇö expect the
  per-trade residual to tighten on volatile sessions.
- **Tricky:** the shard format is locked by `MarketDataShardIoTests.Parses_the_exact_cbot_recorder_line` ΓÇö ADD
  a new locked test for the extended line, keep parsing the old line (backfill = null). cBot change ΓçÆ `.algo`
  rebuild + `RequiresCTrader` E2E.

---

## Track B ΓÇö Journal & run-narrative backbone (the "actually useful journal")

**Today's reality (audited 2026-07-02) ΓÇö four disjoint journal-ish stores:**
1. **`JournalEntries`** (SQLite) ΓÇö the kernel StepRecord journal (every event: BarClosed, OrderProposed,
   fills, trails, equity, rolls) with Risk/Regime/Verdicts. The ONLY complete record. Feeds the report's
   "Journal", "Per-bar why" and "Bar Inspector" ΓÇö three overlapping tables on one page.
2. **`state.RecentJournal`** ΓÇö a 30-entry ring of coarse strings (SIGNAL/ORDER/EXEC/CLOSE/REJECTED/BREACH)
   built in `BacktestOrchestrator.TallyEvent` from progress callbacks; thisΓÇönot the real journalΓÇöis what the
   Live Monitor shows. Different vocabulary, no SL/TP prices, no strategy verdicts, drops everything >30.
3. **`BacktestJournal`** (Web) ΓÇö ephemeral `{eventType,message}` channel + `LogLines` string queue. Debug relic.
4. **`Events`/`Orders`/`Positions` tables + `VenueSessions`** ΓÇö side ledgers, partially iter-33 era.

**Goal:** ONE canonical journal (StepRecords), one derived human-readable **narrative** used identically by
the live monitor and the finished report, and per-trade "why I entered / why I exited".

### B1 ΓÇö Narrative projection (server-side)
- New read-side service `RunNarrativeService`: projects StepRecords ΓåÆ `NarrativeEvent { seq, simTime, severity
  (info/action/warning/critical), category (Signal/Entry/Exit/Risk/AddOn/System), headline, detail }`.
  Headlines are human sentences built server-side ONCE, e.g.:
  - `OrderProposed+RecordDecisionEvent(Accepted)` ΓåÆ "trend-breakout LONG EURUSD @1.1347 ΓÇö SL 1.1320 (27p), TP 1.1401 (2.0R), 0.74 lots (0.5% risk)"
  - `OrderFilled(close, CloseReason=SL)` ΓåÆ "Closed LONG EURUSD @1.1320 ΓÇö stop-loss hit, ΓêÆ$412 net (ΓêÆ1.0R), held 6h"
  - `StopLossModifyRequested(TRAIL)` ΓåÆ "Trailed stop 1.1335 ΓåÆ 1.1349 (ATR├ù2.5)"
  - gate rejection ΓåÆ "Signal rejected: daily-DD budget could not absorb 0.5% risk (remaining 0.3%)"
- Endpoint `GET /api/runs/{id}/narrative?afterSeq=&kinds=&severity=` (cursor-paged ΓÇö this IS the cache-plan-P6
  cursor read, do both at once). BarClosed/EquityObserved are EXCLUDED by default (opt-in kinds).
- **Gate:** for a seeded run, the narrative for one trade reads as a coherent 4ΓÇô8 line story; cursor paging
  returns strictly increasing seq; unit tests for each headline builder.

**Tricky bits + guidance:**
- The needed fields live in `RawEvent`/`RawEffects` on fresh StepRecords but only as serialized
  `EventJson/EffectsJson` when re-read from DB (F3 background sink). Build the projector on the DTO shapes in
  `EventJson` (they're stable ΓÇö golden-locked), with per-kind small parsers; tolerate missing fields (older
  runs) by degrading the headline, never throwing.
- Γ£à Project at READ time (D-B1-A). If profiling shows re-projection cost matters for live polling, add a
  small per-run memo keyed by (runId, lastSeq) in `RunDataCache` ΓÇö do not create a second persisted store.
- Keep formatting OUT of Angular: one C# headline builder = one vocabulary everywhere (report, monitor,
  export, future LLM run-summary).

### B2 ΓÇö Live monitor feeds off the real journal
- Replace the `RecentJournal` ring as the monitor's source: monitor polls `/narrative?afterSeq=` every 1ΓÇô2 s
  (or the SignalR envelope carries `latestSeq` and the client fetches the gap ΓÇö Γ£à simpler: poll; the envelope
  keeps only counters/progress). Delete the ring + `TallyEvent`'s journal branch once the monitor is switched
  (keep the counters). `BacktestJournal`/`LogLines` shrink to error/system reporting only.
- **Gate:** during a live tape run, the monitor journal shows the SAME lines (same seq) that the report shows
  after completion ΓÇö one story, live or finished (the owner's core ask).
- **Tricky:** StepRecords reach SQLite via a buffered background sink ΓÇö the cursor must read through
  `RunDataCache.GetJournal` (already append-invalidated) for the RUNNING run, DB for finished. That's exactly
  the cache-first pattern `RunQueryService` already uses; extend it, don't fork it.

### B3 ΓÇö Trade narrative ("why I entered / why I exited") [D-B2]
- Migration: nullable `EntryReason`, `EntryRegime`, `EntrySnapshotJson` (indicator values from the verdict +
  planned SL/TP/R at entry), `ExitDetailJson` (final SL after trails, #trail moves, BE armed?, partial history)
  on `Trades`. Stamp at open (from the OrderProposed verdict reason + BarEvaluator regime) and at close (from
  position state) in the effect executor ΓÇö write-side only, NO kernel change (the data already flows through
  the executor).
- Surface: Trade detail page + expandable row in report/monitor shows "Why entered" (strategy rule text +
  reason + regime + indicator snapshot) and "Why exited" (reason + add-on history). The strategy's
  `entryRule`/`exitFormula` strings (already on StrategySummary) give static context; the per-trade reason
  gives the dynamic one.
- **Gate:** a driven run produces trades whose detail answers both questions with no journal spelunking;
  golden untouched (executor stamping is outside the kernel reducer).
- **Tricky:** partial closes produce multiple close fills per position ΓÇö decide one `Trades` row semantics
  up-front (today: one row per close fill; keep that, and let `ExitDetailJson` on each row carry its fraction
  context). Don't attempt to restructure the trade ledger in this track.

### B4 ΓÇö Report journal UX unification
- One `<app-journal>` component (kind chips + severity filter + search + cursor "load more") consuming the
  narrative endpoint; used by BOTH monitor and report. The report's three tables collapse to two views:
  **Narrative** (default) and **Bar Inspector** (the per-bar table stays ΓÇö it answers "why NO trade", which the
  narrative hides by design; fold "Per-bar why" verdicts INTO the Bar Inspector row expand).
- Journal NDJSON export stays (raw StepRecords ΓÇö it's the audit artifact).
- **Gate:** report page renders Γëñ2 journal surfaces; WebSmoke green; owner visual pass.

---

## Track C ΓÇö Charts that answer questions

**Today (audited):** trade chart = candles + 4 near-identical full-width dashed horizontal lines (Entry/Exit/
SL/TP price levels via `LineSeries`) + 2 hacky vertical lines whose y-range is min/max of marker PRICES
(`candle-chart.component.ts:100-122`) ΓÇö you genuinely cannot tell where entry/exit happened. "DD Timeline" in
the report is actually a daily-PnL strip (mislabeled). Equity chart derives DD client-side from displayed
points. Live monitor equity keeps last 500 points, no DD line. Daily grouping uses calendar dates, not the
22:00 UTC prop-firm day.

### C1 ΓÇö Trade chart: time-anchored entry/exit + level history
- Use lightweight-charts **series markers** (`createSeriesMarkers` in v5 ΓÇö the repo is on the v5 API,
  `chart.addSeries(CandlestickSeriesΓÇª)`): entry = arrow at the entry BAR (up/belowBar for long, down/aboveBar
  for short, label "Entry 1.1347"), exit = arrow at the exit bar labeled with reason + R ("SL ΓêÆ1.0R").
  Remove the vertical-line hack.
- SL/TP as **step lines over time**, not flat lines: build the stop's path from the journal's
  `StopLossModifyRequested` events (B1 gives them cursor-cheap) so a trailed stop is VISIBLE walking up under
  price; TP flat unless dynamic. Legend: SL red step, TP green, entry/exit markers.
- Add ~20 bars of pre-entry context and shade the in-trade region (light direction-tinted band via
  `createPriceLine`-free approach: a translucent histogram/area series under the candles, or v5 panes; Γ£à
  simplest: keep candles + markers + step-lines, drop the shading if it fights the library).
- **Gate:** for a trailing-stop trade, the chart shows entry arrow ΓåÆ rising stop steps ΓåÆ exit arrow at the
  stop touch; owner can narrate the trade from the chart alone.
- **Tricky:** marker times must be BAR times that exist in the series (lightweight-charts snaps/drops
  otherwise) ΓÇö snap `openedAtUtc/closedAtUtc` to the chart TF's bar opens (the trade-chart endpoint already
  returns the bar list; snap server-side). Single-bar trades: both markers on one bar is fine once they're
  arrows above/below rather than same-price lines (delete `nudgeSamePriceMarkers`).

### C2 ΓÇö Drawdown views (daily bars + period timeline)
- **Server-side** `GET /api/runs/{id}/drawdown`: from EquitySnapshots compute per prop-firm day (22:00 UTC
  roll ΓÇö reuse `ResetClock`/`ResetConfig`, do NOT group by calendar date) ΓåÆ `{ day, startEquity, minEquity,
  endEquity, maxDailyDdPct, breached }`, plus the running underwater series (equity vs running peak) and
  period aggregates (weekly/monthly max DD).
- **UI on the report:** (a) DAILY DD bar chart ΓÇö one red bar per day = maxDailyDdPct with a horizontal 5%
  limit line, breach days highlighted; (b) underwater (drawdown-vs-time) area chart under the equity curve ΓÇö
  rename the current mislabeled "DD Timeline" to "Daily PnL" and keep it; (c) tiles: worst day, days >50%
  budget, current underwater duration.
- **Gate:** for a multi-day oracle run, daily DD matches cTrader's daily numbers within the A1 contract;
  the 22:00 boundary is test-pinned (a trade losing at 21:59 vs 22:01 lands in different days).
- **Tricky:** intrabar equity (T3/F2 watermark) is what makes `minEquity` honest ΓÇö if F2 emitted only
  worst-case per decision bar, use those; document which resolution the endpoint used per run
  (`ExitResolution` already on the run detail).

### C3 ΓÇö Equity & balance, live and final, one semantics
- Unify: monitor and report equity charts both show Equity + Balance + underwater DD pane (monitor currently
  `showDrawdown=false` ΓÇö turn it on once C2's series exists server-side; client-side derive is fine for live).
- Live monitor: append-only via `series.update()` instead of rebuilding `setData` on every frame (the
  current `equityData.update` + full `setData` path re-sorts 500 points per frame; fine but wasteful ΓÇö low
  priority polish).
- Document (in code, one place) the equity semantics chain: venue AccountUpdate (mark-to-market, per bar +
  exits) ΓåÆ kernel EquityObserved ΓåÆ AccountSnapshot store (500 ms poll ΓåÆ SignalR) ΓåÆ EquityPersistenceHandler
  (5 s flush ΓåÆ EquitySnapshots). The monitor shows the polled value; the report shows persisted snapshots;
  after B2 both label the source.
- **Gate:** live equity at completion == report equity last point == run summary NetProfit + balance
  (already a report reconciliation badge ΓÇö extend it to the monitor's final frame).

---

## Track D ΓÇö App information architecture + the two key pages

**Today (audited):** 12 top-level nav areas; New-Backtest is one `max-w-4xl` column with cramped mixed grids
(6 numeric fields + venue select + 7 stacked checkboxes at `new-backtest.component.ts:158-209`); the report is
a single scroll of ~12 sections with an 8-button header and a 19-column trade table; Settings is a static
read-only card with a hardcoded stale branch label (`settings.component.ts:44`); money/risk config is split
across 4 pages (Risk, FTMO, Governor, Packs) that the run form then re-overrides with 7 checkboxes.

### D1 ΓÇö Nav consolidation + Risk hub [D-D1]
- 6 areas (see D-D1). **Risk hub** = one page with sub-tabs: Profiles ┬╖ Prop-firm rules ┬╖ Governor ┬╖ Add-on
  packs, plus a "effective risk at a glance" summary strip (profile ├ù ruleset ├ù governor state) so the owner
  sees the whole money picture in one place. The 4 existing detail components are REUSED inside tabs (they're
  standalone components ΓÇö mount, don't rewrite).
- Runs area gets sub-nav: All runs ┬╖ Compare ┬╖ Trades ┬╖ cTrader sessions.
- **Gate:** every existing route still reachable (old paths redirect); WebSmoke green; nav Γëñ7 items.
- **Tricky:** deep links & routerLinkActive with child tabs ΓÇö use child routes per tab, not component state,
  so refresh/back work.

### D2 ΓÇö New-Backtest redesign
- Full-width two-pane layout: LEFT = what to run (strategy cards ΓåÆ row plan), RIGHT = sticky summary panel
  (rows enabled, date span, est. bar count via the T0 pre-query endpoint, venue + data-coverage check, risk
  profile summary, START button). Kill `max-w-4xl`.
- Field hygiene: group into labeled sections (Data & venue / Money / Protections); numeric inputs get fixed
  sensible widths + units in-suffix ("pips", "$", "/M"); protections become a compact toggle-chip row with an
  "all on/off" master (the current 7-checkbox stack), each with a one-line tooltip of WHAT it disables.
- Inline data-coverage: when venue=tape, per selected (symbol, TF) show Γ£ô/Γ£ù coverage + m1-overlap (T1's
  coverage API) BEFORE start; block start with a "download it" link when missing (kills the run-then-fail loop).
- Validation: date-range sanity (start<end, warn >2yr), balance>0, at least one enabled row; start button
  shows the reason it's disabled.
- **Gate:** owner drives a tape run without touching docs; a missing-data selection is caught pre-start;
  visual pass.
- **Tricky:** keep `StartRunRequest` unchanged (server contract stays; this is layout + affordances). The
  saved-setups strip (localStorage) stays but gets names ("Save asΓÇª" prompt) instead of timestamps.

### D3 ΓÇö Run Monitor redesign
- Layout: top strip = progress + pass + ETA (keep); THEN a 2├ù2 grid: (1) equity+DD live chart (C3), (2) risk
  tiles (equity/balance/daily-DD with % of budget consumed as a bar, max-DD, governor, distance-to-limit ΓÇö
  consolidate the current 8 tiles + 6 counters into these two groups), (3) live narrative journal (B2) with
  severity coloring, (4) open positions table (symbol, dir, lots, entry, current SL, floating PnL ΓÇö from the
  snapshot's open-position data; if not carried today, extend `AccountSnapshot`/envelope minimally).
- Timeline (the ticks bar) stays under the progress strip; its events now come from narrative categories
  (entry/exit/breach/roll) instead of the 6-kind ring.
- Terminal state: when completed/failed, swap the header CTA to "View report", show the final reconcile
  badges (C3 gate) inline.
- **Gate:** during a live run the owner can answer "am I safe, what am I holding, what just happened" in one
  glance each; visual pass.

### D4 ΓÇö Run Report information architecture [D-C1]
- Tabs: **Overview** (tiles + equity/DD + daily DD bars + daily PnL + run plan chips), **Trades** (table with
  DEFAULT 9 columns: Sym/Dir/EntryΓåÆExit/Net/R/Pips/Exit reason/Strategy/Hold; column-chooser for the other 10;
  row expand = trade chart (C1) + narrative (B3)), **Journal** (B4 narrative + Bar Inspector),
  **Costs & Risk** (gross/comm/swap tiles, funnel + rejection histograms, MAE/MFE scatter, reconciliation
  badges, fidelity chip (A2), profiling), and header slimmed to: Duplicate ┬╖ Export Γû╛ (journal/CSV/JSON/MD as
  a menu) ┬╖ Monitor ┬╖ All runs.
- **Gate:** every existing feature reachable in Γëñ2 clicks from the report; initial tab renders with Γëñ4 API
  calls (lazy-load per tab); WebSmoke green.
- **Tricky:** the 8 upfront fetches move into per-tab lazy loaders; keep the trades tab's expand cheap by
  fetching chart/narrative on expand only (already lazy for charts ΓÇö keep).

### D5 ΓÇö Settings becomes real + DB reset [Track E delivers the API]
- Settings page: system info from the API (`/api/system/info`: version/branch/build stamp ΓÇö replace the
  hardcoded label), data locations, cache stats (RunDataCache resident runs), and the three reset actions
  (D-E1) each with a type-the-word confirm modal + result toast.

---

## Track E ΓÇö Data & DB management

### E1 ΓÇö Scoped database reset [D-E1]
- `POST /api/system/reset` with `{ scope: "runs" | "config" | "all" }`, admin-less (single-user app) but
  confirm-token in body (client sends the typed word). Implementation guidance per scope:
  - **runs:** DELETE in FK-safe order (Trades, Orders, Positions, Events, EquitySnapshots, JournalEntries,
    Bars, VenueSessions, ExperimentRuns, Experiments, Datasets, ConfigSets, BacktestRuns) inside one
    transaction; then `VACUUM` (off-transaction); evict `RunDataCache` + `IMemoryCache` runs list.
  - **config:** delete StrategyConfigs/RiskProfiles/PropFirmRuleSets/GovernorOptions/AddOnPacks; re-run the
    seeders (they're idempotent startup services ΓÇö expose their entry point).
  - **all:** close pooled connections (`SqliteConnection.ClearAllPools()` ΓÇö REQUIRED on Windows or the file
    stays locked), move `trading.db*` (db/-wal/-shm) to `trading.db.bak-<ts>`, recreate via
    `db.Database.Migrate()` + seeders. NEVER touch `marketdata.db`.
- Refuse (409) while any run is active (`_runs` non-terminal); UI disables the buttons with the reason.
- **Gate:** each scope driven via UI on a seeded DB; app fully usable after each without restart; an active
  run blocks reset; integration test per scope.
- **Tricky:** the WAL files + connection pooling are the classic Windows failure ("file in use") ΓÇö the
  ClearAllPools + rename dance is the reliable pattern; test on Windows, not just CI Linux.

### E2 ΓÇö Runs housekeeping + marketdata hygiene
- Bulk ops on the runs list: multi-select ΓåÆ delete runs (same FK-safe cascade, scoped to ids); auto-prune
  option in Settings ("keep last N runs", default off).
- Data Manager: per (symbol, TF) delete range; storage size per symbol; last-verified fidelity chip (A2).
- **Gate:** deleting a run removes all its rows (assert via row counts) and its cache entry.

---

## Track F ΓÇö Strategy portfolio & the smart picker [D-F1; methodology in QUANT-ROADMAP ┬º5]

**Positioning (owner's stance honored):** the system is NOT tuning-centric. The portfolio work is about
*composition and gating*, driven by evidence from the sweep/exploration tooling ΓÇö not parameter search.

### F1 ΓÇö Portfolio entity + runner
- New `Portfolio` config (DB-seeded like packs): `{ id, name, rows: [{strategyId, symbol, tf, packId?,
  riskBudgetFraction}], activation: { mode: "always" | "regime", allow: [...] } }`. New-Backtest gains
  "Run portfolio <name>" (one click ΓåÆ the row plan), and the report gains a per-row contribution table
  (net, DD contribution, correlation to portfolio).
- Per-row `riskBudgetFraction` maps to the existing per-strategy risk override (`EffectiveConfigResolver`
  deep-merge) ΓÇö composition without kernel change.
- **Gate:** a 3-row portfolio runs as one backtest; report shows per-row contribution; golden untouched.

### F2 ΓÇö Correlation & contribution evidence (the picker's data)
- From any N finished runs (or one portfolio run), compute pairwise correlation of DAILY net PnL (22:00 roll)
  + each row's marginal DD contribution. Endpoint + a small matrix heatmap in Compare/Portfolio view.
  Cache nothing fancy ΓÇö it's a few hundred days ├ù a handful of streams.
- **Guidance:** correlation on daily PnL, not trade PnL (different trade counts break alignment); require ΓëÑ40
  overlapping days before showing a number (else "insufficient data"); Spearman is fine (robust to fat tails),
  Pearson acceptable v1.
- **Gate:** two runs of the same strategy show corr Γëê 1.0 (sanity pin); trend-vs-mean-reversion shows visibly
  lower correlation on the owner's real data.

### F3 ΓÇö Regime-gated activation (the only "smart" in v1)
- The regime filter already gates per-strategy entry. Elevate to the portfolio: activation rule per row
  (e.g. trend rows only in Trending, MR rows only in Ranging) ΓÇö implemented as the EXISTING
  `RegimeFilterOptions` written by the portfolio resolver (no new kernel path).
- Validate honestly: for each candidate portfolio, run tape backtests with regime-gating ON vs OFF over the
  same window; the report compares net/DD/P(pass). Only ship a default-ON portfolio if the evidence says so.
- **Guidance for the picker recipe (owner-facing, documented on the portfolio page):**
  1. exploration-triage the 9 strategies per QUANT-ROADMAP ┬º5 (kill the flat ones);
  2. pick 2ΓÇô4 survivors from DIFFERENT families (trend / mean-reversion / session);
  3. check F2 correlation < ~0.4 pairwise on daily PnL;
  4. weight by inverse-DD (not by return) with per-row budgets summing Γëñ total risk appetite;
  5. Monte Carlo the combined OOS trade stream ΓåÆ P(pass) (QUANT-ROADMAP ┬º3.3) before adopting.
- **Do NOT build in v1:** dynamic weight rebalancing, ML regime classifiers, strategy auto-retirement. Each is
  a silent-degree-of-freedom generator; revisit only with A2-verified data + walk-forward evidence.

---

## Track G ΓÇö Symbol program (research ΓåÆ features) [D-G1]

**The research answer first (owner's question "few symbols or many, can the system decide?"):**
- **Work on FEW symbols (2ΓÇô4) deliberately chosen; let the system RANK, not choose.** More symbols only help
  if their return streams are lowly correlated AND each passes cost/quality thresholds; most FX majors are
  heavily cross-correlated (EURUSD/GBPUSD ~0.7+), so symbol #5 usually adds correlation, not diversification.
  A nominated-symbol scorecard gives the evidence; automatic in-engine symbol rotation adds an untestable
  degree of freedom (rejected, D-G1).
- **What makes a symbol GOOD for this system:** (1) low **cost-per-volatility** ΓÇö spread (+commission in pips)
  ├╖ ATR(14) on the trading TF; you're paying the spread out of each ATR-sized move (EURUSD H1: ~1 pip cost on
  ~15 pip ATR Γëê 7%; exotic pairs 3ΓÇô5├ù worse); (2) session coverage matching the strategies' active hours;
  (3) clean m1 data availability in the store (gaps kill dual-res fidelity); (4) low correlation to symbols
  already in the set; (5) swap cost if strategies hold overnight.

### G1 ΓÇö Symbol scorecard (feature)
- `GET /api/symbols/scorecard?tf=H1&from=&to=`: for every symbol WITH data in `marketdata.db`, compute from
  bars + `symbols.json`: avgSpreadPips (A3 per-bar spread when present, else typical), atrPips(14) median,
  **costPerAtrPct**, weekend/holiday gap frequency, m1 coverage %, data range, and (when runs exist)
  correlation to the current portfolio set (F2). Render in Data Manager as a sortable table with a
  "nominate" star that just tags the symbol (drives default pick-lists in New-Backtest).
- **Gate:** scorecard ranks the owner's downloaded symbols; EURUSD-class majors rank above crosses on
  costPerAtrPct; numbers spot-checked by hand for one symbol.
- **Tricky:** ATR in PIPS needs the symbol's pip size (JPY!) ΓÇö reuse `PipCalculator`/SymbolInfo, never 0.0001
  hardcode (B10's lesson). Correlation column is null until enough run data exists ΓÇö show "ΓÇö", not 0.

### G2 ΓÇö Symbol-fit exploration (recipe, mostly tooling reuse)
- For a nominated symbol: run the exploration sweep (T5 + QUANT-ROADMAP Q1) of the surviving strategy family
  over the symbol's downloaded history; the scorecard page links "explore fit" ΓåÆ pre-filled sweep. The OUTPUT
  (post-cost expectancy per strategy family) is the final say on adoption ΓÇö cost ranking nominates, backtests
  confirm.
- **Guidance:** don't cross-product everything; explore (nominated symbol ├ù surviving strategies ├ù owner TF)
  only. Symbols whose costPerAtrPct > ~15% on the target TF are rarely worth exploring at all on spread-paying
  strategies.

---

## ┬º2 ΓÇö Sequencing (iterations, dependencies, and what to run when)

```
Iteration 1  = B1 + B2            (narrative backbone + live monitor feed ΓÇö unlocks C1/C2 journal reads, D3)
Iteration 2  = C1 + C2 + C3       (charts; needs B1 for stop-path + narrative categories)
Iteration 3  = D1 + D5 + E1       (nav hub + settings + DB reset ΓÇö independent of B/C, can swap with 1ΓÇô2)
Iteration 4  = D2 + D3            (the two key pages; needs B2 for monitor journal, C3 for live chart)
Iteration 5  = D4 + B3 + B4 + E2  (report tabs + trade narrative + housekeeping)
Iteration 6  = A1 + A2            (oracle set + drift alarm; requires owner cTrader access; can run ANY time
                                   after tape-trust V4 ΓÇö earlier is better, it's the trust anchor)
Iteration 7  = A3                 (per-bar spread; after A1 so the improvement is measurable)
Iteration 8  = F1 + F2            (portfolio + correlation; needs data downloaded + a few real runs)
Iteration 9  = F3 + G1 + G2       (regime-gated portfolio + symbol program; needs T5 sweeps + Q1 exploration)
```
Rules: A-track can interleave anywhere (it's the safety net ΓÇö prefer early). F/G MUST come after real data +
honest costs (tape-trust T2/T3 + V2 downloads) or the evidence they produce is noise. Never two agents in one
worktree; one iteration = one branch off the latest integrated tip.

## ┬º3 ΓÇö What NOT to do (program-wide)

- Do NOT create a new persisted journal/table when a projection over StepRecords will do (D-B1).
- Do NOT let the monitor and report grow separate vocabularies again ΓÇö one narrative builder, C# side.
- Do NOT group anything "daily" by calendar date ΓÇö the prop-firm day rolls at 22:00 UTC (`ResetClock`).
- Do NOT auto-tune, auto-rotate symbols, or add dynamic allocation in v1 of Tracks F/G.
- Do NOT touch `marketdata.db` from any reset path.
- Do NOT redesign the trade-ledger row semantics while B3 stamps narrative columns.
- Do NOT ship a UI phase without the owner's visual pass note in PROGRESS.md.
- Golden 63/63 byte-identical, always; `RequiresCTrader` E2E after any cBot/venue change.
