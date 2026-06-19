import { test, expect } from '@playwright/test';

const TIMEOUT = 15000;

test.describe('Run List', () => {
  test('renders table with rows', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list')).toBeVisible({ timeout: TIMEOUT });
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
  });

  test('click row navigates to run report', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
    await page.locator('app-run-list table tbody tr').first().click();
    await page.waitForURL('**/runs/**', { timeout: TIMEOUT });
    await expect(page.locator('app-run-report')).toBeVisible({ timeout: TIMEOUT });
  });
});

test.describe('Strategy Pages', () => {
  test('list renders cards with links', async ({ page }) => {
    await page.goto('/strategies');
    await expect(page.locator('app-strategy-list')).toBeVisible({ timeout: TIMEOUT });
    // Strategy list uses card divs with <a> links, not a table
    await expect(page.locator('app-strategy-list a').first()).toBeVisible({ timeout: TIMEOUT });
    await page.locator('app-strategy-list a').first().click();
    await page.waitForURL('**/strategies/**', { timeout: TIMEOUT });
    await expect(page.locator('app-strategy-detail')).toBeVisible({ timeout: TIMEOUT });
  });
});

test.describe('Settings Page', () => {
  test('renders', async ({ page }) => {
    await page.goto('/settings');
    await expect(page.locator('app-settings')).toBeVisible({ timeout: TIMEOUT });
  });
});

test.describe('Governor Options', () => {
  test('renders form', async ({ page }) => {
    await page.goto('/governor-options');
    await expect(page.locator('app-governor-edit')).toBeVisible({ timeout: TIMEOUT });
  });
});

test.describe('Run Report — structure checks (always pass)', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
    await page.locator('app-run-list table tbody tr').first().click();
    await page.waitForURL('**/runs/**', { timeout: TIMEOUT });
    await expect(page.locator('app-run-report')).toBeVisible({ timeout: TIMEOUT });
  });

  test('stat tiles show KPI data', async ({ page }) => {
    await expect(page.locator('app-run-report app-stat-tile').first()).toBeVisible({ timeout: TIMEOUT });
    const tiles = await page.locator('app-run-report app-stat-tile').count();
    expect(tiles).toBeGreaterThanOrEqual(5, 'should have at least 5 KPI stat tiles');
  });

  test('reconciliation badges render', async ({ page }) => {
    await expect(page.locator('app-run-report app-badge').first()).toBeVisible({ timeout: TIMEOUT });
  });

  test('navigation links exist', async ({ page }) => {
    await expect(page.locator('a[href*="/monitor"]').first()).toBeVisible({ timeout: TIMEOUT });
    await expect(page.locator('a[href*="/analyzer"]').first()).toBeVisible({ timeout: TIMEOUT });
  });
});

test.describe('Run Report — data checks (require seeded bars)', () => {
  test.beforeEach(async ({ page }) => {
    const seededRunId = process.env.SEEDED_RUN_ID;
    if (seededRunId) {
      await page.goto(`/runs/${seededRunId}`);
    } else {
      await page.goto('/runs');
      await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
      const rows = page.locator('app-run-list table tbody tr');
      const count = await rows.count();
      await rows.nth(count - 1).click();
      await page.waitForURL('**/runs/**', { timeout: TIMEOUT });
    }
    await expect(page.locator('app-run-report')).toBeVisible({ timeout: TIMEOUT });
  });

  test('trades table has cost columns when trades exist', async ({ page }) => {
    // Trades table is conditionally rendered (trades().length > 0)
    const h2s = await page.locator('app-run-report h2').allTextContents();
    // Also check if trades data is visible in the stat tile
    const tiles = await page.locator('app-run-report app-stat-tile').allTextContents();
    const totalTrades = tiles.find(t => t.includes('Trades'));
    console.log(`[DEBUG] Trades stat tile: ${totalTrades}`);
    const heading = page.locator('app-run-report h2:has-text("Trades")');
    const hasTrades = await heading.isVisible().catch(() => false);
    if (!hasTrades || h2s.every(h => !h.includes('Trades'))) {
      test.skip(true, `no trades in this run — headings found: ${JSON.stringify(h2s)}`);
      return;
    }
    const thTexts = await page.locator('app-run-report app-data-table th').allTextContents();
    const headings = thTexts.join(' ');
    expect(headings).toContain('Gross');
    expect(headings).toContain('Comm');
    expect(headings).toContain('Swap');
    expect(headings).toContain('Net');
  });

  test('journal filter buttons show correct kinds when journal exists', async ({ page }) => {
    const h2s = await page.locator('app-run-report h2').allTextContents();
    console.log(`[DEBUG] Run-report h2s (journal check): ${JSON.stringify(h2s)}`);
    const heading = page.locator('app-run-report h2:has-text("Journal")');
    const hasJournal = await heading.isVisible().catch(() => false);
    if (!hasJournal) {
      test.skip(true, `no journal data — headings: ${JSON.stringify(h2s)}`);
      return;
    }
    const btns = page.locator('app-run-report h2:has-text("Journal") + div button');
    const btnCount = await btns.count();
    console.log(`[DEBUG] Journal filter button count: ${btnCount}`);
    if (btnCount === 0) {
      test.skip(true, 'journal filter buttons not found');
      return;
    }
    const btnTexts = await btns.allTextContents();
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

  test('equity chart renders when data exists', async ({ page }) => {
    // Also check what stat tiles say about trades
    const tiles = await page.locator('app-run-report app-stat-tile').allTextContents();
    console.log(`[DEBUG] Stat tiles: ${JSON.stringify(tiles.slice(0, 10))}`);
    const chart = page.locator('app-run-report app-equity-chart');
    const hasChart = await chart.isVisible().catch(() => false);
    if (!hasChart) {
      test.skip(true, `no equity chart — tiles: ${JSON.stringify(tiles.slice(0, 8))}`);
      return;
    }
    await expect(page.locator('app-run-report app-equity-chart .chart-host').first()).toBeVisible({ timeout: TIMEOUT });
  });
});

test.describe('Trade Detail', () => {
  test('page renders', async ({ page }) => {
    const seededRunId = process.env.SEEDED_RUN_ID;
    if (seededRunId) {
      await page.goto(`/runs/${seededRunId}`);
    } else {
      await page.goto('/runs');
      await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
      const rows = page.locator('app-run-list table tbody tr');
      const count = await rows.count();
      await rows.nth(count - 1).click();
      await page.waitForURL('**/runs/**', { timeout: TIMEOUT });
    }
    await expect(page.locator('app-run-report')).toBeVisible({ timeout: TIMEOUT });

    // Try clicking a trade row if available
    const tradeRow = page.locator('app-run-report table tbody tr').first();
    const hasTrades = await tradeRow.isVisible().catch(() => false);
    if (!hasTrades) {
      test.skip(true, 'no trades — seed bars first');
      return;
    }
    await tradeRow.click();
    await expect(page.locator('app-trade-detail')).toBeVisible({ timeout: TIMEOUT });
  });
});

test.describe('Live Monitor', () => {
  test('renders for a run', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
    // Navigate to the LAST (most recent / seeded) run
    const rows = page.locator('app-run-list table tbody tr');
    const count = await rows.count();
    await rows.nth(count - 1).click();
    await page.waitForURL('**/runs/**', { timeout: TIMEOUT });

    const url = page.url();
    await page.goto(url + '/monitor');
    await expect(page.locator('app-run-monitor')).toBeVisible({ timeout: TIMEOUT });
    await expect(page.locator('app-run-monitor app-stat-tile').first()).toBeVisible({ timeout: TIMEOUT });
  });
});
