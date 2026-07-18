# V4 — Session / Time-of-Day Strategy Family (implementation plan)

**For:** the OpenCode / DeepSeek implementation agent (Lane D).
**Branch:** cut `iter/viability-v4-session` off `iter/viability`. Merge back at the stage gate.
**Truth-side contract:** the research pre-registration in
`docs/iterations/iter-viability/LEDGER.md` → *Session 7 → "Pre-registration — V4 session/time-of-day
family"*. **The session windows and knob values below are FROZEN by that pre-registration.** Changing
any window, timeframe, symbol, or default parameter silently invalidates the experiment. If a value
looks wrong, STOP and flag it — do not "improve" it.

**Why this exists:** the frozen 9-strategy bank was refuted out-of-sample at GV2 (F85, whole-bank
negative). The owner authorized one decisive, well-powered shot at a *structurally different*
family: clock-keyed session strategies, which the indicator-based bank never covered. This plan
builds the four strategies + tests. The census run + harvest happen in Lane R (research) after merge
(Phase 5, not your scope — included for context).

---

## Hard rules (violating any one fails the phase)

1. **`decimal` for every price/money/lot value.** `double` only at the Skender indicator boundary
   (ATR comes back `double` in `context.IndicatorValues` — that is the only allowed crossing).
2. **Strategies read time from `MarketContext.EngineTimeUtc` only.** Never `DateTime.UtcNow`, never
   inject `IEngineClock` into a strategy. (Grep your diff for `UtcNow` and `IEngineClock` — must be
   empty in `TradingEngine.Strategies`.)
3. **Kernel is untouched.** These are strategies in `src/TradingEngine.Strategies/`. Do not edit
   `TradingEngine.Engine`, `TradingEngine.Domain/*` reducers, or anything that changes replay bytes.
   **Golden must stay 63/63 byte-identical.**
4. **No `Console.WriteLine`.** Serilog message templates via the injected `ILogger<T>` only.
5. **Braces on every `if`/`else`** — IDE0011 is promoted to a build error; a missing brace only
   surfaces at the end of a full build.
6. **Config auto-discovery, no manual wiring.** A strategy is registered purely by its
   `[StrategyId("...")]` attribute + `public static Create(StrategyConfigEntry, IServiceProvider)`
   factory (scanned by `StrategyRegistry.cs:14-50`). Do not touch `StrategyRegistry` or DI.
7. **No fitting to history.** The window/knob values are the pre-registered constants. Do not tune
   them against any backtest. No optimization, no grid search, no "it works better if…".
8. **No new NuGet deps.** Reuse what `session-breakout` already uses.

**Template to clone:** `session-breakout` is a working ORB with exactly the shape you need —
- `src/TradingEngine.Strategies/SessionBreakout/SessionBreakoutStrategy.cs` (the `IStrategy` impl,
  135 lines: range-build phase `:51-68`, entry phase `:70-95`, `Create` factory `:117-133`).
- `src/TradingEngine.Strategies/SessionBreakout/SessionBreakoutConfig.cs` (the `Parameters` record +
  `Config : IStrategyConfig`; note `FlattenAtUtc => Parameters.FlattenTimeUtc` at `:28` wires the
  loop-level daily flatten via `KernelTimeFlattenEvaluator`).
- `config/strategies/session-breakout.json` (the JSON schema: `id`, `riskProfileId`, `orderEntry`,
  `positionManagement` with SL `AtrMultiple`/TP `RrMultiple`/breakeven/trailing, `parameters`,
  `reentry`).

Reusable infra (do NOT re-implement): `SlTpResolver().Resolve(entryPrice, dir, atr, symbolInfo,
PositionManagement)` for SL/TP; `IndicatorRequest($"ATR_{n}", IndicatorType.Atr, n, Timeframe: tf)`
for ATR; `FlattenAtUtc` for session-end flatten; `SlTpResolver`, `StrategyFactoryHelper.
DeserializeParams<T>`, `ISymbolInfoRegistry`. For the Asian overnight (wrapping) window reuse
`SessionFilter.IsInSession(utc, open, close)` (`src/TradingEngine.Risk/Filters/SessionFilter.cs`,
handles `close < open`) rather than inline compares (SessionBreakout's inline compare assumes a
non-wrapping same-day window and is WRONG for 00:00–06:00 spanning logic if reused naively).

---

## Phase 0 — Shared session primitives (blocks Phases 1–4)

**Goal:** two small, unit-tested, self-contained helpers so the four strategies don't each
copy-paste the fiddly range-build + window logic (4× duplication is where the F78-class bugs hide).

**Do:** new folder `src/TradingEngine.Strategies/Sessions/`:
- `SessionWindow.cs` — `readonly record struct SessionWindow(TimeOnly StartUtc, TimeOnly EndUtc)`
  with `bool Contains(DateTime utc)` that handles the overnight wrap (`EndUtc < StartUtc` ⇒
  `t >= StartUtc || t < EndUtc`). Delegate to `SessionFilter.IsInSession` if it fits cleanly;
  otherwise implement the two-line wrap directly and unit-test both branches.
