#!/usr/bin/env node
// driver.mjs — Orchestrator: builds, launches .NET app with temp seeded DB,
// runs a replay backtest, runs Playwright E2E tests, tears down.
// The browser test specs live in web-ui/tests/e2e/ — this is the launcher.

import { spawn, spawnSync } from 'node:child_process';
import { setTimeout as sleep } from 'node:timers/promises';
import { existsSync, mkdirSync, copyFileSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { dirname, join, resolve, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, '..', '..', '..');
const WEB = resolve(REPO, 'src', 'TradingEngine.Web');
const WEBUI = resolve(REPO, 'web-ui');
const DLL = resolve(WEB, 'bin', 'Debug', 'net10.0', 'TradingEngine.Web.dll');
const SRC_DB = resolve(WEB, 'data', 'trading.db');

const PORT = process.env.PORT || '5134';
const BASE = `http://localhost:${PORT}`;
const isWin = process.platform === 'win32';

const argv = process.argv.slice(2);
const doBuild = argv.includes('--build');
const keepAlive = argv.includes('--serve');

let tempDir = null;
let testDbPath = null;

function sh(cmd, cmdArgs, cwd) {
  console.log(`\n$ ${cmd} ${cmdArgs.join(' ')}   (cwd: ${cwd})`);
  const r = spawnSync(cmd, cmdArgs, { cwd, stdio: 'inherit', shell: isWin });
  if (r.status !== 0) { console.error(`Command failed (exit ${r.status})`); process.exit(1); }
}

function killTree(pid) {
  if (!pid) return;
  if (isWin) spawnSync('taskkill', ['/T', '/F', '/PID', String(pid)], { stdio: 'ignore', shell: true });
  else { try { process.kill(-pid, 'SIGKILL'); } catch { try { process.kill(pid, 'SIGKILL'); } catch {} } }
}

async function get(path, opts = {}) {
  const res = await fetch(BASE + path, opts);
  const ct = res.headers.get('content-type') || '';
  const body = ct.includes('json') ? await res.json() : await res.text();
  return { status: res.status, ct, body };
}

// ── Prepare temp DB with seeded bars ──────────────────────────────────────

function setupTempDb() {
  tempDir = join(tmpdir(), 'shamshir-e2e-' + Date.now());
  mkdirSync(tempDir, { recursive: true });

  // Copy the source DB (already has schema + strategy configs from EF migrations)
  testDbPath = join(tempDir, 'trading.db');
  if (existsSync(SRC_DB)) {
    copyFileSync(SRC_DB, testDbPath);
    console.log(`Copied source DB to temp: ${testDbPath}`);
  } else {
    // No source DB — let EF create it
    writeFileSync(testDbPath, '');
    console.log(`Fresh temp DB at: ${testDbPath}`);
  }

  // Seed bars from CSV into the temp DB
  seedBars(testDbPath);
  return testDbPath;
}

function seedBars(dbPath) {
  const csvPath = join(REPO, 'tests', 'data', 'eurusd-h1-bull-2024.csv');
  if (!existsSync(csvPath)) {
    console.warn(`CSV not found: ${csvPath} — skipping bar seed`);
    return;
  }

  const lines = readFileSync(csvPath, 'utf-8').split('\n').filter(l => l.trim());
  if (lines.length < 2) return;

  // Build SQL insert statements
  const inserts = [];
  for (let i = 1; i < lines.length; i++) {
    const cols = lines[i].split(',');
    if (cols.length < 6) continue;
    const id = crypto.randomUUID();
    const dt = cols[0].replace(' ', 'T') + '.000';
    inserts.push(
      `INSERT OR IGNORE INTO Bars (Id, RunId, Symbol, Timeframe, OpenTimeUtc, Open, High, Low, Close, Volume) ` +
      `VALUES ('${id}','','EURUSD','H1','${dt}',${cols[1]},${cols[2]},${cols[3]},${cols[4]},${cols[5]});`
    );
  }

  // Write SQL to a temp file and pipe to sqlite3
  const sqlPath = join(tempDir, 'seed.sql');
  writeFileSync(sqlPath, inserts.join('\n'));
  const count = inserts.length;

  try {
    spawnSync('sqlite3', [dbPath], { input: readFileSync(sqlPath), stdio: ['pipe', 'inherit', 'inherit'], shell: isWin });
    console.log(`Seeded ${count} bars into ${dbPath}`);
  } catch {
    console.warn('sqlite3 CLI not available — bars not seeded. Install: choco install sqlite');
  } finally {
    try { rmSync(sqlPath); } catch {}
  }
}

// ── Start a replay backtest that will find the seeded bars ─────────────────

async function startBacktest(strategyId) {
  console.log('\n--- Starting replay backtest ---');
  const payload = {
    symbol: 'EURUSD', period: 'h1',
    start: '2024-01-01T00:00:00Z', end: '2024-01-31T00:00:00Z',
    balance: 100000, commissionPerMillion: 30, spreadPips: 1,
    symbols: ['EURUSD'], periods: ['h1'],
    strategyIds: strategyId ? [strategyId] : [],
    riskProfileId: 'standard',
    venue: 'replay',
  };
  const res = await get('/api/runs', {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(payload),
  });
  const runId = res.body?.runId || res.body?.RunId;
  if (!runId) { console.warn('Could not start backtest'); return null; }

  console.log(`Backtest started: ${runId}`);
  for (let i = 0; i < 120; i++) {
    const d = await get('/api/runs/' + runId);
    const st = (d.body?.status || d.body?.Status || '').toLowerCase();
    if (['completed', 'failed', 'cancelled', 'canceled', 'error'].includes(st)) {
      console.log(`Backtest ${runId} finished: ${st} (trades=${d.body?.totalTrades || '?'})`);
      return runId;
    }
    await sleep(2000);
  }
  console.warn(`Backtest ${runId} timed out`);
  return runId;
}

// ── Main ──────────────────────────────────────────────────────────────────

async function main() {
  if (doBuild) {
    sh('npm', ['run', 'build'], WEBUI);
    sh('dotnet', ['build', WEB, '-c', 'Debug'], REPO);
  }
  if (!existsSync(DLL)) {
    console.error(`\nNo build at ${DLL}\nRun with --build first.`);
    process.exit(1);
  }

  // Setup temp DB with seeded bars
  const dbPath = setupTempDb();
  const dataDir = dirname(dbPath);

  console.log(`\nLaunching ${DLL} on ${BASE} with DB: ${dbPath} ...`);
  const child = spawn('dotnet', [DLL], {
    cwd: WEB,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: BASE,
      'Persistence__DbPath': dbPath,
      'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command': 'Warning',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: !isWin,
  });
  child.stdout.on('data', (b) => process.stdout.write(`[web] ${b}`));
  child.stderr.on('data', (b) => process.stderr.write(`[web] ${b}`));

  const teardown = () => {
    if (!keepAlive) killTree(child.pid);
    if (tempDir) { try { rmSync(tempDir, { recursive: true }); } catch {} }
  };
  process.on('exit', teardown);
  process.on('SIGINT', () => { teardown(); process.exit(130); });

  try {
    let up = false;
    for (let i = 0; i < 60; i++) {
      if (child.exitCode !== null) throw new Error(`Process exited early (code ${child.exitCode})`);
      try { const r = await fetch(BASE + '/api/strategies'); if (r.status === 200) { up = true; break; } } catch {}
      await sleep(1000);
    }
    if (!up) throw new Error(`App did not answer on ${BASE} within 60s`);

    // Start a backtest with seeded bars
    const seededRunId = await startBacktest('bb-squeeze');

    console.log(`\n--- Running Playwright E2E tests ---\n`);
    const pw = spawn('npx', ['playwright', 'test'], {
      cwd: WEBUI,
      stdio: 'inherit',
      shell: isWin,
      env: { ...process.env, BASE_URL: BASE, SEEDED_RUN_ID: seededRunId || '' },
    });
    await new Promise((resolve, reject) => {
      pw.on('close', (code) => code === 0 ? resolve() : reject(new Error(`Playwright exited ${code}`)));
    });

    console.log(`\n--- E2E tests passed ---`);
    if (keepAlive) {
      console.log(`\n--serve: leaving app running at ${BASE} (PID ${child.pid}, DB ${dbPath}). Ctrl-C to stop.`);
      await new Promise(() => {});
    }
  } catch (err) {
    console.error(`\nFATAL: ${err.message}`);
    process.exit(1);
  } finally {
    teardown();
  }
}

main();
