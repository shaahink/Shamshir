# iter-structural-edge — Session Ledger (append-only)

**Started:** 2026-07-16 — S0 session

Every session appends below. Mid-session findings go here immediately (stall-kill safety).
Do NOT delete or edit prior entries — this is an audit trail.

---

## S0 — 2026-07-16 — Truth infrastructure (sv2 scoring + committed research tools)

**Session mode:** MANUAL interactive session (owner call — the conductor run was cancelled at
launch; the conductor plan `conductor-structural-edge.plan.json` stays committed and TRACKER.md
stays parseable so remaining stages can be handed to conductor later).

### Setup (pre-S0, same session)

- Iteration opened by owner. Plan docs + seed TRACKER + conductor plan committed to `main` @
  `e3c96e1` and pushed (main was ~15 conductor-chore commits ahead of origin — now synced).
- Work branch `iter/structural-edge` created from `e3c96e1`, pushed with upstream. Conductor plan
  got `branchPattern: ^iter/structural-edge$`.
- AGENTS.md RESUME rewritten: iteration OPEN, stage S0.

### S0.1 — sv2 scoring (F63 executed, D4)

- `ChallengeSimulationService.ComputeSurvivalAsync(runId)` (new): buckets the run's real equity
  into engine-truth trading days (`BuildDailyPoints`, the R4 machinery), rolls a 30-day
  `ChallengeSimulator` window from EVERY daily start, returns
  `ChallengeSurvival(PassRate, Windows, Passes, Fails, Incompletes, RuleSetId)`. **PassRate =
  Pass/Windows — Incomplete counts as non-pass on purpose** (R4's headline failure mode was
  velocity; a survival score that forgave incompletes would hide it). Returns null (component
  skipped, composite renormalizes) when: no snapshots, < 30 daily buckets, or no resolvable
  risk-profile→prop-firm rule set. Rule-set resolution extracted to `ResolveRuleSetAsync`
  (shared with `SimulateAsync`, which still throws).
- `SetupScoreService`: placeholder `ComputeFtmoSurvival` DELETED; survival component =
  `PassRate × 100`; score version strings sv1→**sv2** everywhere (`sv2`/`sv2-partial`/`sv2-null`);
  evidence trail persisted in `Components` (FtmoPassRate/Windows/Passes/Fails/Incompletes/
  RuleSetId). Ad-hoc default bucket renamed `default-sv1`→`default-sv2` (new bucket; the old one
  keeps only sv1 rows). **sv1 rows untouched — no retro-rescore (D4); census 075D5240 stays sv1.**
- Tests pinning the PLAN G0 contract:
  - Unit `ChallengeSimulatorTests.DailyCapBreach_Dominates_TargetHit` — a day that ends above
    the profit target but lost more than the daily cap FAILS (breach checked before target).
  - Integration `SetupScoreSv2Tests`: flat 60d equity → 31 windows all Incomplete →
    FtmoSurvival **0** (0/N scores 0, not null); +1%/day compounding 60d → 31/31 Pass →
    FtmoSurvival **100** / PassRate **1.0** (N/N scores 1); no snapshots → survival **null** and
    composite still computed (renormalized), version `sv2-partial`.

### S0.2 — Research tools committed

- `tools/research/split_half.py` + `tools/research/quant_research.py` ported from the 2026-07-16
  research-session scratchpad, parameterized (`--experiment <prefix> --split <date> --db --base`;
  split defaults to census midpoint = 2025-12-03 for 075D5240). + `tools/research/README.md`.
- **`research persistence` CLI verb** (ResearchCli) + `GET api/experiments/persistence` endpoint +
  `SplitHalfPersistenceService` (Web): the F64 split-half table for ANY experiment, one command,
  no python needed. Line-faithful port of split_half.py's F64 math (same selection, same
  window-walk semantics). Integration-tested on a hand-checkable synthetic experiment
  (`SplitHalfPersistenceTests`), including GUID-prefix resolution and null-Composite exclusion.

### S0.3 — LEDGER.md created, TRACKER.md expanded (this file / alpha-loop format)

### What broke / observations

- `dotnet build` failed at start: Angular src (touched 2026-07-16 00:01) was newer than wwwroot →
  known .NET 10 static-assets gotcha; fixed by `npm --prefix web-ui run build` first.
- First live CLI run printed `VERDICT: FAIL error=unknown`: the success payload serialized
  `"error": null` (PersistenceReport.Error) and the CLI treated property-presence as failure.
  Fixed both sides: `[JsonIgnore(WhenWritingNull)]` on Error + ValueKind check in the verb.
- **Observation (not fixed, pre-existing):** `CliArgs.Verb` joins the first TWO positionals, so
  bare `research score <runId>` produces verb `"score <runId>"` and falls through to usage —
  the `score` verb appears reachable only via the API/controller path. `persistence` takes
  options only, so it is not affected. Candidate for a later bugfix queue.
- Ported `split_half.py` prints cost drag as `-20.9%` where RESEARCH.md §3 pasted `20.9%` —
  same formula, commission/swap are stored negative; the sign was hand-cleaned in the research
  doc. Numbers identical in magnitude ($166,581 / $17,330 / $17,460 / $131,791 — exact match).

### Gate G0 — PASSED (all three legs, at final code state)

1. **sv2 tests green** — included in the suite runs below.
2. **F64 reproduction from the live DB** — app launched on :5134, then
   `research persistence --experiment 075D5240 --split 2025-12-03` printed (verbatim):

   ```
   experiment baseline-sv1-prime (075d5240)
   census 2025-07-04 -> 2026-05-05, split 2025-12-03, H2 span 153d

   === SPLIT-HALF SELECTION TEST (F64) ===
   cells positive in H1: 38/74  (H1 PnL of selection: $116,518)
   same cells in H2:     $-880   -> haircut factor -0.01
   persistence: 9/38 H1-positive cells stayed positive in H2 (24%)
   H2 return of H1-selected portfolio at 1x: -0.17%/30d
   top-8 by H1 PnL -> H2: $-540 = -0.11%/30d (H1 was $58,857)
   reverse check: H2-positive cells (13) earned $44,190 in H2, $23,942 in H1 -> factor 0.54
   H1-selected portfolio, H2 rolling 30d challenge windows (fresh $100,000 each):
    k=1x:  4 pass /  5 fail / 82 incomplete   worstDay=-3.00%
    k=2x: 14 pass / 48 fail / 29 incomplete   worstDay=-6.01%
    k=3x: 26 pass / 65 fail /  0 incomplete   worstDay=-9.01%
   VERDICT: PASS scored=74 h1Positive=38 persisted=9 h1Pnl=116518 h2Pnl=-880
   ```

   Every figure matches RESEARCH.md §1 exactly ($0 delta, tighter than the ±$1 gate). The
   committed python port reproduces the same block + F66 cost drag.
3. **Fast suites green (re-run AFTER the last code change):** build 0err/5warn ·
   Unit **767**/0/6 (+1 vs 766 baseline) · Integration **153**/0/0 (+5 vs 148) ·
   Sim-fast **144**/0/0 (= baseline). No app process left running (host killed before suites).

### Carried forward

- S1 (exit-layer factorial) is next; OWNER GATE sits after S1, but S1 itself is open to run.
  Pre-registration discipline (D5): variants in this ledger BEFORE any scored run.
- EMBARGO-2 untouched: no run rows created this session (verification: the S0 work created zero
  `BacktestRuns` rows; the app was only read from).
