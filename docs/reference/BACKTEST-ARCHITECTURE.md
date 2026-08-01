# Backtest Architecture

**Last updated**: 2026-07-16 (post iter-alpha-loop close + refactor/god-classes merge)

This document explains how backtesting actually works — venue paths, data flow, fill and cost
semantics, and journaling. Read this before touching any backtest-related code. Companions:
`SYSTEM-REFERENCE.md` (system overview), `RESTING-ORDER-CONTRACT.md` (normative fill rules),
the `shamshir-ctrader` skill (cTrader path in depth).

---

## Venue selection

Venue is a **per-run custom param** (`CustomParams["Venue"]`), resolved by the `IVenueRunner`
seam (`TradingEngine.Web/Services/Venues/`) — not by any global config flag. `ctrader` is an
explicit opt-in; everything else runs credential-free through `ReplayVenueRunner`. Every venue
drives the same kernel loop (`Host/KernelBacktestLoop`).

| `Venue` | Adapter | Bars from | Speed | Use |
|---|---|---|---|---|
| `tape` | `TapeReplayAdapter` | `IMarketDataStore` (downloaded canonical history, deduped, auto-synced) | sub-second/run | **All scored research** |
| `replay` (default) | `BacktestReplayAdapter` | legacy `Bars` table | fast | Legacy/dev |
| `sim`/`simulated` | `SimulatedBrokerAdapter` | CSV + synthetic ticks (4/bar) | fast | Test harnesses |
| `ctrader` | `CTraderBrokerAdapter` + cBot over NetMQ | cTrader itself | ~50–80 s/run | Parity guard, E2E, live |

### Path A — Tape (research default)

```
RunsController → BacktestOrchestrator (queue) → ReplayVenueRunner
  → EngineHostFactory (inner IHost, RunDataCache handoff)
  → TapeReplayAdapter streams bars from IMarketDataStore
  → KernelBacktestLoop per bar: BarEvaluator → Kernel (PreTradeGate + KernelSizing)
    → EffectExecutor → fills via VenueFillModel → costs via TradeCostCalculator
  → StepRecord journal + TradeResults + EquitySnapshots (write-through to RunDataCache)
```

Key semantics (all measured against recorded cTrader behaviour, not assumed):

- **Fills**: a resting order (limit/stop/SL/TP) fills at the **first M1 O/H/L/C tick to breach
  its level — never at the level itself** (`VenueFillModel.FirstBreachingTick`, F43). Stops fill
  through; limits fill better; gap-through opens are the `Open` branch of the same rule. Buy-side
  touch tests use the ask (`SpreadConvention`); exit levels get no double spread.
- **HonestFills** (default ON): honest entry timing — no same-bar clairvoyance; opt out only for
  diagnostics (`CustomParams["HonestFills"]="false"`).
- **Entries**: `LimitOffset` is the research default (D11) — entry price reproducible by
  construction. Expiry counted in bars → `OrderCancelled`/`ENTRY_EXPIRED`.
- **Spread**: constant per run (research standard: 1.0 pip). Per-bar recorded spread is roadmap.
- **Excursions** (opt-in `RecordExcursions`): per-bar excursion paths for the exit lab.

### Path B — cTrader (truth venue)

```
BacktestOrchestrator → CTraderVenueRunner
  → CTraderProcessOwner launches ctrader-cli on DYNAMIC ports (PID-owned, orphan-reaped)
  → cBot (TradingEngineCBot) connects via NetMQ, lock-step per bar:
      bar → engine … engine → bar_done{commands} → cBot executes natively → bar_result
  → cTrader owns fills/SL-TP/costs; engine reconciles to the venue's open set per bar
    (ExitMode: VenueManaged)
  → cBot also writes its own ledger (shamshir-report.json) — survives CLI crashes
```

Desktop capture variant: `CTraderListenService` listens on fixed ports 15555/15556; a human runs
the cBot in cTrader Desktop. cTrader runs are strictly serial; tape runs pool concurrently.
Protocol + gotchas: `shamshir-ctrader` skill.

### Parity between A and B

