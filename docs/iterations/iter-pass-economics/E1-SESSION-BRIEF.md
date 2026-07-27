# E1 — Objective Truth: session brief (Lane R, fresh session)

**You are a fresh Claude session executing phase E1 of iter-pass-economics.** This brief is
self-contained. Read order: this file → `PLAN.md` (E1 + D1/D2/D7) →
`docs/AUDIT-POST-VIABILITY-2026-07.md` §1–§4 → `docs/reference/RESEARCH-PROCESS.md` §3/§5 →
`LEDGER.md` (this directory) tail. Then come back here.

**Program state at handoff (2026-07-27):** GE0 ratified. E0 session 1 done (main @ ada5ab6):
F87 cost-truth plan written (`F87-COST-TRUTH-PLAN.md`) but **NOT yet executed** — a separate
implementation session runs it; GE1 is open. E1 does not depend on F87 (E1 is offline, zero
scored runs), so you can run before, after, or parallel to it. Gates baseline: build 0 err ·
Unit 805/0/6 · Int 156/0/0 · Sim-fast 144/0/0.

**Hard constraints (program law):**
- ZERO scored runs. E1 is offline analysis + additive tooling. No engine cost-path changes
  (those are F87's files — if a concurrent session is executing F87, your C# surface is
  disjoint: `TradingEngine.Risk/Compliance/*`, Web challenge/pass services, new files. Do NOT
  touch `StartRunRequest`/`TapeReplayAdapter`/`ReplayVenueRunner`/`TradeCostCalculator`).
- Holdouts sacred: no reads of 2024 era-holdout or post-2026-07-05 EMBARGO-2 data. 2019–2023 only.
- No ML. DB access read-only for analysis (Lane R owns the DB; F84: if you must checkpoint,
  `wal_checkpoint(TRUNCATE)`, never VACUUM; F86: trust `ScoreJson`+`UpdatedAtUtc`, never
  `Experiments.Status`/`CompletedAtUtc`).
- Pre-register metric definitions in LEDGER.md BEFORE computing (V0 Session-1 style); paste
  outputs at gates, never assert. `dotnet test` fast gates green before any commit that
  touches C#.

## GV0 — owner directive (2026-07-27, recorded; final signature at GE2)

Owner rules: **model BOTH products, $100k, and compare their pipeline-EV**:
1. **Swing — as the 2-step** (P1+P2). Existing configs: `config/prop-firms/ftmo-swing.json`
   (+ `ftmo-verification.json` as the P2 shape — verify it matches Swing's P2 terms).
2. **Standard — as the 1-step** (owner's understanding of the current FTMO lineup; ~3% MDL
   class — no existing ruleset models 1-step; author `config/prop-firms/ftmo-1step.json`).

Owner context: prior measurements ran on partly-broken machinery (F78/F79, flat-spread costs)
— anchor nothing on them.

**MANDATORY FIRST STEP — live product verification (V0 practice):** fetch ftmo.com's current
trading objectives + pricing (targets, max daily loss, max loss, min trading days, time limits,
fees per size, fee-refund-on-pass, payout split/scaling, news/weekend rules per product).
Cite every number with URL + fetch date in the ledger, exactly like iter-viability Session 1
did. **If the live lineup contradicts the owner's mapping (e.g. Standard is not a 1-step
product), STOP and present the actual product table to the owner before authoring rulesets** —
map the owner's *intent* (the 1-step product vs the 2-step weekend-holding product), never
silently substitute names.

## Deliverable 1 — Assessment inventory (offline, docs only)

The owner's question: "does the way we assess FTMO passing kill good strategies?" Answer as a
table over program history: for each verdict phase (alpha-loop R-phases, structural-edge G1,
V2, V4), which instrument produced the verdict (pooled 5-yr $ CI / sv2 composite / windowed
anything) and which of PLAN §1.3's three kill mechanisms it was exposed to (stationarity
demand; composite measurement — governor+regime were ON in every census; DD approximation).

Quantification, honestly bounded:
- Governor rejects ARE journaled: `Kernel.cs:50` emits `Event='SignalRejected'`,
  `GuardResult='GOVERNOR:{state}'` (`PreTradeGate.cs:58,65`). BUT census runs may have set
  `SkipJournal=true` (`ReplayVenueRunner.cs:248`) — **first check whether V2/V4 run IDs have
  journal rows at all** (V2 exp `4F56B1AE`, V4 exp `5D06CE0B`; DB at
  `src/TradingEngine.Web/data/trading.db`). If journals are empty, say so — do not simulate
  around it.
