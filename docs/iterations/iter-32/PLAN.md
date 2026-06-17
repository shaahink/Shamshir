# Iter-32 Plan — Strategy & Config as Editable Data (DB-seeded, per-run overrides, browse/tweak UI)

Context (owner's framing): today a strategy's config lives in `config/strategies/*.json`, is loaded
once at startup, **couples symbol + timeframe into the strategy itself**, and **can't be browsed or
edited** in the UI; a backtest can pick *which* strategies and symbols to run but cannot tweak a
strategy's parameters / SL-TP / entry-exit options for that one experiment. The owner wants:

1. **Seed a DB from the JSONs** at create/first run.
2. The DB becomes the **editable source of defaults** — a UI to browse and tweak strategies.
3. Each backtest/experiment **reads defaults from the DB but can override them for that run** (layered:
   stored default → per-run override), and the *effective* config is saved with the run (reproducible).
4. **Decouple symbol/timeframe from the strategy** — a strategy is a reusable template; *which* symbols
   and timeframes it runs on is chosen per run (and/or globally), not hardcoded into the strategy.

This is the **config-architecture + UI** iteration. It realizes Part 10's RW-01 (settings/strategy
editor), RW-02 (layered config), and RW-05 (symbol selection) as one DB-backed store. It is large but
coherent; it is mostly **plumbing + UI** and deliberately **does not change engine internals** (the
strategy math, risk, venue, and the `StrategyRegistry.CreateStrategies(StrategyConfigEntry)` contract
all stay the same — only the *source* of the config and the ability to override it change).

Relationship to iter-31: independent surfaces in the main (iter-31 = engine/venue + journal; this =
config store + UI), with light overlap on the New-Backtest form. **Land iter-31 first** so the
`OrderEntryOptions` shape it stabilizes is what gets seeded here. Branch off the then-current branch.

Working style: failing-test-first, per-phase machine-checkable **Gate**, small commits
(`feat(iter32-pN): …`), solution building + fast suites green at every phase. **Read the
"Decisions to confirm with the owner" block before starting the affected phase** — proceed with the
recommended default if unanswered and record the choice in HANDOVER.

**Acceptance for the whole iteration:**
1. With an empty DB, app start seeds strategy config from the JSONs (idempotent); a backtest then runs
   from the **DB** config (deleting the JSON afterward does not change results).
2. Editing a strategy's parameter in the UI persists to the DB and the **next run uses the new value**;
   the JSON on disk is untouched (DB is canonical).
3. A backtest can run a strategy on a symbol/timeframe that is **not** in the strategy's default set (and
   exclude one that is), without editing the strategy.
4. Two runs of the same strategy with a **different per-run TP override** produce different effective
   configs and different results; the stored default is unchanged; each run's **effective config is
   persisted** with it and re-loadable.

---

## Current-state map (finding → diagnosis → location) — read-verify before trusting line numbers

| # | Symptom | Root cause | Location |
|---|---------|-----------|----------|
| S1 | Strategy config is file-only; no DB store, no edit path | `ConfigLoader.LoadStrategyConfigs` reads `config/strategies/*.json` → `StrategyConfigEntry`; `LoadedConfig` holds them; there is no strategy-config table/entity. | `ConfigLoader.cs:61-99`; `LoadedConfig.cs:16-29` |
| S2 | Symbol + timeframe are baked into the strategy | `StrategyConfigEntry` carries `Symbols` + `Timeframe`; `StrategyBankService.GetActive` filters by `s.Config.Symbols.Contains(symbol)` and `s.RequiredTimeframes.Contains(tf)`; strategies also self-check `_config.Symbols.Contains(...)`. | `LoadedConfig.cs:16-29`; `StrategyBankService.cs:21-29`; each `*Strategy.Evaluate` |
| S3 | No strategy browse/edit UI | `Pages/Strategies.cshtml.cs` is an empty `OnGet()` shell. | `Strategies.cshtml.cs:5` |
| S4 | Per-run config is "pick strategies + symbols", not field-level override | New-Backtest lists `_registry.GetAllIds()`; `EngineHostOptions.ActiveStrategyIds` / `PreloadedConfig` thread a *whole* config, but nothing overrides individual params / SL-TP / entry per run. | `New.cshtml.cs:39`; `EngineServiceCollectionExtensions` (AddRiskFromOptions/PreloadedConfig) |
| S5 | The instantiation contract is `StrategyConfigEntry` | `StrategyRegistry.CreateStrategies(activeIds, LoadedConfig, sp)` builds each strategy from its `StrategyConfigEntry`. A DB store must reproduce these entries so nothing downstream changes. | `StrategyRegistry.cs:203-243` |

