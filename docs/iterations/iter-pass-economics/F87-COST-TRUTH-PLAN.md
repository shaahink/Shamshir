# F87 — Cost Truth: nullable overrides, per-bar spread, venue-true commission (Lane D agent plan)

**For the implementation agent. Self-contained — read this fully before touching code.**
Parent program: `docs/iterations/iter-pass-economics/PLAN.md` (E0, gate GE1). Evidence base:
`docs/AUDIT-POST-VIABILITY-2026-07.md` §2. Finding number **F87** (ledger continues from F86).

## Why this exists (context you must not lose)

Every scored research run of the last two programs (V2 + V4 censuses, 221k positions) charged a
**flat 1-pip spread** and a **flat $30/M commission**, because two non-nullable request defaults
always won over the honest cost paths that already exist in the engine:

1. `src/TradingEngine.Web/Dtos/Runs/StartRunRequest.cs:9` — `public double SpreadPips { get; init; } = 1;`
   flows unconditionally through `src/TradingEngine.Web/Services/Venues/ReplayVenueRunner.cs:227`
   (`spreadPipsOverride: (decimal?)cfg.SpreadPips`) into the **top-priority** branch of
   `TapeReplayAdapter.GetSpread()` (`src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs:432-440`).
   The chain there is: override → `_currentSpread` (recorded per-bar Dukascopy spread, set at
   `:263`/`:291`) → registry `TypicalSpread`. **The per-bar branch is correct and already built —
   it was simply unreachable** because the override was always non-null.
2. `StartRunRequest.cs:8` — `public double CommissionPerMillion { get; init; } = 30;` flows into
   `TradeCostCalculator.ComputePerSideCommission`
   (`src/TradingEngine.Services/Helpers/TradeCostCalculator.cs:126-168`), whose venue-true
   dispatch on `symbol.CommissionType` (AbsolutePerLot / UsdPerMillionUsdVolume / Pips /
   PercentOfNotionalValue — cTrader-verified, F39/F46) **only runs when the override is null**.
   Venue-captured economics are already overlaid onto the symbol registry at engine start
   (`src/TradingEngine.Host/EngineServiceCollectionExtensions.cs:122`,
   `symbolRegistry.UpsertVenueSpec` — F44).

Consequence: FX majors were over-taxed ~2× (EURUSD median recorded spread ≈0.4 pip vs 1 pip
charged), metals/crypto under-taxed 60–500×. The fix is small and surgical: **make both overrides
nullable end-to-end, default null, and null means "use the honest path that already exists."**

There are also **three independent spread sources** live in one run today:
- **Fills**: `TapeReplayAdapter.GetSpread()` (the override chain above).
- **Floating PnL**: `TapeReplayAdapter.cs:772` reads `symbolInfo.TypicalSpread` directly.
- **Strategy tick**: Host evaluators synthesize the close tick with `TypicalSpread / 2`
  (`BarEvaluator.cs:283-291`, `TradingLoop.cs:258`, `KernelTrailingEvaluator.cs:124`,
  `HistoricalDataProvider.cs:71`).
P4 unifies the **number** they read (one resolver, per-bar recorded spread when available).
It does **not** change any site's offset convention — see the hard rules.

## Hard rules

- **R1 — No behavior change when an explicit value is passed.** An explicit `SpreadPips` /
  `CommissionPerMillion` must behave byte-identically to today (this is the compare-both parity
  contract, F32/F39: tape and cTrader legs share ONE number when a number is given). Only the
  *null* path is new.
- **R2 — Costs are NEGATIVE** (`D9`): `Net = Gross + Commission + Swap` must remain exact to the
  cent. Any new cost column follows the same convention. `TradeCostCalculator` is the single
  source of truth — do not duplicate its math anywhere.
- **R3 — Do not change any spread OFFSET convention.** Full-spread ask on fills
  (`SpreadConvention`, P0.2) and half-spread synthesized strategy ticks stay exactly as they
  are. F87 unifies which *number* feeds them, nothing else. If you believe a convention is
  wrong, write it in the handover — do not fix it.
- **R4 — cTrader venue requires an explicit spread.** The cTrader CLI needs a concrete
  `--spread`; there is no per-bar path there. Null `SpreadPips` + `Venue=ctrader` (or
  `CompareBoth=true`) must fail fast at request validation with a clear message, not silently
  pick a number.
- **R5 — Scope.** No refactors beyond what phases say. Do not touch `docs/iterations/iter-viability/`,
  anything under `evidence/`, holdout/embargo data, or research drivers other than the new file
  P5 creates. Do not modify `TradeCostCalculator`'s formulas.
- **R6 — Tests before code** per phase; `scripts/gates.ps1` green before every commit.
  Verified baseline at plan handoff (2026-07-27, main): build 0 err · Unit 805/0/6 ·
  Integration 156/0/0 · Sim-fast 144/0/0. Counts will grow; none may fail. Commit prefix per
  phase, e.g. `f87(p1): ...`.
