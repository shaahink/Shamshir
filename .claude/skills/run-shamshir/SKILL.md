---
name: run-shamshir
description: Build, launch, smoke-test, and drive the Shamshir trading-engine web app (Angular SPA + .NET API served single-origin). Use when asked to run, start, serve, build, smoke-test, or confirm a change works in the actual running Shamshir app (not just unit tests) — e.g. "run Shamshir", "start the web app", "is the backtest UI working", "drive a backtest run".
---

# Run Shamshir (web app)

Shamshir's UI is an **Angular 19 SPA served single-origin by the .NET app**
(`TradingEngine.Web`). One process serves: the built SPA from `wwwroot`, the
Scalar API explorer at `/scalar/v1`, the REST API at `/api/*`, and the SignalR
hub at `/hubs/run`. You do **not** run `ng serve` separately for normal use.

There is **no headless browser in this container**, so the agent path does not
screenshot the SPA. Instead the driver proves the app is alive end-to-end with
`fetch`: it serves the SPA shell, serves Scalar, answers every REST endpoint the
SPA depends on (strategies + full config from the DB, risk profiles, runs,
governor), and **drives a real run through its lifecycle** (POST a run, poll to a
terminal state) — the exact path the "New Backtest" page uses.

> Paths below are relative to the repo root (`<unit>/`), which is also the
> directory holding this skill at `.claude/skills/run-shamshir/`.

## Prerequisites

- **.NET SDK 10** (`dotnet --version` → `10.0.301` here) and **Node 22**
  (`node --version` → `v22.18.0` here). Both were already installed; no
  `apt-get` was needed.
- No browser, no Docker, no cTrader required for the agent path.

## Run (agent path) — the driver

The driver lives at `.claude/skills/run-shamshir/driver.mjs`. It launches the
already-built app headless, runs 11 checks, and tears the app down.

```bash
node .claude/skills/run-shamshir/driver.mjs
```

Verified output this session (last lines):

```
  ✓ GET /  -> 200 html, contains <app-root> (SPA shell)
  ✓ GET /runs/new -> 200 SPA fallback (client routing works)
  ✓ GET /chunk-G6QXTCEI.js -> 200 js (hashed asset served)
  ✓ GET /scalar/v1 -> 200 (API explorer served)
  ✓ GET /api/strategies -> 9 strategies from DB (first: bb-squeeze)
  ✓ GET /api/strategies/bb-squeeze -> 200 full config (params from DB)
  ✓ GET /api/risk-profiles -> 4 profiles (first: aggressive)
  ✓ GET /api/runs -> 200 array (6 prior runs)
  ✓ GET /api/governor/state -> 200
  ✓ POST /api/runs (venue=replay) -> 200 runId=c3497790
  ✓ run c3497790 reached terminal state: "failed" (lifecycle + persistence OK)
--- 11 passed, 0 failed ---
```

Exit code 0 = all checks passed. Flags:

```bash
# From a clean tree (builds the SPA into wwwroot, then `dotnet build`, then smokes):
node .claude/skills/run-shamshir/driver.mjs --build

# Leave the app running so you can poke it yourself (Ctrl-C to stop), custom port:
PORT=5177 node .claude/skills/run-shamshir/driver.mjs --serve
```

While `--serve` is up, hit it directly (verified this session):

```bash
curl -s -o /dev/null -w "GET / -> %{http_code}\n" http://localhost:5177/
curl -s http://localhost:5177/api/strategies
```

## Build (only needed once, or after a code change)

The driver's `--build` does both of these. To run them by hand:

```bash
# Angular SPA -> src/TradingEngine.Web/wwwroot  (run from web-ui/)
cd web-ui && npm run build

# .NET backend (the Tailwind "Empty sub-selector" warnings above are harmless)
dotnet build src/TradingEngine.Web -c Debug
```

Both were run this session: `npm run build` ends with
`Output location: ...\src\TradingEngine.Web\wwwroot`; the .NET build ends with
`Build succeeded. 0 Warning(s) 0 Error(s)`.

