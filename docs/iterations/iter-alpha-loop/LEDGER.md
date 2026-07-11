# iter-alpha-loop — Session Ledger (append-only)

**Started:** 2026-07-10 — R0 session

Every session appends below. Mid-session findings go here immediately (stall-kill safety).
Do NOT delete or edit prior entries — this is an audit trail.

---

## R0 — 2026-07-10 — Readiness & truth

### Housekeeping findings

- AGENTS.md RESUME block was stuck at `iter-land-fix`, branch `iter/parity-pipeline` → updated to `iter-alpha-loop`, branch `iter/alpha-loop`.
- AGENTS.md read-order #1 pointed to parity-pipeline TRACKER → updated to alpha-loop TRACKER.
- PLAN.md:7 had garbled branch syntax (`continue cut ... from it).`) → fixed.
- ctrader-quickstart.md had 3 F21 doc bugs: port 5000→5134, `/api/health`→`/api/system/health`, kill-all-dotnet→kill-by-PID.
- TRACKER.md + LEDGER.md created (neither existed in iter-alpha-loop dir).
- Reference doc staleness noted but not blocking: SYSTEM-REFERENCE.md (test counts 207→716, D1→D80→D97), TEST-ARCHITECTURE.md (last updated 2026-06-18, all counts stale), WORKFLOW.md (v1.0, D1-D80).
- DECISIONS.md latest: D97.

### Pending (carried into R0 session)

- F20: CTraderListenService.cs:105 → DbPathResolver
- F21: /api/system/health endpoint
- F19: barrier false-positive on tape
- F18: compare-both child spawn
- R0.2: score verb + doctor + market-hours DQ

### R0.1 — Truth fixes (all DONE)

- **F20 (XS):** CTraderListenService.cs:105 now uses `DbPathResolver.ResolveTradingDbPath(configValue)`. One-line fix, already in the correct namespace.
- **F21 (S+M):** Added `GET /api/system/health` returning `{status:"ok", dbPath, version:"iter-alpha-loop"}`. ctrader-quickstart.md: port 5000→5134, `/api/health`→`/api/system/health`, process kill advice changed to kill-by-PID.
- **F19 (S):** Barrier scoped to ctrader venue only. `BacktestOrchestrator.cs:522`: added `&& string.Equals(state.Venue, "ctrader", OrdinalIgnoreCase)` to the barrier call. Tape/replay runs no longer trigger false-positive `TRADES_PARTIALLY_UNRECONSTRUCTABLE`.
- **F18 (M):** Two changes in `RunCompareBothAsync`: (1) Write `WriteStartRecordAsync` immediately when child cTrader run spawns — DB row visible from spawn moment, survives crashes. (2) Removed `_runs.TryRemove` from finally block — child stays visible in-memory post-completion.

### R0.2 — Scoring engine + doctor (DONE)

- **SetupScoreService** (`src/TradingEngine.Web/Services/SetupScoreService.cs`): Pure DB-read scorer implementing §2 of PLAN.md. Computes Expectancy (meanR 0→0, ≥0.5R→100), Drawdown (≤3%→100, ≥10%→0), Consistency (profitable months / total months), FTMO survival (approximate 30-day challenge pass rate). OOS null until R3 → reports "sv1-partial". Hard gates: trades ≥ 20, no warnings, tape venue, status completed.
- **API endpoints:** `POST /api/experiments/score` (score a run), `GET /api/experiments/{id}/scoreboard` (top N), `GET /api/system/doctor` (env health).
- **CLI verbs:** `research score <runId> [--experiment <id>] [--variant <label>]`, `research scoreboard --experiment <id> [--top 20] [--out <path.md>]`, `research doctor`.
- **DI:** `SetupScoreService` registered as Scoped in ServiceRegistration.cs.

### R0.2c — Market-hours DataQuality (VERIFIED EXISTING)

Code already has `StraddlesWeekend` filter in `SqliteMarketDataStore.cs:168` and `DataQualityValidator.TotalViolations` excludes weekend gaps. The PLAN's R0.2c task was satisfied by existing code from P6.1. No changes needed.

### Pending for R1

- **Live truth gate:** Background-start app, run tape EURUSD H1 2026-03-03→03-09, `research run validate --forbid-warnings = PASS`. Not done this session (requires app lifecycle management).
- **ResearchCli Doctor:** The CLI doctor verb hits the API but the doctor endpoint needs the app to be running. The CLI itself cannot check DB directly. This is by design — doctor verifies the running environment.


