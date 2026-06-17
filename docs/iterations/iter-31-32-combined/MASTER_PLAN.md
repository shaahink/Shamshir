# Combined Iter-31 + Iter-32 — Master Plan (CONFIRMED)

**Status:** All decisions confirmed by owner. Go for implementation.

## Why combine

Iter-31 (costs/journal/order-entry) and Iter-32 (config-as-editable-data) are specified as sequential but
have mostly independent surfaces. Combining them lets us:

- Run foundational phases in parallel (4 independent tracks at start)
- Coordinate the two DB schema changes (journal taxonomy + config store) in one migration window
- Resolve the New-Backtest form overlap once instead of touching it in both iterations
- Keep suites green with fewer intermediate states

Total: **~22 phases** across 5 waves. Each phase is failing-test-first, gate-gated, small commit.

---

## Architecture decisions — CONFIRMED

### ✅ Q2 — Symbol/timeframe model → **Option A (confirmed)**

Strategy keeps `symbols`/`timeframe` as **defaults**, overridable per run via a **run plan**
`(strategyId, symbol, timeframe)[]`. Gives per-run symbol selection (RW-05) without a schema-wide refactor.

### ✅ Q1 — Source of truth for config → **DB canonical (confirmed)**

JSON = one-time seed + manual export only. UI writes → DB. DB is authoritative.

### ✅ Remaining decisions — all defaults confirmed

| ID | Question | Answer |
|----|----------|--------|
| 31-Q1 | Which strategies get `LimitOffset`? | All `Market`, only `mean-reversion` on `LimitOffset` |
| 31-Q2 | Limit expiry behaviour? | Cancel + journal `ENTRY_EXPIRED` (no market fallback) |
| 31-Q3 | `equityDefinition` string? | Delete it — net already includes costs after A1 |
| 31-Q4 | Cost values for bundled symbols? | FX majors ≈3.5 commission/side, small ±swap, marked as estimates |
| 32-Q3 | Override granularity? | Deep-merge (override only fields you set) |
| 32-Q4 | Where overrides live? | On run record; snapshot effective config with run |
| 32-Q5 | Validation + write-back? | Reuse ConfigLoader cross-reference checks; JSON export only on explicit action |

### ✅ Additional owner requirements

1. **Backtest must record** exactly which strategies, timeframes, parameters, risk profiles ran — the
   effective config snapshot on the run record (32-P3) satisfies this.
2. **All defaults must be viewable/editable in UI** — max position cap, sizing policy, governor limits,
   risk profile parameters. The Strategy edit UI (32-P4) surfaces everything from the stored config
   including `positionManagement`, `orderEntry`, `regimeFilter`, `riskProfileId`, `sizingPolicy`.
3. **Every new field/data added must flow into the backtest report and all relevant UI forms** — costs
   (commission/swap/gross/net) → Report, Trade Detail, Monitor. Journal entries → Report Journal tab.
   Effective config → Run detail. Limit order events → Journal + Monitor.
4. **No new EF migrations** — redo the initial migration (`InitialCreate`) once all schema changes land.
   This is simpler and the project already did this once (`e386159`).

---

## Combined phase plan

### Wave 0 — Verify baseline (before any code)

| Phase | What | Gate |
|-------|------|------|
| **BASE** | Run Unit + Simulation + Architecture suites, capture baseline | Suites green (note any pre-existing failures as out-of-scope) |

### Wave 1 — Foundational (4 parallel tracks, no cross-dependencies)

| Phase | Track | What | Key files | Gate |
|-------|-------|------|-----------|------|
| **31-A0** | A | Cost data on `SymbolInfo` + `symbols.json` | `SymbolInfo.cs`, `symbols.json`, `SymbolCatalog.cs` | SymbolCatalog loads new fields; missing fields → 0s; existing tests green |
| **31-B0** | B | Normalized journal taxonomy + `GET /api/backtest/{runId}/journal` API | `PipelineEventWriter.cs`, new `JournalController.cs`, `RunProjection.cs` | API returns seeded events in seq order; `filter=CLOSE` maps correctly; `afterSeq` pages without gaps |
| **31-C0** | C | `EntryPlanner`: config → order type + limit price + re-derived SL/TP | New `EntryPlanner.cs` (Services), `TradingLoop.cs` | `Market`→`(Market,null)`; `LimitOffset+N`→`(Limit, signal−N·pip)` with SL/TP re-derived; offset 0→limit at signal |
| **32-P0** | D | Strategy-config EF store + idempotent seed-from-JSON | New entity + migration, `IStrategyConfigStore`, `ConfigLoader.cs` (seed path) | Empty DB → seed populates; `GetAll()` ≡ JSON loader output; re-seed is no-op |

