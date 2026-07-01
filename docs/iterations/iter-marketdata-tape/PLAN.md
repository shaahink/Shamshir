# iter-marketdata-tape — Downloadable market data, a verifiable fake venue, and a two-path backtest UI

**Written:** 2026-07-01
**For:** a FRESH implementation agent (OpenCode / DeepSeek) — this doc is self-contained; read it top to bottom before touching code.
**Companion reading (do skim):** `docs/audit/PERF-DEEP-AUDIT.md` (why the current backtest is slow and why this iteration exists).
**Branch base:** `develop`.
**Scope guard:** NO change to the decision kernel (`TradingEngine.Engine`, `EngineWorker`, `KernelBacktestLoop` decision logic) or strategy/risk math. Golden replay must stay byte-identical after every phase. This iteration is about *where market data comes from* and *which venue simulates fills* — not how the strategy decides.

---

## ⚡ STATUS (2026-07-01) — P0–P3 EXECUTED; agent continues from here

**P0–P3 are BUILT + TESTED** on branch `iter/marketdata-tape` (worktree at `C:/code/shamshir-mdtape`, based on
`9b7c79f`). Gates green: Unit **304/0**, golden **63/63 byte-identical** (kernel untouched), all new
market-data/tape/reconcile tests green, full solution builds incl. the net6 cBot. The only red is **9
`WebSmokeTests` (404)** — environmental: this worktree has no built Angular SPA (`wwwroot` absent), not a
regression. Details in `HANDOVER-P0-P3.md`.