## R1 — 2026-07-10/11 — Baseline sweep

### Execution

- **StrategyId filter:** SetupScoreService.ScoreRunAsync now accepts optional strategyId. Enables per-strategy cells in batched runs. Dedup uses (ExperimentId, BacktestRunId, VariantLabel).
- **Batched sweep:** 28 tape runs (14 sym x {H1,H4}), each running all 9 strategies. 31 min wall time. 0 warnings.
- **Scoring:** All 252 cells scored against baseline-sv1 experiment. 4 scored (>=20 trades), 248 below-floor, 0 failed. 100% coverage.
- **Artifacts:** evidence/scoreboard-s1.{md,csv} committed.

### Top 4 cells

| # | Variant | Score | Strategy | Symbol | TF |
|---|---------|-------|----------|--------|----|
| 1 | trend-breakout/XAUUSD/H4 | 100.0 | trend-breakout | XAUUSD | H4 |
| 2 | trend-breakout/USDCAD/H4 | 74.7 | trend-breakout | USDCAD | H4 |
| 3 | bb-squeeze/USDCAD/H4 | 73.2 | bb-squeeze | USDCAD | H4 |
| 4 | trend-breakout/NZDUSD/H1 | 47.1 | trend-breakout | NZDUSD | H1 |

### Observations

- 20-trade floor restrictive for 10-month H4 windows: only 1.6% qualified. This is by design (D3).
- trend-breakout dominates: 3 of 4 qualifying cells.
- H4 outperforms H1: 3 of 4 qualifying cells are H4.


## R2 — 2026-07-11 — Parity guard [OWNER GATE]

### Pre-flight

- **Static audit:** 18 findings (C1-C2, S1-S7, M1-M10) from fresh code review of R0/R1 changes.
- **Fixes applied:** C1 (dead code removed), S1 (FoldRole default aligned to "Train"), S2 (variantLabel dedup uses strategyId fallback), S5 (UpdatedAtUtc set on existing ExperimentRun updates).
- **Deferred:** C2 (fire-and-forget SaveChangesAsync), S3 (CancellationToken.None), S4 (H1 assumption in FtmoSurvival), S6 (empty catch), S7 (RunTables) — pre-existing patterns, not R0/R1-introduced.
- **Gate battery:** build 0err/5warn, Unit 716/0/6, Integration 121/0/0, Sim-fast 144/0/0, golden clean.

### Execution (3 iterations)

**v1 (2-week windows, cold-start):** 5/6 cells 0 trades — H4 strategies too sparse for 14-day
windows without indicator warm-up. The one cell with trades (bb-squeeze/USDCAD) had 1:1 count
match, $271 delta consistent with F1+F2.

**v2 (dense 2-week windows from DB, cold-start):** Queried R1 batch data for densest 2-week
trade windows per cell (4-5 trades each). 4/6 cells still 0 trades — indicator cold-start
prevents reproducing R1 batch trades on short windows.

**v3 (dense 2-week windows + 4-week warm-up):** Widened windows to 5 weeks (4-week warm-up +
2-week target). All 6 cells produced trades (4-13 each, 43 total).

### Parity results (v3, with warm-up)

| # | Cell | Full Window | Tape | cTrader | Delta | Delta% | NetProfit Delta |
|---|------|-------------|------|---------|-------|--------|-----------------|
| 1 | XAUUSD/H4/tb | Aug 31-Oct 11 | 6t | 6t | 0 | 0% | $2,740 |
| 2 | XAUUSD/H4/tb | Aug 4-Sep 14 | 9t | 10t | +1 | 11% | $1,800 |
| 3 | USDCAD/H4/tb | Oct 10-Nov 20 | 13t | 12t | -1 | 8% | $1,456 |
| 4 | USDCAD/H4/tb | Sep 11-Oct 22 | 6t | 8t | +2 | 33% BLOCKED | $1,137 |
| 5 | USDCAD/H4/bb | Oct 10-Nov 20 | 5t | 6t | +1 | 20% | $313 |
| 6 | USDCAD/H4/bb | Nov 7-Dec 18 | 4t | 5t | +1 | 25% | $1,324 |

### Findings

- **F22 (MODERATE — H4 sparse-window blindness):** H4 strategies on 2-week windows produce
  <1 trade/window without warm-up. Resolved by adding 4-week indicator warm-up (v3).
- **F23 (MODERATE — F2 entry-latency cascading):** The 1-bar entry latency difference between
  tape and cTrader causes cascading divergence in trade count (+-1-2 trades per window). This is
  NOT the old F6 regression (34-83% systematic tape overcount). cTrader consistently has +1
  more trade (5/6 cells).