- `OpeningRange.cs` — `sealed class OpeningRangeTracker` that, given the entry-TF bars and a build
  `SessionWindow`, computes `(decimal High, decimal Low)?` over the bars whose `OpenTimeUtc` falls in
  that window **on the current calendar day** (mirror `SessionBreakoutStrategy.cs:53-66` exactly, but
  windowed by `SessionWindow.Contains`). Returns null until the window has ≥1 bar. Has `Reset()`.

**Test-first** (`tests/TradingEngine.Tests.Unit/Strategies/Sessions/`):
- `SessionWindowTests`: non-wrapping `07:00–08:00` includes 07:30 excludes 08:00/06:59; wrapping
  `22:00–06:00` includes 23:00 & 02:00, excludes 12:00. Boundary-inclusive start, exclusive end.
- `OpeningRangeTracker`: builds correct High/Low over 3 in-window bars, ignores out-of-window and
  prior-day bars, returns null with zero in-window bars, `Reset()` clears.

**Acceptance:** Unit green; `dotnet build` 0err/0warn; no changes outside `Sessions/` + its tests.
**Commit:** `feat(v4-session): shared SessionWindow + OpeningRangeTracker primitives`

---

## Phase 1 — `london-orb`

**Goal:** opening-range breakout at the London open. Clone `session-breakout`; swap in Phase-0
helpers and the frozen London windows.

**Do:** `src/TradingEngine.Strategies/LondonOrb/` → `LondonOrbStrategy.cs` (`[StrategyId("london-orb")]`)
+ `LondonOrbConfig.cs`. Logic = SessionBreakout's: build the range over `07:00–08:00` UTC via
`OpeningRangeTracker`; in the entry window `08:00–11:00`, go `Long` if `LatestTick.Mid` breaks range
high, `Short` if it breaks range low; `SlTpResolver` for SL/TP; `FlattenAtUtc = 16:00`. One position
per direction per day (reuse `reentry.blockWhileSameDirectionOpen`).
`config/strategies/london-orb.json` — copy session-breakout.json; set the **frozen** parameters:
```json
"parameters": { "atrPeriod": 14, "rangeStartUtc": "07:00:00", "rangeEndUtc": "08:00:00",
                "entryWindowEndUtc": "11:00:00", "flattenTimeUtc": "16:00:00" }
```
Keep `riskProfileId: "standard"`, SL `AtrMultiple 1.5`, TP `RrMultiple 2.0`, breakeven+trailing as
in the template. `enabled: true`.

**Test-first:** `LondonOrbStrategyTests` — synthetic bars: (a) range builds over 07:00–08:00; (b)
breakout above high in-window → `Long` intent with SL/TP set; (c) below low → `Short`; (d) no intent
before 08:00 or at/after 11:00; (e) no intent when price stays inside the range.

**Acceptance:** Unit + Integration green; strategy resolves via `StrategyRegistry` (add an
auto-discovery assertion if one exists for the bank); config loads via `StrategyConfigSeeder`.
**Commit:** `feat(v4-session): london-orb strategy + config + tests`

---

## Phase 2 — `ny-open-drive`

**Goal:** momentum continuation after the New York open. NOT an opening-range breakout — a
directional drive: if the first move after the NY open is up, go with it; symmetric for down.

**Do:** `src/TradingEngine.Strategies/NyOpenDrive/`. In the signal window `13:30–15:00` UTC: at the
first entry-TF bar that closes after 13:30, take the sign of that bar's close-minus-open (the opening
drive) and enter `Long`/`Short` in that direction (one entry per day). SL/TP via `SlTpResolver` with
ATR; `FlattenAtUtc = 20:00`. Knobs (frozen): `atrPeriod 14`, `signalStartUtc "13:30:00"`,
`signalEndUtc "15:00:00"`, `flattenTimeUtc "20:00:00"`, plus a `mode` param defaulting to `"drive"`
(a `"fade"` value inverts the direction — ship the knob, but the pre-registered census runs `drive`;
`enabled: true`, default `mode: "drive"`).
`config/strategies/ny-open-drive.json` accordingly.

**Test-first:** `NyOpenDriveStrategyTests` — up-drive bar after 13:30 → `Long`; down-drive → `Short`;
`mode: "fade"` inverts both; no intent outside 13:30–15:00; one entry per day.

**Acceptance:** as Phase 1. **Commit:** `feat(v4-session): ny-open-drive strategy + config + tests`

---

## Phase 3 — `asia-range`

**Goal:** breakout of the overnight Tokyo range at the London open. Same as `london-orb` but the
range is built over an **overnight-wrapping** window, so it MUST use `SessionWindow`'s wrap handling
(not the inline same-day compare).

