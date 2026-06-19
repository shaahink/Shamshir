#!/usr/bin/env node
// driver.mjs — Orchestrator: builds, launches the .NET app, runs Playwright E2E tests, tears down.
// The actual browser test specs live in web-ui/tests/e2e/ — this is just the launcher.

import { spawn, spawnSync } from 'node:child_process';
import { setTimeout as sleep } from 'node:timers/promises';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, '..', '..', '..');
const WEB = resolve(REPO, 'src', 'TradingEngine.Web');
const WEBUI = resolve(REPO, 'web-ui');
const DLL = resolve(WEB, 'bin', 'Debug', 'net10.0', 'TradingEngine.Web.dll');

const PORT = process.env.PORT || '5134';
const BASE = `http://localhost:${PORT}`;
const isWin = process.platform === 'win32';

const argv = process.argv.slice(2);
const doBuild = argv.includes('--build');
const keepAlive = argv.includes('--serve');

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

async function main() {
  if (doBuild) {
    sh('npm', ['run', 'build'], WEBUI);
    sh('dotnet', ['build', WEB, '-c', 'Debug'], REPO);
  }
  if (!existsSync(DLL)) {
    console.error(`\nNo build at ${DLL}\nRun with --build first.`);
    process.exit(1);
  }

  console.log(`\nLaunching ${DLL} on ${BASE} ...`);
  const child = spawn('dotnet', [DLL], {
    cwd: WEB,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: BASE, 'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command': 'Warning' },
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: !isWin,
  });
  child.stdout.on('data', (b) => process.stdout.write(`[web] ${b}`));
  child.stderr.on('data', (b) => process.stderr.write(`[web] ${b}`));

  const teardown = () => { if (!keepAlive) killTree(child.pid); };
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

    console.log(`\n--- App is up. Running Playwright E2E tests ---\n`);

    // Run Playwright tests from the web-ui project
    const pw = spawn('npx', ['playwright', 'test'], {
      cwd: WEBUI,
      stdio: 'inherit',
      shell: isWin,
      env: { ...process.env, BASE_URL: BASE },
    });
    await new Promise((resolve, reject) => {
      pw.on('close', (code) => code === 0 ? resolve() : reject(new Error(`Playwright exited ${code}`)));
    });

    console.log(`\n--- E2E tests passed ---`);
    if (keepAlive) {
      console.log(`\n--serve: leaving app running at ${BASE} (PID ${child.pid}). Ctrl-C to stop.`);
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