---

## Decisions to confirm with the owner (resolve before the affected phase — defaults proposed)
> These are architecture/product choices. The agent proceeds with the **recommended default** if
> unanswered, and records the decision in HANDOVER. The first two are load-bearing — prefer an answer.

- **Q1 — Source of truth (affects P0/P1).** DB canonical (JSON = one-time seed + manual export) **vs**
  JSON canonical (DB = cache). *Recommended:* **DB canonical**; keep an "Export to JSON" action so config
  can still be committed to git. Rationale: editing-in-UI is the goal; a cache that JSON overwrites on
  restart would silently discard edits.
- **Q2 — Symbol/timeframe model (affects P2) — the big one.** (a) Keep `symbols`/`timeframe` as **defaults
  on the strategy**, overridable per run (lighter, less churn); (b) **fully decouple** — strategy has no
  symbols; a separate assignment table holds (strategy × symbol × timeframe). *Recommended:* **(a) with a
  per-run "run plan"** — the strategy keeps a default symbol/TF set, and each run specifies the actual
  (strategy, symbol, timeframe) tuples (defaulting to the strategy's set). Gives global/per-run symbol
  selection (RW-05) without a disruptive schema-wide decouple. Confirm before P2.
- **Q3 — Override granularity (affects P3).** Field-level **deep-merge** over the stored default **vs**
  whole-config replace. *Recommended:* **deep-merge** (override only the fields you set; everything else
  inherits) — this is RW-02 layered config.
- **Q4 — Where overrides live (affects P3/P5).** *Recommended:* on the **run/experiment record**, and the
  **resolved effective config is snapshotted with the run** for reproducibility.
- **Q5 — Validation + write-back.** Reuse `ConfigLoader`'s cross-reference checks (riskProfileId,
  propFirmRuleSetId) on every save; *Recommended:* write back to JSON only on **explicit export**, not on
  every edit.

---

## Decisions (authoritative — do not re-litigate)

- **D1 — The engine contract is unchanged.** Everything still flows through
  `StrategyConfigEntry` → `StrategyRegistry.CreateStrategies`. The store produces `StrategyConfigEntry`s;
  the rest of the engine never learns where config came from. No strategy/risk/venue code changes.
- **D2 — One resolver owns layering.** A single `EffectiveConfigResolver` produces the run's config =
  `stored defaults` deep-merged with `per-run overrides` deep-merged with `run plan` (symbol/TF). It is
  pure and unit-tested; the host calls it once per run and passes the result as `PreloadedConfig`.
- **D3 — Reproducibility.** The resolved effective config (full JSON) is persisted on the run so a run can
  be re-opened/re-run exactly, regardless of later edits to the stored defaults.
- **D4 — Guardrails.** Don't touch `aspire/AppHost` (`NU1903`). Keep Unit + Simulation + Architecture
  green. Don't change strategy math or the risk pipeline. `playbook.json`/`position-management.json` are
  still dead — decide (wire or delete) at most as a documented note, not in this iteration.

---

## Phases

> Each phase: failing test first → fix → Gate. Keep the engine green; this is additive plumbing + UI.

### P0 — Strategy-config store + idempotent seed-from-JSON (S1, S5, Q1, Q5)  ⟵ foundational
- Add EF entities + migration for strategy config (one row per strategy; store the structured blocks —
  `parameters`, `positionManagement`, `regimeFilter`, `orderEntry`, default `symbols`, `timeframe`,
  `riskProfileId`, `enabled` — as typed columns or validated JSON columns, whichever round-trips
  `StrategyConfigEntry` cleanly). Add `IStrategyConfigStore` with `GetAll()` → `IReadOnlyList<StrategyConfigEntry>`
  and `Upsert(entry)`. Seed-if-empty on startup from the existing JSON loader (idempotent).
- **Gate (failing-test-first):** with an empty DB, startup seed populates the store; `GetAll()` returns
  entries byte-equivalent (after normalization) to the JSON loader's; re-running seed is a no-op.

