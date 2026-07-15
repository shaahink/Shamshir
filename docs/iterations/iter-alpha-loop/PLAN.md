# iter-alpha-loop — Feed the machine: scored setup search, conductor-driven

**Mission:** turn the (now truthful) engine + (never used) research tooling into a running,
scored search for FTMO-viable setups — with the agent doing the searching, machine verdicts
doing the verifying, and the owner reviewing only candidate cards at the end.

**Branch:** cut `iter/alpha-loop` from `develop`.
**Prerequisite reading (in order):** this file → `../iter-parity-pipeline/DELIVERY-VERIFICATION.md`
→ `../iter-land-fix/ENGINE-TRUTH.md` → `docs/audit/RECONCILE-FINDINGS.md` → `docs/agents/ctrader-quickstart.md` (with F21 corrections).

**Verified starting state (2026-07-10):** tape venue truthful + deterministic (byte-identical
replay verified); 14 symbols × 6 TFs × 1 year of tape (2025-07-04→2026-07-05); ResearchCli
verbs work E2E with machine verdicts; 11 playbooks on disk; walk-forward has an honest test leg
(code-verified); **every research table is empty** — this plan is what finally feeds them.

---

## 0. Owner decisions (locked; override by editing this block before launch)

| # | Decision | Choice |
|---|---|---|
| D1 | Search venue | Tape only for volume; cTrader only as parity guard (R2/R4) + transport tests. Never burn wall-clock searching on cTrader. |
| D2 | Universe | 14 symbols × {H1, H4} primary (M15 only if a candidate demands it). Window: full year, with final 60 days (2026-05-06→2026-07-05) EMBARGOED — untouched until R4. |
| D3 | Validity floor | A scored cell needs ≥ 20 trades in its window, DataQuality PASS (market-hours aware), zero engine warnings. Below floor → score = null, never 0 (0 is information; null is "insufficient data"). |
| D4 | Scoring | SetupScore v1 (§2). Deterministic, versioned (`sv1`), written to `ExperimentRuns.ScoreJson`. Formula changes = new version, never in-place. |
| D5 | Anti-overfit | Variants pre-registered in the session prompt/ledger BEFORE running; ≤ 12 variants/session; survivors must hold OOS ratio ≥ 0.5 in walk-forward; embargoed window touched exactly once. |
| D6 | Human gates | Owner reviews at exactly two points: after R2 (parity verdict) and after R5 (candidate cards). Everything else auto-advances on machine verdicts + conductor verifier score ≥ 80. |
| D7 | Budget | Token ceiling 64k/session, ≤ 16 sessions planned (~$1.50–3.00 at DeepSeek rates). Tape runs are seconds — wall-clock budget lives in gates, keep them tiered (§5). |
| D8 | Reporting | Every session appends to `docs/iterations/iter-alpha-loop/LEDGER.md` (append-only) + scoreboard artifact `evidence/scoreboard-sN.md`. Cost/time land in conductor's report automatically. |

---

---

## 0b. AMENDMENT 2026-07-11 — R1 and R2 are INVALID. Parity work inserted before R3.

**Read `PARITY-TRUTH.md` before anything else in this file.** It supersedes
`R2-DIVERGENCE-INVESTIGATION.md`, whose money analysis was computed with inverted cost signs.

Summary of what was found:

| # | Defect | Impact |
|---|---|---|
| F1 | Tape stores costs **positive**, cTrader stores them **negative**; reconcile compares raw | Every money delta in the R2 doc is void |
| F2 | cBot partial-close subtracts already-negative costs (`TradingEngineCBot.cs:571-573`) | Partial closes over-report by 2× costs. Latent — fires when R3 turns packs on |
| F3 | `config/symbols.json` is fabricated; XAUUSD long swap is a **credit** | The #1 cell (`trend-breakout/XAUUSD/H4`, score 100.0) is subsidised by carry that doesn't exist |
| F4 | Uncommitted `commissionPerMillion` formula uses base-currency units as USD notional | XAUUSD commission ~3,300× too low; BTCUSD ~60,000×. **Do not commit as-is** |
| F5 | R1's "252 cells" were 28 runs with 9 strategies commingled in one account | Strategies blocked each other; 40% of every score comes from a shared equity curve; only 4 `ExperimentRuns` rows persisted (gate required ≥225) |
| F6 | Position sizing diverges ~29% between venues (USDCAD) | Never measured — reconcile compares aggregates, not per-trade lots/prices |
| F7 | Limit-entry machinery exists on **both** venues and is unused by default | The structural fix for entry-price divergence |
| F8 | Kernel is a pure reducer → concurrent tape runs are safe; no queue/limiter exists | Blocks the run-queue feature, not correctness |
| F9 | cTrader leg's progress uses a **calendar** bar estimate (`BacktestOrchestrator.cs:1028`) | The "stuck at ~70%" progress bar |

