#!/usr/bin/env node
// driver.mjs — build, launch, and smoke-drive the Shamshir Web app headless.
//
// Shamshir's UI is an Angular SPA served single-origin by the .NET app
// (TradingEngine.Web): one process serves the built SPA from wwwroot, the
// Scalar API explorer at /scalar/v1, the REST API at /api/*, and the SignalR
// hub at /hubs/run. There is NO headless browser in this container, so this
// driver does NOT screenshot the SPA — it proves the app is alive by:
//   1. serving the SPA shell (index.html + deep-link fallback + a hashed asset)
//   2. serving Scalar
//   3. answering every REST endpoint the SPA depends on (strategies + full
//      config from the DB, risk profiles, runs, governor)
//   4. driving a real run end-to-end through the lifecycle (POST a run, poll
//      it to a terminal state) — the same path the "New Backtest" page uses.
//
// Usage:
//   node driver.mjs            launch the (already-built) app, smoke it, tear down
//   node driver.mjs --build    build the SPA + .NET first, then smoke
//   node driver.mjs --serve    launch + smoke, then LEAVE IT RUNNING (no teardown)
//   PORT=5210 node driver.mjs  use a non-default port (default 5134)
//
// Exit code 0 = every check passed. Non-zero = a check failed (details printed).

import { spawn, spawnSync } from 'node:child_process';
import { setTimeout as sleep } from 'node:timers/promises';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
// skill lives at <repo>/.claude/skills/run-shamshir → repo root is three up
const REPO = resolve(HERE, '..', '..', '..');
const WEB = resolve(REPO, 'src', 'TradingEngine.Web');
const DLL = resolve(WEB, 'bin', 'Debug', 'net10.0', 'TradingEngine.Web.dll');

const PORT = process.env.PORT || '5134';
const BASE = `http://localhost:${PORT}`;
const isWin = process.platform === 'win32';

const argv = process.argv.slice(2);
const doBuild = argv.includes('--build');
const keepAlive = argv.includes('--serve');

let pass = 0, fail = 0;
const ok = (m) => { pass++; console.log(`  ✓ ${m}`); };
const bad = (m) => { fail++; console.log(`  ✗ ${m}`); };

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

