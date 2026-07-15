# X4 — Data-manager auto-sync + cTrader consolidation

Worktree `C:\code\shamshir-x4`, branch `iter/alpha-loop-x4` off `2da944c` (clean-from-latest; zero
file overlap with the other agent's uncommitted X2/X3 — verified). Runs on port **5135** (their 5134
is untouched). Own copy of the 1.2 GB `marketdata.db` snapshot; the other agent's `trading.db` is not
shared. Owner brief: *post-2020 data with on-by-default background auto-sync, one-owner cTrader,
dynamic ports, parallel cTrader, truthful UI, delivered E2E against **actual** cTrader.*

## Architecture decision — one cTrader owner

Today three code paths drive cTrader on **three hardcoded port pairs** and a private serial lane:
`BacktestOrchestrator` (real runs, `_ctraderSemaphore(1,1)`, already dynamic ports via `AllocatePorts`),
`CTraderListenService` (15555/6, persistent desktop capture), `DownloadJobService` (15562/3, download).

**New `CTraderProcessOwner` (singleton) consolidates them:**
- **Dynamic ports** — reuse `AllocatePorts()` (loopback `:0`); kill the hardcoded 15562/3.
- **One shared lane** — `SemaphoreSlim(max)`, default **2** (parallel), configurable, `1`=serial.
  Both backtest and download acquire from it → downloads never race backtests; parallel up to the bound.
- **Owned-PID reaping, NOT image-name.** Finding: `KillCtraderProcessTreeAsync` kills every `ctrader-cli`
  by image name — safe *only* "at most one run at a time." Under parallel cTrader **or** alongside the
  other agent's cTrader-cli it cross-kills. Both launchers already `Kill(entireProcessTree)` their own
  process on cancel; `ChildProcessReaper` (Job Object, kill-on-close) is the crash net. The owner reaps
  only PIDs it launched → parallel-safe and never touches another process's cTrader.

Performance pick: **bounded parallel instances** (not a cBot multi-symbol rewrite) — smaller, reuses the
per-symbol record path.

## Persisted state — marketdata DB context (no trading-DB migration → stays conflict-free)

`MarketDataDbContext` uses `EnsureCreated` ("no migration history by design"). Add `SyncWatchlist` +
`SyncJob` tables there via idempotent `CREATE TABLE IF NOT EXISTS` at startup (EnsureCreated won't add a
table to the existing 1.2 GB DB). Ingest is already idempotent (`INSERT OR IGNORE` on unique
`(Symbol,Timeframe,OpenTimeUtc)`), so every re-sync/retry is free.

## Phases (each with a gate; build+unit per phase)

| # | Phase | Gate |
|---|---|---|
| X4.0 | `CTraderProcessOwner`: dynamic ports + shared bounded lane + owned-PID reaper; wire download path, then orchestrator | build green; existing cTrader backtest still runs (live spot-check); download uses dynamic ports |
| X4.1 | Marketdata schema `SyncWatchlist`/`SyncJob` + idempotent DDL | tables exist on copied DB; app starts |
| X4.2 | Coverage + market-hours gap engine `GET /api/data-manager/coverage` (reuse `InventoryCoverage` + `DataQualityValidator`) | EURUSD H1 full-year gaps ≈ 0; deleted slice shows as gap |
| X4.3 | Auto-sync `BackgroundService`: watchlist → detect tail/interior gap → download → ingest → archive; persisted, resilient, restart-safe | stale cell self-heals to up-to-date against copied DB + shard backlog |
| X4.4 | UI: coverage grid, truthful live per-cell status, "Sync all → latest", per-cell sync; shard-lifecycle reaper for the ~15 orphan dirs | e2e: grid renders real state; a sync transitions truthfully |
| X4.5 | **Live cTrader E2E** — real download→ingest→sync of a small window proves the whole path | actual cTrader bars land in DB via auto-sync; parity of the record path intact |

## Adopted defaults (owner-approved / redline any)
- OBC = on-by-default background auto-sync service (persisted, self-healing).
- Watchlist default = every symbol×TF already in inventory kept current; UI pins/unpins + backfill-to-2020.
- Live cTrader firing is ON (owner chose dynamic-ports+consolidation over gating); pool default 2.
