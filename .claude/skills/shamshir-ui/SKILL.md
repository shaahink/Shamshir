# Skill: shamshir-ui

# Shamshir UI E2E Verification (Playwright)

Verifies the Angular SPA renders correctly by launching the full stack and running
Playwright browser tests from `web-ui/tests/e2e/`. The test specs live in the
project (CI-friendly) — this skill just orchestrates build + launch + run + teardown.

## Prerequisites

- `npx playwright install chromium` (one-time, ~300MB, already done)
- `@playwright/test` in `web-ui/devDependencies` (already installed)

## Run

```bash
# Full CI-style run against an already-running app
npm run e2e --prefix web-ui

# Full orchestrator (build SPA + .NET, launch app, run E2E, teardown)
node .claude/skills/shamshir-ui/driver.mjs --build

# Leave app running for manual inspection
node .claude/skills/shamshir-ui/driver.mjs --serve
```

Test specs live at `web-ui/tests/e2e/ui-smoke.spec.ts`. Add new specs there.
Config at `web-ui/playwright.config.ts`.

## Suite state (measured 2026-07-15) — read before running the full suite

- **Full suite ≈ 7 min wall; ~15 failures are PRE-EXISTING on a healthy tree** (stale specs from
  earlier iterations + tests needing cTrader/seeded bars). Do **not** stash-rebuild a baseline to
  prove a failure is pre-existing — check `web-ui/tests/e2e/KNOWN-FAILURES.md` (created by
  iter-dx-speed D1; until that lands, this note is the record and the triage is
  `docs/iterations/iter-dx-speed/PLAN.md` D1).
- **Iterate on one targeted spec** (`npx playwright test tests/e2e/x2-x3.spec.ts`, ~30s); run the
  full suite once, in the background, before calling a phase done.
- Config gotchas: `screenshot: 'on'` means every passing test pays a screenshot; `retries: 0`;
  per-test timeout 30s; suite runs against `http://localhost:5134` (override with `BASE_URL`).

## Gotchas

- **lightweight-charts v5 removed `series.setMarkers()`.** Use
  `createSeriesMarkers(series, markers)` imported from `'lightweight-charts'`. Silent no-render if
  you follow v4 docs.
- **`npm run build` regenerates stamped/generated files** (build-info via `npm run stamp`,
  `styles.generated.css`) — these collide with `git stash pop`; restore or regenerate rather than
  hand-merging them.
- **Never serially `await` the market-data inventory endpoint in `ngOnInit`** — its first hit does
  a cold full-DB scan (~10s). Fire-and-forget or load it behind the primary data; a silent `catch`
  around it will make the page look dead with no error.

## What it checks

See `web-ui/tests/e2e/ui-smoke.spec.ts` for the full list. Key pages:
- `/runs` — run list table renders
- `/runs/{id}` — equity chart, trades table with cost columns, journal filters
- `/trades/{id}` — candle chart, stat tiles with cost data
- `/strategies` → `/strategies/{id}` — list render, click navigation, detail view
- `/settings` — page renders
- `/governor-options` — form renders
- `/runs/{id}/monitor` — live monitor renders

Base directory for this skill: file:///C:/Code/Shamshir/.claude/skills/shamshir-ui