- **R7 — EF migrations**: use `dotnet ef migrations add M54_TradeSpreadCost` (next free number
  after M53_RunNotes) against `TradingDbContext`; never hand-edit the model snapshot.

## Phases

### P0 — Recon + green baseline
**Goal:** enumerate every touch point; prove the world is green before you change it.
**Do:** `git grep -n "SpreadPips\|CommissionPerMillion"` across `src/`, `tests/`, `web-ui/src/`.
Known sites you must account for (non-exhaustive — trust the grep, not this list):
`StartRunRequest.cs`, `ReplayVenueRunner.cs:226-227,236`, `CTraderVenueRunner.cs`,
`SweepRunnerService.cs`, `BacktestOrchestrator.cs`, `BacktestRunState.cs`, `RunRecordStore.cs`,
`RunDetailQuery.cs` + `RunDetailResponse.cs`, `RunsController.cs`, `SqliteBacktestRunRepository.cs`,
`BacktestRunEntity.cs`, `CtraderListenConfig.cs` / `CTraderListenService.cs`,
`web-ui/src/app/features/runs/new-backtest/new-backtest.component.ts:609,758`,
`web-ui/src/app/features/ctrader-sessions/ctrader-sessions.component.ts:180`,
`web-ui/src/app/models/api.types.ts:68,251`.
Run the fast suites; record counts in the handover.
**Acceptance:** grep inventory table in handover; suites green.
**Commit:** `f87(p0): recon inventory, no code change` (docs/handover only, or skip commit).

### P1 — Nullable `SpreadPips` end-to-end (null ⇒ recorded per-bar spread)
**Goal:** `SpreadPips` becomes `double?`, **default null**; null reaches `TapeReplayAdapter`
as `spreadPipsOverride: null` so the existing chain falls through to per-bar recorded spread.
**Test-first:** flip/extend `tests/TradingEngine.Tests.Unit/Phase31Tests/TapeReplaySpreadOverrideTests.cs`:
- KEEP `WithOverride_UsesRunSpreadPips_NotRegistryValue` and the explicit-zero test (R1).
- The null-path test currently proves null ⇒ registry `TypicalSpread` on bars *without* spread.
  ADD the decisive one: null override + bar carrying `Spread` ⇒ fill priced with **that bar's
  recorded spread** (assert exact fill price), and a following bar with a *different* recorded
  spread produces a *different* fill — flat-spread world is provably gone.
- ADD request-validation test: null `SpreadPips` with `Venue=ctrader` or `CompareBoth=true`
  is rejected with an actionable message (R4).
**Do:** DTO → `BacktestRunConfig`/state → both runners → persistence (`BacktestRunEntity` /
run record / detail response: nullable column or sentinel-free mapping; display "per-bar" when
null) → SPA: run form defaults to blank (placeholder "per-bar (recorded)"), sends null when
blank; `ctrader-sessions.component.ts` keeps its explicit `1` (it drives the cTrader venue, R4).
`RunDetailQuery`/report chip (`run-report.component.ts:195`) renders "per-bar" instead of `?? 0`.
**Acceptance:** new tests green; suites green; a tape run started with an empty spread field
persists and displays null/"per-bar".
**Commit:** `f87(p1): nullable SpreadPips end-to-end, null = recorded per-bar spread`.

### P2 — Nullable `CommissionPerMillion` end-to-end (null ⇒ venue-true dispatch)
**Goal:** same shape as P1: `double?`, default null; null reaches both adapters
(`ReplayVenueRunner.cs:226,236`) so `TradeCostCalculator` dispatches on the symbol's own
venue-captured `CommissionType`/rate.
**Test-first:** unit test: null override + a `SymbolInfo` with `CommissionType=UsdPerMillionUsdVolume,
rate=45` charges 45/M (the venue-true FX rate); explicit 30 still charges 30/M (R1).
Validation mirror of R4 for cTrader venue (its CLI `--commission` also needs a number).
**Do:** DTO/state/runners/persistence/SPA as in P1. Plus a **run warning** (existing RunWarnings
mechanism, M41) when commission is null and a run symbol has **no venue-spec overlay** — its
symbols.json economics are fabricated and the run must say so on its face.
**Acceptance:** tests green; a null-commission tape run on a venue-captured symbol books
commission at the captured rate (visible in trade `CommissionAmount`s).
**Commit:** `f87(p2): nullable CommissionPerMillion, null = venue-true CommissionType dispatch`.

### P3 — Per-trade spread cost recording (`SpreadCostAmount`)
**Goal:** every closed trade records what spread actually cost it, so signal-vs-toll
decomposition becomes a column read instead of offline archaeology.
**Test-first:** synthetic tape run (unit level, the `TapeReplaySpreadOverrideTests` harness
pattern): Long entry at ask with spread s ⇒ `SpreadCostAmount == -(s / pipSize) × pipValue × lots`
(negative, R2); Short pays at exit instead; invariants `Net == Gross + Commission + Swap`
(unchanged) and `SignalPnL ≡ Gross − SpreadCost` equals the mid-to-mid recomputation from the
same bars.
**Do:** add `SpreadCostAmount` (decimal, default 0) to `TradeResultEntity` + migration
`M54_TradeSpreadCost` (R7); `TapeReplayAdapter` computes it at its fill sites (it knows `spread`
and `lots` at each fill — entry side for Long, exit side for Short, both sides where a path
crosses spread twice; partial closes prorate like `EntryCommission` at `:711`); thread through
the close/record path into persistence. `BacktestReplayAdapter` may write 0 (it is not the
research path) — document that in the column comment.
**Acceptance:** invariant tests green; migration applies cleanly to a copy of trading.db.
**Commit:** `f87(p3): record per-trade SpreadCostAmount (M54)`.