**New decisions (locked; override by editing this block):**

| # | Decision | Choice |
|---|---|---|
| D9 | Cost sign convention | **One convention everywhere: costs are NEGATIVE.** `Net = Gross + Commission + Swap` (cTrader/industry). Tape changes to match cTrader, not the reverse. Enforced by an invariant test on every `TradeResult`. |
| D10 | Symbol economics | **The venue declares them; we never invent them.** The cBot emits a `symbol_spec` message (commission + type, swap long/short + calc type, lot size, pip/tick size, digits, triple-swap day). Persisted as `VenueSymbolSpec`; the tape reads it. `symbols.json` becomes a loudly-logged fallback only. **No hardcoded fudge factors, ever** — if the tape disagrees with cTrader, we fix the model, not the number. |
| D11 | Entry style | **Limit entries are the research default** (`OrderEntry.Method = LimitOffset`). A limit fills at the price we named on both venues, so entry price is identical *by construction*. Market entries stay available but are not used for scored search. |
| D12 | Parity is a permanent gate, not a phase | `research parity` verb + a pre-registered tolerance budget (§P4). Any scored candidate carries a parity verdict ≤ 14 days old. A cell that cannot pass parity cannot be a candidate. |
| D13 | R1 re-run shape | **One cell = one run.** No strategy commingling in a census. Below-floor cells persist a null score **with reason** (D3 was not honoured). |
| D14 | Platform track | The X-phases (§X) run in parallel with P — different files, no engine overlap. They are prerequisites for R3 being *drivable* (queue, progress truth, cTrader process ownership). |

---

## 1. Phase map (revised 2026-07-11)

```
R0  Readiness & truth                   — DONE
R1  Baseline sweep                      — INVALID (F5). Re-run as R1' after P.
R2  Parity guard                        — INVALID (F1). Re-run as P4 verdict.

P0  Cost-sign truth (1 session)         — one convention, invariant test, cBot partial-close fix
P1  Venue-declared symbol specs (1–2)   — cBot emits spec → DB → tape; correct commission + swap model
P2  Limit-entry parity (1)              — identical resting-order semantics both venues, contract test
P3  Exit + spread parity (1)            — gap-through fills, one spread number both venues
P4  Parity as a gate (1)                — `research parity` verb + tolerance budget  [OWNER GATE]

X0  Run queue + concurrency (1–2)       — bounded tape pool, serial cTrader lane, persisted queue
X1  Progress + status truth (1)         — real bar counts server-side, lifecycle, cTrader PID ownership
X2  Runs page + notes + copy-run (1)    — richer table, notes, clone params, compare-pair grouping
X3  Trade chart rework (1)              — context window, real entry/exit markers, prev/next navigation
X4  Data manager auto-sync (1)          — coverage view, sync-to-latest, gap report

R1' Baseline sweep, redone (2)          — one cell per run, null-with-reason persisted
R3  Refinement loop (3–5)               — DONE (2 sessions; owner call to proceed to R4)
R4  FTMO dress rehearsal (1–2)          — unchanged
R5  Final audit + candidate cards (1)   — unchanged                                  [OWNER GATE]
```

**Ordering:** P0 → P1 → P2 → P3 → P4 strictly linear (each depends on the last). X-phases run in
parallel with P (different files). R1' needs P4 green. R3 needs R1' + X0/X1.

**The P-phases are not optional polish.** Until parity holds, every score is measured against a tape
that pays you to hold gold.

---

## 2. SetupScore v1 (the scoring system — implement in R0, never bend in R1–R5)

Computed per (strategy, symbol, timeframe, packId, riskProfileId, window) from DB data only.