- Regime-gated strategies are likely structurally invisible (filtered in
  `StrategyBankService.GetActive` before evaluation — verify) — if so, state the observability
  limit explicitly.
- The clean quantification arrives free at E2: its targeted re-runs execute the SAME cells in
  research mode (governor+regime OFF, D2). Define in the inventory the exact E2-vs-census
  comparison that will measure the control layer's suppression — that pre-registration is this
  deliverable's product, not a journal archaeology heroic.

## Deliverable 2 — Challenge-pipeline-EV objective (D1)

Windowed MC: block-bootstrap-resampled trade streams → anchored challenge windows →
P(pass step(s)), P(bust), E[time-to-target], funded-phase EV, minus fees, under a stated retry
policy. Per product (Swing-2step, Standard-1step), per candidate trade stream.

- **Build ON the verified semantics, do not re-implement rules.** The rule engine is
  `src/TradingEngine.Risk/Compliance/ChallengeSimulator.cs` (+`PassProbabilityEstimator.cs`),
  V0-verified: NO evaluation time limit (verified 2026-07-16); trading day = day with a trade
  OPENED; daily reset semantics per F73 fix. Web-side orchestration prior art:
  `ChallengeSimulationService.cs` (anchored windows, censoring semantics — Incomplete ≡
  censored, NOT fail), `PassProbabilityService.cs`, `BlockBootstrapController.cs` pattern for
  exposing research endpoints. Ruleset JSONs: `config/prop-firms/` (ConfigLoader validates
  riskProfile→ruleSet references — new `ftmo-1step.json` must be registered wherever the
  catalog enumerates firms).
- Resampling: block bootstrap consistent with `tools/research/block_bootstrap.py` conventions
  (that file also defines the MDE convention, 2.8016×SE_boot). Windows must preserve
  clustering (block on days, not trades).
- **Retry policy:** propose and report under 2–3 stated policies (e.g. single attempt;
  retry-on-fail with fresh fee, capped N/year; stop-on-2-consecutive-busts) — owner ratifies
  ONE at GE2. Fees and refund-on-pass from the live verification above.
- Two-sided reporting is LAW (D1): pipeline-EV is reported NEXT TO the pooled $ CI, never
  instead of it. And passing is necessary, not sufficient — a candidate must independently
  show positive net expectancy at true costs (CI excluding zero) on its deployment-conditional
  stream.
- Test streams to exercise the machinery (2019–2023 IS only): session-breakout and
  ema-alignment position streams from the V2/V4 ledgers (signal-positive families, audit §2) —
  as machinery validation, NOT as verdicts (their costs are still the flat-spread ones until
  E2 re-scores).

## Deliverable 3 — Zero-edge floor (the anti-lottery guard)

Same MC on zero-edge synthetic trade streams (E[net]=0 by construction at true costs) at 2–3
vol/frequency profiles matched to the real families (e.g. ~2.3 trades/day intraday profile;
~0.5/day swing profile). The floor = what a coin flip earns from the fee/retry structure.
**This number goes into every candidate report from now on** (PLAN §7). A candidate whose
pipeline-EV beats zero but not the floor is FTMO's fee model working as designed — excluded
(PLAN §5, variance-lottery clause).

## Done looks like

- LEDGER.md Session entry: pre-registrations (metric definitions, retry policies, synthetic
  profiles) BEFORE results; live FTMO product/fee table with citations; assessment-inventory
  table; pipeline-EV machinery demonstrated on the two test streams; zero-edge floors for both
  products; every number pasted from output.
- `ftmo-1step.json` authored (or the STOP-and-ask above executed instead).
- Code: additive only, gates green, committed on branch `iter/pass-economics-e1`, merged to
  main per repo convention.
- GE2 remains OPEN (it is joint with E2): what E1 hands the owner is the objective definition
  + floors + the account-type signature request.
- Update `MEMORY.md`-indexed project memory (post-viability-audit file) with E1 outcome.

## Open items you inherit (do not lose)

- Owner runs `tools/ops/backup-offmachine.ps1` when a destination drive is attached —
  **must happen before E2's scored re-runs** (ledger ruling, Session 1 addendum).
- GE1 (F87) pending a separate implementation session; L0 live compare-both smoke at next
  cTrader session; GV0 signature finalizes at GE2.