Permanent gate (`research parity`, `ParityGateService`/`LedgerReconciler`) with a pre-registered
tolerance budget: trade count exact · entry ≤1 tick · lots exact · exit ≤1 tick on ≥95% ·
commission ≤2% · swap ≤5% · net ≤1% of gross. EURUSD `VERDICT: PASS`. Open residuals: F47
(venue's one-spot commission pricing — accepted), F48 (XAUUSD ~1.37% pip cross-rate timing).
Owner-facing candidates need a parity verdict ≤ 14 days old (D12).

---

## Cost computation (D9/D10)

One convention everywhere: **costs are NEGATIVE**, `Net = Gross + Commission + Swap`,
invariant-tested on every `TradeResult` row on both venues.

```
Gross      = PipCalculator.GrossPnL(entry, exit, direction, lots, symbolInfo, crossRate)
Commission = per CommissionType — this broker: USD per million USD notional, charged per SIDE
             (half at entry price on open, half at exit price on close); research runs: $30/M RT
Swap       = nightsHeld × venue-declared signed rate(direction) × lots
             (weekends free; triple on TripleSwapWeekday, default Wednesday)
```

Symbol economics are **venue-declared**: the cBot emits `symbol_spec` on connect
(commission+type, swap rates+calc type, lot/pip/tick size, digits);
`SymbolInfoRegistry.MergeVenueSpec` merges everything **except spread** (F24 — spread would
poison the ATR-based `MaxSlPips` heuristic). `config/symbols.json` is a loudly-logged fallback
only. Caveats: specs are process-lifetime in-memory (F25); `SwapCalculationType` captured but not
dispatched on (F28); the pre-trade gate's worst-case commission ignores `CommissionType` (F26).

---

## Journal

The **StepRecord stream is the only journal** (D83): `ChannelJournalWriter` (Wait-mode, lossless)
→ `ScopedStepRecordSink` → `SqliteStepRecordSink` (`JournalEntries`, batched, cache-pushed).
`PipelineEvents`/`BarEvaluations` are dead. API: `GET /api/runs/{id}/journal?afterSeq=&limit=`
(+ `/journal/export` NDJSON). Rejections carry `GuardResult` reasons (e.g. `SL_TOO_WIDE:...`);
closes carry itemized costs.

---

## Data flow: RunId → DB

```
BacktestOrchestrator: RunId (8-char) → RunRecordStore (BacktestRuns row incl. EffectiveConfigJson,
  DatasetId/ConfigSetId/Seed identity, Venue, ParentRunId for duplicates)
Inner host (EngineHostOptions): every writer stamps RunId —
  SqliteStepRecordSink   → JournalEntries
  TradePersistenceHandler→ TradeResults  (StrategyId + Symbol + EntryTimeframe attribution)
  EquityPersistenceHandler→ EquitySnapshots
Finalization: single FinalizeRunAsync (orchestrator) → terminal status + totals.
"No lies" invariant (completed runs): TotalTrades == COUNT(TradeResults) — see PLAN.md §6 query.
```

Reads are cache-first (`RunDataCache`, write-through singleton shared Web ↔ inner host); the
live monitor is SignalR push from in-memory state (zero DB). Research tables: `Experiments`
(SpecJson = pre-registration), `ExperimentRuns` (ScoreJson), `StrategyCellParks`,
walk-forward jobs/window results.

---

## PartialTp row-splitting (F70 — read before analyzing trades)

A `PartialTp` close writes **two `TradeResult` rows per position** (the 50%-at-1R row is a
mechanically near-guaranteed positive R). Row-level `ExpectancyR` is therefore **not comparable
across partial/non-partial configs** — it inflated R3's "8/8 runner-aggressive" result. Family-
or variant-level evaluation must use **position-level dollars** (rows minus PARTIAL rows for
position counts). See `iter-structural-edge/LEDGER.md` S1.1.

---

## Schema management

EF Core migrations only — no raw SQL patches.

```
dotnet ef migrations add <Name> --startup-project src/TradingEngine.Web --project src/TradingEngine.Infrastructure
```

---

## Test status (baseline 2026-07-16)

| Suite | Count | Credentials |
|-------|-------|-------------|
| Unit | 767 pass / 6 skip | No |
| Integration | 153 | No |
| Simulation fast (`RequiresCTrader!=true`) incl. determinism + golden + resting-order contract | 144 | No |
| Architecture | few, seconds | No |
| cTrader E2E (`RequiresCTrader=true`) | — | Yes (+ desktop install) |

Credential-free green does **not** prove cTrader behaviour — venue-path changes require a live
compare-both smoke (F24 doctrine; `docs/reference/INVESTIGATION-METHOD.md`).
