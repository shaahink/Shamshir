# Track A — Angular SPA (full UI rebuild)

**Worktree:** `git worktree add ../shamshir-frontend iter/33-track-a-frontend`
**Starts after:** Phase 1 (contract frozen). Builds against `contract/openapi.v1.json`.
**Goal (D-A):** a new Angular + Tailwind + TypeScript SPA that fully replaces Razor. The .NET side is a
pure JSON + SignalR API.
**Isolation:** all work lives under a new top-level `web-ui/` folder → zero file overlap with Track B.

Defaults assumed (flagged as doubt #3 in MASTER_PLAN — change here if owner overrides): standalone
components, signals-based per-feature stores (no NgRx), Tailwind + headless components (no heavy
component library), Lightweight-Charts (v4 API already in the repo) wrapped as a chart component.

---

## A1 — Workspace & toolchain

1. `web-ui/` Angular workspace (standalone API, strict TS). Add Tailwind + PostCSS. Set up ESLint +
   Prettier.
2. Dev proxy: `/api` + `/hubs` → the .NET API (`proxy.conf.json`).
3. **Generated client:** wire `openapi-generator`/NSwag to emit a typed API client + DTO models from
   `contract/openapi.v1.json` into `web-ui/src/app/api/` via an npm script. **Never hand-write DTOs.**

**Gate:** `ng build` succeeds; the generated client compiles; a smoke call hits a real `/api/v1`
endpoint through the proxy.

---

## A2 — App shell, routing, design system

1. App shell mirroring the current nav intent (LIVE / RESEARCH / LIBRARY), but as Angular routes:
   `/` (engine/live), `/runs`, `/runs/:id` (report), `/runs/:id/monitor`, `/backtests/new`,
   `/strategies`, `/strategies/:id`, `/trades`, `/trades/:id`, `/compliance`, `/events`.
2. Tailwind design tokens (color scale incl. green-profit/red-loss, spacing, typography) → kill the
   bespoke `site.css`. A small set of shared components (table, card, stat tile, badge, tabs, toast).
3. A `SignalRService` (core) managing hub connection + typed envelope streams; a `chart` component
   wrapping Lightweight-Charts.

**Gate:** all routes render a skeleton; theme tokens applied; SignalR connects.

---

## A3 — Research core: New Backtest → Monitor → Report

This is the primary loop; build it first and well (it's where the UX pain is).

1. **New Backtest** (`/backtests/new`): symbols × timeframes pickers, date range, balance, strategy
   selection + per-run override knobs with an effective-config preview (feeds the StartBacktest
   command; uses `EffectiveConfigResolver` output via the API). Replaces `Backtests/New.cshtml`.
2. **Monitor** (`/runs/:id/monitor`): live SignalR — progress %, ETA, bars/sec, equity curve
   (throttled/diffed, no flicker — A6), counters with tooltips explaining each (A7), **lossless**
   journal feed via the journal API (not the 30-item ring — 31-B2), cancel button (A6).
3. **Report** (`/runs/:id`): reads canonical `RunStats` (P0.1) — KPIs incl. cost-inclusive net/gross,
   commission/swap totals, profit factor, max DD, R-distribution; trades table with ALL columns
   (Commission/Swap/Gross/Net/R/MAE/MFE/pips/hold/entry-type/strategy/exit-reason — A2); per-strategy
   funnel; per-bar "why rejected / why no signal" view from BarEvaluations + REJECTED events (A3);
   unified Orders+Fills journal view (A4); DD timeline (A6). Reconciliation badge from the gate.

**Gate:** a real replay run drives Monitor live and renders a Report whose KPIs equal the API's
`RunStats` (no client-side recompute). Equity curve does not flicker.

---

## A4 — Trade detail (with chart) + Trades list

1. **Trades list** (`/trades`): paged/filterable.
2. **Trade detail** (`/trades/:id`): the price chart with bars before/after, entry/exit markers, and
   strategy-relevant indicators (RSI for rsi-divergence, EMA for trend) — the A5 "most important
   feature" that currently shows "No price data." Proper formatting/coloring (green profit/red loss),
   real duration, real commission/swap.

**Gate:** trade detail shows a populated chart + markers for a real trade; numbers formatted/colored.

---

## A5 — Library + Live

1. **Strategies** (`/strategies`, `/strategies/:id`): read-only view of every strategy's effective
   params first (E1), then edit + validate write-back via the Strategies slice (E2/E3). Custom profile
   save/load (E2).
2. **Live/Engine** (`/`): account state, daily/max DD, equity; **venue status** panel (started
   handshake / stop requested / error / finalized / connected) from the P0.4 venue-status events (C1).
3. **Compliance / Events**: port last (doubt #5 — parked unless owner pulls forward).

**Gate:** strategies render effective config from the API; edits validate before save; venue status
reflects real events.

---

## A6 — Cutover

1. Decide hosting: API serves `ng build` output as static files, or a separate static host. Either way
   the contract is the only coupling.
2. Remove the proxy-only assumptions; production build pipeline.
3. Update `README.md` run instructions (run API + run/serve SPA).

**Track A exit gate:** every Razor surface has an Angular equivalent at parity-or-better; SPA builds in
CI; no Razor dependency remains for the UI.