**Hard validity gates (fail any → score = null + reason):**
- trades ≥ 20 in window; DataQuality PASS on window; run status `completed` (no warnings — F19
  must be fixed first); tape venue; parity guard ≤ 14 days old at scoring time (R2 onward).

**Components (0–100 weighted sum):**
| Component | Weight | Definition |
|---|---|---|
| Expectancy | 30 | mean R per trade, mapped: ≤0R→0, ≥0.5R→100, linear between |
| FTMO survival | 25 | % of rolling 30-day sim-challenges passed (5% daily / 10% max DD, from EquitySnapshots + governor rules), 0–100 direct |
| Drawdown | 15 | MaxDD%: ≤3%→100, ≥10%→0, linear |
| Consistency | 15 | share of profitable calendar months, 0–100 direct |
| Robustness (OOS) | 15 | walk-forward test/train expectancy ratio, capped at 1 → ×100; **null until R3 runs walk-forward — report "sv1-partial" before that** |

**Persistence:** one `Experiment` row per batch (Name, Hypothesis, SpecJson = the pre-registered
variant list); one `ExperimentRun` row per scored cell (BacktestRunId, VariantLabel, FoldIndex/
FoldRole for walk-forward, ScoreJson = components + total + version + validity reasons).
**New CLI verb:** `research score --run <id> [--experiment <id> --variant <label>]` → computes,
persists, prints `VERDICT: PASS score=NN.N version=sv1` (or `VERDICT: FAIL reason=below-floor`).
And `research scoreboard --experiment <id> --top 20 --out <path.md>` → the artifact.

---

## 3. Stages

### R0.1 — Truth fixes (1 session)
**Approach:** fix in this order, smallest first: (a) F20 `CTraderListenService.cs:105` →
`DbPathResolver`; (b) F21 rewrite `docs/agents/ctrader-quickstart.md` — port 5134, remove
`/api/health` reference (endpoint doesn't exist — add a real one: `GET /api/system/health`
returning `{status,dbPath,version}`), replace "kill all dotnet" with kill-by-PID; (c) F19 —
the P0.3 barrier false-positive: reproduce on a fresh tape run first (expect
`TRADES_PARTIALLY_UNRECONSTRUCTABLE` on a healthy run), then fix the pairing to recognize the
tape journal shape (or scope the barrier to venue=ctrader), verify warning gone; (d) F18 —
compare-both child spawn: trace `BacktestOrchestrator.RunCompareBothAsync` (~line 955–1048),
restore B3-style child registration (manual `_runs` insertion), make the child row visible in
the DB from spawn moment, never torn down in `finally`.
**Truth gate:** `research run start` tape EURUSD H1 2026-03-03→03-09 → `run validate --min-trades 1
--require-status completed --forbid-warnings` = PASS; compare-both spawns BOTH children (DB rows).
**Watch out:** background the web app (`Start-Process` + log file), poll `/api/runs/{id}`;
NEVER foreground `dotnet run`; port 5134; kill only PIDs you started. Golden fixtures must not move.

### R0.2 — Score verb + doctor + data-quality calendar (1 session)
**Approach:** (a) implement §2 exactly — `research score` + `research scoreboard`, unit tests
for the formula (pin the mapping edges: 0R→0, 0.5R→100, etc.), integration test persisting an
ExperimentRun for run `8bd9cedb` (known: 3 trades → below floor → score=null, reason recorded —
that IS the test); (b) `research doctor` verb — app reachable (5134), DB migrated, marketdata
coverage for a requested window, cTrader CLI locatable, creds file exists → one VERDICT line;
(c) make DataQuality market-hours aware: FX weekend closures are not gaps (verify: EURUSD H1
full-year check should drop from ~25k violations to near-0; crypto stays 24/7).
**Truth gate:** `research doctor` PASS; `research score --run 8bd9cedb` persists a row with
validity-null; `data quality` EURUSD H1 full year = PASS.
**Watch out:** don't touch decision paths — scoring reads DB only. If any golden moves, stop.

