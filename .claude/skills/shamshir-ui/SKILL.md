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