**Do:** `src/TradingEngine.Strategies/AsiaRange/`. Build range over `00:00–06:00` UTC (Tokyo);
entry window `07:00–10:00`; breakout direction as london-orb; `SlTpResolver` SL/TP;
`FlattenAtUtc = 16:00`. Frozen knobs: `atrPeriod 14`, `rangeStartUtc "00:00:00"`,
`rangeEndUtc "06:00:00"`, `entryWindowEndUtc "10:00:00"`, `flattenTimeUtc "16:00:00"`.
(00:00–06:00 does not actually wrap midnight, but route it through `SessionWindow` anyway so the
family is uniform and the primitive is exercised.) `config/strategies/asia-range.json`.

**Test-first:** `AsiaRangeStrategyTests` — range builds over 00:00–06:00; breakout in 07:00–10:00 →
correct direction; no entry outside window; plus one explicit `SessionWindow` wrap test at the
strategy level (a 22:00–06:00 fixture) so the wrap path is covered even though the shipped window
doesn't wrap.

**Acceptance:** as Phase 1. **Commit:** `feat(v4-session): asia-range strategy + config + tests`

---

## Phase 4 — `day-of-week`

**Goal:** a weekday directional-bias strategy — the lowest-frequency family member. One entry at a
fixed hour on the pre-registered weekday(s), fixed hold to EOD flatten. This is net-new: no
day-of-week gate exists in the codebase today (only ad-hoc weekend checks).

**Do:** `src/TradingEngine.Strategies/DayOfWeek/`. Knobs (frozen): `entryHourUtc "00:00:00"`,
`flattenTimeUtc "23:00:00"`, `atrPeriod 14`, and a `weekdays` list (default `["Monday"]`) + a
`direction` param (default `"Long"`). At `entryHourUtc` on a listed weekday, open one position in
`direction` with ATR-based SL/TP via `SlTpResolver`; flatten at `flattenTimeUtc`. Gate purely on
`context.EngineTimeUtc.DayOfWeek` (inline is fine here — it's a single comparison). `enabled: true`.
`config/strategies/day-of-week.json`.

**Test-first:** `DayOfWeekStrategyTests` — entry fires on Monday at 00:00, not on Tuesday, not at
other hours; `direction: "Short"` flips; SL/TP set; `weekdays: ["Monday","Wednesday"]` fires both.

**Acceptance:** as Phase 1. **Commit:** `feat(v4-session): day-of-week strategy + config + tests`

---

## Definition of Done (whole plan, before merge to `iter/viability`)

- `dotnet build` — **0 errors, 0 new warnings**.
- `dotnet test tests/TradingEngine.Tests.Unit` — green (existing 780 + the new strategy/primitive
  tests).
- `dotnet test tests/TradingEngine.Tests.Integration` — green (156).
- `dotnet test tests/TradingEngine.Tests.Simulation` — green (144).
- **Golden 63/63 byte-identical** (`dotnet test` the golden suite / kernel oracle — these strategies
  must not have moved a single replay byte).
- `python tools/research/determinism_probe.py` — PASS (concurrent tape runs deterministic; required
  before the census uses `--parallel`).
- All four strategies auto-discovered by `StrategyRegistry`; all four config files load via
  `StrategyConfigSeeder` with the **exact frozen parameter values** from the pre-registration.
- Diff touches only `src/TradingEngine.Strategies/{Sessions,LondonOrb,NyOpenDrive,AsiaRange,DayOfWeek}/`,
  `config/strategies/{london-orb,ny-open-drive,asia-range,day-of-week}.json`, and the matching test
  folders. **No kernel, no `StrategyRegistry`, no DI, no `Domain` reducer changes.**

**Sequencing:** Phase 0 first (blocks 1–4). Phases 1–4 are independent — parallelizable, one commit
each. Do one phase per commit; keep the suite green between them.

---

## Phase 5 — Census + harvest (Lane R / research author — NOT your scope, context only)

After merge, the research lane will: clone `tools/research/census_driver.py` →
`v4_census_driver.py` (swap `STRATEGIES` = the 4 ids, `SYMBOLS` = the 10 FX symbols, `TIMEFRAMES` =
`["M15","H1"]`, `EXPERIMENT_NAME`/`SPEC`; **keep the `exp_id[:8]` idempotency namespacing at
`census_driver.py:143` — the F83 safeguard**; keep `maxDdEnabled=false`, `--parallel 3`,
`--prune-journal`); run a 2-cell pilot chosen to be able to FAIL (verify healthy trade counts — the
F78/F79 starvation guard), paste the wall-time + trade-count extrapolation, then the 80-cell batch;
harvest via `v2_harvest.py` (its 4-part `census/fam/sym/tf` label parser already accepts M15). The
family-pooled H-SESSION verdict is the gate; the stop rule is binding.
