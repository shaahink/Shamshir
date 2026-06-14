# Iter-21 — The Operator UI: watch a strategy run, read the result like a journal

**Audience**: the implementing agent (OpenCode + DeepSeek v4 Pro). Self-contained — do not assume access to the conversation that produced it.
**Branch**: create `iter/21-ui` off `main`. **One commit per phase**, prefix `feat(iter21-uN):` / `refactor(iter21-uN):` / `fix(iter21-uN):`.
**Runs in parallel with iter-20** (the engine decision-kernel refactor). See §1 for the coordination rule that keeps the two from colliding.

---

## 0. The vision (what the owner asked for, in their words)

> "Run a strategy smoothly, see how it's going — feel that you're moving from a date to another, see the status live, like viewing your journal. Trade detail shouldn't be a placeholder, and the chart is broken. When a backtest is done you need to know how it performed, how long it took, what it did — from strategies, to trades, to breaches or not."

Translated into product goals:

1. **Live run experience.** Start a run and *watch it happen* — a sim-clock advancing through time, a progress bar with ETA, equity moving, the journal streaming, counters climbing (signals → orders → fills → closes), governor state and breaches surfacing the moment they occur. This is MetaTrader Strategy Tester's "visual mode" feeling, modernized.
2. **Trade detail that's real.** A proper price chart (candles + entry/exit/SL/TP markers, MAE/MFE), and every field correct — no placeholders, no format bugs.
3. **A completion report that answers "how did it go?"** Headline performance, wall-clock duration + speed, the full funnel (bars → signals → orders → trades, per strategy), the equity/drawdown curve, and a compliance panel: did it breach daily/max DD, and when.
4. **Smart navigation** organized around the two modes the engine has: **Live** (the running engine) and **Research** (backtests & analysis), with AJAX for fetches and **SignalR** for the live stream.

### Benchmark engines we borrow from (and the one feature we take from each)

- **MetaTrader 5 Strategy Tester** — *visual mode*: watching bars advance with the clock. This is goal #1; it's the signature feature.
- **QuantConnect / LEAN** — the *results dashboard*: dense, well-organized statistics + a live log console during a run.
- **TradingView** — *the chart*: candlesticks with trade markers and an underwater (drawdown) plot. Use their LightweightCharts library (already loaded via CDN).
- **NinjaTrader / Backtrader** — the *trade analyzer*: R-multiple distribution, MAE/MFE scatter, holding-time histogram.
- **FTMO/prop dashboards** — the *compliance view*: distance-to-limit gauges and a breach timeline. We already have the governor + protection ledger; we just need to show them.

---

## 1. Coordination with iter-20 (read before writing any data code)

iter-20 is replacing the engine's internals with a pure kernel that emits a unified `DecisionRecord` journal and `AccountSnapshot` (its Phase 7 reroutes the UI onto one `RunProjection`). To avoid building UI data plumbing that iter-20 throws away, follow **one rule**:

> **Contract-first.** Every page talks to an endpoint or SignalR message whose **JSON shape is the shape iter-20's `RunProjection`/`DecisionRecord`/`AccountSnapshot` will emit.** Build the endpoint now against *today's* data sources (existing repos, the orchestrator's in-memory state); when iter-20 P7 lands, only the endpoint's internals change — the page never moves.

Concretely:
- **Safe to build now** (survives iter-20): all markup, CSS, charts, navigation, SignalR hub + client, page→endpoint contracts, the live-run UX.
- **Do NOT depend on**: `BacktestController.ResolveGovernor` reading `state.EngineHost.Services.GetRequiredService<ITradingGovernor>()` (service-location — iter-20 P5 moves governor into engine state). Instead, the engine should *push* governor state into the SignalR progress envelope (U1/U2). Read it from the envelope, not by reaching into the host.
- **Data-accuracy gate**: backtest equity/drawdown is not reliably persisted until iter-20 **P2**. Until then the **live progress envelope** (pushed from the running engine) is the source of truth for the run monitor; the post-run report's equity curve may be sparse until P2. Note this in the report UI with an honest empty-state, don't fabricate.

---

## 2. Hard rules

- Money/price display: format with the value's precision; never show raw unformatted decimals. **Fix the `@x:F5` Razor bug pattern** wherever it appears (it renders the literal `:F5`) — use `@x.ToString("F5")` or `@($"{x:F5}")`.
- No business logic in the UI. Pages render; endpoints/services compute. Compliance verdicts (breach/no-breach) come from the engine/repos, never recomputed in JS.
- One charting library: **LightweightCharts** (TradingView). Remove Chart.js and the fake CSS-gradient "chart". See U0.
- Live updates via **SignalR** (per-run groups); one-shot reads via **AJAX (fetch)**. No raw SSE in new code (retire the existing `/stream` SSE once the hub replaces it).
- Accessibility & states: every async view has loading / empty / error states. No silent `.catch(() => {})`.
- Keep `IEngineClock` discipline server-side; UI may use wall-clock for "elapsed/ETA" display only.

Test/build commands:
```
dotnet build TradingEngine.sln
dotnet test tests/TradingEngine.Tests.Integration      # API contract tests live here
dotnet test tests/TradingEngine.Tests.Simulation       # keep green (28/28)
# Manual UI verification steps are listed per phase; use the /verify or /run skill.
```

---

## 3. Tech & design decisions

- **Framework**: standardize on **Razor Pages + progressive-enhancement JS modules + SignalR**. The repo has a vestigial Blazor `_Host.cshtml` (the only place LightweightCharts is currently loaded) and all real pages are Razor Pages — this split is why charts are broken. **Retire the unused Blazor host** (or, if kept, document why); do not add new Blazor.
- **Charts**: LightweightCharts standalone build, loaded once in `_Layout`. Wrap it in one ES module `wwwroot/js/charts/` exposing `candleChart`, `equityChart` (with drawdown band), `markers`, `histogram`, `scatter`. Delete `charts.js` (Chart.js) and `trading-charts.js` after migrating callers.
- **Live transport**: `RunHub : Hub` with groups keyed by `runId`. Client (`wwwroot/js/run-client.js`) handles connect, join group, auto-reconnect, and dispatches typed messages. The engine publishes a throttled **progress envelope** (≈2–5/sec, never per-bar — the current per-bar SSE counter floods the browser on M1 runs).
- **Design language**: define CSS custom properties (color tokens, spacing, radius, typography) in `site.css`; convert the heavy inline styles in `Progress.cshtml`/`Trades/Detail.cshtml` to component classes. Dark theme consistent with the chart palette (`#1a1a2e` bg, `#26a69a` up, `#ef5350` down).
- **Navigation/IA** (replaces the flat Dashboard/Trades/Performance/Backtests/Events):
  ```
  LIVE            RESEARCH                 LIBRARY
  ├ Engine        ├ New Backtest (wizard)  ├ Strategies
  └ Compliance    ├ Runs (history/compare) └ Events / Journal
                  ├ Run Monitor (live)
                  ├ Report (per run)
                  └ Trades → Trade detail
  ```
  Run-scoped sub-nav + breadcrumbs once inside a run (Monitor ↔ Report ↔ Trades share the `runId`).

---

## Phase U0 — Foundation: one chart stack, fixed bugs, nav & design tokens

**Goal**: kill the broken/duplicated charting, fix the rendering bugs, lay the shared JS/CSS/nav base. No new features yet — make the floor solid.

**Do**:
1. Load LightweightCharts once in `_Layout`; remove Chart.js + chartjs-financial CDN tags and `/js/charts.js`. Create `wwwroot/js/charts/index.js` (ES module) with `equityChart(el, points)` (line + drawdown band), `candleChart(el, bars)`, `setMarkers(...)`, `histogram(...)`, `scatter(...)`. Migrate the dashboard equity curve to it.
2. Fix all `@expr:Fn` Razor format bugs (start with `Trades/Detail.cshtml`, `Backtests/Detail.cshtml`); grep the whole `Pages/` tree for the pattern.
3. Replace the dashboard's silent `fetch('/api/equity').catch(()=>{})` with loading/empty/error states.
4. Implement the new nav/IA in `_Layout` + a shared `_RunNav` partial (breadcrumbs, run-scoped tabs).
5. Add CSS custom-property design tokens; extract the worst inline-style blocks into classes.
6. Retire the Blazor `_Host.cshtml` (or document why it stays).

**Verify**: dashboard equity chart renders via LightweightCharts; no `:F5` literals anywhere; nav shows the new IA; no console errors.
**Commit**: `refactor(iter21-u0): consolidate on LightweightCharts, fix Razor format bugs, new nav + design tokens`

---

## Phase U1 — SignalR run hub + progress envelope contract

**Goal**: the live channel and its message contract — shaped to match iter-20's future `RunProjection`.

**Do**:
1. Add SignalR (`AddSignalR`, `MapHub<RunHub>("/hubs/run")`). `RunHub` exposes `JoinRun(runId)` / `LeaveRun(runId)` (group membership).
2. Define the **progress envelope** (server DTO + documented JSON):
   ```
   RunProgress {
     runId, status,                       // running|completed|failed
     simTimeUtc,                          // THE clock that advances ("moving date to date")
     barsProcessed, barsTotal, percent, etaSeconds,
     wallElapsedMs, barsPerSec,
     equity, balance, openPositions,
     dailyDdPct, maxDdPct, distanceToDailyLimit,
     governorState, governorReason,       // from engine, NOT service-location
     counters { signals, orders, fills, closes, rejections, breaches },
     recentJournal: DecisionRecord[]      // last N lines (same shape as iter-20 DecisionRecord)
   }
   ```
3. Have the running engine publish a throttled `RunProgress` (≈250ms cadence) into the hub group. Until iter-20 wires the kernel, source the fields from the orchestrator's run state + existing risk/governor snapshots; **expose them through the envelope, not by the page reaching into the host.**
4. Client `run-client.js`: connect, join, auto-reconnect, dispatch `onProgress`/`onJournal`/`onDone`.
5. **Integration test**: a contract test asserting the serialized envelope contains every documented field with correct casing (so iter-20 P7 can target it).

**Verify**: connect two browser tabs to a running run; both receive throttled progress.
**Commit**: `feat(iter21-u1): SignalR RunHub + throttled RunProgress envelope (RunProjection-shaped)`

---

## Phase U2 — Live Run Monitor (the centerpiece: "watch it move")

**Goal**: the MetaTrader-visual-mode experience. Start a run → watch it advance.

**Do** — build `/runs/{runId}/monitor` consuming `RunProgress`:
1. **Sim-clock** front and center — the advancing `simTimeUtc`, large, with the date prominent (this is the "moving from a date to another" feeling).
2. **Progress bar** with `percent`, `barsProcessed/barsTotal`, **ETA**, and `barsPerSec` (speed).
3. **Live equity sparkline** updating from the envelope (append points; cap buffer).
4. **Live KPI tiles**: equity, daily DD (with distance-to-limit gauge), max DD, open positions, governor state badge (color by Normal/Reduced/SoftStop/HardStop).
5. **Live funnel**: signals → orders → fills → closes (+ rejections, breaches) as animated counters.
6. **Live journal feed**: streaming `recentJournal` `DecisionRecord`s, color-coded by event (signal/order/trade/reject/breach), auto-scroll with pause-on-hover, filter chips. This is the "viewing your journal" goal.
7. **Breach alert**: when `counters.breaches` increments or governor → SoftStop/HardStop, surface a prominent banner with the reason.
8. On `status=completed`, show a "View Report" CTA → U4.

**Verify**: launch a multi-month run; the clock advances smoothly, journal streams without flooding, ETA decreases, a forced breach shows the banner.
**Commit**: `feat(iter21-u2): live Run Monitor — sim-clock, progress/ETA, live equity, funnel, journal feed, breach alerts`

---

## Phase U3 — Trade detail done right (real chart, real data)

**Goal**: replace the placeholder gradient + buggy fields with a real price chart and correct detail.

**Do**:
1. Endpoint `GET /api/trades/{id}/chart` → candles around the trade window (reuse `api/bars`; pad N bars before entry / after exit) + entry/exit/SL/TP levels + MAE/MFE points. Contract-shaped for reuse.
2. Rewrite `Trades/Detail.cshtml`: a **candleChart** with entry/exit markers, SL/TP price lines, and an MAE/MFE shaded band. Remove the CSS-gradient placeholder.
3. Fix every field: correct formatting (the `:Fn` bug), R-multiple, exit reason, holding time (computed from open/close), commission/swap/gross vs net, risk profile, strategy, engine mode.
4. Loading/empty/error states (e.g., bars unavailable → honest message, not blank).

**Verify**: open several trades (win, loss, SL-hit, TP-hit); chart shows the right candles, markers at the right times/prices, all fields correct.
**Commit**: `feat(iter21-u3): real trade-detail price chart with markers; fix all placeholder/format fields`

---

## Phase U4 — Backtest completion Report

**Goal**: after a run, answer "how did it perform, how long, what did it do, did it breach?" on one page.

**Do** — build `/runs/{runId}/report` (consolidate the current `/backtests/detail`):
1. **Headline KPIs**: net P&L, return %, max DD, daily-DD worst, profit factor, win rate, expectancy, avg R, Sharpe/Sortino if available, total trades.
2. **Run facts**: wall-clock **duration**, bars processed, **bars/sec**, date range, symbols, strategies, initial balance, algo hash.
3. **Equity + drawdown chart**: equity line with an underwater (drawdown) sub-plot (LightweightCharts). Honest empty-state until iter-20 P2 persists backtest equity.
4. **Strategy funnel** (enhance the existing breakdown table): per strategy bars → signals → orders → trades → W/L/win%, **with the top-rejections drill-down already present**. Add a visual funnel.
5. **Compliance panel**: did it breach daily/max DD? When? A timeline of governor transitions + protection-ledger entries (from `api/protection`, `api/governor`). Big green "No breaches" or red breach list.
6. **Trades table** → links to U3 detail.
7. Export (CSV exists at `api/export/trades.csv`; surface it).

**Verify**: run a backtest to completion; report shows duration/speed, populated funnel, breach verdict, equity+drawdown chart, trade links.
**Commit**: `feat(iter21-u4): backtest Report — KPIs, duration/speed, equity+drawdown, strategy funnel, compliance verdict`

---

## Phase U5 — Trade Analyzer + Strategies + Compliance pages

**Goal**: surface the analytical depth and the currently-invisible subsystems.

**Do**:
1. **Trade Analyzer** (tab on the Report): R-multiple distribution (histogram), MAE/MFE scatter, holding-time histogram, P&L by symbol / by strategy / by hour-of-day / by day-of-week. Use existing trade fields + `api/backtest/analytics/*` (correlation, regime-history, daily-pnl).
2. **Strategies page** (`/strategies`): the 9-strategy bank from `api/strategies` — name, symbols, timeframe, parameters, enabled state.
3. **Compliance page** (`/compliance`, LIVE section): live distance-to-limit gauges (daily/max DD), governor state, and the protection-ledger history from `api/protection/days`.

**Verify**: analyzer charts render for a completed run; strategies page lists all loaded strategies; compliance page shows gauges + ledger.
**Commit**: `feat(iter21-u5): trade analyzer, strategies bank page, live compliance page`

---

## Phase U6 — New-Backtest wizard + run management

**Goal**: make starting and comparing runs pleasant.

**Do**:
1. **New Backtest wizard** (`/backtests/new`): symbol(s) + period(s) multi-select, date range with presets (last month/quarter/year), balance, spread/commission, strategy selection. POST to `api/backtest/start`, then redirect to the U2 Monitor.
2. **Runs history** (`/runs`): list past runs with status, key KPIs, duration; multi-select → **Compare** (use `api/backtest/compare`, overlay equity curves via `addMultiEquitySeries`-style multi-line chart).
3. Cancel-run control wired through the hub/command service.

**Verify**: start a run from the wizard → land on live monitor; compare two finished runs' equity curves overlaid.
**Commit**: `feat(iter21-u6): new-backtest wizard + run history with compare`

---

## Phase U7 — Polish: states, responsive, a11y, perf

**Goal**: production-feel finish.

**Do**: consistent loading skeletons / empty / error across all pages; responsive layout (tiles reflow); keyboard navigation + ARIA on nav, tables, journal feed; cap/window client-side buffers (journal, equity points) so long runs stay smooth; verify SignalR reconnect UX (banner on disconnect/reconnect).

**Verify**: throttle the network and confirm graceful degradation; run a long backtest and confirm the monitor stays responsive; tab through the app with keyboard only.
**Commit**: `fix(iter21-u7): loading/empty/error states, responsive layout, a11y, client buffer caps`

---

## 4. Definition of Done

- [ ] One chart library (LightweightCharts); Chart.js, the fake gradient, and the dead `trading-charts.js` wiring removed; no `@x:Fn` literal bugs.
- [ ] SignalR `RunHub` streams a throttled, `RunProjection`-shaped `RunProgress` envelope; no per-bar flooding.
- [ ] Live Run Monitor: advancing sim-clock, progress + ETA + speed, live equity, live funnel, streaming journal feed, breach alerts.
- [ ] Trade detail: real candle chart with entry/exit/SL/TP markers + MAE/MFE; every field correct.
- [ ] Backtest Report: KPIs, duration/speed, equity+drawdown, strategy funnel, compliance breach verdict, trade links, export.
- [ ] Analyzer, Strategies, and Compliance pages surface the previously-invisible subsystems.
- [ ] New-backtest wizard + run history/compare.
- [ ] Loading/empty/error states everywhere; responsive; basic a11y; long runs stay smooth.
- [ ] All page data flows through endpoints/hub messages whose JSON shape matches iter-20's projection (so P7 swaps internals only). Simulation tests stay green.

## 5. Sequencing notes for the agent

- **U0 first, always** — it fixes the broken chart and removes the dual-stack that causes it; everything else builds on the shared chart module + nav.
- **U1 before U2** — the monitor needs the hub + envelope contract.
- U3 and U4 are independent of each other and of the live monitor; either can follow U0.
- **Coordinate the envelope/endpoint contracts with iter-20's `DecisionRecord`/`AccountSnapshot`/`RunProjection` (§1).** If iter-20 has already defined those types when you reach a data phase, consume them directly; if not, define the JSON shape per this doc and leave a `// iter-20 P7: back this with RunProjection` marker.
- Each phase must build green and be manually verifiable via the steps listed; use the `/run` or `/verify` skill to launch the web app and check.
- Convert any relative dates you write into absolute dates. Today is 2026-06-14.

---

## Appendix A — Design System & Visual Spec (implement in U0)

**Framework decision: no CSS framework.** Hand-rolled CSS organized as design tokens + a small component layer. Do **not** add Tailwind (would force a Node/PostCSS build into the .NET repo) or Bootstrap (generic, fights the dense dark aesthetic). The current `site.css` is GitHub-Primer-dark and is the correct base — formalize it, don't replace it. Optionally pull non-color tokens from **Open Props** (zero-build) but it is not required.

### A.1 Color tokens (`:root` in `site.css`)
The existing CSS and the chart module currently disagree (`#3fb950/#f85149` vs `#26a69a/#ef5350`, `#0d1117` vs `#1a1a2e`). **Unify here and feed these same values into the LightweightCharts config** so a green candle == a green P&L number.
```css
:root {
  /* surfaces */
  --canvas:#0d1117; --surface:#161b22; --surface-2:#1c2128;
  --border:#30363d; --border-muted:#21262d; --overlay:#1117;
  /* text */
  --text:#c9d1d9; --text-muted:#8b949e; --text-strong:#f0f6fc;
  /* brand */
  --accent:#58a6ff; --accent-muted:#1f6feb;
  /* semantic P&L — used by UI AND charts (one source) */
  --pos:#3fb950; --neg:#f85149; --flat:#8b949e;
  --long:#3fb950; --short:#f85149;
  /* governor states */
  --gov-normal:#3fb950; --gov-reduced:#d29922; --gov-softstop:#db6d28;
  --gov-hardstop:#f85149; --gov-cooling:#58a6ff; --gov-profitlock:#39c5cf;
  /* severity */
  --info:#58a6ff; --warn:#d29922; --crit:#f85149;
  /* chart-specific (derive from above, do not introduce new greens/reds) */
  --chart-bg:var(--canvas); --chart-grid:var(--border-muted); --chart-axis:var(--border);
}
```

### A.2 Typography tokens
```css
:root {
  --font-ui: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; /* Inter optional upgrade */
  --font-mono: 'JetBrains Mono','Cascadia Code', ui-monospace, SFMono-Regular, Consolas, monospace;
  --fs-xs:12px; --fs-sm:13px; --fs-base:14px; --fs-lg:16px;
  --fs-xl:20px; --fs-2xl:28px; --fs-3xl:40px; /* --fs-3xl = sim-clock */
}
/* ALL numeric data gets tabular figures so streaming values don't jiggle */
.num, td.num, .value, .kpi, .sim-clock { font-variant-numeric: tabular-nums; font-feature-settings:"tnum"; }
/* journal feed + price/P&L cells use mono */
.journal, .price, .pnl { font-family: var(--font-mono); }
```

### A.3 Spacing / radius / elevation
```css
:root {
  --sp-1:4px; --sp-2:8px; --sp-3:12px; --sp-4:16px; --sp-5:24px; --sp-6:32px;
  --radius:8px; --radius-sm:4px; --radius-pill:999px;
  --shadow:0 1px 3px #0006; --shadow-lg:0 8px 24px #0009;
}
```

### A.4 App shell & page template
- **Shell**: persistent left **sidebar** (LIVE / RESEARCH / LIBRARY groups from §3), a **top bar** (run breadcrumb + `sim-clock` when inside a run + SignalR connection dot), fluid-width content. Sidebar collapses to icons < 1024px, drawer < 768px. Drop the rigid `max-width:1200px` for data-dense pages (Monitor/Report) — let them go fluid with side padding.
- **Page template**: `page-header` (title + breadcrumbs + actions) → `kpi-row` (tile grid) → `primary` (chart) → `secondary` (tables/funnels/tabs).

### A.5 Component inventory (build in U0, reuse after)
| Component | Class | Introduced | Notes |
|---|---|---|---|
| KPI tile | `.kpi` | U0 | label + big `tabular-nums` value + delta arrow |
| Status pill | `.pill` / `.pill--{state}` | U0 | governor/severity, color from tokens + **icon** (not color alone) |
| Distance-to-limit gauge | `.gauge` | U2/U5 | radial or bar; green→amber→red by `distanceToLimit` |
| Progress bar + ETA | `.progress` | U2 | percent fill + label |
| Tabs | `.tabs` | U4 | Report/Analyzer/Trades |
| Data table | `.table` (extend existing) | U0 | sortable, **sticky header**, `.num` cells |
| Journal line | `.journal-line--{event}` | U2 | mono, color by event, time-prefixed |
| Toast / breach banner | `.toast` / `.banner--crit` | U2 | breach + governor SoftStop/HardStop |
| Skeleton loader | `.skeleton` | U0 | shimmer for async panels |
| Empty state | `.empty` | U0 | icon + message (e.g. "No equity data yet — persists after iter-20 P2") |
| Drawer (quick-view) | `.drawer` | U3 | trade quick-view from tables |

### A.6 Accessibility
- **Never encode P&L/direction/governor by color alone** — pair with glyphs (▲/▼ for P&L, ●/state label for governor). Critical for red/green colorblindness.
- Maintain WCAG AA contrast: body text `--text` on `--surface` passes; `--text-muted` is for secondary labels only, not data.
- Visible focus rings on nav, tabs, table rows, drawer; full keyboard path through nav → tabs → table → drawer.
- Journal feed: `aria-live="polite"` with a pause-on-hover/focus control so screen readers and humans can stop the stream.

### A.7 What is intentionally NOT shown (and why)
- **Backtest equity/drawdown curve** is sparse until **iter-20 P2** persists backtest equity — show `.empty` ("populates after iter-20 P2"), never fabricate a curve.
- **Tick-level fills** on trade charts — only bar granularity is available; markers sit on bar times.
- **Live per-strategy P&L** on the Strategies page — deferred to the Analyzer (avoids a second live aggregation path before iter-20 unifies state).
- **Per-bar log lines** in the live journal — throttled out of the envelope by design (the old per-bar SSE flooded the browser); full journal is queryable on the Report.