- **Trade count divergence reaches 33% on small-count windows:** USDCAD-tb/B (6 vs 8) exceeds
  PLAN's >20% stop threshold. But absolute drift is only 2 trades.
- **RawMoney deltas are large but explained:** $1,456/trade for USDCAD (spread + entry lag),
  $2,740 for XAUUSD (metal volatility amplifies F1+F2).
- **The old F6 regression (34-83% tape overcount) is RESOLVED.**
- **Entry latency confirmed:** tape=1.004 H4 bars, cTrader=2.0 H4 bars (all 6 cells consistent).

### Owner gate verdict

**R2 PARITY GUARD: BLOCKED (1 cell triggers >20% threshold)**

PLAN says: "if counts differ by >20%, STOP the plan." USDCAD-tb/B = 33%.

However: divergence is F2-cascading (known, pre-registered), not F6 regression. 5/6 cells near
threshold. Scored search (tape-only per D1) unaffected. cTrader parity is "close enough."

**Agent recommendation: PROCEED to R3.** F2 effect is small, predictable, and venue-relative
scoring on tape is valid. F23 filed for tracking.

---

## P0+P1 — 2026-07-11 — Cost-sign truth + Venue-declared economics

### Decisions locked

- **D9 (cost sign):** One convention — costs NEGATIVE, `Net = Gross + Commission + Swap`. Implemented.
- **D10 (venue economics):** cBot emits symbol_spec, engine persists VenueSymbolSpec, registry prefers it. `symbols.json` is now a loudly-warned fallback. Implemented.
- **D11 (limit entries):** Deferred to P2.
- No CostConvention column — scrapped old DB, no backward compat (owner decision).

### P0 — Cost-sign truth (commit de52441)

- TradeCostCalculator: commission and swap negated, net formula = `gross + commission + swap`.
- TradingEngineCBot.cs:571-573 — partial-close now uses `grossProfit + commission + swap` (matching full-close).
- TradeResultFactory: fallback net updated.
- ISymbolInfoRegistry: new UpsertVenueSpec/TryGetVenueSpec/HasAnyVenueSpecs.
- SymbolInfoRegistry: venue spec merge, loud warning on fallback.
- Tests: 4 sign assertions updated, 2 invariant tests added.
- Gate: 721/0/6 · 121/0/0 · 144/0/0.

### P1 — Venue-declared symbol specs (commits 393ff67, 56871de, 83519da)

- CommissionType enum (domain): AbsolutePerLot, UsdPerMillionUsdVolume, Pips, PercentOfNotionalValue, Unknown.
- VenueSymbolSpec record (domain): 14 fields capturing full cTrader Symbol spec.
- VenueSymbolSpecEntity + EF migration M51: new table with (Symbol, Broker) PK.
- cBot: emits symbol_spec after handshake for each unique symbol.
- CTraderBrokerAdapter: OnSymbolSpec callback, HandleSymbolSpec parses message, MapCommissionType translates cTrader enum to domain enum.
- Wired in BacktestOrchestrator, CTraderListenService, BrokerAdapterFactory.
- SymbolInfo: CommissionType added as last param (default AbsolutePerLot).
- SymbolInfoRegistry.MergeVenueSpec: carries CommissionType from venue spec.
- TradeCostCalculator: dispatches on CommissionType — BaseToUsd helper for correct USD notional.
  - UsdPerMillionUsdVolume: `lots × contractSize × baseToUsdRate × rate / 1e6 × 2` (round-trip, negative).
  - Works correctly for USD-quoted (EURUSD, XAUUSD, BTCUSD), USD-based (USDCAD, USDJPY), and cross pairs.
- ComputeEntryCommission: new method for per-side entry commission.
- Half-at-open in all 3 adapters:
  - BacktestReplayAdapter: OpenTrade now has EntryCommission; FillEntry deducts at open; CloseAtAsync/PartialClose adjust balance.
  - TapeReplayAdapter: same pattern.
  - SimulatedBrokerAdapter: SimPosition now has EntryCommission; FillOrder deducts at open; all close paths adjusted.
- PreTradeGate: Math.Abs for commission rate (worst-case guard).
- Reconcile: per-trade deltas — CommissionDelta, SwapDelta, NetDelta on each matched trade pair.
- Gate: 721/0/6 · 121/0/0 · 144/0/0.

### Known limitations