### P1 — Read run config from the store (S1, D1)
- Route the host's strategy-config source through `IStrategyConfigStore` (JSON path becomes seed/export
  only). Keep `StrategyRegistry.CreateStrategies` unchanged. Add a JSON **export** endpoint/action.
- **Gate:** a backtest runs using DB config; editing a row in the store (e.g. an RSI threshold) and
  re-running changes behavior; deleting `config/strategies/*.json` after seed does not affect a run.

### P2 — Decouple symbol/timeframe via a run plan (S2, Q2)
- Introduce a **run plan**: the set of `(strategyId, symbol, timeframe)` tuples a run executes, defaulting
  to each active strategy's stored `symbols × timeframe`. Feed it into `StrategyBankService.GetActive` /
  the loop so symbol/TF come from the plan, not (only) the strategy. Keep the strategy's stored set as the
  default source. (Per Q2 default; if owner picks full decouple, add the assignment table instead.)
- **Gate:** a run plan that adds `USDJPY`/`H4` to a strategy whose default set lacks them trades there;
  one that removes a default symbol does not trade it — all without editing the strategy row. Strategy
  self-checks (`_config.Symbols.Contains`) must honor the plan (resolve by passing the planned symbols
  into the per-run `StrategyConfigEntry`).

### P3 — Per-run overrides + effective-config resolver + reproducibility (S4, D2, D3, Q3, Q4)
- Add `EffectiveConfigResolver` (deep-merge: stored default ← per-run overrides ← run plan). Persist the
  resolved effective config on the run record. The host consumes the resolved config via `PreloadedConfig`.
- **Gate:** unit tests — overriding only `takeProfit.rrMultiple` leaves all other fields inherited; two
  runs with different overrides yield different effective configs; the stored default is unchanged; the
  persisted effective config reloads identically.

### P4 — Strategy browse/edit UI (S3, RW-01, Q5)
- Replace the empty Strategies page with a list + detail/edit view exposing **every** tunable: parameters,
  SL/TP, breakeven/trailing, regime filter, order entry, default symbols/timeframes, risk profile,
  enabled. Show the **effective** value (stored-or-default) and allow making it explicit. Validate via the
  cross-reference checks (Q5) before `Upsert`. (This is the RW-01 settings surface, scoped to strategies.)
- **Gate:** editing a field in the UI persists to the store, survives reload, and the next run uses it;
  an invalid edit (bad riskProfileId) is rejected with a clear message and not saved.

### P5 — New-Backtest per-run override UI (S4, D2, Q4)
- Extend the New-Backtest form to override on top of the stored defaults for *this run only*: pick the run
  plan (symbols × timeframes), and tweak key knobs (parameters, SL/TP, entry method/offset). Feed into the
  P3 resolver; show the effective config before launch.
- **Gate:** launching a run with an overridden TP/entry-offset from the form produces a run whose persisted
  effective config reflects the override, while the stored default is unchanged.

### P6 — Cleanup + export + docs (Q1, Q5, D4)
- JSON export (round-trips the store back to `config/strategies/*.json` for git); document the new flow in
  `docs/`; note the `playbook.json`/`position-management.json` dead-config decision (wire or delete) as a
  follow-up, do not action here.
- **Gate:** export produces JSON the loader re-imports to an equivalent store; solution builds; Unit +
  Simulation + Architecture suites green; no new warnings.

---

## Out of scope / guardrails
- **No engine-internals changes** (strategy math, risk pipeline, venue) — config *source* and *override*
  only. If a phase seems to require touching a strategy's logic, stop and reconsider the contract (D1).
- **RW-03 (batch runner)** and **RW-04 (auto strategy mode)** stay deferred — but this iteration is their
  prerequisite (batch sweeps = many per-run overrides; auto-mode = run-plan selection). Note the link.
- No data import / multi-TF data work; `mtf-trend` still needs H4 data to actually fire.
- Don't touch `aspire/AppHost`; keep the simulation FTMO suite green (stop-the-line on red).

## Definition of Done
All non-optional gates met; the four acceptance scenarios pass as automated tests; `docs/OPEN-ISSUES.md`
updated — S1–S5 addressed and RW-01/RW-02/RW-05 marked done-or-progressed (Part 10); Unit + Simulation +
Architecture green; no new warnings; a `HANDOVER.md` records the **answers to Q1–Q5** (or defaults taken),
the chosen symbol/TF model, and the store schema, so RW-03/RW-04 can build on it.
