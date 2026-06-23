# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: live-monitor-links.spec.ts >> Tier 1 — SignalR connectivity >> monitor status changes from connecting when SignalR connects
- Location: tests\e2e\live-monitor-links.spec.ts:47:3

# Error details

```
Error: page.goto: net::ERR_CONNECTION_REFUSED at http://localhost:5134/runs
Call log:
  - navigating to "http://localhost:5134/runs", waiting until "load"

```

# Test source

```ts
  1   | import { test, expect } from '@playwright/test';
  2   | 
  3   | // ============================================================================
  4   | // Live Monitor Chain — Link Tests (isolation first, then full chain)
  5   | //
  6   | // Run order:
  7   | //   Tier 1: SignalR connectivity (loads existing run's monitor, checks console)
  8   | //   Tier 2: Progress events reach browser (starts short backtest, waits for 'running')
  9   | //   Tier 3: Full chain (start → monitor → complete → report)
  10  | //
  11  | // Each tier gates the next. Don't debug Tier 2 if Tier 1 is broken.
  12  | // ============================================================================
  13  | 
  14  | const APP_TIMEOUT = 10_000;
  15  | 
  16  | // ── Tier 1: SignalR connectivity ──────────────────────────────────────────
  17  | 
  18  | test.describe('Tier 1 — SignalR connectivity', () => {
  19  |   test('SignalR connects within 8 seconds (existing run monitor)', async ({ page }) => {
  20  |     const logs: string[] = [];
  21  |     page.on('console', msg => logs.push(msg.text()));
  22  | 
  23  |     // Load any completed run's monitor page
  24  |     await page.goto('/runs');
  25  |     await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: APP_TIMEOUT });
  26  | 
  27  |     await page.locator('app-run-list table tbody tr').first().click();
  28  |     await page.waitForURL('**/runs/**', { timeout: APP_TIMEOUT });
  29  | 
  30  |     const url = page.url();
  31  |     await page.goto(url + '/monitor');
  32  |     await expect(page.locator('app-run-monitor')).toBeVisible({ timeout: APP_TIMEOUT });
  33  | 
  34  |     // The run-hub.service.ts now logs:
  35  |     //   "[SignalR] connected" on success
  36  |     //   "[SignalR] start failed: ..." on error
  37  |     await expect(async () => {
  38  |       const all = logs.join(' ');
  39  |       if (all.includes('[SignalR] start failed')) {
  40  |         const err = logs.find(l => l.includes('start failed')) || 'unknown';
  41  |         throw new Error('SignalR start failed: ' + err);
  42  |       }
  43  |       expect(all).toContain('[SignalR] connected');
  44  |     }).toPass({ timeout: 8_000, intervals: [500] });
  45  |   });
  46  | 
  47  |   test('monitor status changes from connecting when SignalR connects', async ({ page }) => {
> 48  |     await page.goto('/runs');
      |                ^ Error: page.goto: net::ERR_CONNECTION_REFUSED at http://localhost:5134/runs
  49  |     await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: APP_TIMEOUT });
  50  | 
  51  |     await page.locator('app-run-list table tbody tr').first().click();
  52  |     await page.waitForURL('**/runs/**', { timeout: APP_TIMEOUT });
  53  | 
  54  |     const url = page.url();
  55  |     await page.goto(url + '/monitor');
  56  |     await expect(page.locator('app-run-monitor')).toBeVisible({ timeout: APP_TIMEOUT });
  57  | 
  58  |     // For a completed run, the status should quickly go to 'running' or 'completed'
  59  |     // If it stays at 'connecting', SignalR is broken.
  60  |     await expect(async () => {
  61  |       const text = await page.locator('app-run-monitor app-stat-tile').first().textContent();
  62  |       if ((text || '').toLowerCase().includes('connecting')) {
  63  |         throw new Error('Still connecting — SignalR never established');
  64  |       }
  65  |     }).toPass({ timeout: 10_000 });
  66  |   });
  67  | });
  68  | 
  69  | // ── Tier 2: Progress events reach browser ─────────────────────────────────
  70  | 
  71  | test.describe('Tier 2 — Progress events reach browser', () => {
  72  |   test('live backtest shows running status within 15 seconds', async ({ page }) => {
  73  |     // Start a minimal backtest: 1 day, EURUSD H1
  74  |     await page.goto('/runs/new');
  75  |     await expect(page.locator('app-new-backtest')).toBeVisible({ timeout: APP_TIMEOUT });
  76  |     await expect(page.locator('app-new-backtest input[type="checkbox"]').first()).toBeVisible({ timeout: APP_TIMEOUT });
  77  | 
  78  |     // 1-day range
  79  |     const yesterday = new Date();
  80  |     yesterday.setDate(yesterday.getDate() - 1);
  81  |     const yStr = yesterday.toISOString().slice(0, 10);
  82  |     const tStr = new Date().toISOString().slice(0, 10);
  83  | 
  84  |     await page.locator('app-new-backtest input[type="date"]').first().fill(yStr);
  85  |     await page.locator('app-new-backtest input[type="date"]').nth(1).fill(tStr);
  86  | 
  87  |     await page.locator('button', { hasText: 'Start Backtest' }).click();
  88  |     await page.waitForURL('**/monitor', { timeout: APP_TIMEOUT });
  89  |     await expect(page.locator('app-run-monitor')).toBeVisible({ timeout: APP_TIMEOUT });
  90  | 
  91  |     // The first progress event sets status to 'running'
  92  |     await expect(
  93  |       page.locator('app-run-monitor app-stat-tile', { hasText: 'running' })
  94  |     ).toBeVisible({ timeout: 15_000 });
  95  | 
  96  |     // Bar count should go above 0
  97  |     await expect(async () => {
  98  |       const tiles = await page.locator('app-run-monitor app-stat-tile').allTextContents();
  99  |       const equityTile = tiles.find(t => /^\d+/.test(t.trim()));
  100 |       if (!equityTile || equityTile.trim() === '0') {
  101 |         throw new Error('Bar count or equity still at 0');
  102 |       }
  103 |     }).toPass({ timeout: 20_000 });
  104 |   });
  105 | });
  106 | 
  107 | // ── Tier 3: Full chain (start → monitor → complete → report) ──────────────
  108 | 
  109 | test.describe('Tier 3 — Full chain', () => {
  110 |   test('EURUSD H1 3-day: completes and report has trades', async ({ page }) => {
  111 |     test.setTimeout(180_000);
  112 | 
  113 |     await page.goto('/runs/new');
  114 |     await expect(page.locator('app-new-backtest')).toBeVisible({ timeout: APP_TIMEOUT });
  115 |     await expect(page.locator('app-new-backtest input[type="checkbox"]').first()).toBeVisible({ timeout: APP_TIMEOUT });
  116 | 
  117 |     const now = new Date();
  118 |     const end = now.toISOString().slice(0, 10);
  119 |     const start = new Date(now);
  120 |     start.setDate(start.getDate() - 3);
  121 |     const startStr = start.toISOString().slice(0, 10);
  122 | 
  123 |     await page.locator('app-new-backtest input[type="date"]').first().fill(startStr);
  124 |     await page.locator('app-new-backtest input[type="date"]').nth(1).fill(end);
  125 | 
  126 |     await page.locator('button', { hasText: 'Start Backtest' }).click();
  127 |     await page.waitForURL('**/monitor', { timeout: APP_TIMEOUT });
  128 | 
  129 |     // Wait for completion (max 3 min)
  130 |     await expect(
  131 |       page.locator('app-run-monitor').filter({ hasText: 'completed' })
  132 |     ).toBeVisible({ timeout: 150_000 });
  133 | 
  134 |     // Navigate to report
  135 |     const url = page.url();
  136 |     const runId = url.split('/').filter(s => s.length === 36)[0] || '';
  137 |     await page.goto('/runs/' + runId);
  138 |     await expect(page.locator('app-run-report')).toBeVisible({ timeout: APP_TIMEOUT });
  139 | 
  140 |     // Verify at least one stat tile is non-zero
  141 |     const tiles = await page.locator('app-run-report app-stat-tile').allTextContents();
  142 |     const nonZero = tiles.some(t => {
  143 |       const n = parseFloat(t.replace(/[^0-9.-]/g, ''));
  144 |       return !isNaN(n) && n !== 0;
  145 |     });
  146 |     expect(nonZero).toBe(true);
  147 |   });
  148 | });
```