### P4 — Unify the three spread sources (one number, same conventions)
**Goal:** floating PnL and synthesized strategy ticks read the same spread *number* as fills.
**Test-first:** characterization tests pinning each site's CURRENT offset convention (full-spread
ask on fills and floating PnL, half-spread on synthesized ticks) — they must still pass after
the change; new tests: with per-bar spread present, floating PnL (`TapeReplayAdapter.cs:772`)
and the Host tick resolvers use it; without, registry fallback as today.
**Do:** `TapeReplayAdapter.cs:772` → `GetSpread()`. Host evaluators
(`BarEvaluator`/`TradingLoop`/`KernelTrailingEvaluator`/`HistoricalDataProvider.ResolveHalfSpread`):
resolve from the observed bar's recorded `Spread` when present, else `TypicalSpread`, keeping the
`/ 2` at those sites (R3). Extract one small shared resolver so a fourth divergent copy can't
appear; keep it in Infrastructure/Services where all four call sites can reach it.
**Acceptance:** characterization + new tests green; suites green.
**Commit:** `f87(p4): single spread-number resolver across fills / floating PnL / strategy tick`.

### P5 — Canonical cost-decomposition helper (harvest columns)
**Goal:** `gross / spread / commission / swap / net / signal` become standing columns every
future harvest gets from ONE place (the V2/V4 harvests each discarded `GrossPnLAmount`
independently — that class of loss ends here).
**Do:** new `tools/research/cost_decomposition.py`: given a sqlite path + run-id set (or
experiment label), emits per-run and per-cell aggregates of `GrossPnLAmount`, `SpreadCostAmount`,
`CommissionAmount`, `SwapAmount`, `NetPnLAmount`, `SignalPnL = Gross − SpreadCost`, n, $/pos.
`--selftest` flag builds an in-memory fixture DB and asserts the invariants (R2 conventions,
Net identity, Signal identity). Follow `block_bootstrap.py`'s style (stdlib-only, selftest
pattern).
**Acceptance:** `python tools/research/cost_decomposition.py --selftest` PASS, pasted in handover.
**Commit:** `f87(p5): cost_decomposition.py — standing harvest columns + selftest`.

### P6 — GE1 probe (live proof on real data)
**Goal:** one real run demonstrably charging recorded spread + venue commission.
**Do:** launch the app (see `docs/` run instructions / `run-shamshir` conventions), start ONE
tape cell via API: EURUSD H1, 2023-01-01→2023-03-31, `SpreadPips: null`,
`CommissionPerMillion: null`, research toggles as the program's D2 dictates
(`MaxDdEnabled:false`, `GovernorEnabled:false`, `DisableRegime:true`). Then SQL against the DB:
per-trade `SpreadCostAmount` implied pips vary trade-to-trade and match the recorded M1/H1
`Spread` at each entry minute (spot-check ≥10 trades); `CommissionAmount` consistent with the
venue-captured rate, not $30/M; `Net = Gross + Comm + Swap` exact on every row.
**Acceptance:** paste the queries + outputs verbatim into the handover (Lane R re-verifies at
GE1 — outputs, never assertions).
**Commit:** `f87(p6): GE1 probe evidence in handover`.

## Definition of Done
- All phases committed, fast suites green (Unit/Arch/Golden), counts reported vs baseline.
- `TapeReplaySpreadOverrideTests` proves: explicit value wins (unchanged); null ⇒ per-bar
  recorded spread; per-bar variation reaches fills.
- No scored-run path can silently reintroduce a flat spread: null is the default everywhere a
  research run is born (API default, SPA form default).
- Handover file `docs/iterations/iter-pass-economics/F87-HANDOVER.md`: inventory table, test
  counts, migration name, probe evidence, and any convention doubts (R3) — written for the Lane R
  session that ratifies GE1.

## Sequencing / notes
- P1 and P2 are independent after P0; P3 depends on P1 (it reads the spread the fill actually
  used); P4 after P3 (shared resolver touches the same sites); P5 after P3 (needs the column);
  P6 last.
- The two-lane rule (iter-viability D9) applies: this plan owns engine + `tools/research/cost_decomposition.py`
  only; Lane R owns the DB and everything under `docs/iterations/` except your handover file.
- F83 (idempotency namespacing) and F84 (WAL: `wal_checkpoint(TRUNCATE)`, never VACUUM
  mid-batch) are inherited operational law if you touch the DB for the probe.
