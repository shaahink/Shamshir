import { test, expect } from '@playwright/test';

test.describe('Run List', () => {
  test('renders table with rows', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list')).toBeVisible();
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
  });
});

test.describe('Strategy Pages', () => {
  test('list renders and navigates to detail', async ({ page }) => {
    await page.goto('/strategies');
    await expect(page.locator('app-strategy-list')).toBeVisible({ timeout: 10000 });
    // Strategy list renders via app-data-table — wait for it to load
    await expect(page.locator('app-strategy-list table')).toBeVisible({ timeout: 10000 });
    const rows = page.locator('app-strategy-list table tbody tr');
    await expect(rows.first()).toBeVisible({ timeout: 10000 });
    await rows.first().click();
    await expect(page.locator('app-strategy-detail')).toBeVisible({ timeout: 10000 });
  });
});

test.describe('Settings Page', () => {
  test('renders', async ({ page }) => {
    await page.goto('/settings');
    await expect(page.locator('app-settings')).toBeVisible();
  });
});

test.describe('Governor Options', () => {
  test('renders', async ({ page }) => {
    await page.goto('/governor-options');
    await expect(page.locator('app-governor-edit')).toBeVisible();
  });
});

test.describe('Run Report', () => {
  test('has journal filter buttons with correct kinds', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
    await page.locator('app-run-list table tbody tr').first().click();
    await page.waitForURL('**/runs/**', { timeout: 10000 });
    await expect(page.locator('app-run-report')).toBeVisible({ timeout: 15000 });

    const btnTexts = await page.locator('app-run-report button.text-xs').allTextContents();
    const all = btnTexts.join(',');

    expect(all).toContain('SIGNAL');
    expect(all).toContain('ORDER');
    expect(all).toContain('FILL');
    expect(all).toContain('CLOSE');
    expect(all).toContain('REJECTED');
    expect(all).toContain('BREACH');
    expect(all).toContain('GOVERNOR');
    expect(all).toContain('CANCELLED');
    expect(all).not.toContain('BAR');
  });

  test('trades table has cost columns', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
    await page.locator('app-run-list table tbody tr').first().click();
    await page.waitForURL('**/runs/**', { timeout: 10000 });
    await expect(page.locator('app-run-report')).toBeVisible({ timeout: 15000 });

    const thTexts = await page.locator('app-run-report th').allTextContents();
    const headings = thTexts.join(' ');
    expect(headings).toContain('Gross');
    expect(headings).toContain('Comm');
    expect(headings).toContain('Swap');
    expect(headings).toContain('Net');
  });

  test('equity chart renders', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
    await page.locator('app-run-list table tbody tr').first().click();
    await page.waitForURL('**/runs/**', { timeout: 10000 });
    await expect(page.locator('app-run-report')).toBeVisible({ timeout: 15000 });
    await expect(page.locator('app-run-report .chart-host').first()).toBeVisible({ timeout: 10000 });
  });
});

test.describe('Trade Detail', () => {
  test('candle chart and stat tiles render', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
    await page.locator('app-run-list table tbody tr').first().click();
    await page.waitForURL('**/runs/**', { timeout: 10000 });
    await expect(page.locator('app-run-report')).toBeVisible({ timeout: 15000 });

    // Try clicking a trade row if available
    const tradeLink = page.locator('app-run-report tbody tr').first();
    if (await tradeLink.count() > 0) {
      await tradeLink.click();
      await expect(page.locator('app-trade-detail')).toBeVisible({ timeout: 10000 });
      await expect(page.locator('app-trade-detail app-candle-chart .chart-host').first()).toBeVisible({ timeout: 10000 });
      await expect(page.locator('app-trade-detail app-stat-tile').first()).toBeVisible();
    }
  });
});

test.describe('Live Monitor', () => {
  test('renders for a run', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: 10000 });
    await page.locator('app-run-list table tbody tr').first().click();
    await page.waitForURL('**/runs/**', { timeout: 10000 });

    // Navigate to monitor
    const url = page.url();
    await page.goto(url + '/monitor');
    await expect(page.locator('app-run-monitor')).toBeVisible({ timeout: 15000 });
    await expect(page.locator('app-run-monitor .text-lg').first()).toBeVisible();
  });
});
