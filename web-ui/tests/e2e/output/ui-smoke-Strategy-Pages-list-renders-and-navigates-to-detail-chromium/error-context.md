# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: ui-smoke.spec.ts >> Strategy Pages >> list renders and navigates to detail
- Location: tests\e2e\ui-smoke.spec.ts:12:3

# Error details

```
Error: expect(locator).toBeVisible() failed

Locator: locator('app-strategy-list table')
Expected: visible
Timeout: 10000ms
Error: element(s) not found

Call log:
  - Expect "toBeVisible" with timeout 10000ms
  - waiting for locator('app-strategy-list table')

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
  - link "Compliance":
    - /url: /compliance
  - link "Events":
    - /url: /events
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
  - heading "Strategies" [level=1]
  - link "Bollinger Squeeze":
    - /url: /strategies/bb-squeeze
  - button "Active"
  - paragraph: bb-squeeze
  - text: Trades 3 Win Rate 0% P/L -885
  - link "EMA Alignment v1":
    - /url: /strategies/ema-alignment
  - button "Active"
  - paragraph: ema-alignment
  - text: Trades 15 Win Rate 0% P/L -4343
  - link "MACD Momentum":
    - /url: /strategies/macd-momentum
  - button "Active"
  - paragraph: macd-momentum
  - text: Trades 0 Win Rate 0% P/L 0
  - link "Mean Reversion v1":
    - /url: /strategies/mean-reversion
  - button "Active"
  - paragraph: mean-reversion
  - text: Trades 3 Win Rate 0% P/L -1573
  - link "Multi-Timeframe Trend":
    - /url: /strategies/mtf-trend
  - button "Active"
  - paragraph: mtf-trend
  - text: Trades 0 Win Rate 0% P/L 0
  - link "RSI Divergence":
    - /url: /strategies/rsi-divergence
  - button "Active"
  - paragraph: rsi-divergence
  - text: Trades 22 Win Rate 18% P/L 5816
  - link "Session Breakout v1":
    - /url: /strategies/session-breakout
  - button "Active"
  - paragraph: session-breakout
  - text: Trades 3 Win Rate 0% P/L -2122
  - link "SuperTrend":
    - /url: /strategies/super-trend
  - button "Active"
  - paragraph: super-trend
  - text: Trades 7 Win Rate 0% P/L -4330
  - link "Trend Breakout v1":
    - /url: /strategies/trend-breakout
  - button "Active"
  - paragraph: trend-breakout
  - text: Trades 9 Win Rate 0% P/L -4190
```

# Test source