### R1 — Baseline sweep (2 sessions, batch playbook)
**Approach:** create Experiment `baseline-sv1` (Hypothesis: "default configs ranked").
Script the batch via ResearchCli (a new playbook `alpha-baseline.json` or a loop in the session):
9 strategies × 14 symbols × {H1,H4}, defaults, full year minus embargo (2025-07-04→2026-05-05).
~250 tape runs; expect seconds each — run serially, log progress every 10 runs (heartbeat).
Score every run; commit `evidence/scoreboard-s1.md` (top 20 + full CSV).
**Truth gate:** `research scoreboard --experiment baseline-sv1` shows ≥ 90% cells scored-or-null
with reasons; ExperimentRuns ≥ 225; artifact committed.
**Watch out:** below-floor cells are EXPECTED (H4 especially — 20-trade floor on 10 months);
null ≠ failure. Don't "fix" strategies mid-sweep — record findings to the ledger; the sweep is
a census, not a repair mission. If a run errors, record + skip, don't retry-loop.

### R2 — Parity guard (1 session) [OWNER GATE after]
**Approach:** for the top-3 cells: compare-both on two 2-week windows each (needs F18 green).
Auto-reconcile: `research reconcile --left <tape> --right <ctrader>`; fill the V4 table in
RECONCILE-FINDINGS.md. Classify: RawMoney delta explained by F1 spread + F2 1-bar lag model?
Trade-count divergence (the old F6) — if counts differ by >20%, STOP the plan and file the
signal-parity investigation as the next stage (a scored search on a diverged tape is worthless).
**Truth gate:** reconcile artifacts committed; explicit PASS/FAIL verdict per pair; tracker row
carries `HUMAN:` line for owner sign-off.
**Watch out:** cTrader runs need creds + the cBot build; expect BAR_STREAM_TIMEOUT warning
(F23, known, not a failure); serialize cTrader runs (no parallel CLI instances).

---

## 3b. P-phases — Parity truth (inserted 2026-07-11; see PARITY-TRUTH.md)

**Governing principle: stop modelling the venue, make the venue declare itself.** Every parity fix
below removes a guess and replaces it with a number cTrader gave us. If a gap remains after that, we
fix the *model*. We never tune a constant to make the numbers meet (D10).

### P0 — Cost-sign truth (1 session)
**Approach:** (a) adopt D9 everywhere: costs NEGATIVE, `Net = Gross + Commission + Swap`. Change
`TradeCostCalculator.cs:65` and the tape/replay adapters' `TradeResult` writes; cTrader already
complies. (b) Fix `TradingEngineCBot.cs:571-573` — the partial-close path must not subtract
already-negative costs; derive net the same way the full-close path does. (c) Add an invariant test
over `TradeResults`: `|Net − (Gross + Commission + Swap)| < 0.01` for **every** row, both venues —
this is the regression guard that makes F1 unrepeatable. (d) Teach the reconcile to compare costs as
signed values under the one convention, and to emit **per-trade** rows (lots, entry, exit, SL, commission,
swap) — not just aggregates, so F6 becomes visible.
**Truth gate:** invariant test green on a fresh tape run AND a fresh cTrader run; reconcile of the
four R2 runs (`9f0ea5e5`/`197598ab`, `e29c5dfe`/`00aaba6a`) reprinted with corrected deltas, committed
to `evidence/p0-signs.md`.
**Watch out:** existing `TradeResults` rows carry the OLD tape sign. Either migrate them or stamp a
`CostConvention` column — do NOT leave a table with two conventions in it. Golden fixtures WILL move
(net is unchanged, but Commission/Swap flip sign): re-bless deliberately, in its own commit, and say so.

