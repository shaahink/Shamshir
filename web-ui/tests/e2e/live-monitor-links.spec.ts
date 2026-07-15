import { test, expect } from '@playwright/test';

// ============================================================================
// Live Monitor Chain — Link Tests (isolation first, then full chain)
//
// Run order:
//   Tier 1: SignalR connectivity (loads existing run's monitor, checks console)
//   Tier 2: Progress events reach browser (starts short backtest, waits for 'running')
//   Tier 3: Full chain (start → monitor → complete → report)
//
// Each tier gates the next. Don't debug Tier 2 if Tier 1 is broken.
// ============================================================================

const APP_TIMEOUT = 10_000;

// ── Tier 1: SignalR connectivity ──────────────────────────────────────────

test.describe('Tier 1 — SignalR connectivity', () => {
  // D1 triage 2026-07-15: the "[SignalR] connected" console-log test was DELETED — run-hub.service.ts
  // no longer emits that log line, and the intent (SignalR actually connects) is covered by the
  // status-tile test below plus x2-x3.spec's live run-list updates. See KNOWN-FAILURES.md.

  test('monitor status changes from connecting when SignalR connects', async ({ page }) => {
    await page.goto('/runs');
    await expect(page.locator('app-run-list table tbody tr').first()).toBeVisible({ timeout: APP_TIMEOUT });

    await page.locator('app-run-list table tbody tr').first().click();
    await page.waitForURL('**/runs/**', { timeout: APP_TIMEOUT });

    const url = page.url();
    await page.goto(url + '/monitor');
    await expect(page.locator('app-run-monitor')).toBeVisible({ timeout: APP_TIMEOUT });

    // For a completed run, the status should quickly go to 'running' or 'completed'
    // If it stays at 'connecting', SignalR is broken.
    await expect(async () => {
      const text = await page.locator('app-run-monitor app-stat-tile').first().textContent();
      if ((text || '').toLowerCase().includes('connecting')) {
        throw new Error('Still connecting — SignalR never established');
      }
    }).toPass({ timeout: 10_000 });
  });
});

// ── Tier 2: Progress events reach browser ─────────────────────────────────

test.describe('Tier 2 — Progress events reach browser', () => {
  // QUARANTINED (D1 triage 2026-07-15): drives the PRE-redesign backtest form — it never selects a
  // strategy, so the row-builder's Start button stays disabled; it also assumes a yesterday→today
  // replay window has bars. Unquarantine by rewriting against the row builder (pick strategy chip +
  // symbol + TF, venue=tape, a known-bars window) — or retire in favour of scripts/verify-live.ps1
  // (iter-dx-speed D4) + x2-x3.spec's live list test. See KNOWN-FAILURES.md.
  test.fixme('live backtest shows running status within 15 seconds', async ({ page }) => {
    // Start a minimal backtest: 1 day, EURUSD H1
    await page.goto('/runs/new');
    await expect(page.locator('app-new-backtest')).toBeVisible({ timeout: APP_TIMEOUT });
    await expect(page.locator('app-new-backtest input[type="checkbox"]').first()).toBeVisible({ timeout: APP_TIMEOUT });

    // 1-day range
    const yesterday = new Date();
    yesterday.setDate(yesterday.getDate() - 1);
    const yStr = yesterday.toISOString().slice(0, 10);
    const tStr = new Date().toISOString().slice(0, 10);

    await page.locator('app-new-backtest input[type="date"]').first().fill(yStr);
    await page.locator('app-new-backtest input[type="date"]').nth(1).fill(tStr);

    await page.locator('button', { hasText: 'Start Backtest' }).click();
    await page.waitForURL('**/monitor', { timeout: APP_TIMEOUT });
    await expect(page.locator('app-run-monitor')).toBeVisible({ timeout: APP_TIMEOUT });

    // The first progress event sets status to 'running'
    await expect(
      page.locator('app-run-monitor app-stat-tile', { hasText: 'running' })
    ).toBeVisible({ timeout: 15_000 });

    // Bar count should go above 0
    await expect(async () => {
      const tiles = await page.locator('app-run-monitor app-stat-tile').allTextContents();
      const equityTile = tiles.find(t => /^\d+/.test(t.trim()));
      if (!equityTile || equityTile.trim() === '0') {
        throw new Error('Bar count or equity still at 0');
      }
    }).toPass({ timeout: 20_000 });
  });
});

// ── Tier 3: Full chain (start → monitor → complete → report) ──────────────

test.describe('Tier 3 — Full chain', () => {
  // QUARANTINED (D1 triage 2026-07-15): same stale form contract as Tier 2. See KNOWN-FAILURES.md.
  test.fixme('EURUSD H1 3-day: completes and report has trades', async ({ page }) => {
    test.setTimeout(180_000);

    await page.goto('/runs/new');
    await expect(page.locator('app-new-backtest')).toBeVisible({ timeout: APP_TIMEOUT });
    await expect(page.locator('app-new-backtest input[type="checkbox"]').first()).toBeVisible({ timeout: APP_TIMEOUT });

    const now = new Date();
    const end = now.toISOString().slice(0, 10);
    const start = new Date(now);
    start.setDate(start.getDate() - 3);
    const startStr = start.toISOString().slice(0, 10);

    await page.locator('app-new-backtest input[type="date"]').first().fill(startStr);
    await page.locator('app-new-backtest input[type="date"]').nth(1).fill(end);

    await page.locator('button', { hasText: 'Start Backtest' }).click();
    await page.waitForURL('**/monitor', { timeout: APP_TIMEOUT });

    // Wait for completion (max 3 min)
    await expect(
      page.locator('app-run-monitor').filter({ hasText: 'completed' })
    ).toBeVisible({ timeout: 150_000 });

    // Navigate to report
    const url = page.url();
    const runId = url.split('/').filter(s => s.length === 36)[0] || '';
    await page.goto('/runs/' + runId);
    await expect(page.locator('app-run-report')).toBeVisible({ timeout: APP_TIMEOUT });

    // Verify at least one stat tile is non-zero
    const tiles = await page.locator('app-run-report app-stat-tile').allTextContents();
    const nonZero = tiles.some(t => {
      const n = parseFloat(t.replace(/[^0-9.-]/g, ''));
      return !isNaN(n) && n !== 0;
    });
    expect(nonZero).toBe(true);
  });
});