```ts
  1   | import { test, expect } from '@playwright/test';
  2   | 
  3   | test.describe('Run List', () => {
  4   |   test('renders table with rows', async ({ page }) => {
  5   |     await page.goto('/runs');
  6   |     await expect(page.locator('app-run-list')).toBeVisible();
  7   |     await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
  8   |   });
  9   | });
  10  | 
  11  | test.describe('Strategy Pages', () => {
  12  |   test('list renders and navigates to detail', async ({ page }) => {
  13  |     await page.goto('/strategies');
  14  |     await expect(page.locator('app-strategy-list')).toBeVisible({ timeout: 10000 });
  15  |     // Strategy list renders via app-data-table — wait for it to load
> 16  |     await expect(page.locator('app-strategy-list table')).toBeVisible({ timeout: 10000 });
      |                                                           ^ Error: expect(locator).toBeVisible() failed
  17  |     const rows = page.locator('app-strategy-list table tbody tr');
  18  |     await expect(rows.first()).toBeVisible({ timeout: 10000 });
  19  |     await rows.first().click();
  20  |     await expect(page.locator('app-strategy-detail')).toBeVisible({ timeout: 10000 });
  21  |   });
  22  | });
  23  | 
  24  | test.describe('Settings Page', () => {
  25  |   test('renders', async ({ page }) => {
  26  |     await page.goto('/settings');
  27  |     await expect(page.locator('app-settings')).toBeVisible();
  28  |   });
  29  | });
  30  | 
  31  | test.describe('Governor Options', () => {
  32  |   test('renders', async ({ page }) => {
  33  |     await page.goto('/governor-options');
  34  |     await expect(page.locator('app-governor-edit')).toBeVisible();
  35  |   });
  36  | });
  37  | 
  38  | test.describe('Run Report', () => {
  39  |   test('has journal filter buttons with correct kinds', async ({ page }) => {
  40  |     await page.goto('/runs');
  41  |     await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
  42  |     await page.locator('app-run-list table tbody tr').first().click();
  43  |     await page.waitForURL('**/runs/**', { timeout: 10000 });
  44  |     await expect(page.locator('app-run-report')).toBeVisible({ timeout: 15000 });
  45  | 
  46  |     const btnTexts = await page.locator('app-run-report button.text-xs').allTextContents();
  47  |     const all = btnTexts.join(',');
  48  | 
  49  |     expect(all).toContain('SIGNAL');
  50  |     expect(all).toContain('ORDER');
  51  |     expect(all).toContain('FILL');
  52  |     expect(all).toContain('CLOSE');
  53  |     expect(all).toContain('REJECTED');
  54  |     expect(all).toContain('BREACH');
  55  |     expect(all).toContain('GOVERNOR');
  56  |     expect(all).toContain('CANCELLED');
  57  |     expect(all).not.toContain('BAR');
  58  |   });
  59  | 
  60  |   test('trades table has cost columns', async ({ page }) => {
  61  |     await page.goto('/runs');
  62  |     await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
  63  |     await page.locator('app-run-list table tbody tr').first().click();
  64  |     await page.waitForURL('**/runs/**', { timeout: 10000 });
  65  |     await expect(page.locator('app-run-report')).toBeVisible({ timeout: 15000 });
  66  | 
  67  |     const thTexts = await page.locator('app-run-report th').allTextContents();
  68  |     const headings = thTexts.join(' ');
  69  |     expect(headings).toContain('Gross');
  70  |     expect(headings).toContain('Comm');
  71  |     expect(headings).toContain('Swap');
  72  |     expect(headings).toContain('Net');
  73  |   });
  74  | 
  75  |   test('equity chart renders', async ({ page }) => {
  76  |     await page.goto('/runs');
  77  |     await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
  78  |     await page.locator('app-run-list table tbody tr').first().click();
  79  |     await page.waitForURL('**/runs/**', { timeout: 10000 });
  80  |     await expect(page.locator('app-run-report')).toBeVisible({ timeout: 15000 });
  81  |     await expect(page.locator('app-run-report .chart-host').first()).toBeVisible({ timeout: 10000 });
  82  |   });
  83  | });
  84  | 
  85  | test.describe('Trade Detail', () => {
  86  |   test('candle chart and stat tiles render', async ({ page }) => {
  87  |     await page.goto('/runs');
  88  |     await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
  89  |     await page.locator('app-run-list table tbody tr').first().click();
  90  |     await page.waitForURL('**/runs/**', { timeout: 10000 });
  91  |     await expect(page.locator('app-run-report')).toBeVisible({ timeout: 15000 });
  92  | 
  93  |     // Try clicking a trade row if available
  94  |     const tradeLink = page.locator('app-run-report tbody tr').first();
  95  |     if (await tradeLink.count() > 0) {
  96  |       await tradeLink.click();
  97  |       await expect(page.locator('app-trade-detail')).toBeVisible({ timeout: 10000 });
  98  |       await expect(page.locator('app-trade-detail app-candle-chart .chart-host').first()).toBeVisible({ timeout: 10000 });
  99  |       await expect(page.locator('app-trade-detail app-stat-tile').first()).toBeVisible();
  100 |     }
  101 |   });
  102 | });
  103 | 
  104 | test.describe('Live Monitor', () => {
  105 |   test('renders for a run', async ({ page }) => {
  106 |     await page.goto('/runs');
  107 |     await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
  108 |     await page.locator('app-run-list table tbody tr').first().click();
  109 |     await page.waitForURL('**/runs/**', { timeout: 10000 });
  110 | 
  111 |     // Navigate to monitor
  112 |     const url = page.url();
  113 |     await page.goto(url + '/monitor');
  114 |     await expect(page.locator('app-run-monitor')).toBeVisible({ timeout: 15000 });
  115 |     await expect(page.locator('app-run-monitor .text-lg').first()).toBeVisible();
  116 |   });
```