### P1 — Venue-declared symbol specs (1–2 sessions) — kills F3 + F4
**Approach:** (a) cBot: on connect, emit `symbol_spec` for the run's symbol — `Symbol.Commission`,
`Symbol.CommissionType`, `Symbol.SwapLong`, `Symbol.SwapShort`, `Symbol.SwapCalculationType`,
`Symbol.LotSize`, `Symbol.PipSize`, `Symbol.TickSize`, `Symbol.TickValue`, `Symbol.Digits`,
triple-swap day, current spread. (b) Engine: persist `VenueSymbolSpec` (symbol, broker, capturedAtUtc,
all fields). (c) `ISymbolInfoRegistry` prefers the venue spec; falls back to `symbols.json` with a
**loud warning naming the symbol** (a silent fallback is how F3 survived this long). (d) Capture specs
for all 14 symbols. (e) Rewrite the commission model to honour `CommissionType` properly —
for `UsdPerMillionUsdVolume`: `notionalUsd = lots × contractSize × baseToUsdRate(price)`, charged
**per side**: half at entry price on open, half at exit price on close. (f) Swap from the venue's
rates + calc type.
**Truth gate:** on a 2-month XAUUSD compare-both with identical trade sets —
`|tape commission − cTrader commission| ≤ 2%` and `|tape swap − cTrader swap| ≤ 5%`. Evidence at
`evidence/p1-symbol-specs.md` with the captured spec JSON for all 14 symbols.
**Watch out:** the shipped `commissionPerMillion` diff is wrong (F4) — **revert the formula, keep the
`OrderEntry` override plumbing in `BacktestOrchestrator.cs:896-916`.** Commission moving from
close-only to half-at-open changes intra-trade equity, so MaxDD and FTMO-survival scores will shift.
That is correct, not a regression — say so in the ledger. `PreTradeGate.cs:243` also computes
commission for the worst-case gate; it must use the same model or the sizer and the ledger disagree.

### P2 — Limit-entry parity (1–2 sessions) — kills the entry-price gap (old F1/F2)
**Approach:** (a) Write the resting-order contract down first (`docs/reference/RESTING-ORDER-CONTRACT.md`):
the touch rule (buy limit fills when **ask** ≤ limit), the fill price (**exactly** the limit, never
better), expiry in **bars** and how bars map to cTrader's wall-clock expiry, and cancel semantics.
(b) Make both venues obey it — tape (`TapeReplayAdapter.cs:434-465`) already fills at exactly
`LimitPrice`; verify the cBot's `PlaceLimitOrder` (line 393) uses the same expiry and the same
cancel-on-expiry path (line 477-483). (c) A **contract test** that drives the same synthetic bar
sequence through both venues and asserts identical fill/no-fill decisions and identical fill prices.
(d) Flip the research default to `LimitOffset` (D11).
**Truth gate:** compare-both on 2 cells × 2 windows with limit entries → **entry prices identical to
the tick on every matched trade**, and fill/no-fill decisions identical (zero unmatched orders).
**Watch out:** the failure mode here is one venue filling an order the other expired — that shows up
as a trade-count divergence and looks like a signal bug. If counts diverge, suspect expiry semantics
before you suspect the strategy. Also: a limit that never fills is a *skipped trade*, and skipped
trades change the equity path — expect trade counts to DROP vs the market-entry baseline. That is
expected and is not a regression.

### P3 — Exit + spread parity (1 session)
**Approach:** (a) Gap-through fills (the never-measured F4 gap): when a bar opens beyond the stop, the
tape must fill at the **bar open**, not at the stop price. Verify cTrader does the same; test with a
synthetic gap bar. (b) One spread number: the tape reads `TypicalSpread` from `symbols.json` while
cTrader gets `--spread`. Feed both from the same source (venue spec, or the run's `spreadPips` applied
identically to both). (c) Confirm the exit side applies the spread in the correct direction on both
venues (`SpreadConvention`).
**Truth gate:** per-trade exit-price delta ≤ 1 tick on ≥ 95% of matched trades; gap fills listed
explicitly and explained; evidence at `evidence/p3-exit-parity.md`.

