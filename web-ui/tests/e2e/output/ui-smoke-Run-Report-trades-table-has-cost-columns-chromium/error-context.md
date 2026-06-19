# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: ui-smoke.spec.ts >> Run Report >> trades table has cost columns
- Location: tests\e2e\ui-smoke.spec.ts:60:3

# Error details

```
Error: expect(received).toContain(expected) // indexOf

Expected substring: "Gross"
Received string:    ""
```

# Page snapshot

```yaml
- generic [ref=e3]:
  - navigation [ref=e4]:
    - generic [ref=e5]:
      - link "Shamshir" [ref=e6] [cursor=pointer]:
        - /url: /runs
      - generic [ref=e7]:
        - link "Live" [ref=e8] [cursor=pointer]:
          - /url: /
        - link "Runs" [ref=e9] [cursor=pointer]:
          - /url: /runs
        - link "Trades" [ref=e10] [cursor=pointer]:
          - /url: /trades
        - link "Strategies" [ref=e11] [cursor=pointer]:
          - /url: /strategies
        - link "Compliance" [ref=e12] [cursor=pointer]:
          - /url: /compliance
        - link "Events" [ref=e13] [cursor=pointer]:
          - /url: /events
        - link "Risk" [ref=e14] [cursor=pointer]:
          - /url: /risk-profiles
        - link "FTMO" [ref=e15] [cursor=pointer]:
          - /url: /prop-firm-rules
        - link "Governor" [ref=e16] [cursor=pointer]:
          - /url: /governor-options
        - link "Settings" [ref=e17] [cursor=pointer]:
          - /url: /settings
        - link "+ New Backtest" [ref=e18] [cursor=pointer]:
          - /url: /runs/new
      - link "API Docs" [ref=e20] [cursor=pointer]:
        - /url: /scalar/v1
  - main [ref=e21]:
    - generic [ref=e23]:
      - generic [ref=e24]:
        - generic [ref=e25]:
          - heading "Run 047f94cb" [level=1] [ref=e26]
          - paragraph [ref=e27]: EURUSD h1 Jan 1, 2024 - Jan 31, 2024 · Balance 100,000
        - generic [ref=e28]:
          - link "Monitor" [ref=e29] [cursor=pointer]:
            - /url: /runs/047f94cb/monitor
          - link "Analyzer" [ref=e30] [cursor=pointer]:
            - /url: /runs/047f94cb/analyzer
          - link "All Runs" [ref=e31] [cursor=pointer]:
            - /url: /runs
      - generic [ref=e32]:
        - generic [ref=e34]:
          - generic [ref=e35]: Net P/L
          - generic [ref=e36]: "0.00"
        - generic [ref=e38]:
          - generic [ref=e39]: Return %
          - generic [ref=e40]: 0.00%
        - generic [ref=e42]:
          - generic [ref=e43]: Max DD
          - generic [ref=e44]: 0.00%
        - generic [ref=e46]:
          - generic [ref=e47]: Profit Factor
          - generic [ref=e48]: "0.00"
        - generic [ref=e50]:
          - generic [ref=e51]: Win Rate
          - generic [ref=e52]: 0.0%
        - generic [ref=e54]:
          - generic [ref=e55]: Trades
          - generic [ref=e56]: "0"
        - generic [ref=e58]:
          - generic [ref=e59]: Gross P/L
          - generic [ref=e60]: "0.00"
        - generic [ref=e62]:
          - generic [ref=e63]: Commission
          - generic [ref=e64]: "0.00"
        - generic [ref=e66]:
          - generic [ref=e67]: Swap
          - generic [ref=e68]: "0.00"
        - generic [ref=e70]:
          - generic [ref=e71]: Avg R
          - generic [ref=e72]: "0.00"
      - generic [ref=e73]:
        - generic [ref=e74]:
          - text: "Net = Σ trades:"
          - generic [ref=e76]: OK
        - generic [ref=e77]:
          - text: "Closes = trade count:"
          - generic [ref=e79]: OK
        - generic [ref=e80]:
          - text: "ΣGross - ΣComm - ΣSwap = ΣNet:"
          - generic [ref=e82]: OK
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
  16  |     await expect(page.locator('app-strategy-list table')).toBeVisible({ timeout: 10000 });
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
> 69  |     expect(headings).toContain('Gross');
      |                      ^ Error: expect(received).toContain(expected) // indexOf
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
  117 | });
  118 | 
```