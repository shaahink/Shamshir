# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: ui-smoke.spec.ts >> Live Backtest Chain (start → monitor → report) >> EURUSD H1 3-day backtest: monitor updates, report shows trades
- Location: tests\e2e\ui-smoke.spec.ts:400:3

# Error details

```
Error: expect(locator).toBeVisible() failed

Locator: locator('app-run-monitor app-stat-tile').filter({ hasText: 'completed' }).or(locator('app-run-monitor').filter({ hasText: 'completed' }))
Expected: visible
Timeout: 120000ms
Error: element(s) not found

Call log:
  - Expect "toBeVisible" with timeout 120000ms
  - waiting for locator('app-run-monitor app-stat-tile').filter({ hasText: 'completed' }).or(locator('app-run-monitor').filter({ hasText: 'completed' }))

```

```yaml
- navigation:
  - link "Shamshir":
    - /url: /runs
  - link "Live":
    - /url: /
  - link "Runs":
    - /url: /runs
  - link "Trades":
    - /url: /trades
  - link "Strategies":
    - /url: /strategies
  - link "Packs":
    - /url: /addon-packs
  - link "Risk":
    - /url: /risk-profiles
  - link "FTMO":
    - /url: /prop-firm-rules
  - link "Governor":
    - /url: /governor-options
  - link "Settings":
    - /url: /settings
  - link "+ New Backtest":
    - /url: /runs/new
  - link "API Docs":
    - /url: /scalar/v1
- main:
  - heading "Live Monitor" [level=1]
  - paragraph: Run 271a5c66
  - button "Cancel Run"
  - link "Report":
    - /url: /runs/271a5c66
  - text: "Progress 0.0% (0 / ?) Speed: 0.0 bars/s ETA: -- Elapsed: -- Sim: Status connecting Equity 0 Balance 0 Open Positions 0 Daily DD % 0.00% Max DD % 0.00% Governor -- Distance to Limit 0.0% 0 Signals 0 Orders 0 Fills 0 Closes 0 Rejections 0 Breaches"
  - heading "Journal (0)" [level=2]
  - text: Waiting...
```

# Test source

