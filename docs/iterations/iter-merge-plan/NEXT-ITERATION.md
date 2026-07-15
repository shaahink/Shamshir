# Next Session — Continuation Handover

**Written:** 2026-07-03 (session close)
**Branch:** `iter/data-mgmt` (active) — child of `iter/tape-trust`
**Gates:** Unit 314/0/6 · Integration 94/0 · Golden 63/63 · build 0 · npm 0

---

## What happened this session

### Tape backtest pipeline (full pass)
1. **Bulk insert** — replaced per-row EF `Add/SaveChanges` with `INSERT OR IGNORE INTO ... VALUES (...),(...),...` in 500-row batches. DateTime format fixed (`yyyy-MM-dd HH:mm:ss.FFFFFFF` to match EF Core's SQLite provider — the `:O` format's `T` vs space lexicographic mismatch caused all range queries to return 0 rows).
2. **Ingest progress** — fire-and-forget job-based ingest (like downloads), progress bar showing files/bars/lines, frontend polls every 2s.
3. **Shards pipeline** — download writes to `data/shards/`, ingest moves to `archive/`, `KeepShards` flag preserves files.
4. **Data Manager** — pending files list, Ingest All button, market data reset in Settings.
5. **Date coverage fix** — date-only comparison (stripped time from firstBar/lastBar) so 22:00 UTC bars pass validation. Tape adapter end date extended by 1 day to include last-day bars.

### Limit orders (across all venues)
6. **Default changed** — `OrderEntryMethod.LimitOffset` (was `Market`), `LimitOffsetPips=2.0`. All new strategies use limit orders.
7. **Tape dual-res expiry** — `decrementExpiry: true` (was `false` — limit orders never expired with M1 exit bars).
8. **cBot expiry** — `expiryBars` now tracked in `_pendingLimits`, `ProcessLimitExpiry()` cancels expired orders.
9. **cBot null-safety** — pending limit orders (where `Position` is null) no longer crash.
10. **Full audit** at `docs/audit/LIMIT-ORDER-AUDIT.md`.

### Tape speed control
11. **Speed 0-10x** — slider on new-backtest and run monitor, `PATCH /api/runs/{id}`, `ManualResetEventSlim` pause.
12. **Pause/resume** — `speed=0` pauses, `speed>0` resumes.

### Build reliability
13. **Angular build race** — `BeforeTargets="ResolveStaticWebAssetsConfiguration"`, `[IO.Path]::GetFullPath`, PS 5.1 `Join-Path` compat.

### Angular fixes
14. **Journal close-fill** — `isCloseFill` detection, close events now visible.
15. **trade-chart-card** — `effect()` replaces `OnChanges`, `OnDestroy` cleanup.
16. **Date range guidance** — safeRange computed + "Snap to available" button.

### Infrastructure
17. **Test DB cleanup** — all 8 WebApplicationFactory fixtures + `TempMarketData` now call `SqliteConnection.ClearAllPools()` + delete `.db-wal`/`.db-shm`.

### Server-side validation
18. **Tape data check** — `RunsController.Start` validates market data exists before starting tape run.
19. **Dead config** — removed `ConnectionStrings:Trading` from `appsettings.json`.

---

## What's still open

Read `docs/OPEN-ISSUES.md` — the single source of truth. Quick summary:

| # | Item | Priority | Golden risk |
|---|------|----------|-------------|
| C1 | Short entries miss half-spread cost (2 lines) | Critical | Yes — needs re-baseline |
| D1 | DB fragmentation — single `TRADING_DB_PATH` | Medium | No |
| D2 | Hardcoded defaults audit | Low | No |
| P1 | Sell-limit halfSpread alignment | Low | No |
| P3 | Limit-order integration tests | Medium | No |
| V2–V5 | cTrader reconcile (owner only) | Owner | No |

---

## 9 commits on `iter/data-mgmt`

| Commit | What |
|--------|------|
| `92bf587` | Shards pipeline, limit expiry P0, journal close-fill, chart effect |
| `653b6e3` | Market data reset in Settings |
| `6fa5728` | Angular build race fix |
| `28219ad` | Tape speed control, pending-shards archive |
| `c1d67c9` | Default to limit orders, cBot expiry + null-safety |
| `60155ca` | Bulk insert perf, ingest progress bar, smart ranges |
| `eb4f8f2` | Bulk INSERT OR IGNORE + test DB cleanup + progress |
| `da80974` | Tape coverage date comparison fix |
| f8c5f3a | Server-side tape validation + date range guidance + docs |

---

## What to read first

1. **`docs/OPEN-ISSUES.md`** — all remaining work
2. **`docs/audit/PROGRESS.md`** — what's been done, gate numbers
3. **`docs/audit/LIMIT-ORDER-AUDIT.md`** — limit order research
4. **`AGENTS.md`** — build commands

## Build commands

```powershell
dotnet build -p:NgProjectDir=C:/nonexistent-skip
npm run build --prefix web-ui
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"
```