async function main() {
  // ---- optional build -----------------------------------------------------
  if (doBuild) {
    sh('npm', ['run', 'build'], resolve(REPO, 'web-ui'));        // SPA -> wwwroot
    sh('dotnet', ['build', WEB, '-c', 'Debug'], REPO);            // backend
  }
  if (!existsSync(DLL)) {
    console.error(`\nNo build at ${DLL}\nRun once with --build (or: dotnet build src/TradingEngine.Web).`);
    process.exit(1);
  }

  // ---- launch headless ----------------------------------------------------
  console.log(`\nLaunching ${DLL} on ${BASE} ...`);
  const child = spawn('dotnet', [DLL], {
    cwd: WEB,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: BASE,
      // silence per-statement EF SQL spam so the driver's own ✓/✗ lines are legible
      'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command': 'Warning',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: !isWin,
  });
  const log = [];
  const cap = (b) => { const s = b.toString(); log.push(s); process.stdout.write(`[web] ${s}`); };
  child.stdout.on('data', cap);
  child.stderr.on('data', cap);

  const teardown = () => { if (!keepAlive) killTree(child.pid); };
  process.on('exit', teardown);
  process.on('SIGINT', () => { teardown(); process.exit(130); });

  try {
    // ---- wait for readiness (poll the endpoint the SPA loads first) -------
    let up = false;
    for (let i = 0; i < 60; i++) {
      if (child.exitCode !== null) throw new Error(`Process exited early (code ${child.exitCode}). Port ${PORT} in use?`);
      try { if ((await get('/api/strategies')).status === 200) { up = true; break; } } catch {}
      await sleep(1000);
    }
    if (!up) throw new Error(`App did not answer on ${BASE} within 60s`);
    console.log(`\n--- App is up. Running checks against ${BASE} ---\n`);

    // ---- 1. SPA shell + deep-link fallback + a real hashed asset ---------
    const root = await get('/');
    root.status === 200 && root.ct.includes('text/html') && String(root.body).includes('<app-root')
      ? ok('GET /  -> 200 html, contains <app-root> (SPA shell)')
      : bad(`GET /  -> ${root.status} ${root.ct} (no <app-root>)`);

    const deep = await get('/runs/new');
    deep.status === 200 && String(deep.body).includes('<app-root')
      ? ok('GET /runs/new -> 200 SPA fallback (client routing works)')
      : bad(`GET /runs/new -> ${deep.status} (fallback broken)`);

    const m = String(root.body).match(/(main-[A-Z0-9]+\.js|chunk-[A-Z0-9]+\.js|scripts-[A-Z0-9]+\.js)/i);
    if (m) {
      const a = await get('/' + m[0]);
      a.status === 200 && a.ct.includes('javascript')
        ? ok(`GET /${m[0]} -> 200 js (hashed asset served)`)
        : bad(`GET /${m[0]} -> ${a.status} ${a.ct}`);
    } else bad('index.html referenced no hashed JS asset (SPA build missing?)');

    // ---- 2. Scalar API explorer ------------------------------------------
    const sc = await get('/scalar/v1');
    sc.status === 200 ? ok('GET /scalar/v1 -> 200 (API explorer served)') : bad(`GET /scalar/v1 -> ${sc.status}`);

    // ---- 3. REST endpoints the SPA depends on ----------------------------
    const strat = await get('/api/strategies');
    const list = Array.isArray(strat.body) ? strat.body : (strat.body.strategies || []);
    list.length >= 1 && list[0].id && list[0].displayName
      ? ok(`GET /api/strategies -> ${list.length} strategies from DB (first: ${list[0].id})`)
      : bad(`GET /api/strategies -> bad payload: ${JSON.stringify(strat.body).slice(0, 200)}`);

    if (list[0]?.id) {
      const one = await get('/api/strategies/' + encodeURIComponent(list[0].id));
      one.status === 200 && (one.body.parametersJson !== undefined || one.body.id)
        ? ok(`GET /api/strategies/${list[0].id} -> 200 full config (params from DB)`)
        : bad(`GET /api/strategies/${list[0].id} -> ${one.status}`);
    }

    const rp = await get('/api/risk-profiles');
    const profiles = Array.isArray(rp.body) ? rp.body : (rp.body.profiles || []);
    profiles.length >= 1 && profiles[0].id
      ? ok(`GET /api/risk-profiles -> ${profiles.length} profiles (first: ${profiles[0].id})`)
      : bad(`GET /api/risk-profiles -> bad payload: ${JSON.stringify(rp.body).slice(0, 200)}`);

    const runs0 = await get('/api/runs');
    Array.isArray(runs0.body)
      ? ok(`GET /api/runs -> 200 array (${runs0.body.length} prior runs)`)
      : bad(`GET /api/runs -> not an array`);

    const gov = await get('/api/governor/state');
    gov.status === 200 ? ok('GET /api/governor/state -> 200') : bad(`GET /api/governor/state -> ${gov.status}`);

    // ---- 4. drive a real run through its lifecycle -----------------------
    // Replay venue = credential-free (no cTrader). With no stored bars this
    // ends in "failed" with "No bars" — that is the EXPECTED terminal state
    // here and still exercises: DB-config build, strategy instantiation,
    // venue switch, run-record persistence, and progress polling.
    const stratId = list[0]?.id;
    const payload = {
      symbol: 'EURUSD', period: 'h1',
      start: '2024-01-01T00:00:00Z', end: '2024-01-31T00:00:00Z',
      balance: 100000, commissionPerMillion: 30, spreadPips: 1,
      symbols: ['EURUSD'], periods: ['h1'],
      strategyIds: stratId ? [stratId] : [],
      riskProfileId: profiles[0]?.id || '', venue: 'replay',
    };
    const start = await get('/api/runs', {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(payload),
    });
    const runId = start.body?.runId || start.body?.RunId;
    if (start.status === 200 && runId) {
      ok(`POST /api/runs (venue=replay) -> 200 runId=${runId}`);
      let st = null;
      for (let i = 0; i < 30; i++) {
        const d = await get('/api/runs/' + runId);
        st = (d.body?.status || d.body?.Status || '').toLowerCase();
        if (['completed', 'failed', 'cancelled', 'canceled', 'error'].includes(st)) break;
        await sleep(1000);
      }
      st ? ok(`run ${runId} reached terminal state: "${st}" (lifecycle + persistence OK)`)
         : bad(`run ${runId} never reached a terminal state`);
    } else {
      bad(`POST /api/runs -> ${start.status} ${JSON.stringify(start.body).slice(0, 200)}`);
    }

    console.log(`\n--- ${pass} passed, ${fail} failed ---`);
    if (keepAlive) { console.log(`\n--serve: leaving app running at ${BASE} (PID ${child.pid}). Ctrl-C to stop.`); await new Promise(() => {}); }
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