```ts
  340 |     await expect(page.locator('app-run-report', { hasText: 'Disable Regime Detection' })).toBeVisible({ timeout: TIMEOUT });
  341 | 
  342 |     const cancel = page.locator('button', { hasText: 'Cancel' });
  343 |     if (await cancel.isVisible().catch(() => false)) await cancel.click();
  344 |   });
  345 | });
  346 | 
  347 | // Phase B: lossless monitor journal polling (31-B2)
  348 | test.describe('Live Monitor journal polling (31-B2)', () => {
  349 |   test('renders journal section with entries', async ({ page }) => {
  350 |     await page.goto('/runs');
  351 |     await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
  352 |     const rows = page.locator('app-run-list table tbody tr');
  353 |     const count = await rows.count();
  354 |     await rows.nth(count - 1).click();
  355 |     await page.waitForURL('**/runs/**', { timeout: TIMEOUT });
  356 |     const url = page.url();
  357 |     await page.goto(url + '/monitor');
  358 |     await expect(page.locator('app-run-monitor')).toBeVisible({ timeout: TIMEOUT });
  359 |     // Journal section should be present
  360 |     await expect(page.locator('app-run-monitor', { hasText: 'Journal' })).toBeVisible({ timeout: TIMEOUT });
  361 |   });
  362 | });
  363 | 
  364 | // Phase C5: CreateModal in risk-profile-list
  365 | test.describe('Risk Profile create modal (C5)', () => {
  366 |   test('opens and closes via Cancel', async ({ page }) => {
  367 |     await page.goto('/risk-profiles');
  368 |     await expect(page.locator('app-risk-profile-list')).toBeVisible({ timeout: TIMEOUT });
  369 |     const newBtn = page.locator('app-risk-profile-list button', { hasText: 'New Profile' });
  370 |     await expect(newBtn).toBeVisible({ timeout: TIMEOUT });
  371 |     await newBtn.click();
  372 |     // Modal should appear
  373 |     await expect(page.locator('app-create-modal')).toBeVisible({ timeout: TIMEOUT });
  374 |     await expect(page.locator('app-create-modal', { hasText: 'New Risk Profile' })).toBeVisible({ timeout: TIMEOUT });
  375 |     // Cancel should close it
  376 |     await page.locator('app-create-modal button', { hasText: 'Cancel' }).click();
  377 |     await expect(page.locator('app-create-modal')).not.toBeVisible({ timeout: TIMEOUT });
  378 |   });
  379 | });
  380 | 
  381 | test.describe('Per-bar why (T4)', () => {
  382 |   test('per-bar why section renders', async ({ page }) => {
  383 |     await page.goto('/runs');
  384 |     const rows = page.locator('app-run-list table tbody tr');
  385 |     await expect(rows.first()).toBeVisible({ timeout: TIMEOUT });
  386 |     const count = await rows.count();
  387 |     await rows.nth(count - 1).click();
  388 |     await page.waitForURL('**/runs/**', { timeout: TIMEOUT });
  389 |     const headings = await page.locator('app-run-report h2').allTextContents();
  390 |     if (!headings.join(' ').includes('Per-bar')) { test.skip(true, 'no per-bar why section'); return; }
  391 |     await expect(page.locator('app-run-report h2', { hasText: 'Per-bar' })).toBeVisible({ timeout: TIMEOUT });
  392 |   });
  393 | });
  394 | 
  395 | // ============================================
  396 | // LIVE BACKTEST CHAIN E2E — QA full-flow tests
  397 | // ============================================
  398 | 
  399 | test.describe('Live Backtest Chain (start → monitor → report)', () => {
  400 |   test('EURUSD H1 3-day backtest: monitor updates, report shows trades', async ({ page }) => {
  401 |     test.setTimeout(180_000);
  402 | 
  403 |     // 1. Navigate to new-backtest
  404 |     await page.goto('/runs/new');
  405 |     await expect(page.locator('app-new-backtest')).toBeVisible({ timeout: TIMEOUT });
  406 | 
  407 |     // 2. Wait for strategies to load (checkboxes appear)
  408 |     await expect(page.locator('app-new-backtest input[type="checkbox"]').first()).toBeVisible({ timeout: TIMEOUT });
  409 | 
  410 |     // 3. Set 3-day date range
  411 |     const now = new Date();
  412 |     const end = now.toISOString().slice(0, 10);
  413 |     const start = new Date(now);
  414 |     start.setDate(start.getDate() - 3);
  415 |     const startStr = start.toISOString().slice(0, 10);
  416 | 
  417 |     await page.locator('app-new-backtest input[type="date"]').first().fill(startStr);
  418 |     await page.locator('app-new-backtest input[type="date"]').nth(1).fill(end);
  419 | 
  420 |     // 4. Ensure EURUSD is selected (it's the default)
  421 |     await expect(page.locator('app-new-backtest', { hasText: 'EURUSD' })).toBeVisible({ timeout: TIMEOUT });
  422 | 
  423 |     // 5. Click Start Backtest
  424 |     await page.locator('button', { hasText: 'Start Backtest' }).click();
  425 | 
  426 |     // 6. Verify we navigated to monitor
  427 |     await page.waitForURL('**/runs/**/monitor', { timeout: TIMEOUT });
  428 |     await expect(page.locator('app-run-monitor')).toBeVisible({ timeout: TIMEOUT });
  429 | 
  430 |     // 7. Wait for bar count to go above 0 (proves live updates work)
  431 |     await expect(async () => {
  432 |       const tileTexts = await page.locator('app-run-monitor app-stat-tile').allTextContents();
  433 |       const equity = tileTexts.join(' ');
  434 |       if (!equity || equity.includes('TypeError')) throw new Error('monitor not yet populated');
  435 |     }).toPass({ timeout: 30_000 });
  436 | 
  437 |     // 8. Wait for completion (max 3 min for a 3-day backtest)
  438 |     await expect(page.locator('app-run-monitor app-stat-tile', { hasText: 'completed' })
  439 |       .or(page.locator('app-run-monitor', { hasText: 'completed' })))
> 440 |       .toBeVisible({ timeout: 120_000 });
      |        ^ Error: expect(locator).toBeVisible() failed
  441 | 
  442 |     // 9. Extract the run ID from URL, navigate to report
  443 |     const url = page.url();
  444 |     const runId = url.split('/').filter(s => s.length === 36)[0]; // GUID
  445 |     if (runId) {
  446 |       await page.goto(`/runs/${runId}`);
  447 |       await expect(page.locator('app-run-report')).toBeVisible({ timeout: TIMEOUT });
  448 | 
  449 |       // Verify at least one trade tile shows a non-zero value
  450 |       const reportTiles = await page.locator('app-run-report app-stat-tile').allTextContents();
  451 |       console.log('[QA] Report tiles:', JSON.stringify(reportTiles.slice(0, 8)));
  452 |     }
  453 |   });
  454 | });
  455 | 
  456 | // 32-P4: strategy create + delete + new button
  457 | test.describe('Strategy CRUD (32-P4)', () => {
  458 |   test('New Strategy button navigates to create page', async ({ page }) => {
  459 |     await page.goto('/strategies');
  460 |     await expect(page.locator('app-strategy-list a', { hasText: 'New Strategy' })).toBeVisible({ timeout: TIMEOUT });
  461 |     await page.locator('app-strategy-list a', { hasText: 'New Strategy' }).click();
  462 |     await page.waitForURL('**/strategies/new', { timeout: TIMEOUT });
  463 |     await expect(page.locator('app-strategy-detail')).toBeVisible({ timeout: TIMEOUT });
  464 |     await expect(page.locator('app-strategy-detail', { hasText: 'Create Strategy' })).toBeVisible({ timeout: TIMEOUT });
  465 |   });
  466 | 
  467 |   test('delete button visible on detail page', async ({ page }) => {
  468 |     await page.goto('/strategies');
  469 |     await expect(page.locator('app-strategy-list a[href^="/strategies/"]').first()).toBeVisible({ timeout: TIMEOUT });
  470 |     await page.locator('app-strategy-list a[href^="/strategies/"]').first().click();
  471 |     await page.waitForURL('**/strategies/**', { timeout: TIMEOUT });
  472 |     await expect(page.locator('app-strategy-detail')).toBeVisible({ timeout: TIMEOUT });
  473 |     await expect(page.locator('app-strategy-detail button', { hasText: 'Delete' })).toBeVisible({ timeout: TIMEOUT });
  474 |   });
  475 | });
  476 | 
  477 | // 32-P5: new-backtest per-strategy pack dropdown
  478 | test.describe('New-Backtest per-strategy pack (32-P5)', () => {
  479 |   test('shows pack dropdown per selected strategy', async ({ page }) => {
  480 |     await page.goto('/runs/new');
  481 |     await expect(page.locator('app-new-backtest')).toBeVisible({ timeout: TIMEOUT });
  482 |     await expect(page.locator('app-new-backtest', { hasText: 'Pack: strategy default' })).toBeVisible({ timeout: TIMEOUT });
  483 |   });
  484 | });
  485 | 
```