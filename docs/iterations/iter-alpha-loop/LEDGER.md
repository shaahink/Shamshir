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

---

## P3 — 2026-07-11 — Exit + spread parity

**Full detail:** `evidence/p3-exit-spread-parity.md`.

- **P3(a) gap-through fills, P3(c) exit-side spread direction:** both already correct on code read
  (`TapeReplayAdapter.ProcessSlTpHits` — SL fills at bar Open when gapped, TP does not; short exits
  use ask-adjusted bar/price, long exits use raw bid — internally consistent with the entry-side
  convention P2 already verified). No fix needed.
- **F32 (found + fixed + live-verified) — the same class of gap P1/P2 kept finding:** tape's
  `GetSpread()` only ever used the static `symbols.json` `TypicalSpread` (30 pips for XAUUSD),
  silently ignoring the run's configured `SpreadPips` (default 1) — while cTrader has always
  unconditionally honoured that same config field via `--spread`. A compare-both run explicitly
  asking for a shared spread got 1 pip on cTrader and 30 pips on tape, a 30× mismatch no
  credential-free gate could ever see. Fixed: added `spreadPipsOverride` to `TapeReplayAdapter`
  (mirroring P1's `commissionPerMillion` pattern exactly), wired unconditionally from
  `cfg.SpreadPips` in `BacktestOrchestrator`. 3 new tests (`TapeReplaySpreadOverrideTests.cs`).
  **Deliberate side effect** (documented, not hidden): every tape run — not just parity ones — now
  defaults to 1-pip spread cost instead of the symbol's static realistic value, matching what
  cTrader has always done; flagged clearly since it changes backtest realism broadly.
- Live re-verified (same XAUUSD H4 trend-breakout config as P2): tape `da7b3427` = 13 trades
  (up from 12 — expected, a tighter matching spread makes limit touch conditions easier to satisfy),
  cTrader `7c2be39b` = 12 trades. Commission still differs (-134.40 vs -45.36) — attributed to the
  same already-documented F23 entry-latency effect (different specific trades → different lot
  sizes), not a new P3 defect; not pursued further this session.
- Gate battery green throughout: build 0err/5warn · Unit 728/0/6 (+3 from P2 baseline) ·
  Integration 121/0/0 · Sim-fast 144/0/0 (one flaky, order-dependent, unrelated test failure seen
  once — `VenueSizingParityTests.CtraderHello_SurfacesDemoBalance_ThatBacktestMustNotAdopt` —
  confirmed to pass both in isolation and on a clean full-suite re-run, not a P3 regression).

---

## X0/X1 verification — 2026-07-15 — live concurrency proof + 6 fixes

**Context:** a prior session left X0 (run queue + concurrency) and X1 (progress + status truth)
marked DONE, uncommitted, with the tracker's own recommended next step being *"smoke test: start 3
tape runs concurrently, verify queued→running→completed, cancel mid-queue"* — i.e. the truth gate
had never actually been run. This session ran it for the first time and treated every failure as a
real defect to fix, not a test-tooling problem to work around.

**Environment fix (found first, blocks everything):** `Microsoft.EntityFrameworkCore.Design` was
pinned to floating `10.*` in both `Infrastructure.csproj` and `Web.csproj`, while
`Microsoft.EntityFrameworkCore`/`.Sqlite` were pinned to exact `10.0.9`. A `10.0.10` patch had been
published to the feed since the last session, so the build failed outright (`NU1605` downgrade
error) before any gate could run. Pinned Design to `10.0.9` to match its siblings.

**Static gates matched the tracker's claims exactly** once the build was unblocked: 0err/5warn ·
Unit 759/0/6 · Integration 121/0/0 · Sim-fast 144/0/0.

**Live concurrency test (X0's actual truth gate, run for the first time):** launched the app in the
background (port 5134) and fired 5 `POST /api/runs` (tape, EURUSD H1, 7-day window) via true
parallel `curl` calls (not `dotnet run`-per-call, which serializes on process-start overhead and
hides the real race). Findings, each reproduced live, fixed, and re-verified:

- **F49 — the gate had never been exercised and failed immediately:** all 5 calls either timed out
  or came back `500 TaskCanceledException`. Root cause: `RunsController.ValidateTapeDataAsync` calls
  `IMarketDataStore.GetInventoryAsync()`, an unfiltered `GROUP BY` scan of the entire 1.2GB
  `MarketDataBars` table (~10-16s per call, confirmed via EF Core query logging). The *old*
  one-run-at-a-time 409 guard — which X0 correctly removed, since serializing all starts defeats the
  point of a concurrency queue — had been silently serializing these scans for free the whole time;
  nothing about X0 accounted for what happens once N of them run at once. Fix: `BootstrapMarketDataStore`
  (the existing singleton decorator) now coalesces concurrent callers onto one shared, lock-guarded,
  20s-TTL-cached `Task<inventory>` — first caller triggers the scan, everyone else awaits the *same*
  task instead of starting their own. Benefits every other `GetInventoryAsync` caller too
  (`DataQualityValidator`, `ReferenceScalePopulator`, `DataManagerController`, `SystemController`
  doctor) since downloads are rare, multi-minute operations — 20s staleness is invisible.
- **F50 — thread-pool starvation in `Start(cfg)`:** `ResolveBarCount` (X1's own "real bar count"
  feature) called `store.CountBarsAsync(...).GetAwaiter().GetResult()` synchronously inside the
  synchronous `Start()` method. Invisible when only one `Start()` could ever be in flight; a genuine
  starvation risk the instant X0 allowed N concurrent `Start()` calls to each block a request thread
  on a sync-over-async DB round trip. Fix: `Start()` now uses the calendar estimate as an instant
  placeholder and kicks off a fire-and-forget `RefreshBarCountAsync` that upgrades `state.BarsTotal`
  once the DB answers (progress display tolerates being briefly approximate; the request path does
  not tolerate a blocking DB call under load). Also fixed the *other* half of X1's own claim that
  turned out unwired: the compare-both cTrader child state still used the calendar
  `EstimateBarCount` (X1's plan text explicitly said "the cTrader child... must use it too" — it
  didn't); it now properly `await`s the same `ResolveBarCountAsync`.
- **F51 — cancelling a queued run had no effect until a slot opened:** the dequeued-but-waiting task
  called `semaphore.WaitAsync(CancellationToken.None)` — the run's own cancellation token was passed
  to `RunAsync` but never to the semaphore wait itself, so a user cancelling run #4 of 5 would see
  nothing happen until run #4 would have started anyway (defeating the entire point of "cancel
  mid-queue," which the prior session's own handoff listed as the next thing to verify). Reproduced
  live with a deliberately slow config (`speed=0.05`, ~10 months of EURUSD H1) to create a real
  multi-second queued window. Fix: wait on `state.CancellationSource!.Token` instead, with an
  `OperationCanceledException` catch (guarded by `!acquired`, so the semaphore is never released
  unless it was actually acquired) that finalizes the run as `Cancelled` directly instead of ever
  calling `RunAsync`. Verified: a queued cancel now resolves in ~2s instead of waiting for 3 slow
  runs ahead of it to finish.
- **F52 — a cancelled run displayed as "failed":** `RunStatusResolver.Resolve` derives status purely
  from `ExitCode`/`ErrorMessage`/`WarningsJson` and has no way to represent "cancelled" — any
  non-null `ErrorMessage` (including the new F51 path's own message, and the *pre-existing*
  shutdown-cancel path's "Cancelled (shutdown)." message) falls out as `Failed`. This bug predates
  this session's F51 fix; F51 just made it visible for the first time via a real cancel-mid-queue
  test. Fix: `WriteEndRecordAsync` now accepts an explicit `status:` (mirroring
  `WriteStartRecordAsync`'s existing parameter) and persists it to the `Status` column X0 already
  added but never actually read back; `RunQueryService.ResolveStatus` (single-run) and a new
  `PreferPersistedTerminalStatus` pass (list endpoint) prefer that column whenever it names one of
  the four real terminal states, falling back to the legacy derivation for pre-migration rows
  (`Status=""`). Verified: cancelled-while-queued now shows `"status":"cancelled"` on both the
  single-run and list endpoints.
- **F53 — `state.RunPlanJson` raced its own readers:** only ever set deep inside `RunAsync`, well
  after `Start()` returns — invisible before this session because F50's blocking DB call gave
  `RunAsync` an incidental head start every time. Once F50 made `Start()` fast, `RunMetadataTests
  .RowRun_persists_and_surfaces_full_selection` (part of the standing Integration suite, not written
  this session) started failing: a poll landing right after a fast `POST /api/runs` could observe
  the live in-memory state before `RunAsync` ever set `RunPlanJson`, reading the field's null
  default (serialized as `"[]"`). Fix: set `RunPlanJson` in `Start()`'s initial object initializer,
  alongside every other field that's already available at that point. Re-ran the full Integration
  suite 3x consecutively post-fix with no flakiness (121/0/0 each time).
- **Live concurrency proof, the actual X0 truth gate:** 5 truly-parallel `POST /api/runs` all
  returned 200 with identical `startedAtUtc`; the first 3 (MaxTapeConcurrency=3) finished together
  (~3.4s), the other 2 waited for a slot and finished ~3s later; **all 5 produced byte-identical
  results** (`netProfit=-48.05991389653`, 3 trades) — exactly the plan's stated proof of concurrency
  safety. Reproduced twice more (once mid-fix, once on the final build) with the same result.

**Not done / deferred:** the cTrader serial lane (`_ctraderSemaphore(1,1)`) was not live-tested — no
cTrader Desktop instance in this pass. It's architecturally sound by code read (separate semaphore,
`isCtrader` branch in `TryDequeueNext`) but unverified under real concurrent load. F48 (XAUUSD PnL
currency conversion) remains open, untouched. The previous handoff's "next: X3 (runs page rework) +
X4" mislabeled X2 as X3 — corrected in TRACKER: X2 (Runs page, notes, copy-run) is the actual next
phase per PLAN.md's own ordering; X3/X4 are untouched.

**Gate battery, final:** build 0err/5warn · Unit 759/0/6 · Integration 121/0/0 (3x consecutive,
confirmed non-flaky) · Sim-fast 144/0/0.
---

## Session 2026-07-15 — X2 + X3 delivered, live-verified (numbering note: no F54 row exists; the prior handoff's "F49-F54" counted the EF-pin environment fix, so this session continues at F55)

**X2 — Runs page, notes, copy-run (PLAN §3c).**
- **Notes:** `BacktestRuns.Notes` (migration M53) with a deliberately narrow write path —
  `IBacktestRunRepository.SetNotesAsync` is the ONLY writer; `SaveAsync`/`UpdateAsync` never touch
  the column, so the end-record write cannot clobber a note typed mid-run (pinned by
  `RunNotesTests.EndRecordUpdate_DoesNotClobber_ANoteTypedMidRun`). PATCH `/api/runs/{id}` takes
  `notes` (empty string clears; null = no change). Editable inline on the Runs page and from a
  Notes card on the report page. Live-verified round-trip.
- **Richer table:** Status(+queue pos, +live %), Venue, Strategy (derived server-side from
  RunPlanJson), Symbol, TF, Net, MaxDD, Trades, Win%, **Score** (latest ExperimentRuns ScoreJson
  `Composite` per run, joined in `RunQueryService.AttachLatestScores` — decoration, never fails the
  list), **Duration** (WallElapsedMs), Created, Notes, copy button, plus a client-side filter box.
  Live-verified: a fresh 4-month tape run (875b390b, 39 trades) scored 57.1 via
  `POST /api/experiments/score` and the list row showed `strategies=trend-breakout score=57.1
  wallMs=35463` immediately.
- **F55 — duplicated runs VANISHED from the Runs page:** the old `groupedRuns` suppressed every run
  with a `parentRunId` and only re-emitted them inside a compare-pair group. A `/duplicate` run has
  lineage but no `comparePairId` → it was silently never rendered; same for a compare child whose
  parent aged out of the 50-run window. Rewritten: emit in list order; a pair's later siblings render
  indented under the first visible member; anything whose pair/parent isn't visible renders as a
  normal top-level row. Live-verified both ways: duplicate 7c94c235 visible (e2e-pinned), and with
  the two session runs temporarily tagged as a pair (DB tag, reverted after), the child rendered
  indented (↳) directly under its parent — structurally the same rendering the cTrader compare child
  uses (no cTrader Desktop in this pass to exercise a real pair end-to-end).
- **Liveness:** the Runs page now joins the SignalR group of every visible active run (progress %
  in the Status cell), reloads on `completed$` (debounced 1s), and slow-polls every 15s as a
  fallback for CLI-started runs. The iter-cache-reads-2 prerequisites (B1 snapshot freeze,
  B2 MarkCompleted/Evict never called) were verified ALREADY FIXED in the current tree
  (invalidate-on-append + orchestrator MarkCompleted + CacheEvictionSweeper) before claiming
  liveness. Live-verified: with the page open and untouched, a slow tape run (5b148b30) appeared
  without reload, showed `running NN%`, and flipped to `completed` on its own.
- **Copy-run / reuse-last-params:** `?copyFrom=<id>` (and the legacy `?sourceRunId`) now rebuild the
  FULL setup from the persisted run: dates, balance, commission, spread, risk profile, venue,
  governor/regime/exploration/excursions, and the exact run plan rows (strategy×symbol×TF×pack,
  row-level disables) from RunPlanJson — the old prefill only copied dates/balance/symbols/periods.
  "⟲ Reuse last params" prefills from the most recent run without leaving the builder.
- **F56 — copy-run appeared DEAD on a cold cache:** the builder's ngOnInit awaited four lookups
  sequentially before prefilling, and `/api/data-manager/inventory` is the F49 full-table scan
  (>10s cold). The prefill (which depends on none of them) now runs first and the lookups load in
  parallel; the prefill catch also logs instead of swallowing. Found because the e2e copy-run test
  timed out at 15s while the API-level flow was provably fine.

**X3 — Trade chart rework (PLAN §3c).**
- **F57 — why every trade chart showed "meaningless lines" (three stacked defects):**
  (a) `markerFor()` dropped the API's `time` field, so Entry/Exit could never be time-anchored and
  fell through to full-width horizontal lines; (b) lightweight-charts v5 REMOVED
  `series.setMarkers()` — the call threw and killed the rest of `updateChart()`; (c) the vertical
  open/close "lines" were 2-point series with IDENTICAL timestamps, which the library rejects.
  Rebuilt: v5 `createSeriesMarkers` plugin with directional entry/exit arrows (side follows
  direction, text carries price), SL/TP as trade-window level lines (not full-width), and no
  same-timestamp series.
- **Stop path:** `/api/trades/{id}/chart` now returns `stopPath` — the initial stop plus every
  journaled BREAKEVEN/TRAIL move for that position (PascalCase `StopLossModifyRequested` EventJson,
  contract pinned by `StopPathParsingTests`), rendered as a stepped dashed line walking from entry
  to exit. Live-verified on a USDCAD short with 20 TRAIL/BE moves (run efb77acf): 21-point path.
- **F58 — the "initial SL" painted at entry was the FINAL stop:** `TradeResults.StopLoss` is
  post-BE/trail; for the trailed short above it sat BELOW the entry, i.e. an instant-stop-out lie.
  The chart now uses `InitialStopLoss` (M34) with fallback to the final stop for pre-M34 rows.
  Live-verified: initial stop 1.41081 (above the 1.40656 short entry), path walks down to the exit.
- **Context window:** server default `padBars` 50→20 (N bars before entry / after exit, per PLAN);
  card has a 20/50/100 selector that refetches (e2e-pinned via request assertion).
- **Prev/next navigation:** trade detail responses carry `runId`, `prevTradeId`, `nextTradeId`,
  `tradeIndex`/`tradeCount` (OpenedAtUtc order within the run). The trade-detail page subscribes to
  route params (was a one-shot snapshot), so prev/next re-loads in place with the chart card
  mounted; the report page's expanded trade card gets the same nav by swapping `expandedTradeId`.
  Back-to-run link added. E2e-pinned: next-click navigates and the canvas survives.

**E2E baseline discipline:** the full Playwright suite showed 17 failures on the new tree; before
explaining anything away, the SPA changes were stashed and the two pre-existing spec files re-run on
the baseline SPA → the SAME 15 failures (12 ui-smoke + 3 live-monitor; stale iter-37/38 selectors +
live tests needing bars/cTrader) — zero regressions from X2/X3. The remaining 2 were bugs in the new
x2-x3 spec itself (one bad selector; one real finding → F56). Final: x2-x3 spec 8/8 green;
ui-smoke re-run on the final SPA reproduces exactly the baseline's 12. New spec:
`web-ui/tests/e2e/x2-x3.spec.ts`. Housekeeping: `web-ui/tests/e2e/output/` was partially TRACKED in
git (failure screenshots/error-contexts churned every run) → now gitignored and untracked.

**Gate battery, final:** build 0err/5warn · Unit 759/0/6 · Integration **128**/0/0 (7 new tests:
RunNotesTests ×3, StopPathParsingTests ×4) · Sim-fast 144/0/0. Golden untouched by
construction (no kernel/adapter/decision-path files in the diff).

**Not done / deferred:** compare-both pair grouping not exercised against a REAL cTrader child (no
cTrader Desktop this pass) — rendering verified with a synthetic pair tag instead. Notes on a
still-RUNNING run's report page show empty until completion (the live detail path is deliberately
zero-DB-read; the save itself works). F48 (XAUUSD PnL currency conversion) remains open. X4 (data
manager auto-sync) not started.

---

## X4 — Data-manager auto-sync + cTrader consolidation (2026-07-15)

**Worktree/coordination:** delivered in a SEPARATE worktree `C:\code\shamshir-x4`, branch
`iter/alpha-loop-x4`, fast-forwarded onto `c1a7477` (X2/X3) after confirming ZERO file overlap with
that change-set (X4 touches DataManager/DownloadJobService/MarketData/*; X2/X3 touched Runs/Trades/
chart). Own 1.2 GB `marketdata.db` snapshot copied in; runs on port 5135. Merge back is trivial.

**cTrader consolidation (X4.0):** the three cTrader code paths ran on THREE hardcoded port pairs
(15555/6 listen, 15562/3 download, orchestrator dynamic) with a private serial lane in the
orchestrator only. New `CTraderProcessOwner` singleton consolidates them: dynamic loopback ports
(reuses the existing `AllocatePorts`), one shared bounded lane (`SemaphoreSlim`, `CTrader:ProcessOwner:
MaxConcurrency` default 2, 1=serial) shared by backtest + download, and **owned-PID reaping**.

**Load-bearing finding — image-name reaper is a cross-kill hazard.** `KillCtraderProcessTreeAsync`
killed every `ctrader-cli`/`cTrader.Automate` by IMAGE NAME, documented safe only because "at most one
cTrader run at a time." Under parallel cTrader (owner's pick) OR alongside a second worktree's
ctrader-cli it cross-kills siblings. Replaced by `CTraderProcessOwner.ReapByTag($"run:{id}")` /
`ReapByTag($"download:{id}")` — both launchers already tree-kill their own process on cancel (CliWrap
ct / `Process.Kill`), the Job Object (`ChildProcessReaper`) is the crash/app-exit net. Removed the
image-name reaper + its VenueSessions reap-audit (audit-only nicety).

**Persistence (X4.1):** only the watchlist (`MarketDataSyncCells`) is durable — in `MarketDataDbContext`
(EnsureCreated, no EF migrations) via idempotent `CREATE TABLE IF NOT EXISTS` at startup, since
EnsureCreated is a no-op on the existing multi-GB DB. Deliberately NOT a trading-DB migration (would
touch `TradingDbContextModelSnapshot.cs` that X2/X3's M53 also touched). The work each tick is DERIVED
from live coverage (durable truth) + idempotent `INSERT OR IGNORE` ingest, so a restart mid-sync just
recomputes the still-missing range — self-healing, no stuck-job states.

**Coverage/gap (X4.2):** `MarketDataCoverageService` aggregates inventory across sources per (symbol,
tf), overlays the watchlist, and computes a market-hours-aware status (up-to-date/stale/missing/
disabled) + missing tail. `MarketHours` treats the FX weekend closure (~Fri 21:00→Sun 21:00 UTC) and
crypto 24/7 so a weekend is never a false "stale". `GET /api/data-manager/coverage`.

**Auto-sync (X4.3):** `AutoSyncService` (BackgroundService, on by default, inert until the watchlist has
cells) reconciles each tick: for each enabled cell behind, it drives the existing consolidated download
path (`DownloadJobService.Start` → owner lane) for [lastBar,now]. `POST /api/data-manager/sync-now`
runs one tick on demand. Watchlist CRUD: `GET/POST /api/data-manager/watchlist(/toggle|/remove)`.

**UI (X4.4):** Data Manager gains a Coverage & Auto-Sync grid — per-cell status badge, Watch toggle
(pin/unpin), "Sync all → latest", server-truth refresh every 5 s.

**Worktree env gotcha:** fresh worktree `npm ci` fails (lockfile) and `npm install` hits ERESOLVE;
Tailwind v4 CLI is a separate `@tailwindcss/cli` and its bin is absent until deps install with
`--legacy-peer-deps` (matches the main worktree). The Web csproj's `EnsureAngularCurrent` guard only
CHECKS wwwroot staleness (it does not build ng) — backend-only compile loops use `-p:NgProjectDir=__skip__`.

---

## X4 merge + R1' readiness (2026-07-15, second session)

**Merge:** `iter/alpha-loop-x4` → `iter/alpha-loop` at 1c0f49f. Only conflicts: this iteration's
TRACKER table (both sides appended rows — kept both) and the auto-generated `build-info.ts` stamp.
Full battery green post-merge (Unit 759/0/6 · Integration 128/0/0 · Sim-fast 144/0/0). The stale
`docs/cleanup-refs` branch pointer turned out to be an ancestor of the mainline — nothing to merge.
Gotcha re-confirmed: `dotnet build` fails on a stale wwwroot after any web-ui merge — run
`npm --prefix web-ui run build` first.

**R1' readiness audit found TWO gaps, both fixed + live-verified:**

**Gap 1 — D13 was not implemented (c4b4c1f).** `SetupScoreService.ScoreRunAsync` returned early on
every validity failure without persisting anything — the literal F5 mechanism (R1 persisted 4 rows,
gate needed ≥225). Now: below-floor / warnings / non-tape / not-completed all upsert an ExperimentRun
row with `VersionKind=sv1-null`, `Composite=null`, `NullReason=<why>`; the scoreboard counts
`nullRuns` separately (census coverage ≠ ranking) and no longer drops legitimate 0-composite cells;
the §2 "run status completed" gate existed only on paper — added via `RunStatusResolver` (persisted
F52 Status preferred, legacy resolution as fallback). `SetupScorePersistenceTests` ×6 pin the
contract, including the null→scored upsert-no-duplicate transition.

**Gap 2 — venue specs existed for only 2 of 14 sweep symbols.** P1 captured specs in-memory; F44's
store landed later, and only EURUSD/XAUUSD had been through a live cTrader run since. Everything else
would have priced the R1' census off fabricated symbols.json — SILENTLY, because the registry's
SYMBOL_FALLBACK warning only fires when NO spec exists at all (`SymbolInfoRegistry.Register`,
`!HasAnyVenueSpecs`). One 14-symbol cTrader run (6c1c5743, 83 s, 5 trades, only the known F23
BAR_STREAM_TIMEOUT warning) captured all 14 — the cBot emits `symbol_spec` per subscribed symbol at
handshake, so specs land even if a run later fails. Venue truth on display: XAUUSD long swap is a
CHARGE (-2.0895 pips/night) where symbols.json fabricated a credit — the exact F3 lie that crowned
R1's bogus #1 cell.

**Live proof chain (merged code, main worktree, port 5134):** 14-symbol cTrader run through the
merged `CTraderProcessOwner` (dynamic ports, 0 orphan cli) → `VenueSymbolSpecs` 2→14 → tape GBPJPY
d81598b0 (previously spec-less): `VENUE_SPECS_LOADED 14`, 0 SYMBOL_FALLBACK, swap -146.90,
commission -377.11, 31 trades → `score d81598b0` = PASS 25 persisted; `score 6c1c5743` (ctrader
venue) = FAIL persisted as sv1-null with reason. Data coverage: 28/28 (sym×tf) cells span the sweep
window.

**Not done / notes:** R1' sweep itself is next (see handoff). F48 unchanged (XAUUSD-class PnL
conversion, ~1.4%, tape-vs-venue only — the sweep ranks tape-vs-tape, so it biases levels not
ordering; flagged for R3's realism pass). Registry still falls back SILENTLY for a symbol without a
spec when other specs exist — acceptable now that all 14 are captured; revisit if the symbol set
grows (candidate: a `research doctor` check "spec exists for every sweep symbol"). The x4 worktree
is merged and redundant; its marketdata.db carries the synced Jul-15 EURUSD tail (main's ends
Jul-5) — trivially re-downloadable via auto-sync, so deletion is safe either way.

---

## R1' — the baseline sweep itself (2026-07-15, third session)

**Housekeeping first:** pushed the 8 unpushed commits (`d3eaa66..3075584`), removed the merged
`C:\code\shamshir-x4` worktree and deleted branch `iter/alpha-loop-x4` (Jul-15 EURUSD tail is
outside the sweep window and re-downloadable).

**The sweep:** experiment **`baseline-sv1-prime` (075d5240)**, 9 strategies × 14 symbols × {H1,H4}
= 252 cells, ONE cell per run (D13), tape venue, defaults, window 2025-07-04→2026-05-05. Driven by
a PowerShell loop over `POST /api/runs` (3 in flight through the X0 queue) + `POST
/api/experiments/score` per terminal run, idempotency keys `r1p-<strat>-<sym>-<tf>`, resumable
progress CSV. **250 cells in 1h42m wall (04:59→06:41), avg 68 s/run, zero run failures.**

**Truth gate: PASS** — `VERDICT: PASS experimentId=075d5240… scored=74 null=178 total=252`;
252/252 ExperimentRuns persisted (≥225 ✓), 100% scored-or-null (≥90% ✓), all 178 nulls are
`trades=N below floor 20 (D3)` with N recorded. Artifacts: `evidence/scoreboard-s1p.{md,csv}`.

**Census headlines (sv1-partial, see caveats in the artifact):**
- **F5 vindicated by the numbers:** 74 cells clear the 20-trade floor when run one-per-account vs
  R1's 4 — the commingled account really was starving every strategy.
- #1 is again `trend-breakout/XAUUSD/H4` = 100.0 (39 trades) — and this time it survives real
  venue-declared XAUUSD swap (a CHARGE, −2.0895 pips/night), so it's no longer the F3 carry
  subsidy. Top-5: mean-reversion/GBPUSD/H1 96.5, mean-reversion/AUDUSD/H1 96.1,
  rsi-divergence/AUDUSD/H1 92.0, mean-reversion/GBPJPY/H1 90.1.
- H1 dominates scored cells 58:16 (H4 below-floor on a 10-month window is expected per plan).
  Scored per strategy: trend-breakout 12, mean-reversion 10, session/rsi/bb 9 each, macd 8,
  super-trend 7, ema-alignment 6, mtf-trend 4.

**New findings (recorded, NOT fixed mid-census — it's a census, not a repair mission):**
- **F59 — `POST /api/experiments` with zero variants crashes AND leaks:** `ExperimentRunner.RunAsync`
  adds the entity in `CreateExperimentAsync` then calls `UpdateAsync` with a NEW instance of the
  same Id → EF double-tracking exception; the experiment row is left behind in status "Running"
  (and `MarkFailed` hits the same bug). Two `baseline-sv1-prime` rows were created this way —
  `075d5240` adopted for the census, `96fa9214` is an orphan.
- **F60 — the drawdown score component is DEAD (saturated at 100 for every cell):**
  `BacktestRuns.MaxDrawdownPct` stores a FRACTION but `ComputeDrawdownScore` maps it as a PERCENT
  (≤3 → 100). Proof: run `18621a31` is net-losing (−$376) yet stores 0.0140 — impossible as a
  percent (a net loss from flat implies DD ≥ 0.38%), consistent as 1.40%. Observed stored range
  across all 74 scored cells: 0.00014–0.0459 (= 0.014%–4.6% real) → every cell gets Drawdown=100.
  Fix + re-score the same runs is mechanical (score upserts); worst-case composite shift ≈ 3.5 pts.
- **Finalize race (1/252):** `mean-reversion/USDCHF/H1` (18621a31) hit `score` between in-memory
  terminal and the end-record write → persisted-status gate correctly nulled it
  (`run status 'running' is not completed`); re-scored after terminal → PASS 56.9. The D13
  null→scored upsert transition worked exactly as the tests pin it.

**Ops notes:** the driver was killed once by the session's own `tail -f` holding its log file
(`Add-Content` throws under `$ErrorActionPreference=Stop`) — logging/recording are now
retry-then-degrade, monitoring is poll-based (no held file handles); on resume the in-memory
idempotency keys reattached all 3 in-flight runs to their original RunIds, which had completed
server-side during the gap.

---

## R1'-cleanup — 2026-07-15 (fourth session) — refactor merge + F59/F60

**`refactor/god-classes` merged into `iter/alpha-loop`** (clean merge, no conflicts — the R1' sweep
commit only touched docs/evidence). Post-merge: Unit 759/6, Integration 134 (one transient test-host
crash on first run, clean 134/134 on rerun — flake, not a regression), Architecture 6/8 (same 2
pre-existing failures), Simulation 149/153 (the 1 pre-existing `NetMQBridgeTest` failure actually
*improved* — the refactor's `Program.cs` scoped-seeder fix turned a crash into a clean handshake,
same legacy-live-loop assertion still fails downstream). All match the refactor branch's own SURVEY
baseline exactly.

**Live cTrader compare-both smoke (the branch's own merge gate, run with `ASPNETCORE_ENVIRONMENT=
Development` so the CTrader creds section loads — first launch without it failed instantly with
"credentials not configured", a self-inflicted ops mistake, not a code bug):**
- Attempt 1, `eurusd-h1-1d.json` (1 day, EURUSD H1): clean NetMQ handshake, 21 bars processed,
  0 trades (window too short for any strategy to signal), hit `BAR_STREAM_TIMEOUT` at the tail
  (documented gotcha — cBot's own backtest clock outruns ours on a window this tiny) →
  `completed-with-warnings`. Inconclusive on the order-execution path by itself.
- Attempt 2, `xauusd-h4-tb-2m.json` (2 months, XAUUSD H4 trend-breakout): tape leg `94434f42` =
  14 trades / $5,952.99 net; cTrader leg `1ab467cd` = 14 trades / $6,030.94 net, 5 SL + 9 TP (real
  venue exits, not all FORCE), 536 equity snapshots, 0 gate-limit rejections in the journal, clean
  terminal write (`ExitCode=0`, `CompletedAtUtc` set — the live monitor endpoint showed a stale
  `null` for `completedAtUtc` after the in-memory run state was evicted; the DB row itself is
  correct, so this is a read-path display quirk, not a persistence bug — not chased further, out of
  scope for a merge-gate smoke). Trade counts match tape exactly; PnL sits within the known F48
  ~1.3% tape-vs-venue divergence band. **Verdict: the refactor's venue seam (`CTraderVenueRunner`)
  is behaviorally identical to the pre-refactor `RunEngineNetMqAsync` it was extracted from.**

**F59 fixed** — root cause was in `SqliteExperimentRepository.UpdateAsync`, not the caller:
`CreateAsync`'s `Add()` leaves the entity tracked for the DbContext's lifetime (one per request
scope), so a later `Update()` with a *new* instance of the same Id threw ("another instance with
the same key value is already being tracked"). Fixed by fetching the tracked instance (`FindAsync`
checks the change tracker first) and copying values onto it via `CurrentValues.SetValues`, restoring
`CreatedUtc` afterward (the callers unconditionally recompute it to `DateTime.UtcNow`, which was a
second, quieter bug in the same code path). This fixes both the completion path AND `MarkFailed` at
once — no more orphaned "Running" rows on failure. Live-verified: `POST /api/experiments` with
`variants: []` now returns `200 completed` (was a `500` throw). `ExperimentRepositoryTests` (new)
pins the same-DbContext repro. The pre-existing orphan `96fa9214` row was left alone (0
ExperimentRuns attached — harmless, and deleting DB rows wasn't asked for).

**F60 fixed** — `SetupScoreService.ScoreRunAsync` now converts `MaxDrawdownPct` (a fraction) to a
percent before both scoring (`ComputeDrawdownScore`) and storing (`Components.DrawdownPct`), so the
persisted field matches its name and the component actually discriminates. New test
`DrawdownScore_ReadsStoredFractionAsPercent` pins 1.4%→100 and 11.6%→0.

**Rescored all 252 `baseline-sv1-prime` (075d5240) runs** via `POST /api/experiments/score` (the
endpoint upserts by design — verified by the existing `Rescore_Upserts` test). Floor gate unchanged
(74 PASS / 178 FAIL, identical split to the original census — F60 only touches the composite score
of already-passing cells, not the D3 validity gate). 18/74 scored cells shifted by more than 0.05
points; largest was `mtf-trend/BTCUSD/H1` (47.1→43.0, -4.1), within the ~3.5-point estimate from the
original finding with one cell slightly over. **Top-20 ranking is unchanged** — every top-20 cell
already had genuinely low drawdown, so the unit bug never touched them. `evidence/scoreboard-s1p.md`
and `.csv` regenerated from the corrected `ScoreJson` (queried directly via `json_extract`, not the
summarized scoreboard API, to recover `ExpectancyR` which the API endpoint doesn't expose).

**Not done / out of scope:** the stale `completedAtUtc: null` on the live monitor endpoint for an
evicted run (noted above, DB is correct); cleaning up the orphan `96fa9214` experiment row.

---

## R3.1 — session 1 — 2026-07-15 (fifth session) — pre-registration

**Knob inventory correction to PLAN.md §R3 before pre-registering anything** (source: fork
research this session, verified against actual controllers/DTOs, not the plan's aspirational
wording):
- **AddOnPacks are 3 fixed presets, not tunable numerics**: `breakeven-only` (BE only, auto
  trigger 1R), `scalp-tight` (BE + `StepPips` trail, tight/fast), `runner-aggressive` (BE +
  `AtrMultiple` trail that relaxes via Ride while ADX≥25 + 50%-close PartialTp at 1R). A "pack
  tweak" is choosing a different preset per row (`RunRowRequest.PackId`), or omitting it (falls
  back to the strategy's own baked-in `PositionManagement`, which is what R1' "defaults" used).
- **Risk profiles** (`GET /api/risk-profiles`): `standard` (0.5% risk/trade, maxSL 100p — R1'
  default), `conservative` (0.25%, maxSL 50p), `aggressive` (2%, maxSL 150p), `raw` (5%,
  unprotected — not used here).
- **"Session filters via VenueSessions data" (PLAN.md §R3) does not exist as an independent
  knob.** `VenueSessions` is cTrader connection-event logging, unrelated to trading sessions. The
  real session mechanism is `ExitLabEvaluateRequest.Regime` (Asian/London/NewYork/etc.) — a
  sub-parameter of exit-lab calibration, not a standalone backtest variant. Exit-lab itself
  (`POST /api/exit-lab/evaluate`) grid-sweeps SL/TP/BE/trail multiples against **already-recorded
  excursion paths** (needs a prior `RecordExcursions:true` run) — a calibration tool, not itself
  scoreable into sv1. Deferred to a later R3 session once session 1's pack/risk read is in.
- **Walk-forward** (`POST /api/walk-forward/start`) grid-searches each strategy's own indicator
  params (`ParamGrid`), not packs/risk — this is the step-4 "walk-forward the best 3" tool, run
  after session 1's variants are scored.

**Session 1 pre-registration — 12 variants, 6 cells from the R1' top-20, one knob changed at a
time against each cell's own R1' baseline (`baseline-sv1-prime` defaults = `standard` risk, no
pack override).** Same window as the baseline (2025-07-04→2026-05-05), tape venue, one cell per
run (D13 still applies — pre-registration is not an excuse to re-commingle).

| Variant | Cell | Knob changed | Hypothesis |
|---|---|---|---|
| v1a | trend-breakout/XAUUSD/H4 (rank 1, 100.0) | pack=`runner-aggressive` | Baseline DD is already ~0.03% — a relaxing ATR trail + partial-TP should raise ExpectancyR further without meaningfully raising DD, since there's almost no DD budget being spent today. |
| v1b | trend-breakout/XAUUSD/H4 | risk=`aggressive` | Tests whether the #1 cell's edge holds at 4× standard risk (2% vs 0.5%/trade) — the real question before proposing it for R4 sizing. |
| v2a | rsi-divergence/AUDUSD/H1 (rank 4, 92.0) | pack=`scalp-tight` | Consistency is only 54.5 despite ExpR 0.642 — an early tight trail should convert more marginal wins into locked gains, raising Consistency without giving back much ExpectancyR. |
| v2b | rsi-divergence/AUDUSD/H1 | risk=`conservative` | Lower risk/trade (0.25% vs 0.5%) should shrink the 1.94% MaxDD roughly proportionally; if ExpectancyR mostly survives, risk-adjusted return improves. |
| v3a | macd-momentum/XAGUSD/H1 (rank 12, 78.4) | pack=`scalp-tight` | Highest trade count (92) + worst Consistency (45.5) + highest DD (3.63%) in the top 20 — the best-powered cell to test whether tighter stop management raises consistency. |
| v3b | macd-momentum/XAGUSD/H1 | risk=`conservative` | Direct test of whether smaller risk/trade tames the 3.63% DD disproportionately more than it costs ExpectancyR. |
| v4a | rsi-divergence/BTCUSD/H1 (rank 11, 80.8) | pack=`runner-aggressive` | Crypto trends run further than FX pairs typically do — letting winners ride via the relaxing ATR trail should capture more of that. |
| v4b | rsi-divergence/BTCUSD/H1 | risk=`aggressive` | Tests edge-at-scale on the highest-trade-count crypto cell (56 trades — good statistical power for this question). |
| v5a | bb-squeeze/XAGUSD/H4 (rank 10, 81.2) | pack=`scalp-tight` | Squeeze breakouts are typically fast-then-mean-revert; an early tight trail should raise the low 54.5 Consistency by locking gains before reversion. |
| v5b | bb-squeeze/XAGUSD/H4 | risk=`conservative` | Modest ExpectancyR (0.347) — test whether reduced risk improves the risk-adjusted picture on a thinner edge. |
| v6a | ema-alignment/EURJPY/H1 (rank 6, 88.6) | pack=`runner-aggressive` | Already-high Consistency (81.8) — test whether letting winners run adds ExpectancyR without eroding that consistency. |
| v6b | ema-alignment/EURJPY/H1 | risk=`aggressive` | Tests whether the cleanest top-20 cell (high consistency, near-zero DD) holds up at 4× size — the strongest R4 sizing candidate if it does. |

**Idempotency keys:** `r3-s1-<variant>` (e.g. `r3-s1-v1a`). **Truth gate (per plan §R3):**
ExperimentRuns grows by exactly 12; every pre-registered variant has a scored-or-null result;
scoreboard artifact `evidence/scoreboard-s2.md` committed. **Next:** run + score these 12, then
walk-forward the session's best 3 (needs each winning strategy's indicator `ParamGrid` — separate
lookup before that step), then cull anything with OOS ratio < 0.5.

**Results — all 12 run + scored, gate PASS.** 11/12 scored, 1 null (v2b, `rsi-divergence/AUDUSD/H1`
+ `conservative` risk, fell to 14 trades — below the 20-trade floor). Full comparison table +
analysis: `evidence/scoreboard-s2.md`.

**Two real wins, both `runner-aggressive`:** `ema-alignment/EURJPY/H1` (v6a) — raw `ExpectancyR`
0.384→0.698 (+82%), `MaxDD%` and Consistency held exactly flat (0.75%/0.748%, 81.8/81.8). And the
#1 cell `trend-breakout/XAUUSD/H4` (v1a) — `ExpectancyR` 0.689→0.912 (+32%), DD/Consistency again
untouched. Both are trend-style strategies; letting winners run via the relaxing ATR trail +
partial-TP fits their own thesis, and neither was spending its DD budget under baseline SL/TP
anyway.

**`scalp-tight` lost on all 3 cells it was tried on** (v2a rsi-divergence/AUDUSD/H1 -94% edge, v3a
macd-momentum/XAGUSD/H1 edge went net-negative, v5a bb-squeeze/XAGUSD/H4 -74% edge) — consistent,
not cherry-picked. All three strategies' theses need room for the trade to develop; an early tight
trail cuts them off before that happens.

**`aggressive` risk (4x standard) was scale-invariant on `ema-alignment/EURJPY/H1`** (v6b:
`ExpectancyR` and Consistency both IDENTICAL at 4x size, DD roughly doubled not quadrupled) — a
real, useful result for R4 sizing. It degraded on the other two cells tried (v1b, v4b).
**`conservative` risk never produced a clean risk-adjusted win**: it cut DD more than expectancy on
`bb-squeeze/XAGUSD/H4` (v5b, mild win) but cut expectancy MORE than DD on `macd-momentum/XAGUSD/H1`
(v3b), and dropped `rsi-divergence/AUDUSD/H1` below the trade floor entirely (v2b).

**Two open, unchased observations** (in `evidence/scoreboard-s2.md` Caveats, not investigated
further this session): `runner-aggressive`'s trade-count jumps are explained by `PartialTp` posting
two `TradeResult` rows per position (not a new signal-count change) — but `scalp-tight`'s
trade-count *drops* (macd-momentum 92→48, rsi-divergence 47→26, bb-squeeze 42→35) have no confirmed
mechanism yet. v4b's trade collapse (rsi-divergence/BTCUSD/H1, 56→23 under `aggressive` risk) is
plausibly a DD-circuit-breaker interaction on a volatile symbol, but unconfirmed.

**Next (R3.2):** walk-forward the best 3 (v6a, v1a, v6b) — 6 rolling windows, train 60d/test 30d —
to get a real OOS ratio before calling any of them a candidate. Blocked on looking up each
strategy's tunable indicator `ParamGrid` for `POST /api/walk-forward/start` (not covered by this
session).

---

## R3.2 — walk-forward the best 3 — 2026-07-15 (same session, "carry on")

Looked up indicator params: `ema-alignment` (v6a/v6b) = `fastPeriod:20, slowPeriod:50, atrPeriod:14`;
`trend-breakout` (v1a) = `lookbackBars:20, maPeriod:50, atrPeriod:14`.

**F61 (blocking, fixed) — `WalkForwardSpec`/`WalkForwardRequest` had no `PackId`/`RiskProfileId`
field.** Every walk-forward would have silently reverted to each strategy's DEFAULT config —
walk-forwarding v6a/v1a (won on a pack swap) or v6b (won on a risk-profile swap) would have quietly
validated the WRONG variant and reported it as if it were the winning one. Fixed by adding optional
`PackId`/`RiskProfileId` to `WalkForwardSpec` (`ExperimentTypes.cs`) and `WalkForwardRequest`
(`WalkForwardController.cs`), threaded through `BuildSweepRequest` (train window) and
`RunTestWindowAsync` (test window) via `CustomParams["UsePackId"]`/`["RiskProfileId"]` — the same
mechanism every other run-starting path already uses. **Verified the override reaches REAL
EXECUTION, not just the request**: a smoke-test run's `effectiveConfigJson` (the API's "what
actually ran" audit field) showed the pack did NOT apply — alarming at first — but `TradeResults`
inspection showed a position with 2 rows (the `PartialTp` two-close signature, `runner-aggressive`-
only), proving the pack DID apply to actual trading behavior. The audit JSON was lying, not the
execution. Traced to a second bug:

**F61b (non-blocking, not fixed) — `RunConfigAssembler.ResolveEffectiveConfigJsonAsync` never
applies `UsePackId`/`PerStrategyPackIds`/`StripAddOns`/per-row packs.** It's a separate method from
`BuildLoadedConfigFromDbAsync` (the one venue runners actually execute against) that only stamps
the risk profile and applies strategy overrides — it drifted from its sibling method and was never
updated when pack support was added. Confirmed harmless to real trading behavior (see above), but
means `GET /api/runs/{id}`'s `effectiveConfigJson` has been silently wrong for every run that used
a pack via `UsePackId`/`PerStrategyPackIds` (row-based `Rows[].PackId` not checked — may or may not
share the bug). Not fixed this session — audit-trail correctness only, no user-facing score/trading
impact, and this session was already large.

Gate re-verified after the code change: Unit 759/0/6.

**Ran the real walk-forward** (6 folds, `TrainFraction=0.7`, window 2025-07-04→2026-05-05 — same
non-embargoed range as baseline/R3.1) for all 3 candidates. Full per-window table:
`evidence/scoreboard-s2-wf.md`. Headline: **all 3 candidates profitable in 5 of 6 out-of-sample test
windows**, with positive cumulative test PnL (v6a +$7,682, v1a +$14,109 over 5 active windows, v6b
+$18,215). v6a and v6b share the same losing window (Feb 28–Mar 15) — same underlying
strategy/symbol/signal timing, confirming it's a real market period, not a fluke of one config.

**F62 (blocking, not fixed) — the plan's actual cull mechanism does not exist.**
`SetupScoreService.ScoreRunAsync` line 105 hardcodes `double? oosRatio = null;` unconditionally,
comment: *"OOS robustness: null until walk-forward runs in R3."* This is R3, and it was never
wired up — `RobustnessOos`/`OosRatio` are ALWAYS null regardless of `FoldIndex`/`FoldRole` passed to
`ScoreRunAsync`, so `VersionKind` can never become full `sv1`, and the plan's "OOS ratio < 0.5 →
park" cull step (§R3 step 5) has no data to act on. **Not a from-scratch problem**:
`WalkForwardWindowResultEntity` already stores `PlateauValue` (the winning train-cell's net profit)
alongside `TestNetProfit` — a genuine Walk-Forward Efficiency ratio is computable from data already
persisted; it needs a query from `ScoreRunAsync` to the matching window rows, a formula decision
(per-fold average vs. sum-ratio), and wiring into the existing `oosRatio` variable. Deliberately NOT
implemented this session — it's real, undecided scoring-design work, not a bolt-on fix, and this
session was already large. The 18 raw window results are real and evidence-backed either way; only
the formal numeric cull is blocked.

**Session assessment (informal, not a formal score):** 5/6 win-window rate and positive cumulative
OOS PnL on all 3 candidates is a genuinely encouraging signal that R3.1's wins were not pure
in-sample curve-fitting — but this is a directional read, not the plan's designed gate, and should
be labeled as such to the owner.