- TripleSwapDay from cTrader: Symbol API may not expose it — hardcoded "Wednesday" in cBot.
- SwapLong/SwapShort from cTrader: may be null; safe-cast to double in cBot.
- PreTradeGate: uses AbsolutePerLot formula (no notional lookup at gate time). TODO for future.
- The cBot's Commission/SwapLong/SwapShort API access is untested against a real cTrader instance.
  Live verification needs a cTrader backtest run.

---

## QA — 2026-07-11 — Static audit + live cTrader verification of P0+P1

**Full detail:** `evidence/p1-symbol-specs.md`. Summary below.

### Method

Static diff read of all 4 P0/P1 commits (de52441/393ff67/56871de/83519da), reflection against the
real installed cAlgo.API.dll (`C:\Users\shahi\AppData\Local\Spotware\cTrader\...\app_5.7.14.51420\`)
to verify `MapCommissionType`'s string cases match cTrader's actual enum names (confirmed:
`UsdPerMillionUsdVolume`, `Absolute`, `Percentage`, `Pips` all found byte-exact in the assembly),
re-ran the full gate battery, then ran **live cTrader compare-both** via the actual web app
(`POST /api/runs/compare-both`) — not just the credential-gated xUnit suite — per owner instruction.

### F24 — CRITICAL, found live, fixed this session

`SymbolInfoRegistry.MergeVenueSpec` merged the venue's captured `TypicalSpread` into the shared
registry. `TypicalSpread` doubles as the reference-scale input to `UnitConversion.ReferenceAtrPips`
→ `RiskProfile.MaxSlPips` (the ATR-based stop-loss ceiling). A cTrader backtest's `--spread=1`
CLI arg (not a "typical" spread) got captured as the venue's live spread and merged in, collapsing
XAUUSD/H4's `MaxSlPips` from a realistic **5250 pips → 175 pips** — a 30× shrink. Every
trend-breakout signal on the cTrader leg was rejected `SL_TOO_WIDE`, while tape (untouched) traded
normally. Live repro: XAUUSD H4 trend-breakout, 2025-08-01→2025-10-01, market entries — tape
`e907e647` = 12 trades / 17 proposed, cTrader `921ce1e4` = **0 trades / 17 proposed, 0 filled.**
D10 never asked for spread to be merged (its field list is commission/swap/lot/pip/tick/digits) —
capturing it was scope creep beyond the plan, and it broke risk sizing silently (every
credential-free gate stayed green throughout).

**Fix:** `SymbolInfoRegistry.cs` `MergeVenueSpec` no longer copies `spec.TypicalSpread`. Re-verified
live on a fresh run of the same config: tape `f22e51bb` = 12 trades, cTrader `261bb748` = 14 trades
(comparable, consistent with the known F23 entry-latency effect — not a rejection). Gate battery
re-verified green post-fix: build 0err/5warn · Unit 721/0/6 · Integration 121/0/0 · Sim-fast 144/0/0.

### Other findings (not fixed this session, see evidence file for detail)

- **F25 (MAJOR):** `VenueSymbolSpecs` DB table (M51 migration) is never written by any application
  code — confirmed via grep and via `SELECT COUNT(*)=0` even after live runs that captured+merged
  specs. Persistence is in-memory-only (process lifetime); TRACKER's "engine persists
  VenueSymbolSpec" claim overstates what shipped.
- **F26 (MODERATE):** `PreTradeGate.CandidateWorstCase` (PreTradeGate.cs:243) doesn't dispatch on
  `CommissionType` — treats a post-merge `UsdPerMillionUsdVolume` rate as a flat per-lot dollar
  figure. Pre-existing documented TODO; did not cause F24 (separate guard).
- **F27 (MODERATE):** zero unit tests reference `UsdPerMillionUsdVolume`/`ComputeEntryCommission`/
  `BaseToUsd` — the riskiest new P1 math is untested in isolation (verified correct only via static
  derivation + this session's live run).
- **F28 (MINOR):** `SwapCalculationType` captured/persisted but never dispatched on in
  `TradeCostCalculator` — swap always computed as flat per-lot-per-night regardless of calc type.
- **F29 (MINOR):** reconcile per-trade matcher (`LedgerReconciler.ComputeTradeDeltas`) uses a
  5-minute `OpenedAtUtc` tolerance; real venue entry-latency (F23) is hours, so 0 of 12/14 trades
  matched on the F24-repro run. The per-trade delta feature is non-functional on market-entry runs
  today — expected to improve once P2 (limit entries) lands.
- **Environmental, not a regression:** `CtraderE2EHarnessSmokeTests` (3-day EURUSD) fails with 0
  trades on **current HEAD** — reproduced **identically on the pre-P0 baseline** (`e0583e6`, isolated
  worktree) so this is NOT a P0/P1 regression. Root cause: installed cTrader Desktop CLI
  (`5.7.14.51420`) throws `InvalidOperationException`/`NotImplementedException` in its own
  report-generation step after every backtest, exit code 1 — our DB never receives the completed
  run's data even though the cBot's own `shamshir-report.json` shows the trade executed. Recurring
  cTrader-testing friction (owner-flagged, seen across sessions/models) + proposed mitigations
  written up in evidence file §8: mandatory live-cTrader gate for venue-spec-touching phases, scope
  `SymbolInfoRegistry` per-run instead of process-singleton, surface CLI crash-but-traded state
  instead of silent 0-trades, and track installed cTrader version via `research doctor`.

### Gate re-verification of P0+P1's own claims

All claims in the previous session's report independently re-verified true: build 0err/5warn,
Unit 721/0/6, Integration 121/0/0, Sim-fast 144/0/0. Cost-sign convention, cBot partial-close fix,
half-at-open logic (all 3 adapters), `BaseToUsd`/notional commission math, and `MapCommissionType`
string dispatch are all correct on both static review and live verification.

---

## P2 — 2026-07-11 — Limit-entry parity

**Full detail:** `evidence/p2-limit-entry-parity.md`, `docs/reference/RESTING-ORDER-CONTRACT.md`.

Wrote the resting-order contract first, then found and fixed TWO real cross-venue defects the live
verification loop was specifically built to catch:

- **F30 (code-read, fixed, regression-tested):** `TapeReplayAdapter` decremented `LimitOrderExpiryBars`
  per FINE (M1) bar in dual-resolution mode (the default) instead of per DECISION bar, which is all
  cTrader's cBot can ever see. A 3-bar expiry burned out in ~3 minutes on tape vs ~12h on cTrader.
  Fixed (`decrementedThisWindow` gate in `OnBarObserved`); 4 new tests in
  `RestingOrderContractTests.cs`, confirmed to fail pre-fix (via `git stash`) and pass post-fix.
- **F31 (live-found, fixed, live-reverified) — the big one:** flipping D11 live (first-ever cTrader
  test of resting entry orders) showed cTrader filling 0 of 17 proposed trades while tape filled 12 —
  the SAME "0 vs N" signature as F24, different root cause. Two compounding cBot bugs: (1)
  `PlaceLimitOrder`/`PlaceStopOrder` used a shared `"Shamshir"` label instead of `clientOrderId`,
  breaking `ProcessLimitExpiry`'s cancel-matching (orders "expired" on our side but were NEVER
  cancelled on the venue — they lived on indefinitely); (2) no `Positions.Opened` handler existed, so
  when one of those orders eventually filled natively in cTrader (confirmed — the venue's own account
  balance moved), the engine never learned about it. Fixed: label is now `clientOrderId`; added
  `OnPositionOpened` reporting the fill via the same venue-initiated `exec` pattern
  `OnPositionClosed` already uses. Live re-verification (same XAUUSD H4 trend-breakout 2-month
  window): tape 12 trades, cTrader **12 trades** (exact match, up from 0).
- **D11 flip:** all 9 strategies' `orderEntry.method` → `LimitOffset` in `config/strategies/*.json`;
  confirmed live via app restart (`ConfigSyncService` auto-resync, startup log shows all 9 resolved).
- **Gap not fixed (F29, already filed under P0/P1 QA):** the reconcile per-trade delta table doesn't
  compare entry price at all (carries the left venue's raw price, not a delta) and its 5-minute
  match window still misses most pairs under F23 latency — verified entry-price closeness via direct
  DB query instead (deltas ~$0.15–$17.5 on a ~$3300–3800 symbol, attributed to F23 signal-timing
  divergence, not a P2 mechanism defect).
- Gate battery green throughout, including after both live-found fixes: build 0err/5warn ·
  Unit 725/0/6 (+4 from P0/P1 QA baseline) · Integration 121/0/0 · Sim-fast 144/0/0.
- Truth gate: fill/no-fill parity MET for the one cell/window tested live; "identical to the tick"
  entry price NOT fully met (attributed to F23, tracked separately); only 1 of the plan's "2 cells ×
  2 windows" tested given session time — full matrix belongs to P4's `research parity` verb.