**cTrader-side verification was NOT done** (that session couldn't run cTrader headlessly). The recorder cBot
`--Record`, a real tape backtest, and a real reconcile are **UNVERIFIED**. The continuing agent CAN run cTrader
— use the `ctrader-e2e`, `run-shamshir`, `shamshir-e2e` skills, the `RequiresCTrader` tests, and the documented
creds/params — and MUST do **Phase M** then **Phase V** before P4.

### Agent runbook (do in order — nothing below in this plan is omitted or superseded)
1. **Phase M** — merge this branch with the caching worktree (`iter-cache-reads-2`). See §4.
2. **Phase V** — cTrader verification & findings loop: record real data → tape backtest → reconcile vs cTrader
   → E2E/determinism → **document findings → fix** → repeat. See §4. *(This is where "download all the data +
   test the new way + run cTrader when required" happens.)*
3. **P4 → P5 → P6** — as originally planned below. P4 also finishes the one engine-side reconcile stub P0 left.

Phases **P0–P3 below are ✅ DONE** (kept in full for context + the owner-decision rationale); their per-phase
gates were met **except the cTrader-only checks**, which **Phase V now owns**.

---

## 0. Start here — the one idea everything hinges on

The user's backtests run through **cTrader-cli** (an external process that replays m1 data) talking to our
.NET 8 engine over **NetMQ**, one synchronous round-trip per bar. That IPC + external-process floor is why
it's slow (see the audit). cTrader is also the user's **source of truth**: it owns account/equity/drawdown,
commission, swap, partial fills, limit orders, and produces the authoritative trade list.

We are building a **fast, in-process fake venue** that replays **downloaded market data** and **models fills
itself**, and we PROVE it matches cTrader by reconciling against cTrader's own recorded report.

> **CRITICAL — do not get this backwards.** The fake venue replays cTrader's **INPUTS** (bars/ticks) and
> **re-simulates** fills with our own model. It does **NOT** replay cTrader's recorded fills/PnL. If it
> replayed recorded outputs it could only "backtest" configs already run through cTrader — useless for
> experimentation. cTrader's recorded run (the **oracle**) is used ONLY to *verify and calibrate* our fill
> model (swap, long-shadow/wick exits, floating drawdown). Once our model provably agrees with cTrader on a
> set of oracle runs, the user can run thousands of NEW experiments on the fast path with justified trust.

Three data artifacts, keep them distinct:

| Artifact | What it is | Volume | Used for |
|---|---|---|---|
| **Market data** (canonical) | Source-agnostic OHLCV bars (+ ticks later) per (symbol, resolution, time). Deduped, provenance-tagged. | med (bars) / huge (ticks) | INPUT to every fast backtest |
| **Oracle tape** | cTrader's recorded OUTPUTS for a specific run: native report (tradeStats/equity/positions/orders) + per-event ledger. | small | VERIFY/calibrate the fake venue's fills |
| **Run results** | Our engine's DB output (trades/equity/journal) for any run, either path. | small | the actual backtest result + UI |

---

## 1. Goals / non-goals

**Goals**
1. Download and store X symbols × Y timeframes of history (target: **5 years**), from cTrader now, from
   **other providers later** (pluggable). No re-download to run a backtest.
2. A **canonical, source-agnostic market-data store** that is fast to range-scan and won't become a
   read/write bottleneck (explicit storage design in §5).
3. A **fake venue** (`TapeReplayAdapter`) that runs the engine fully in-process against that data, with
   **dual-resolution exits** (decide on H1, detect SL/TP/trailing on m1) to capture long shadows/wicks.
4. A **reconciliation harness**: run the SAME short config through BOTH paths (cTrader + fake venue) and
   diff the ledgers/aggregates, so divergences (swap gap, wick fills, floating DD) are named, not guessed.
5. **UI**: choose the path per run; a **Data Manager** page that reports exactly what data we hold (no
   guesswork); a **compare mode** that runs both and shows the diff; the backtest table shows **which method**
   each run used.
6. **Measurement** wired so we never optimize blind.

**Non-goals (this iteration)**
- Per-tick strategy evaluation (record/replay must not *preclude* ticks — schema + interfaces reserve for
  them — but tick playback into strategy logic is deferred).
- Retiring the cTrader path. It stays: as the oracle/recorder and for final validation.
- .NET 10 upgrade (orthogonal; do not couple).
- Kernel/strategy changes.

---

## 2. Owner decisions (VOTE) — agent: default to the ✅ pick unless the owner overrides here

> Owner: tick a box or write an override next to each before the agent starts the affected phase.

**D1 — Bar storage backend.**
✅ **A. Separate `marketdata.db` (SQLite), keyed (symbol, timeframe, openTimeUtc), deduped, WAL.** Reuses our
EF/SQLite infra; a few million rows (5yr m1 × handful of symbols) is well within SQLite with the right index.
· B. Reuse `trading.db` (simpler, but bloats the run DB and couples market data to run lifecycle — the
current `Bars` table is per-RunId, which we explicitly do NOT want). · C. Parquet/columnar files (compact,
great for bulk, but adds a dependency and weak incremental append). *Rec A; revisit C only if m1 row counts
prove painful.*

**D2 — Tick storage (when ticks land, later phase).**
✅ **A. Append-only binary/columnar "tick tape" files per (symbol, month), indexed in SQLite.** Sequential,
memory-mappable, compact; SQLite holds only the file index. · B. SQLite rows (billions of rows for 5yr — no).
· C. Parquet per (symbol, month). *Rec A; C acceptable if the team prefers Parquet tooling.*

**D3 — Recorder output + ingest path.**
✅ **A. Recorder cBot writes append-only NDJSON/CSV shards to disk (resumable, paged); a .NET 8 ingester
bulk-loads + dedupes into the canonical store.** Robust for a one-off 5yr pull, and the file schema doubles
as the portable interchange format for other data sources (D5). · B. Stream over NetMQ to a live ingester
(reuses transport, but not resumable — bad for huge pulls). · C. cBot writes the DB directly (net6 sandbox +
schema-coupling pain). *Rec A.*

**D4 — Fake-venue fill economics (the crux — see §0).**
✅ **A. Re-simulate fills from market data with our model; calibrate/verify against the oracle tape.** The
only option that supports NEW experiments. · B. Replay recorded cTrader fills (only works for already-run
configs — rejected for experimentation). *Rec A, firmly. B is not viable as the primary; the oracle is for
verification only.*

**D5 — One normalized interchange schema for all data sources?**
✅ **Yes.** Define a single columnar CSV/NDJSON bar schema (+ tick schema) that cTrader recorder AND future
providers (Dukascopy/Polygon/…) both emit, so the ingester is source-agnostic. *Rec yes.*

**D6 — Compare UX.**
✅ **A. Explicit "run both & diff" compare mode in the UI** (runs cTrader + fake venue on the same short
config, shows side-by-side ledger + reconciliation verdict). · B. Just store the method and diff offline via
a script. *Rec A for the day-to-day "1h/1d both methods" workflow the owner described; keep the offline
script (P0) too.*

**D7 — Default resolution strategy.**
✅ **Download m1 as the base resolution; default fast backtests to "decide on strategy TF, detect exits on
m1"; ticks opt-in per (symbol, range).** · B. Decision-TF only until an oracle diff proves m1 is needed.
*Rec A — the owner explicitly cares about long shadows/wicks, which need sub-decision-TF resolution.*

**D8 — First vertical slice before scaling.**
✅ **One symbol (EURUSD), H1 decisions / m1 exits, 1-day range, both paths, full reconcile GREEN** — prove
the loop end-to-end before downloading 5yr × many symbols. · B. Bulk-download first. *Rec A; download breadth
comes after the slice reconciles.*

---

## 3. Architecture / new components

```
                         ┌──────────────── DATA SUPPLY ────────────────┐
  cTrader (recorder cBot)│  MarketData.GetBars/GetTicks → NDJSON/CSV    │   future providers
        └────────────────┤  shards on disk  ──▶  Ingester (.NET 8)      ├── Dukascopy / Polygon / …
                         │        (dedupe, gap-detect, provenance)      │  (same interchange schema, D5)
                         └───────────────────────┬──────────────────────┘
                                                 ▼
                              Canonical Market-Data Store (marketdata.db + tick tapes)
                              IMarketDataStore  (range reads, inventory, gaps)
                                                 │
        ┌────────────────────────────────────────┼───────────────────────────────┐
        ▼ FAST PATH (new)                         ▼ TRUTH PATH (existing)          ▼ INVENTORY
  TapeReplayAdapter (IBrokerAdapter)        RunEngineNetMqAsync → ctrader-cli   Data Manager API/UI
   • feeds decision-TF bars to strategy      → cBot → NetMQ → engine            "what do we have"
   • feeds m1 bars to exit detection         → CtraderReportHarvester           downloads / status / gaps
   • models fills/costs (our model, D4)         = ORACLE TAPE
        │                                         │
        └──────────────┬──────────────────────────┘
                       ▼
             Reconciliation harness  (engine DB  vs  oracle report/events)
                       ▼
             UI: path selector · compare mode · method column · reconcile verdict
```

**Reuse, don't reinvent:**
- `CtraderReportHarvester` (`src/TradingEngine.CTraderRunner/CtraderReportHarvester.cs`) already extracts
  cTrader's native `backtesting-report` blob (main/equity/tradeStatistics/history/positions/orders) +
  `events.json` (per-event ledger + equity). **This is the oracle** — the reconcile side is largely done.
- `ShamshirTradeLogger` (cBot) already writes `shamshir-report.json` / `shamshir-events.json` / equity.
- `BacktestReplayAdapter` is the seed for `TapeReplayAdapter` (already models commission/swap/spread + uses
  `EngineReducer.DetectSlTpExit` — venue-managed, byte-identical to the kernel). Upgrade, don't rewrite.
- `verify-ctrader-run.ps1` / `run-ctrader-verify.ps1` — existing reconcile scaffolding to fold into P0.
- `BufferedBarWriter` / `IBarRepository` — existing bar persistence; generalize to the canonical store.

---

## 4. Phases (each independently shippable, one commit/phase, gates must stay green)

### Phase M — Merge with the caching worktree (`iter-cache-reads-2`) — DO FIRST
The cache fixes (iter-cache-reads-2) and this iteration are orthogonal (cache = in-memory read layer; this =
market-data source + fake venue). Merge so the rest of the work sits on one branch.
- **Steps:** from the main repo on `iter-cache-reads-2` (or a fresh integration branch off it), merge
  `iter/marketdata-tape` (`git merge iter/marketdata-tape`).
- **Expected conflicts: LOW, in two files** — resolve by **keeping BOTH additions** (they're independent blocks):
  - `Web/Services/BacktestOrchestrator.cs` — cache adds `IRunDataCache` plumbing; this adds the `Venue=tape`
    branch + `marketDataStore`/`exitTf` locals. Keep both.
  - `Web/Configuration/ServiceRegistration.cs` — cache registers `IRunDataCache`; this registers
    `IMarketDataStore` + `MarketDataDbContext` factory + `marketdata.db` init. Keep both. Different services,
    no semantic overlap.
- **Rebuild the Angular SPA** (`web-ui` build → `wwwroot`) so the 9 `WebSmokeTests` pass (they only 404 for
  lack of a built SPA in the isolated worktree).
- **Gate (Phase M):** `dotnet build TradingEngine.slnx` clean; Unit green; golden **63/63 byte-identical**;
  Integration green **incl. WebSmoke** (after SPA build); the cache suite green.

### Phase V — cTrader verification & findings loop (after M, before P4)
Prove the new fast path is real AND trustworthy end-to-end against real cTrader, and fix what diverges. The
agent CAN run cTrader. **Document every finding in `docs/audit/RECONCILE-FINDINGS.md`; log fixes + gate
results in a new `docs/iterations/iter-marketdata-tape/VERIFICATION.md`.** Any cBot/transport change ⇒ the
`RequiresCTrader` E2E + golden gates apply after each change.

- **V1 — Recorder produces valid shards (cBot `--Record`).** Rebuild + deploy the `.algo` (stamp + zero-friction
  deploy; confirm `AlgoHash` changed). Run a recorder backtest over a SHORT range (e.g. EURUSD, 1 day) with
  `--Record=true --ReportPath=<dir> --Periods=m1` (and separately `--Periods=h1`) using the documented creds.
  Verify `<dir>/EURUSD_m1.ndjson` (+ `_h1`) exist, each line parses via `MarketDataShardIo`, and
  `MarketDataIngester` loads them into `marketdata.db` with the expected inventory + no unexpected gaps;
  re-ingest ⇒ 0 new (idempotent). **Fix** any wire-format / lowercase-timeframe / UTC-kind mismatch (the .NET
  format is locked by `MarketDataShardIoTests.Parses_the_exact_cbot_recorder_line`; the cTrader-side WRITE is
  what's unverified).
- **V2 — Download the owner's working set.** Using the recorder (or `FileDrop` for other sources), download the
  owner's actual symbols/timeframes — at minimum EURUSD **H1 + m1, 1–6 months** (the owner's real profile) plus
  any other symbols in use; m1 is the base resolution (D7). Record the inventory in `VERIFICATION.md`.
- **V3 — Tape backtest runs and is fast.** Run the SAME strategy/symbol/range via `Venue=tape` (add
  `ExitTimeframe=m1`). Confirm it completes with NO cTrader-cli (no ~23 s fixed cost, no NetMQ). Record
  wall-clock + bars/sec in `docs/audit/PROGRESS.md` — the Tier-1 baseline; expect a large speedup vs the
  cTrader path.
- **V4 — Reconcile tape vs cTrader (the trust gate).** Run the SAME short config BOTH ways — (a) cTrader path
  (writes `shamshir-report.json`), (b) `Venue=tape`. Build a `ReconcileLedger` for each and
  `LedgerReconciler.Compare`. **Finish the one P0 stub:** the engine-side ledger builder (thin map
  `BacktestRunSummary` + `Trades` → `ReconcileLedger`) — put it in `Web` (has DB) + a `scripts/reconcile-run.ps1`.
  Interpret per `RECONCILE-FINDINGS.md`: **RawMoney divergence = bug (fix now); Aggregation (MaxDD)/TradeSet =
  expected (feed P5)**. Document actual numbers.
- **V5 — Reconcile engine-DB vs cTrader (the owner's ORIGINAL pain).** Independently of the tape, reconcile a
  cTrader-path run's DB against its own `shamshir-report.json` to explain the existing "DB ≠ cTrader"
  inconsistencies. Confirm/correct the prediction (Aggregation/MaxDD, not RawMoney). **Fix the clearly-wrong
  ones; log the rest as P5 tasks.**
- **V6 — Regression gates.** After any cBot/engine change: golden byte-identical; `RequiresCTrader` E2E green
  (incl. report-vs-DB reconciliation); Unit green. Record in `VERIFICATION.md`.
- **Findings loop:** for every V4/V5 divergence, either FIX it (RawMoney / clear bug) or log it as a P5
  modelling task (Aggregation), then re-run the relevant reconcile until it is green-or-explained.

### P0 — Measure + reconcile (foundation; no new architecture) — ✅ DONE (cTrader measure/reconcile → Phase V)
- **Measure:** capture the cBot `roundTrip ÷ wall-clock` ratio on ONE real H1/1–3-month cTrader run
  (`Engine:Diagnostics:Enabled=true` already wires `--Diagnostics=true` and scrapes `CBOT|TIMING`). Record in
  `docs/audit/PROGRESS.md` (create it).
- **Reconcile:** a harness/tool that, for a given cTrader run, loads (a) our engine DB result and (b) the
  harvested cTrader native report + `events.json`, and emits a categorized diff:
  per-trade (entry/exit/lots/PnL/commission/swap), and aggregates (net PnL, MaxDD, win rate, trade count,
  equity curve). Classify each divergence: *raw-money* (should be ~0 — the cBot already forwards cTrader's
  economics) vs *engine-aggregation* (MaxDD/floating-equity/late-swap — the expected culprits).
- **Deliverable:** `docs/audit/PROGRESS.md` (measurement) + `docs/audit/RECONCILE-FINDINGS.md` (named
  divergences). This tells us what the fake venue must reproduce.
- **Gate:** determinism/golden byte-identical; the harness runs on an existing/seed run and prints a diff.

### P1 — Canonical market-data store + read/inventory API — ✅ DONE
- New `marketdata.db` + EF entities: `MarketDataBar(Symbol, Timeframe, OpenTimeUtc, O,H,L,C,Volume, Source,
  Quality, IngestedAtUtc)` with a unique index on (Symbol, Timeframe, OpenTimeUtc). Reserve a `MarketDataTick`
  tape-index table (files referenced, not rows) for later.
- `IMarketDataStore`: `WriteBars`, `ReadBars(symbol, tf, from, to)` (streamed/paged), `GetInventory()`
  (symbols × tfs × [ranges] × gaps × source), `GetGaps(symbol, tf, from, to)`.
- Point `TapeReplayAdapter`/replay reads at `IMarketDataStore` (not the per-RunId `Bars` table).
- **Gate:** unit tests for write/dedupe/range-read/gap-detect; inventory returns correct ranges on seeded data.

### P2 — Recorder (cTrader) + ingester + provider abstraction — ✅ DONE (cBot `--Record` unverified on cTrader → Phase V1)
- **Recorder cBot mode/param** (`--Record=true` with symbols/tfs/range): pages history via
  `MarketData.GetBars` (and, gated, `GetTicks`), writes append-only NDJSON/CSV shards to `--ReportPath`
  (resumable: skip shards already written). Reserve tick shards behind a flag.
- **Ingester (.NET 8):** bulk-load shards → `IMarketDataStore`, dedupe, detect gaps, stamp `Source=ctrader`.
- **`IMarketDataProvider`** abstraction (download → shards) so Dukascopy/Polygon/etc. plug in later; cTrader
  is the first impl. Normalized interchange schema per **D5**.
- Support a **5-year bulk pull** (paged, resumable, progress reported).
- **Gate:** a scripted small pull (e.g. EURUSD H1+m1, 1 week) ingests, dedupes, inventory shows the range with
  no gaps; re-running the pull is idempotent (0 new rows).

### P3 — Fake venue (`TapeReplayAdapter`) with dual-resolution exits  [D4, D7] — ✅ DONE (venue wired `Venue=tape`; reconcile trust-gate → Phase V4)
- New `TapeReplayAdapter : IBrokerAdapter` (seeded from `BacktestReplayAdapter`): feeds **decision-TF** bars to
  the strategy while advancing **m1** bars through exit/trailing detection (`OnBarObserved` per m1 bar), so
  long shadows/wicks and intrabar SL-vs-TP ordering are resolved. Reuses `EngineReducer.DetectSlTpExit`.
- Model swap the way cTrader does (per-night, symbol swap rate, triple-swap day) in `TradeCostCalculator`;
  calibrate the swap rate + spread from the P0/oracle findings.
- Wire as a selectable venue (`Venue="replay"`/`"tape"`) in `BacktestOrchestrator` (the `RunEngineReplayAsync`
  path already exists — point it at `IMarketDataStore` + `TapeReplayAdapter`).
- **Gate:** reconcile harness (P0) run on a short config through BOTH paths shows raw-money diffs within
  tolerance and the previously-named aggregate diffs closed or explained; determinism byte-identical.

### P4 — UI: path selector, Data Manager, compare mode, method column
- **New-Backtest:** choose path — *cTrader (truth)* / *Fast replay (tape)* / *Compare both*.
- **Data Manager page:** inventory table (symbol × tf × ranges × gaps × source × #bars), a **download form**
  (pick symbols/tfs/range → kick off recorder+ingest), and ingestion/job status. The UI is the source of
  truth for "what data we have" — **no guesswork** (owner requirement).
- **Backtest table:** add/confirm a **Method** column (Venue) so every run shows how it was produced. (The
  `Venue` field is already persisted — surface it.)
- **Compare mode:** run the same short config both ways, render side-by-side ledger + the reconciliation
  verdict from P0.
- **Gate:** WebSmoke/integration green; a driven "compare both" run renders both ledgers + a verdict;
  Data Manager shows the P2 inventory.

### P5 — Fidelity hardening (close the named diffs)  [uses P0/P3 findings]
- Fix the specific divergences the reconcile harness names (floating/intrabar MaxDD, swap at rollover,
  long-shadow fills, limit/partial-fill edge cases). Each fix is gated by the reconcile harness going greener
  on a fixed oracle set. Where a metric is genuinely cTrader-only (e.g. its internal DD method), decide per
  metric: match it, or display cTrader's number as truth on the cTrader path.
- **Gate:** reconcile diffs within agreed tolerance on ≥N oracle runs (short + long TF); determinism green.

### P6 — Ticks (optional, only if/when needed)  [D2]
- Tick tape format + `GetTicks` recording + tick-resolution exit detection in `TapeReplayAdapter`. Interfaces
  from P1/P2/P3 already reserve for this; this phase just lights it up for nominated (symbol, range).
- **Gate:** a tick-tape short run reconciles against a tick-mode cTrader run within tolerance.

---

## 5. Storage & performance design (explicit — must not become the new bottleneck)

- **Separate `marketdata.db`** from `trading.db` (D1): market data is long-lived and shared; run data is
  churny and per-run. Don't couple their lifecycles or lock domains.
- **Bars:** one row per (symbol, tf, openTime); unique index on that tuple (dedupe + fast range scan). Store
  price as REAL or scaled-int for compactness (the run DB stores money as TEXT — do NOT copy that here;
  market data is high-volume). WAL + the existing `SqlitePragmaInterceptor` pragmas.
- **Playback reads are sequential range scans**, streamed/paged (`IAsyncEnumerable<Bar>`), never per-bar
  point queries. Load a run's window once; don't re-query per bar.
- **Ticks (P6):** never row-per-tick in SQLite. Append-only binary tape per (symbol, month), memory-mapped,
  SQLite holds only a file index. 5yr × few symbols of m1 is fine in SQLite; ticks are 100–1000× and must be
  columnar/binary.
- **Ingest is bulk + idempotent:** `AddRange` + one transaction per shard; dedupe by the unique index
  (`INSERT OR IGNORE` semantics); resumable by shard.
- **Provenance + quality columns** so mixed sources (cTrader vs a future higher-quality feed) are
  distinguishable and a run records which source it used.

---

## 6. Verification gates (every phase keeps these green)

```powershell
# Determinism / golden — MUST stay byte-identical (proves no kernel drift)
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"

# Fast unit suite
dotnet test tests/TradingEngine.Tests.Unit

# cTrader E2E — required after ANY cBot/recorder/transport change (needs live creds)
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"
```
Plus each phase's own gate above. Record before/after numbers in `docs/audit/PROGRESS.md` — no "faster/slower"
without a number (±20% run-to-run variance).

---

## 7. File map (where new things go)

| Concern | Location |
|---|---|
| Canonical store entities + store | `src/TradingEngine.Infrastructure/MarketData/` (`MarketDataDbContext`, `MarketDataBar`, `SqliteMarketDataStore`) |
| Store interface | `src/TradingEngine.Domain/Interfaces/IMarketDataStore.cs` |
| Provider abstraction + cTrader impl | `src/TradingEngine.Infrastructure/MarketData/Providers/` (`IMarketDataProvider`, `CtraderRecorderProvider`) |
| Ingester | `src/TradingEngine.Infrastructure/MarketData/MarketDataIngester.cs` |
| Recorder cBot mode | `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` (+ `--Record`, paging) |
| Fake venue | `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs` (from `BacktestReplayAdapter`) |
| Reconcile harness | `src/TradingEngine.CTraderRunner/Reconcile/` (reuse `CtraderReportHarvester`) + `scripts/reconcile-run.ps1` |
| Orchestrator wiring | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` (path selection) |
| UI | `web-ui/src/app/…` Data Manager page, path selector, compare view, Method column |
| Docs | `docs/audit/PROGRESS.md`, `docs/audit/RECONCILE-FINDINGS.md`, this PLAN |

---

## 8. What NOT to do
- Do NOT replay cTrader's recorded fills as the fake venue's economics (see §0/D4).
- Do NOT touch the decision kernel or strategy/risk math; do NOT let golden drift a single byte.
- Do NOT put ticks in row-per-tick SQLite; do NOT reuse the per-RunId `Bars` table as the canonical store.
- Do NOT optimize any perf item before P0 records a number for it.
- Do NOT couple this to the .NET 10 upgrade.
- Do NOT remove or bypass the cTrader path — it's the recorder and the oracle.

---

## 9. First actions for the continuing agent
> P0–P3 are already built (see the STATUS banner at the top). Do NOT re-implement them — verify and continue.
1. Read `docs/audit/PERF-DEEP-AUDIT.md`, `HANDOVER-P0-P3.md`, and this PLAN. Confirm the §0 mental model back
   to the owner in one line.
2. **Phase M** — merge with `iter-cache-reads-2` (keep both additions in the two conflicting files), rebuild
   the SPA, run the Phase-M gate.
3. **Phase V** — run cTrader for real: V1 record shards → V2 download the owner's H1+m1 working set → V3 tape
   backtest (record the speedup) → V4 reconcile tape-vs-cTrader (finish the engine-side ledger stub +
   `scripts/reconcile-run.ps1`) → V5 reconcile engine-DB-vs-cTrader (the owner's original pain). **Document
   every finding in `RECONCILE-FINDINGS.md` + `VERIFICATION.md`; fix RawMoney/clear bugs; log Aggregation as
   P5.** Keep golden byte-identical + `RequiresCTrader` E2E green after any cBot change.
4. Then the **D8 vertical slice** (EURUSD, H1/m1, 1 day, both paths, reconcile GREEN) → **P4 → P5 → P6**.