## Run (human path)

To open the SPA in a real browser on your own machine (useless headless — it
just waits for a window):

```bash
dotnet run --project src/TradingEngine.Web --launch-profile https
```

This serves `/` (the SPA) on `http://localhost:5134` / `https://localhost:7108`
and auto-opens the browser. For live Angular dev with hot reload, use the
`ng-serve` launch profile instead (sets `Dev:NgServe=true`, proxies to `:4200`).

## Gotchas (things that bit me this session)

- **No headless browser here.** `chromium-cli`/`chrome`/`chromium` are all
  absent, so the SPA is verified via its served HTML shell (`<app-root>`), a
  hashed JS asset, and the REST API — **not** a screenshot. The driver is
  `fetch`-based for exactly this reason.
- **A `replay` run with no stored bars ends in `failed` ("No bars found") — that
  is the EXPECTED terminal state in this container, and the driver treats it as a
  pass.** Bars come from **cTrader over NetMQ**, which isn't available here, and
  bars are deliberately **never seeded** (no-bars-after-a-real-run = a storage
  bug, by project policy). The `failed`/no-bars path still exercises DB-config
  build, strategy instantiation, the venue switch, run-record persistence, and
  progress polling. To get actual trades/equity/journal you need a real cTrader
  run (`venue: "ctrader"`).
- **Kill the running host before rebuilding** or `dotnet build` fails with file
  locks (MSB3026/MSB3027/MSB3021). The DLL host shows up as `dotnet.exe`, not
  `TradingEngine.Web.exe`. The driver tears down its own child, but a stray
  manual run or an IDE instance will hold the lock — kill it:
  ```bash
  powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { \$_.CommandLine -like '*TradingEngine.Web.dll*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }"
  ```
- **`--serve` does not auto-kill on `kill <node-pid>`.** In serve mode teardown
  is intentionally skipped, so killing the Node process orphans the .NET child.
  Use foreground `Ctrl-C`, or the CIM kill above.
- **The app must launch with cwd = `src/TradingEngine.Web`** so it finds
  `data/trading.db` and `wwwroot`. The driver sets this; if you launch the DLL by
  hand, `cd` there first.
- **DB at `src/TradingEngine.Web/data/trading.db`** is pre-seeded (9 strategies,
  4 risk profiles) — strategy params and risk profiles are read from the DB, not
  JSON, at request time.

## Fast backend loop (2026-07-15)

- Every backend change costs kill → build → relaunch → re-warm (locks, see Gotchas). Do it as ONE
  step: `scripts/dev-restart.ps1` once iter-dx-speed D3 lands
  (`docs/iterations/iter-dx-speed/PLAN.md`); until then, chain the CIM kill + build + launch in a
  single command instead of interleaving them with probes.
- **EF Core Debug logging floods the console** (query plans, thousands of lines per request) and
  buries the run's progress lines — set `Logging:LogLevel:Microsoft.EntityFrameworkCore` to
  `Warning` in `appsettings.Development.json` while iterating.
- **EF migrations need the context named:** `dotnet ef migrations add <Name> --project
  src/TradingEngine.Infrastructure --startup-project src/TradingEngine.Web --context
  TradingDbContext` — omitting `--context` fails when multiple contexts resolve.
- DB facts that cost probes to rediscover: the journal table is named **`Journal`** (singular);
  money columns are stored as TEXT.

## Troubleshooting

- **Driver prints `Process exited early ... Port in use?`** → another instance is
  bound to the port. Kill it (CIM command above) or run with a different
  `PORT=`.
- **`GET / -> 404`** → `wwwroot` is empty / SPA not built. Run
  `node .claude/skills/run-shamshir/driver.mjs --build` (or `cd web-ui && npm run build`).
- **Build fails with MSB3026/MSB3027/MSB3021** → a running host is locking the
  output DLL. Kill it with the CIM command in Gotchas, then rebuild.
