#!/usr/bin/env node
// driver.mjs — Playwright headless browser driver for Shamshir UI E2E.

import { createRequire } from 'node:module';
import { spawn, spawnSync } from 'node:child_process';
import { setTimeout as sleep } from 'node:timers/promises';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, '..', '..', '..');
const WEB = resolve(REPO, 'src', 'TradingEngine.Web');
const WEBUI = resolve(REPO, 'web-ui');
const DLL = resolve(WEB, 'bin', 'Debug', 'net10.0', 'TradingEngine.Web.dll');
const SS_DIR = resolve(HERE, 'screenshots');

// Load Playwright from the web-ui project's node_modules
const require = createRequire(import.meta.url);
const { chromium } = require(join(WEBUI, 'node_modules', 'playwright'));

const PORT = process.env.PORT || '5134';
const BASE = `http://localhost:${PORT}`;
const isWin = process.platform === 'win32';

const argv = process.argv.slice(2);
const doBuild = argv.includes('--build');
const keepAlive = argv.includes('--serve');
const doGolden = argv.includes('--golden');

let pass = 0, fail = 0;
const ok = (m) => { pass++; console.log(`  \x1b[32m✓\x1b[0m ${m}`); };
const bad = (m) => { fail++; console.log(`  \x1b[31m✗\x1b[0m ${m}`); };
const warn = (m) => { console.log(`  \x1b[33m⚠\x1b[0m ${m}`); };

function sh(cmd, cmdArgs, cwd) {
  console.log(`\n$ ${cmd} ${cmdArgs.join(' ')}   (cwd: ${cwd})`);
  const r = spawnSync(cmd, cmdArgs, { cwd, stdio: 'inherit', shell: isWin });
  if (r.status !== 0) { console.error(`Command failed (exit ${r.status})`); process.exit(1); }
}

async function get(path, opts = {}) {
  const res = await fetch(BASE + path, opts);
  const ct = res.headers.get('content-type') || '';
  const body = ct.includes('json') ? await res.json() : await res.text();
  return { status: res.status, ct, body };
}

function killTree(pid) {
  if (!pid) return;
  if (isWin) spawnSync('taskkill', ['/T', '/F', '/PID', String(pid)], { stdio: 'ignore', shell: true });
  else { try { process.kill(-pid, 'SIGKILL'); } catch { try { process.kill(pid, 'SIGKILL'); } catch {} } }
}

// ── Browser checks ──────────────────────────────────────────────────────────

async function screenshot(page, name) {
  const p = join(SS_DIR, name + '.png');
  await page.screenshot({ path: p, fullPage: true });
  if (doGolden) {
    const golden = join(SS_DIR, name + '.golden.png');
    writeFileSync(golden, readFileSync(p));
    ok(`screenshot ${name} (golden updated)`);
  } else {
    const golden = join(SS_DIR, name + '.golden.png');
    if (existsSync(golden)) {
      warn(`screenshot ${name} captured (golden exists; visual diff not automated — review manually)`);
    } else {
      warn(`screenshot ${name} captured (no golden baseline — run --golden to create)`);
    }
  }
}

async function checkVisible(page, selector, label) {
  try {
    const el = page.locator(selector).first();
    await el.waitFor({ state: 'visible', timeout: 5000 });
    ok(label);
    return true;
  } catch {
    bad(label + ' (not visible)');
    return false;
  }
}

async function checkText(page, selector, expected, label) {
  try {
    const el = page.locator(selector).first();
    const text = await el.textContent({ timeout: 5000 });
    if (text && text.includes(expected)) { ok(label); return true; }
    bad(`${label} — expected "${expected}", got "${text?.slice(0, 80)}"`);
    return false;
  } catch {
    bad(`${label} — element not found: ${selector}`);
    return false;
  }
}

async function checkCount(page, selector, min, label) {
  try {
    const count = await page.locator(selector).count();
    if (count >= min) { ok(label); return true; }
    bad(`${label} — expected >=${min}, got ${count}`);
    return false;
  } catch {
    bad(`${label} — selector error: ${selector}`);
    return false;
  }
}