**Wave 1 cross-track dependency:** 32-P0 seeds `StrategyConfigEntry` from JSON — it picks up the `OrderEntryOptions` shape as-is from today's JSON. 31-C0 doesn't change that shape (it activates existing dead fields), so 32-P0 is safe to run in parallel. The new `RegimeOptions` added in the iter-28/29/30 commit (`config/regime.json`) should also be seeded if it's per-strategy. Verify before starting.

### Wave 2 — Core plumbing (3–4 parallel tracks)

| Phase | Track | What | Depends on | Gate |
|-------|-------|------|------------|------|
| **31-A1** | A | Simulated venue computes & stamps costs on `ExecutionEvent` | 31-A0 | Sim test: trade across N rollovers → `Commission==lots×perSide×2`, `Swap==N×rate×lots`, `Net==Gross−Commission−Swap`, equity/drawdown reflect net balance |
| **31-C1** | C | Sim venue rests + expires limit orders | 31-C0 | Sim test: limit fills at limit price when bar reaches it; non-reaching limit expires with cancellation event |
| **32-P1** | D | Route host config through `IStrategyConfigStore` (JSON → seed/export only) | 32-P0 | Backtest runs from DB config; deleting JSONs after seed doesn't affect run |
| **31-B1** | B | Persisted journal viewer on Report page | 31-B0 | Finished run displays full journal; filters work; >1000 events loads paged |

**Note:** 31-A1 and 31-C1 both modify `SimulatedBrokerAdapter` — these MUST be sequential on that file to avoid merge conflicts. Do A1 first (simpler), then C1 builds on it. OR merge carefully after both.

### Wave 3 — Integration + decoupling

| Phase | Track | What | Depends on | Gate |
|-------|-------|------|------------|------|
| **31-A2** | A | Live itemization — cBot emits `commission`/`swap` | 31-A1 | Adapter test: EXEC parse populates commission/swap; net unchanged |
| **31-A3** | A | Costs reach Report UI + `equityDefinition` cleanup | 31-A2 | Run-level `NetProfit == Σ trade net`; Report shows commission/swap totals |
| **31-B2** | B | Lossless live journal (poll API, drop 30-item cap) + equity sparkline fix | 31-B0 | >30 events all shown in Monitor; sparkline past 500 frames |
| **31-C2** | C | Live limit path end-to-end | 31-C1 | Adapter test: `LimitOffset` intent → non-zero `limitPrice` in order frame |
| **32-P2** | D | Run plan: decouple symbol/TF from strategy (per Q2 default) | 32-P1 | Run plan adds `USDJPY/H4` to a strategy → trades there; removes default symbol → doesn't trade it |

### Wave 4 — UI + overrides + polish

| Phase | Track | What | Depends on | Gate |
|-------|-------|------|------------|------|
| **32-P3** | D | `EffectiveConfigResolver` + per-run override persistence | 32-P2 | Overriding TP only leaves others inherited; two runs different overrides → different configs; stored default unchanged |
| **32-P4** | D | Strategy browse/edit UI (replaces empty Strategies page) | 32-P3 | Edit in UI persists to store; survives reload; invalid edit rejected with message |
| **31-B3** | B | Persist equity curve (AccountSnapshots) | 31-B2 | Post-run equity curve from persisted snapshots; curve-end == `initialBalance + Σ trade net` |
| **31-B4** | B | Unified reconciled stats panel | 31-B3 | `NetPnL(stats) == Σ trade net == equityCurve.end` |
| **31-C3** | C | Config + worked example (mean-reversion → LimitOffset) + journal entries | 31-C2, 31-B1 | Limit-configured strategy → at least 1 limit fill + 1 expiry in journal; all others stay Market |

### Wave 5 — Final integration + cleanup

