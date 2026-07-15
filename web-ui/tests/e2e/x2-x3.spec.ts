import { test, expect } from '@playwright/test';

const TIMEOUT = 15000;

// X2 — Runs page: richer table, notes, copy-run, pair grouping, liveness scaffolding.
test.describe('X2 Runs page', () => {
  test('table has the richer column set', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
    const headers = (await page.locator('app-run-list table thead th').allTextContents()).join(' ');
    for (const col of ['Status', 'Venue', 'Strategy', 'Symbol', 'TF', 'Net P/L', 'Max DD', 'Trades', 'Score', 'Duration', 'Notes']) {
      expect(headers).toContain(col);
    }
  });

  test('a duplicated run (parent lineage, no compare pair) is visible in the list', async ({ page }) => {
    const runs = await (await page.request.get('/api/runs')).json();
    const dup = runs.find((r: any) => r.parentRunId && !r.comparePairId);
    test.skip(!dup, 'no duplicated run in DB');
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
    // The pre-X2 groupedRuns suppressed every parentRunId row — this is the regression guard.
    await expect(page.locator(`app-run-list tbody tr:has-text("${dup.runId.slice(0, 8)}")`)).toBeVisible({ timeout: TIMEOUT });
  });

  test('notes can be added from the runs page and cleared again', async ({ page }) => {
    await page.goto('/runs');
    const firstRow = page.locator('app-run-list table tbody tr').first();
    await expect(firstRow).toBeVisible({ timeout: TIMEOUT });

    await firstRow.locator('button:has-text("note")').click();
    const textarea = firstRow.locator('textarea');
    await textarea.fill('e2e note check');
    await firstRow.locator('button:has-text("✓")').click();
    await expect(firstRow.locator('text=e2e note check')).toBeVisible({ timeout: TIMEOUT });

    // restore: clear the note so e2e runs don't accrete data
    await firstRow.locator('button[title="e2e note check"], button:has-text("e2e note check")').first().click();
    await firstRow.locator('textarea').fill('');
    await firstRow.locator('button:has-text("✓")').click();
    await expect(firstRow.locator('text=e2e note check')).toBeHidden({ timeout: TIMEOUT });
  });

  test('copy-run lands on the prefilled builder', async ({ page }) => {
    await page.goto('/runs');
    const firstRow = page.locator('app-run-list table tbody tr').first();
    await expect(firstRow).toBeVisible({ timeout: TIMEOUT });
    await firstRow.locator('button[title*="Copy run"]').click();
    await page.waitForURL('**/runs/new?copyFrom=**', { timeout: TIMEOUT });
    await expect(page.locator('app-new-backtest')).toBeVisible({ timeout: TIMEOUT });
    await expect(page.locator('text=copied from')).toBeVisible({ timeout: TIMEOUT });
  });

  test('filter box narrows the table', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
    await page.locator('app-run-list input[type="text"]').fill('zzz-no-such-run');
    await expect(page.locator('app-run-list table tbody tr')).toHaveCount(0, { timeout: TIMEOUT });
  });
});

// X2 — report page: notes editing + copy run.
test.describe('X2 Run report', () => {
  test('has a notes editor and a copy-run button', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: TIMEOUT });
    await page.locator('app-run-list table tbody tr td:nth-child(2)').first().click();
    await page.waitForURL('**/runs/**', { timeout: TIMEOUT });
    await expect(page.locator('app-run-report textarea[placeholder*="note"]')).toBeVisible({ timeout: TIMEOUT });
    await expect(page.locator('app-run-report button:has-text("Copy run")')).toBeVisible({ timeout: TIMEOUT });
  });
});

// X3 — trade chart: candles render, context selector, prev/next navigation stays mounted.
test.describe('X3 Trade chart', () => {
  test('trade detail renders chart with context selector and nav', async ({ page }) => {
    const runs = await (await page.request.get('/api/runs')).json();
    let tradeId: string | null = null;
    let tradeCount = 0;
    for (const r of runs) {
      if (r.totalTrades > 1 && (r.status === 'completed' || r.status === 'completed-with-warnings')) {
        const trades = (await (await page.request.get(`/api/runs/${r.runId}/trades`)).json()).trades;
        if (trades.length > 1) { tradeId = trades[0].id; tradeCount = trades.length; break; }
      }
    }
    test.skip(!tradeId, 'no completed run with >1 trade');

    await page.goto(`/trades/${tradeId}`);
    await expect(page.locator('app-trade-chart-card')).toBeVisible({ timeout: TIMEOUT });
    // lightweight-charts renders into a canvas; "meaningless lines" regression = no canvas or no arrows.
    await expect(page.locator('app-trade-chart-card canvas').first()).toBeVisible({ timeout: TIMEOUT });
    await expect(page.locator('app-trade-chart-card').getByText('Context:')).toBeVisible({ timeout: TIMEOUT });
    await expect(page.getByText(`of ${tradeCount}`).first()).toBeVisible({ timeout: TIMEOUT });

    // prev/next: one of the two must be enabled; clicking navigates in place and the chart survives.
    const next = page.locator('app-trade-chart-card button:has-text("Next")');
    await expect(next).toBeVisible({ timeout: TIMEOUT });
    if (await next.isEnabled()) {
      const urlBefore = page.url();
      await next.click();
      await page.waitForURL((u) => u.toString() !== urlBefore, { timeout: TIMEOUT });
      await expect(page.locator('app-trade-chart-card canvas').first()).toBeVisible({ timeout: TIMEOUT });
    }
  });

  test('context selector refetches a wider window', async ({ page }) => {
    const runs = await (await page.request.get('/api/runs')).json();
    let tradeId: string | null = null;
    for (const r of runs) {
      if (r.totalTrades > 0 && (r.status === 'completed' || r.status === 'completed-with-warnings')) {
        const trades = (await (await page.request.get(`/api/runs/${r.runId}/trades`)).json()).trades;
        if (trades.length > 0) { tradeId = trades[0].id; break; }
      }
    }
    test.skip(!tradeId, 'no completed run with trades');

    let padSeen: string | null = null;
    page.on('request', (req) => {
      const m = req.url().match(/\/chart\?padBars=(\d+)/);
      if (m) padSeen = m[1];
    });
    await page.goto(`/trades/${tradeId}`);
    await expect(page.locator('app-trade-chart-card canvas').first()).toBeVisible({ timeout: TIMEOUT });
    await page.locator('app-trade-chart-card button:has-text("50")').click();
    await expect.poll(() => padSeen, { timeout: TIMEOUT }).toBe('50');
  });
});