### P4 — Parity as a permanent gate (1 session) [OWNER GATE after]
**Approach:** (a) New verb `research parity --strategy S --symbol Y --tf T --from D --to D` — runs
compare-both, reconciles per-trade, prints a signed tolerance report + one `VERDICT:` line.
(b) **Pre-registered tolerance budget** (the owner's "identical, or the margin is minimal"):

| Quantity | Tolerance | Rationale |
|---|---|---|
| Trade count | **exact** | limit entries make this deterministic; any mismatch = FAIL + list unmatched trades |
| Entry price | **≤ 1 tick** | limit fills at the named price on both venues |
| Position size (lots) | **exact** | same inputs → same sizer; a mismatch means F6 is alive |
| Exit price | ≤ 1 tick on ≥95% | gap fills exempted but must be listed |
| Commission | ≤ 2% | venue-declared spec, same formula |
| Swap | ≤ 5% | venue-declared rates; night-count edges are the residual |
| Net PnL | ≤ 1% of gross | falls out of the above |

(c) Record a cTrader ledger as a **golden pair** checked into the repo so parity can be re-verified
offline in CI without a live cTrader.
**Truth gate:** `VERDICT: PASS` on 3 cells × 2 windows. Any FAIL → **STOP and escalate to the owner**;
do not widen the window, do not widen the tolerance, do not proceed (see AGENTS.md "Gates are not
negotiable").

---

## 3c. X-phases — Platform (parallel with P; owner's request list)

### X0 — Run queue + concurrency (1–2 sessions)
Kernel is a pure reducer with per-run state (F8) → concurrent **tape** runs are safe. Build: a
persisted run queue (`queued`/`running`/`completed`/`failed`/`cancelled`), a bounded worker pool for
tape (start at 3, configurable), and a **strictly serial lane for cTrader** (one CLI, one desktop —
never parallel). Queue visible and manageable from the Runs page. Guard SQLite writes (WAL = one
writer): serialize writes or retry on `SQLITE_BUSY`.
**Gate:** 5 tape runs queued at once → all complete, results byte-identical to running them serially
(this is the real proof of concurrency safety). A cTrader run queued behind them waits its turn.

### X1 — Progress + status + cTrader process ownership (1 session)
(a) Kill the calendar estimate (F9): resolve the **real** bar count server-side for every venue before
the run starts, using the query the tape path already uses (`BacktestOrchestrator.cs:~1185`); the
cTrader child at line 1028 must use it too. One progress source, server-side, for all venues.
(b) Reliable lifecycle: honest `startedAt`/`finishedAt`/terminal status for every run, every venue.
(c) **cTrader process ownership:** track the CLI PID with the run, kill it on cancel/failure, reap
orphans on startup. (d) Root-cause "every cTrader backtest is stored with a warning" — the
`TRADES_PARTIALLY_UNRECONSTRUCTABLE` barrier was *scoped* to cTrader (`BacktestOrchestrator.cs:522`)
rather than fixed. **Fix the journal pairing; do not scope the warning away.**
**Gate:** a cTrader run finishes with **zero** warnings; progress is monotonic and lands on 100%;
cancelling a run leaves no orphaned `ctrader-cli.exe`.

### X2 — Runs page, notes, copy-run (1 session)
Richer table (strategy, symbol, TF, venue, trades, net, DD, score, duration, notes, queue position).
Notes on a run — create from the Runs page, **edit from the report page**. "Copy run" clones params
into a new run; "reuse last params" prefills the form. Fix compare-both pair grouping so the cTrader
child is displayed as its own venue and paired with its tape sibling (the child *is* tagged
`Venue="ctrader"` at `BacktestOrchestrator.cs:1025` — verify whether the bug is in the API projection
or the SPA before changing the backend). Live via SignalR, reconciled against `RunDataCache` —
**note the known cache bugs first** (`docs/iterations/iter-cache-reads-2/PLAN.md`: live snapshots
freeze after first read; `MarkCompleted`/`Evict` never called). Fix those or the "liveness" is a lie.

### X3 — Trade chart rework (1 session)
Per-trade chart currently shows meaningless lines. Rebuild: window = N bars **before** entry through N
bars **after** exit (context, zoomed to focus — default N ≈ 20, configurable); real entry/exit markers
(directional arrows with price + time); SL/TP lines and any modifications (BE/trail) as they moved;
prev/next trade navigation that keeps the chart mounted.

### X4 — Data manager auto-sync (1 session)
Coverage view per symbol × TF; "sync to latest" against the current date; gap report that is
market-hours aware (weekends are not gaps).

---

### R3 — Refinement loop (3–5 sessions, identical protocol each)
**Approach per session:** (1) read scoreboard + ledger; (2) PRE-REGISTER ≤ 12 variants in the
ledger with hypothesis each (pack tweaks from the 3 AddOnPacks, risk profile swaps, exit-lab
calibrations via `exitlab eval --grid`, session filters via VenueSessions data); (3) run + score;
(4) walk-forward the session's best 3 (6 rolling windows, train 60d / test 30d) — populates
FoldIndex/FoldRole and upgrades their score to full sv1; (5) cull: OOS ratio < 0.5 → parked
(StrategyCellParks), never deleted; (6) commit scoreboard-sN.md + ledger delta.
**Truth gate per session:** ExperimentRuns grew by ≥ pre-registered count; walk-forward
WindowResults ≥ 18 (3×6); scoreboard artifact fresh; every variant in the ledger has a result.
**Watch out:** the loop's failure mode is p-hacking — the verifier must reject any session whose
run variants don't match its pre-registration. Walk-forward wall-clock: ~6× sweep cost per
candidate — background + poll, heartbeat every 3m. Embargoed window is UNTOUCHABLE until R4.

### R4 — FTMO dress rehearsal (1–2 sessions)
**Approach:** top-3 surviving candidates: governor ON, prop-rule set ON, exploration OFF,
HonestFills default, tape on the EMBARGOED window (2026-05-06→2026-07-05, first and only touch).
Then 3 rolling 30-day challenge sims each from EquitySnapshots. Produce
`evidence/candidate-cards.md`: config JSON, full-year + OOS + embargo scores, challenge pass
rate, worst day, expected time-to-target.
**Truth gate:** cards exist with every number traceable to a RunId; embargo runs flagged in
ledger as one-shot.
**Watch out:** if all 3 fail the embargo, that IS the deliverable — report honestly; do not
iterate on the embargo window (that's what it exists to prevent).

### R5 — Final audit + owner pack (1 session)
Audit R0–R4 against this plan (CONFORMS/CWF/DEVIATES per stage), verify empty-table fact is
dead (row counts printed), ≤ 5-item bugfix queue, update AGENTS.md RESUME, hand the candidate
cards + a one-page "what I'd do next" to the owner.

---

## 4. Session protocol (every session, non-negotiable)

1. `research doctor` first — env verdict before any work (replaces rediscovery).
2. QA the previous session's claims against artifacts (run one spot-check yourself).
3. Deliver YOUR stage only; pre-register before running anything scored.
4. Append findings to LEDGER.md AS YOU LEARN THEM — not at session end (stall-kill loses
   end-of-session knowledge; mid-session ledger writes survive).
5. Background everything > 3 min; heartbeat print every 3 min; port 5134; kill by PID only.
6. Evidence or it didn't happen: every DONE row cites an artifact path + a VERDICT line.
7. End with `SESSION-RESULT:` paragraph + tracker handoff (≤ 12 lines, overwrite).

## 5. Conductor configuration (for `conductor.plan.json` of this run)

- **Gates, tiered to kill duplicate burden:** `fast` per session = build + unit only (~90s);
  `truth` per stage = the stage's ResearchCli verdict line (seconds — it's an HTTP call);
  `full` battery (integration + sim-fast + golden) ONLY at stage confirm, once, cached by HEAD
  SHA — never re-run on an unchanged tree.
- **Stall:** stallMinutes 15 is fine ONCE protocol §4.5 is in the prompt preamble; conductor's
  soft-kill debrief (v-next F3) makes this safe permanently.
- **Verifier:** score sessions 0–100 against §4; < 80 → findings feed the retry prompt
  (fail-then-better-retry, systematized). Auto-advance ≥ 80 except R2/R5 owner gates.
- **Budget:** tokenCeiling 64k; maxResumes 2; same-failure breaker at 2.
- **readOrder:** this PLAN.md → TRACKER.md → DELIVERY-VERIFICATION.md → ENGINE-TRUTH.md → LEDGER.md.

## 6. Verification matrix (what the owner can check in 5 minutes, any time)

```sql
-- the machine is eating:
SELECT COUNT(*) FROM ExperimentRuns;            -- grows every R1/R3 session
SELECT COUNT(*) FROM WalkForwardWindowResults;  -- grows in R3
-- no lies (completed runs only — an interrupted/'running' run legitimately shows a stats-vs-trades
-- skew because summary stats are written before trades settle; scope matches the D13 scoring gate,
-- which only ever reads Status='completed'. Fixed R5: the unscoped query returned 9 false positives
-- from stuck/pre-migration test-proof runs, all non-completed — see R5-AUDIT.md §3 item #3):
SELECT COUNT(*) FROM BacktestRuns WHERE Status = 'completed' AND TotalTrades !=
  (SELECT COUNT(*) FROM TradeResults t WHERE t.RunId = BacktestRuns.RunId); -- always 0
```
Plus: latest `evidence/scoreboard-sN.md` top-20, and the conductor REPORT.md cost/time lines.