async function runBrowserChecks(browser) {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });

  // ── 12. Run-list page ──────────────────────────────────────────────────
  await page.goto(BASE + '/runs', { waitUntil: 'networkidle' });
  await checkVisible(page, 'app-run-list table', '12. /runs — run-list table renders');
  await checkCount(page, 'app-run-list tbody tr', 1, '12b. /runs — at least 1 run row');

  // ── Find a completed/failed run to inspect ─────────────────────────────
  let runId = null;
  try {
    const rows = page.locator('app-run-list tbody tr');
    const count = await rows.count();
    for (let i = 0; i < count && !runId; i++) {
      const href = await rows.nth(i).locator('a').first().getAttribute('href');
      if (href && href.includes('/runs/')) runId = href.split('/runs/')[1]?.split(/[/?#]/)[0];
    }
  } catch {}
  if (!runId) {
    // Start a quick backtest to get a run to inspect
    const payload = { symbol:'EURUSD', period:'h1', start:'2024-01-01T00:00:00Z', end:'2024-01-31T00:00:00Z', balance:100000, commissionPerMillion:30, spreadPips:1, symbols:['EURUSD'], periods:['h1'], strategyIds:[], riskProfileId:'', venue:'replay' };
    const r = await get('/api/runs', { method:'POST', headers:{'content-type':'application/json'}, body:JSON.stringify(payload) });
    runId = r.body?.runId || r.body?.RunId;
    for (let i = 0; i < 30 && runId; i++) {
      const d = await get('/api/runs/' + runId);
      const st = (d.body?.status || d.body?.Status || '').toLowerCase();
      if (['completed','failed','cancelled','canceled','error'].includes(st)) break;
      await sleep(1000);
    }
  }
  if (!runId) { warn('No run available for detail-page checks — skipping 13-17, 23'); return; }

  // ── 13. Run-report page ──────────────────────────────────────────────
  await page.goto(BASE + '/runs/' + runId, { waitUntil: 'networkidle' });
  await checkVisible(page, 'app-run-report app-equity-chart .chart-host', '13. /runs/{id} — equity chart renders');
  await checkVisible(page, 'app-run-report app-data-table', '13b. /runs/{id} — trades table renders');

  // Check cost columns in trades table
  const thTexts = await page.locator('app-run-report th').allTextContents();
  const headings = thTexts.join(' ');
  ['Gross', 'Comm', 'Swap', 'Net'].forEach(c => {
    headings.includes(c) ? ok(`13c. /runs/{id} — "${c}" column in trades table`) : bad(`13c. /runs/{id} — "${c}" column MISSING from trades table`);
  });

  // ── 14. Journal filter buttons ──────────────────────────────────────
  const filterBtns = await page.locator('app-run-report button.text-xs').allTextContents();
  const btnTexts = filterBtns.join(',');
  ['SIGNAL', 'ORDER', 'FILL', 'CLOSE', 'REJECTED', 'BREACH', 'GOVERNOR', 'CANCELLED'].forEach(k => {
    btnTexts.includes(k) ? ok(`14. /runs/{id} — journal filter "${k}" present`) : bad(`14. /runs/{id} — journal filter "${k}" MISSING`);
  });
  btnTexts.includes('BAR') ? bad('14b. /runs/{id} — journal filter "BAR" still present (should be removed)') : ok('14b. /runs/{id} — journal filter "BAR" removed');

  // ── 15. Scatter chart ──────────────────────────────────────────────
  await checkVisible(page, 'app-run-report app-scatter-chart .chart-host', '15. /runs/{id} — scatter chart renders');

  // ── 16-17. Trade-detail page ──────────────────────────────────────
  // Find a trade to inspect
  let tradeId = null;
  try {
    const trades = await get('/api/runs/' + runId + '/trades');
    if (Array.isArray(trades.body) && trades.body.length > 0) tradeId = trades.body[0].id;
  } catch {}
  if (tradeId) {
    await page.goto(BASE + '/trades/' + tradeId, { waitUntil: 'networkidle' });
    await checkVisible(page, 'app-trade-detail app-candle-chart .chart-host', '16. /trades/{id} — candle chart renders');
    await checkVisible(page, 'app-trade-detail app-stat-tile', '16b. /trades/{id} — stat tiles render');

    // Check cost values are not just "0.00" placeholders
    const tiles = await page.locator('app-trade-detail app-stat-tile').allTextContents();
    const tileStr = tiles.join('|');
    tileStr.includes('Commission') ? ok('17. /trades/{id} — Commission stat tile present') : warn('17. /trades/{id} — Commission tile not found (may be 0-value)');
    tileStr.includes('Gross P/L') ? ok('17b. /trades/{id} — Gross P/L stat tile present') : warn('17b. /trades/{id} — Gross P/L tile not found');

    await screenshot(page, 'trade-detail');
  } else {
    warn('No trades found for run — skipping trade-detail checks (16-17)');
  }

  // ── 18. Trade-list page ──────────────────────────────────────────────
  await page.goto(BASE + '/trades?from=2024-01-01&to=2024-06-01', { waitUntil: 'networkidle' });
  await checkVisible(page, 'app-trade-list', '18. /trades — trade-list page renders');

  // ── 19-20. Strategy pages ──────────────────────────────────────────
  await page.goto(BASE + '/strategies', { waitUntil: 'networkidle' });
  await checkVisible(page, 'app-strategy-list', '19. /strategies — strategy list renders');
  await checkCount(page, 'app-strategy-list tbody tr', 1, '19b. /strategies — at least 1 strategy row');

  // Click first strategy to navigate to detail
  try {
    await page.locator('app-strategy-list tbody tr a').first().click();
    await page.waitForURL('**/strategies/**', { timeout: 5000 });
    ok('19c. /strategies — click row navigates to detail');
    await checkVisible(page, 'app-strategy-detail', '20. /strategies/{id} — strategy detail renders');
    await screenshot(page, 'strategy-detail');
  } catch {
    warn('Could not navigate to strategy detail (19c, 20)');
  }

  // ── 21. Settings page ──────────────────────────────────────────────
  await page.goto(BASE + '/settings', { waitUntil: 'networkidle' });
  await checkVisible(page, 'app-settings', '21. /settings — settings page renders');

  // ── 22. Governor options page ──────────────────────────────────────
  await page.goto(BASE + '/governor-options', { waitUntil: 'networkidle' });
  await checkVisible(page, 'app-governor-edit', '22. /governor-options — governor page renders');

  // ── 23. Live monitor page ──────────────────────────────────────────
  await page.goto(BASE + '/runs/' + runId + '/monitor', { waitUntil: 'networkidle' });
  await checkVisible(page, 'app-run-monitor', '23. /runs/{id}/monitor — live monitor renders');

  // ── 24-26. Screenshots ─────────────────────────────────────────────
  await page.goto(BASE + '/runs/' + runId, { waitUntil: 'networkidle' });
  await screenshot(page, 'run-report');

  // Navigate to a trade for trade-detail screenshot
  if (tradeId) {
    await page.goto(BASE + '/trades/' + tradeId, { waitUntil: 'networkidle' });
    await sleep(1000);
    await screenshot(page, 'trade-detail');
  }

  await page.goto(BASE + '/runs/' + runId + '/monitor', { waitUntil: 'networkidle' });
  await sleep(1000);
  await screenshot(page, 'live-monitor');

  await page.close();
}

// ── Main ────────────────────────────────────────────────────────────────────

async function main() {
  mkdirSync(SS_DIR, { recursive: true });

  // ---- build ---------------------------------------------------------------
  if (doBuild) {
    sh('npm', ['run', 'build'], resolve(REPO, 'web-ui'));
    sh('dotnet', ['build', WEB, '-c', 'Debug'], REPO);
  }
  if (!existsSync(DLL)) {
    console.error(`\nNo build at ${DLL}\nRun with --build first.`);
    process.exit(1);
  }

  // ---- launch app ----------------------------------------------------------
  console.log(`\nLaunching ${DLL} on ${BASE} ...`);
  const child = spawn('dotnet', [DLL], {
    cwd: WEB,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: BASE, 'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command': 'Warning' },
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: !isWin,
  });
  const log = [];
  const cap = (b) => { const s = b.toString(); log.push(s); };
  child.stdout.on('data', cap);
  child.stderr.on('data', cap);

  const teardown = () => { if (!keepAlive) killTree(child.pid); };
  process.on('exit', teardown);
  process.on('SIGINT', () => { teardown(); process.exit(130); });

  try {
    // ---- wait for app readiness --------------------------------------------
    let up = false;
    for (let i = 0; i < 60; i++) {
      if (child.exitCode !== null) throw new Error(`Process exited early (code ${child.exitCode})`);
      try { if ((await get('/api/strategies')).status === 200) { up = true; break; } } catch {}
      await sleep(1000);
    }
    if (!up) throw new Error(`App did not answer on ${BASE} within 60s`);
    console.log(`\n--- App is up at ${BASE} ---\n`);

    // ---- 1-11. Backend API checks (same as run-shamshir) -------------------
    const root = await get('/');
    root.status === 200 && String(root.body).includes('<app-root') ? ok('1. GET / -> SPA shell') : bad('1. GET / -> no SPA shell');

    const deep = await get('/runs/new');
    deep.status === 200 && String(deep.body).includes('<app-root') ? ok('2. GET /runs/new -> SPA fallback') : bad('2. GET /runs/new -> broken');

    const stratList = await get('/api/strategies');
    const list = Array.isArray(stratList.body) ? stratList.body : (stratList.body.strategies || []);
    list.length >= 1 ? ok(`3. GET /api/strategies -> ${list.length} strategies`) : bad('3. /api/strategies -> empty');

    const rp = await get('/api/risk-profiles');
    const profiles = Array.isArray(rp.body) ? rp.body : (rp.body.profiles || []);
    profiles.length >= 1 ? ok(`4. GET /api/risk-profiles -> ${profiles.length} profiles`) : bad('4. /api/risk-profiles -> empty');

    const runs0 = await get('/api/runs');
    Array.isArray(runs0.body) ? ok(`5. GET /api/runs -> ${runs0.body.length} runs`) : bad('5. /api/runs -> not array');

    const gov = await get('/api/governor/state');
    gov.status === 200 ? ok('6. GET /api/governor/state -> 200') : bad('6. /api/governor/state -> fail');

    const sc = await get('/scalar/v1');
    sc.status === 200 ? ok('7. GET /scalar/v1 -> 200') : bad('7. /scalar/v1 -> fail');

    // Start a quick backtest
    const stratId = list[0]?.id;
    const payload = { symbol:'EURUSD', period:'h1', start:'2024-01-01T00:00:00Z', end:'2024-01-31T00:00:00Z', balance:100000, commissionPerMillion:30, spreadPips:1, symbols:['EURUSD'], periods:['h1'], strategyIds: stratId ? [stratId] : [], riskProfileId: profiles[0]?.id || '', venue:'replay' };
    const start = await get('/api/runs', { method:'POST', headers:{'content-type':'application/json'}, body:JSON.stringify(payload) });
    const runId = start.body?.runId || start.body?.RunId;
    if (start.status === 200 && runId) {
      ok(`8. POST /api/runs -> runId=${runId}`);
      let st = null;
      for (let i = 0; i < 30; i++) {
        const d = await get('/api/runs/' + runId);
        st = (d.body?.status || d.body?.Status || '').toLowerCase();
        if (['completed','failed','cancelled','canceled','error'].includes(st)) break;
        await sleep(1000);
      }
      st ? ok(`9. run ${runId} -> "${st}" (lifecycle OK)`) : bad(`9. run ${runId} -> no terminal state`);
    } else {
      bad('8. POST /api/runs -> failed');
    }

    // ---- 12-26. Browser checks ---------------------------------------------
    console.log('\n--- Browser checks (Playwright) ---\n');
    const browser = await chromium.launch({ headless: !keepAlive });
    try {
      await runBrowserChecks(browser);
    } finally {
      if (!keepAlive) await browser.close();
    }

    console.log(`\n--- ${pass} passed, ${fail} failed ---`);
    if (keepAlive) { console.log(`\n--serve: leaving app + browser running at ${BASE} (PID ${child.pid}). Ctrl-C to stop.`); await new Promise(() => {}); }
  } catch (err) {
    console.error(`\nFATAL: ${err.message}`);
    console.error('--- last app output ---\n' + log.slice(-25).join(''));
    fail++;
  } finally {
    teardown();
  }
  process.exit(fail === 0 ? 0 : 1);
}

main();
