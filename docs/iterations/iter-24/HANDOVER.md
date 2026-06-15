# Iter-24 Handover — 2026-06-15

**Branch**: `iter/23-close-gap` (iter-24 work sits on top). **Read first**: `SYSTEM-MODEL.md`
(how the engine is glued + findings) then `PLAN.md` (phases). This file = where we are + your queue.

## Working rules
- Failing-test-first. Build + the fast suites green at **every** commit.
- Fast suites (trust these; <5s, no IHost, no orphans):
  `dotnet test tests/TradingEngine.Tests.Unit` (163), `--filter "FullyQualifiedName~Golden"` (7),
  `--filter "FullyQualifiedName~TradingLoopDirect"` (1). Current baseline: **163 / 8 green**.
- Do **not** trust `ReplayTestHarness`/`CtraderTestHarness` timing — IHost ~60s floor, mocks RiskManager.
  Build the fast `EngineHarnessBuilder` instead (Phase 0a). Kill stray `ctrader-cli` before cTrader tests.
- Full-solution build fails on `aspire/AppHost` (NU1903 MessagePack) — build test projects directly, don't "fix" it.

## What's DONE this iteration (commits iter24-p1 … p6)
- **One trading loop**: `BacktestDriver` deleted; live+backtest share `EngineWorker`→`EngineRunner`.
- **`TradingLoop`** extracted — per-bar body is a first-class, IHost-free unit (drive it directly; see
  `TradingLoopDirectTests`). Pure decide-and-dispatch (no execution side-effects).
- **`EngineRunner`** split out of `EngineWorker` (now a ~16-line BackgroundService shell); 5 dead fields gone.
- **Live concurrency fixed**: lean tick hot path; single serialized execution consumer
  (`MarketEventSource.ConsumeExecutionsAsync`); `PositionTracker` mutators serialized with a `SemaphoreSlim`.
- **Venue-decoupled**: `EngineRunner` no longer type-sniffs concrete adapters — four behaviours are
  default-no-op `IBrokerAdapter` hooks (`RegisterConnectedHandler`/`OnTickObserved`/`OnBarObserved`/`CompleteBarAsync(ct)`).
- **Money**: M1 — venue-authoritative PnL (net/commission/swap) now flows to `TradeResult`.
  V4 — exec dedup is a bounded LRU.
- **Test infra**: `ChildProcessReaper` (Job Object) kills orphan `ctrader-cli`; `EngineHarnessBuilder` skeleton.

## Your queue (highest value first)
1. **Phase 0a — finish `EngineHarnessBuilder.BuildAsync`** (real `RiskManager` + `WireRiskRules`, fake
   broker, deterministic stop). Then un-skip `BacktestActuallyTradesTests`. Unblocks all constraint tests.
2. **Phase 5 — FTMO golden journeys** (daily-DD halt, max-DD halt, flatten-on-breach, lot-size==risk%)
   via the harness, asserting on `DrawdownState`/journal. Reuse the `TradingLoopDirectTests` wiring.
3. **Phase 6 — venue/account integrity** (`SYSTEM-MODEL §3.6`): V1 startup/reconnect position
   reconciliation (`GetAccountStateAsync` returns `(0,0,[])` today — engine is blind to open positions on
   restart), V2 durable Guid↔venue-id map, V5 don't drop buffered commands on disconnect, M2 synthetic
   1.0 close fill, V3 write venue-confirmed SL back, A1–A4 reset/clock/baseline edges. Add M1's asserting test.
4. **Phase 0e residual** — live-path concurrency stress test (fills + force-close racing TrackOrder).
5. **Phase 0f residual (architecture)** — remove the last `if (_engineMode==Backtest)` fork via an
   `IEnginePacer`/venue-drive abstraction so the engine runs one path.
6. **Phases 2–4** — one drawdown/governor owner; one limit config (`RiskProfile` vs `PropFirmRuleSet`);
   real open-positions into `OrderDispatcher` worst-case; decimal money math; `MonthRolled`.

## Commit log (iter-24)
```
e978c1d fix(iter24-p6): venue-authoritative PnL (M1) + bounded exec-dedup LRU (V4)
2b9c459 docs(iter24): audit findings — venue/account/PnL integrity gaps
f6b2d4b refactor(iter24-p0f): de-couple engine from concrete venues via IBrokerAdapter hooks
5f800be fix(iter24-p0e): lean tick hot path + serialize PositionTracker; kill live execution race
9517fba refactor(iter24-p0d): split EngineRunner out of EngineWorker; drop BackgroundService from core
69138fa test(iter24-p0c): direct TradingLoop unit test — IHost-free seam
7c799a9 refactor(iter24-p0c): extract TradingLoop
7ed0bdb feat(iter24-p0): child-process reaper + deterministic FTMO harness skeleton
710b08a refactor(iter24-p1): unify live+backtest into one trading loop; delete BacktestDriver
```
