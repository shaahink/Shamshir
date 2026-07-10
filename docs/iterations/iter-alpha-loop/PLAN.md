# iter-alpha-loop — Feed the machine: scored setup search, conductor-driven

**Mission:** turn the (now truthful) engine + (never used) research tooling into a running,
scored search for FTMO-viable setups — with the agent doing the searching, machine verdicts
doing the verifying, and the owner reviewing only candidate cards at the end.

**Branch:** continue on `iter/parity-pipeline` (or cut `iter/alpha-loop` from it).
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

## 1. Phase map

```
R0  Readiness & truth (2 sessions)      — fix F18/F19, market-hours DataQuality, score verb, doctor verb
R1  Baseline sweep (2 sessions)         — score all 9 strategies × 14 sym × {H1,H4}, defaults
R2  Parity guard (1 session)            — compare-both + reconcile on top cells   [OWNER GATE]
R3  Refinement loop (3–5 sessions)      — pre-registered variants, walk-forward, cull
R4  FTMO dress rehearsal (1–2 sessions) — governor ON, embargoed window, challenge sims
R5  Final audit + candidate cards (1)   — audit, bugfix queue, owner review pack  [OWNER GATE]
```
Dependencies strictly linear except R0.2 ∥ R0.1 (different files). No other parallelism —
sessions are cheap, merge conflicts are not.

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
-- no lies:
SELECT COUNT(*) FROM BacktestRuns WHERE TotalTrades !=
  (SELECT COUNT(*) FROM TradeResults t WHERE t.RunId = BacktestRuns.RunId); -- always 0
```
Plus: latest `evidence/scoreboard-sN.md` top-20, and the conductor REPORT.md cost/time lines.