| Phase | Track | What | Depends on | Gate |
|-------|-------|------|------------|------|
| **32-P5** | D | New-Backtest per-run override UI (run plan + knob tweaks + effective config preview) | 32-P3, 31-B0 | Run launched with overridden TP → persisted effective config reflects override; stored default unchanged |
| **32-P6** | D | JSON export + docs + dead-config note | 32-P5 | Export round-trips; solution builds; all suites green; no new warnings |
| **31-A4** | A | (Optional) Commission-aware risk budget | 31-A3 | Unit test: sized lots produce worst-case loss within budget |

---

## Combined DoD

1. All non-optional gates met across both iterations
2. Iter-31 acceptances: costs applied + itemized, full journal viewable, limit orders fill/expire correctly
3. Iter-32 acceptances: DB seed + canonical, UI edit persists, run plan works, per-run overrides work
4. `docs/OPEN-ISSUES.md` updated — C1–C6, J1–J6, E1–E4 from iter-31 marked fixed; S1–S5 + RW-01/02/05 from iter-32 marked done
5. Unit + Simulation + Architecture suites green
6. No new warnings
7. `HANDOVER.md` recording answers to all Q1–Q5 and the combined phasing decisions

## Guardrails (both iterations)

- No engine-internals changes beyond what's specified (strategy math, risk pipeline untouched)
- Don't touch `aspire/AppHost` (`NU1903`)
- `playbook.json`/`position-management.json` — note as follow-up, don't wire or delete
- Pre-existing `AccountProcessor.cs:125` NRE is out of scope
- Simulation FTMO suite is stop-the-line — red there blocks progress
- Fast feedback: `dotnet test tests/TradingEngine.Tests.Unit` + `tests/TradingEngine.Tests.Simulation`

### Schema strategy

**No new migrations.** All schema changes accumulate across the waves, and a single fresh
`InitialCreate` migration is regenerated at the end of Wave 4 (before Wave 5 UI polish). The approach:

- Wave 1–3: Entities get new properties/columns; EF runs against in-memory SQLite for tests but
  the actual migration is deferred
- End of Wave 4: `dotnet ef migrations remove` to drop the old migration, then regenerate a single
  `InitialCreate` that captures the full final schema including:
  - New `StrategyConfig` table (32-P0)
  - Extended `PipelineEvents` taxonomy column (31-B0)
  - New cost fields on `SymbolInfo` (31-A0) — added to `SymbolCatalog` model, not EF (it's JSON-backed)
  - Extended `TradeResult`/`AccountSnapshot` for cost data (already has Commission/Swap columns)
  - Run record effective config column (32-P3)
  - Run plan table or run-config JSON column (32-P2)
- Wave 5: Migration is already in place; UI phases validate against it

### UI coverage — every new field flows to reports

| Data added | Must appear in |
|------------|---------------|
| Commission / Swap / Gross / Net (per trade) | Report trades table, Trade Detail, Monitor close events |
| Commission / Swap / Gross / Net (run totals) | Report summary KPIs, Analyzer |
| Journal entries (SIGNAL/ORDER/FILL/CLOSE/REJECTED/BREACH/GOVERNOR) | Report Journal tab, Monitor live feed |
| Limit order events (ENTRY_EXPIRED, CANCELLED) | Journal tab, Monitor |
| Persisted equity curve (AccountSnapshots) | Report equity chart, Monitor sparkline |
| Effective config snapshot | Run detail page (read-only) |
| Strategy editable defaults (all knobs) | Strategies page (list + edit form) |
| Per-run overrides | New-Backtest form, Run detail (effective config) |
| Max position cap, sizing policy, governor limits | Strategies edit form (32-P4) |

### Key file collision points (need careful sequencing)

| File | Touched by | Risk | Mitigation |
|------|-----------|------|------------|
| `SimulatedBrokerAdapter.cs` | 31-A1, 31-C1 | Merge conflict if parallel | Do A1 first, then C1 |
| `TradingLoop.cs` | 31-C0, 32-P2 | Both modify entry plan / symbol dispatch | C0 adds EntryPlanner call; P2 threads run plan — compatible if C0 lands first |
| `Report.cshtml.cs` | 31-B1, 31-B4, 32-P5 | Three phases touch Report model | Sequential within Wave 4 |
| `*.cshtml` (Monitor, Report, New) | 31-B2, 32-P4, 32-P5 | JS/HTML conflicts | Sequential per Wave |
