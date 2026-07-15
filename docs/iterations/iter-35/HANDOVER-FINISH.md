# Iter-35 Finish — HANDOVER

**Branch:** `iter/35-kernel-finish-ab` (26 commits on top of `iter/35-kernel`)
**Date:** 2026-06-19
**State:** Parts A, B, C, D, E delivered. All suites green.

---

## 1. What was delivered

### Kernel — single production authority
- Order gate: `KernelOrderGate` → `PreTradeGate` → `KernelSizing`
- Bar exits: `EngineReducer.DetectSlTpExit` (SimulateBarExitsAsync delegates)
- Equity/breach: `Kernel.EvaluateDrawdownBreach` (toggle-gated, weekly/monthly included)
- Resets: `ProtectionState.ClearsOn` matrix (Never/AccountReset/NextTradingDay)
- Governor: `GovernorMachine` implements `ITradingGovernor`
- Sizing: `PositionSizer`/`DrawdownScaler` deleted, callers → `KernelSizing`
- Determinism: `PositionLifecycle` no longer mints `Guid.NewGuid()`, body-scan test

### Web lifecycle (10 bugs)
C11, C12, C13, H10, H22, H23, H24, H25, H26, H27

### Data-loss stop (6 bugs)
C9, C10, H18, H19, H21, M16

### Trade chart + reporting (12 bugs)
NEW-6, M11, M12, H20, H28, H29, H30, M20, M21, L1, L2, L3

### Venue + cTrader (8 bugs)
C1, C2, M1, M2, M5, H11, H15, H16, M19

### Audit fixes (4 bugs)
M18 (GovernorOptions), ProtectionState MonthlyDD + ResetPolicy, multi-boundary roll, M5/M6/M9/M13

### Angular UI fixes (Phase A-E)
- Trade-list cost columns (Gross/Comm/Swap)
- SL/TP markers on trade-detail chart
- Data-table rowClick → trade navigation
- Violations JSON parsing in journal
- Strategy detail formatted config
- Settings dynamic data
- Breach banner recovery
- `toFixed` null-guards (9 template guards)

### E2E infrastructure
- Playwright + Chromium, `web-ui/tests/e2e/ui-smoke.spec.ts` (13 tests)
- `npm run e2e` in web-ui/package.json
- Temp DB + seed bars (2000 EURUSD H1 → 16 real trades)
- `Persistence__DbPath` isolation

---

## 2. Verification

| Suite | Result |
|-------|--------|
| `dotnet build` | 0 errors |
| Unit | 209 pass, 4 skip |
| Golden replay | 18 pass |
| Architecture | 4 pass |
| E2E (Playwright) | 13/13 pass |

---

## 3. Remaining (documented)

### Angular/frontend (all logged in OPEN-ISSUES.md)

| Item | Reason not done |
|------|-----------------|
| A3 — Per-bar "why rejected" UI | Needs backend endpoint for bar-evaluations + Angular tab |
| A4 — Unified journal (orders+fills joined) | Needs journal API enrichment with orderId grouping |
| C1 — Venue status page | New Angular component + SignalR feed |
| C3 — Live open-positions table | New SignalR fields or API polling |
| C3 — Download journal (NDJSON) button | Backend endpoint exists (`/kernel-journal/export`), needs UI button |
| E1 — Strategy read-only formatted view | DONE (Phase D) |
| E2 — Per-run strategy override UI | Needs New-Backtest component expansion |
| E3 — Config validate-before-save | Needs validation endpoint |
| 32-P4/P5/P6 — Config UX | Significant Angular work (browse/edit/override/export) |

### Backend (low severity)

| Item | Reason not done |
|------|-----------------|
| M3 (cBot Stop thread) | Owner live-verify — needs cTrader platform runtime |
| H11 (RiskManager race) | Live path only |
| H13 (NetMQ counters) | Telemetry, not correctness |
| M8 (DrawdownVelocity) | Updates correctly at daily reset (day-over-day velocity) |
| M14 (PublishAsync exceptions) | Known fire-and-forget pattern |
| M15 (TradeResults dedup) | Needs EF migration + unique constraint |
| L4 (cBot 5s sleep) | cTrader OnStart lifecycle |
| UNF01-06, MIN03-05 | Cosmetic/optimization |
| OBS01-03 | Observability — needs design |

---

## 4. cTrader E2E verification

The cTrader E2E tests run without further config. Credentials are read from:
- `src/TradingEngine.Web/appsettings.Development.json` under `CTrader.CtId` / `CTrader.PwdFile` / `CTrader.Account`
- OR environment variables `CTrader__CtId` / `CTrader__PwdFile` / `CTrader__Account`

**To run:**
```powershell
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"
```

This exercises the full stack: engine host + `CTraderBrokerAdapter` + real NetMQ + `ctrader-cli.exe` running the cBot inside cTrader. The `CtraderDiffHarness` joins cBot's `shamshir-report.json` (has `clientOrderId`) to DB `TradeResults` for per-trade cost reconciliation.

cTrader's own `--report-json` CLI flag is known to crash — our `ShamshirTradeLogger` in the cBot writes `shamshir-report.json` independently. The `CtraderReportHarvester` uses cTrader's native `report.html` as fallback only.

---

## 5. How to run

```powershell
# Full build
dotnet build

# Tests
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Simulation --filter "Category!=E2E&Category!=Slow&RequiresCTrader!=true"

# E2E (headless browser — requires `npx playwright install chromium` one-time)
node .claude/skills/shamshir-ui/driver.mjs --build

# cTrader E2E (requires credentials)
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"
```

## 6. Skills available

| Skill | What it does |
|-------|-------------|
| `run-shamshir` | Build, launch, smoke-test the web app (11 API checks) |
| `shamshir-ui` | Build, launch, seed bars, run E2E with Playwright (13 browser checks) |
| `shamshir-e2e` | cTrader E2E harness, diff, logging chain reference |
| `shamshir-kernel` | Kernel architecture, determinism rules, cutover patterns |
