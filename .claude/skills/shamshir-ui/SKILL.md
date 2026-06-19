# Skill: shamshir-ui

# Shamshir UI E2E Driver (Playwright headless browser)

Verifies the Angular SPA renders correctly by driving a real backtest, then
opening key pages in headless Chromium via Playwright and asserting DOM
elements, text content, and visual appearance (screenshots).

## Prerequisites

- `npx playwright install chromium` (one-time, ~300MB)
- Playwright 1.61+ (already installed globally)
- The app must be built: `npm run build` in `web-ui/`, `dotnet build` on `TradingEngine.Web`

## Run

```bash
# Full run (build SPA + .NET, launch, 11 API checks + ~15 browser checks)
node .claude/skills/shamshir-ui/driver.mjs --build

# Run without rebuild (app already built)
node .claude/skills/shamshir-ui/driver.mjs

# Leave app + browser open for manual inspection
node .claude/skills/shamshir-ui/driver.mjs --serve

# Update golden screenshots (after a confirmed good change)
node .claude/skills/shamshir-ui/driver.mjs --golden
```

Exit code 0 = all checks passed. Screenshots saved to `screenshots/`.

## What it checks

| # | Page | Check |
|---|------|-------|
| 1-11 | API | Same 11 backend checks as run-shamshir |
| 12 | /runs | Run list table exists, has rows |
| 13 | /runs/{id} | Equity chart canvas exists, trades table has cost columns |
| 14 | /runs/{id} | Journal filter buttons show correct kinds (no 'BAR') |
| 15 | /runs/{id} | Scatter chart renders (canvas element present) |
| 16 | /trades/{id} | Candle chart renders, Entry/Exit markers visible |
| 17 | /trades/{id} | Cost stat tiles show non-zero values (not "0.00") |
| 18 | /trades?from=...&to=... | Trade list has Gross/Comm/Swap columns |
| 19 | /strategies | Strategy list renders, click navigates to detail |
| 20 | /strategies/{id} | Config sections render (not just raw JSON) |
| 21 | /settings | Page renders, values are not hardcoded strings |
| 22 | /governor-options | Form fields present and editable |
| 23 | /runs/{id}/monitor | Journal feed visible, breach banner logic |
| 24-26 | Screenshots | Full-page captures of run-report, trade-detail, live-monitor |

## Adding new checks

Add a function in `driver.mjs`:

```js
async function checkMyFeature(page) {
  const el = await page.locator('.my-selector').first();
  const text = await el.textContent();
  return text.includes('expected') ? { ok: true } : { ok: false, detail: text };
}
```

Then call it in the `runBrowserChecks` function and add it to the `checks` array.

## Screenshots

Golden screenshots are committed to `.claude/skills/shamshir-ui/screenshots/`.
On each run, new screenshots are compared to golden by filename. A pixel diff
> 1% triggers a warning. Use `--golden` to update baselines after a deliberate
visual change.

Base directory for this skill: file:///C:/Code/Shamshir/.claude/skills/shamshir-ui
