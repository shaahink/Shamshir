# iter-pass-economics — Session Ledger (append-only)

**Started:** 2026-07-27 — program opened on GE0 ratification (owner "proceed", same day the
PLAN draft landed on main @ 637d9b0). Findings continue at **F87** (F1–F86 live in the
alpha-loop / structural-edge / viability ledgers and RESEARCH.md).

Every session appends below. Mid-session findings go here immediately (stall-kill safety).
Do NOT delete or edit prior entries — audit trail. Every pre-registration includes an MDE line;
every gate pastes queries/outputs, never asserts. Program-specific standing checks (PLAN §7):
zero-edge floor in every candidate report; **no scored run after GE1 with a non-null
`SpreadPips`**; era-holdout/embargo guard queries stay 0 until GE5's entry exists.

---

## Session 1 — 2026-07-27 — GE0 + E0 (ops + cost truth) (Lane R)

**Session mode:** MANUAL. **GE0: RATIFIED** — owner "proceed" with D1–D8 unedited; PLAN.md
status line updated. Gates re-run on main before close-out (`scripts/gates.ps1`): build 0 err ·
Unit 805/0/6 · Integration 156/0/0 · Sim-fast 144/0/0 (80 s) — this is Lane D's verified-green
starting baseline (no engine code touched this session).

### Protocol step 1 — QA of the audit's F87 claims against source (all CONFIRMED)

- `StartRunRequest.cs:9` — `double SpreadPips { get; init; } = 1;` non-nullable; `:8`
  `CommissionPerMillion = 30` flat.
- `ReplayVenueRunner.cs:227` — `spreadPipsOverride: (decimal?)cfg.SpreadPips` passed
  unconditionally; `:226` same for commission.
- `TapeReplayAdapter.cs:432-440` — `GetSpread()` priority: override → `_currentSpread`
  (recorded per-bar, set `:263`/`:291`) → registry `TypicalSpread`. Null path already prefers
  per-bar spread; it was simply unreachable on every scored run.
- Three spread sources confirmed live at once: fills (`GetSpread()`), floating PnL
  (`TapeReplayAdapter.cs:772` reads `TypicalSpread` directly), strategy tick
  (`BarEvaluator.cs:285` et al., `TypicalSpread / 2`).
- **Better than the audit assumed:** `TradeCostCalculator.cs:126-168` already implements the
  venue-true commission dispatch (`CommissionType` switch, cTrader-verified F39/F46) — it is
  preempted by the non-null override exactly as the spread path is. And
  `EngineServiceCollectionExtensions.cs:122` already overlays venue-captured specs onto the
  registry (F44). **F87 is therefore two nullable plumbing changes plus unification — no new
  cost math anywhere.**
- Sequential-pass claim confirmed: `ReplayVenueRunner.cs:175` pass loop, `:250`
  `InitialBalance = cfg.Balance` per pass.
- Pinned test to flip located: `tests/.../Phase31Tests/TapeReplaySpreadOverrideTests.cs`
  (`WithOverride_UsesRunSpreadPips_NotRegistryValue` stays — R1 parity; null-path gains the
  per-bar assertion).

### E0 actions this session

- **(b) Doc corrections owed by the audit — DONE:**
  - `docs/reference/SYSTEM-REFERENCE.md` §Multi-symbol: "N rows on one account" replaced with
    the verified sequential-pass truth (fresh balance per (symbol,tf) pass; shared-account
    portfolio run unbuilt).
  - `docs/iterations/iter-viability/LEDGER.md` Session-3 spread-policy block: bracketed
    **[CORRECTION 2026-07-27 — F87]** annotation appended (claim was false for every scored
    run; direction-of-verdict analysis included; original text left intact — audit trail).
- **(c) F87 Lane-D agent plan — WRITTEN:** `F87-COST-TRUTH-PLAN.md` (this directory). P0 recon
  → P1 nullable SpreadPips (null ⇒ per-bar) → P2 nullable CommissionPerMillion (null ⇒
  venue-true dispatch) → P3 per-trade `SpreadCostAmount` (M54) → P4 single spread-number
  resolver → P5 `cost_decomposition.py` standing harvest columns → P6 GE1 probe cell.
  Hard rules pin: explicit values behave byte-identically (F32/F39 parity), no offset-convention
  changes, cTrader venue requires explicit spread (R4).
- **(a) Off-machine backup — SCRIPT DELIVERED, EXECUTION BLOCKED ON OWNER:**
  `tools/ops/backup-offmachine.ps1` (sqlite online `.backup` + quick_check + row-count verify
  vs source [BacktestRuns/Experiments/TradeResults names verified against live DB] + robocopy
  archive + manifest). Machine facts: trading.db = **11.5 GB** at
  `src/TradingEngine.Web/data/trading.db`; archive = **1.45 GB** at `C:\ShamshirData\backfill`;
  **only one physical drive, 23 GB free** — no off-machine target exists on this machine.
  **OWNER: attach an external drive (or mount a cloud-synced folder) and run**
  `powershell -File tools\ops\backup-offmachine.ps1 -Destination <drive>` (~14 GB needed).

### Gate GE1 — OPEN (pending Lane D execution of F87)

GE1 closes when: flipped pinned tests green; one probe cell demonstrably charges recorded
per-bar spread + venue-true commission (queries pasted, not asserted); `Net = Gross + Comm +
Swap` exact; fast suites green. Lane R re-verifies the agent's P6 evidence before ratifying.

### Session 1 addendum — 2026-07-27 (same day, owner rulings)

- **Executor clarification (owner):** there is NO OpenCode/DeepSeek agent — all lanes are
  driven by Claude sessions (or another model), one session at a time, owner-dispatched.
  "Lane D" in this program = a separate implementation session executing a phased plan doc.
  F87 executor: a fresh Claude session running `F87-COST-TRUTH-PLAN.md` as written.
- **Backup ruling (owner defers, blocker question answered):** the off-machine backup is NOT a
  blocker for E1/GV0 — that work is offline and creates no new irreplaceable state. It IS
  mandatory **before E2's scored re-runs** write new results into trading.db. E0(a) stays a
  standing debt until then; script is ready (`tools/ops/backup-offmachine.ps1`).
- **GV0 owner directive (final signature still at GE2):** model **BOTH** products at $100k and
  compare pipeline-EV: **Swing as the 2-step** and **Standard as the 1-step** (owner's
  understanding of the current FTMO lineup; owner also notes prior measurements ran on
  partly-broken machinery — anchor nothing on them). E1 must live-verify the current product
  terms first (V0 practice, citations); if the lineup contradicts the Standard=1-step mapping,
  STOP and present the actual product table to the owner before authoring rulesets.
- **E1 handoff prepared:** `E1-SESSION-BRIEF.md` written this session — self-contained brief
  for a fresh Lane R session (deliverables 1–3, GV0 directive, tooling/data pointers
  [`ChallengeSimulator`/`PassProbabilityEstimator` at `src/TradingEngine.Risk/Compliance/`,
  ruleset JSONs at `config/prop-firms/`, V2 exp `4F56B1AE` / V4 exp `5D06CE0B`], observability
  caveats [census `SkipJournal` may have suppressed journals — check first; regime-gated
  strategies likely invisible pre-journal], hard constraints).

### RESUME (next session — owner dispatches ONE of these)

- **E1 (Lane R, ready NOW):** fresh session reads `E1-SESSION-BRIEF.md` and executes.
  Delivers the objective definition, `ftmo-1step` ruleset (or the STOP-and-ask), zero-edge
  floors, assessment inventory. GV0 signature lands at GE2.
- **F87 (Lane D, ready NOW):** fresh session executes `F87-COST-TRUTH-PLAN.md` P0→P6 on
  branch `iter/pass-economics-f87`, handover to `F87-HANDOVER.md`. Closes GE1 after Lane R
  re-verifies the P6 probe.
- The two are file-disjoint and may run in either order (or interleaved sessions).
- **Owner:** backup when a destination drive is available (must precede E2 re-runs).
- Standing debt unchanged: L0 live compare-both smoke at next cTrader session.

---

## Session 2 — 2026-07-27 — E1 (objective truth) (Lane R, branch `iter/pass-economics-e1`)

**Session mode:** fresh Claude session executing `E1-SESSION-BRIEF.md`. Offline only — ZERO
scored runs; DB access read-only (`mode=ro` URI throughout); C# surface additive and disjoint
from F87's files.

### Protocol step 1 — QA of Session 1's claims (all CONFIRMED)

- `F87-COST-TRUTH-PLAN.md` present in this directory ✓; `tools/ops/backup-offmachine.ps1`
  present ✓; `docs/reference/SYSTEM-REFERENCE.md` §Multi-symbol now states sequential passes
  (line 66) ✓; iter-viability LEDGER carries exactly one `[CORRECTION 2026-07-27 — F87]`
  block ✓. Gates re-verified at session close (pasted below).

### MANDATORY FIRST STEP — live FTMO product verification (fetched 2026-07-27)

**Verdict on the GV0 mapping: COMPATIBLE — no STOP.** The current lineup is exactly two
evaluation products: **FTMO Challenge: 2-Step** (account types Standard *or* Swing) and
**FTMO Challenge: 1-Step** (Standard only — "the Swing account type is not offered for the
FTMO Challenge: 1-Step"). The owner's "Swing = 2-step" and "Standard = 1-step (~3% MDL)" both
map cleanly onto real products; the only naming nuance is that "Standard" is an account *type*
(which also exists for 2-step), not the 1-step product's name. E1 models: **product A =
2-Step Swing $100k; product B = 1-Step (Standard) $100k.**

| Term | 2-Step (P1 / P2) | 1-Step |
|---|---|---|
| Profit target | 10% / 5% | 10% |
| Max daily loss | 5% of initial | **3% of initial** |
| Max loss | 10% of initial, **static** | 10% of initial, **EOD-TRAILING** (limit recomputed once daily at 23:59:59 CE(S)T off the highest EOD balance, floor never below initial−10%) |
| Consistency | — | **Best Day rule:** best single day must not exceed 50% of Positive Days' Profit; exceeding is NOT a breach — it defers pass eligibility until diluted |
| Min trading days | 4 per phase (non-consecutive) | **none** (FTMO: 2-day pass possible in the exact-50/50 case; ~3 days practical) |
| Time limit | unlimited | unlimited |
| Fee $100k | $540 | $499 |
| Fee refund | 100% with first reward withdrawal (ftmo.com primary) | stated only by third parties — carried as ±$499 sensitivity, owner sees both |
| Funded split | 80% base → 90% via Scaling Plan/Premium | 90% flat |
| News/weekend | evaluation: unrestricted both types; **funded**: Standard = news-restricted + no weekend holding; Swing = unrestricted | funded account is Standard ⇒ news + weekend restrictions apply |

Citations (all fetched 2026-07-27): ftmo.com/en/trading-objectives/ (lineup, targets, MDL/ML,
min-days P1, Best Day 50%); ftmo.com/en/1-step-challenge/ (unlimited period, 90% split, no
Swing, no Verification phase, evaluation restriction-free); ftmo.com/en/how-it-works/ (sizes
10k–200k, refund with first withdrawal, up-to-90% split); ftmo.com/en/blog/introducing-the-1-step-ftmo-challenge/
(3% MDL of initial recalc per trading day; ML "updated only once a day after the market close
(at 23:59:59 CE(S)T)", EOD-trailing; Best Day formula + not-a-breach); FAQ search hit (trailing
limit = highest EOD balance − 10% of initial); ftmo.com/au/faq/what-is-the-minimum-time-required-to-pass-ftmo-challenge-1-step/
(no fixed minimum; 2-day exceptional pass; 3-day practical); ftmo.com/en/faq/is-the-swing-account-type-available-for-ftmo-challenge-1-step/
(no Swing on 1-step); ftmo.com/en/reward-growth-and-scaling-plan/ + FAQ (80→90 scaling 2-step;
90 flat 1-step); fees per size corroborated propfirmkey.com/en/blog/ftmo-challenge-cost-2026
(1-step 79/199/319/499/999; 2-step 89/250/345/540/1080 USD — ftmo.com pricing itself is
JS-rendered and was not statically fetchable; **exact native fee re-verify at GE2 signature**).

**Product-fit facts surfaced for GE2 (not STOPs):** (1) the 1-step's EOD-trailing ML and Best
Day rule are NEW semantics our verified simulator did not model — implemented this session
(additive, off by default, unit-pinned). (2) The funded account behind the 1-step is Standard
⇒ no weekend holding — structurally hostile to multi-day swing streams in the funded phase;
the daily-close MC cannot see this rule, so 1-step funded EV is OPTIMISTIC for swing-style
candidates (flagged in every report). (3) 1-step fee-refund-on-pass is not confirmed by a
primary source — modeled both ways.

### Pre-registrations (BEFORE any computation — metric definitions frozen here)

**PR-E1-1 — Challenge-pipeline-EV metric.** Per (product, candidate stream, retry policy):
- Stream = position-level (OpenedAtUtc, ClosedAtUtc, Net $) list, 2019–2023 IS only; guard
  query pasted with every extraction (min/max ClosedAtUtc inside [2019-01-01, 2024-01-01)).
- Daily bucketing: CE(S)T (Europe/Prague, DST-aware) calendar days; day PnL = Σ Net of trades
  CLOSED that day (closed-basis; floating invisible ⇒ breach detection OPTIMISTIC — same
  daily-close caveat the verified simulator already carries; V6 intraday envelope is E6 work);
  TradesOpened = count of trades OPENED that day (min-trading-days counter, V0 semantics).
  Series = trading-day tuples (dayNet$, tradesOpened) over days with ≥1 open or close.
- Resampled world-path per MC replicate: Politis–Romano stationary bootstrap on the day-tuple
  series (mean block 10 trading days, wrap-around — block_bootstrap.py conventions), path
  length 2,500 trading days; seed = 20260727 + replicate index; 2,000 replicates.
- Pipeline walk per replicate: challenge attempts consume the path sequentially. Each attempt
  = fresh $100k account, `ChallengeSimulator.SimulateWindow` semantics (V0-verified; extended
  this session with EOD-trailing ML + Best Day, used ONLY by `ftmo-1step`): 2-Step = P1
  (ftmo-swing) then P2 (ftmo-verification) starting the next trading day; 1-Step = single
  phase (ftmo-1step). Fail ⇒ retry policy decides (fresh fee); pass(es) ⇒ funded phase.
- Funded phase: same loss rules as the product's evaluation (2-step funded: 5% MDL + 10%
  static ML; 1-step funded: 3% MDL + 10% EOD-trailing ML), no profit target, Best Day off;
  payout every 21 trading days (~30 calendar): trader receives split × max(0, balance −
  100k), balance and (for trailing) HWM reset to $100k after payout (FTMO reset-after-payout
  semantics, modeling assumption); fee refund added at FIRST payout (2-step: confirmed; 1-step:
  both-ways sensitivity); funded horizon 520 trading days from funding, or bust.
- Split: 2-step 80% base (90% scaling-plan sensitivity); 1-step 90%.
- Outputs per (product, stream, policy): P(pass evaluation), P(ever funded), E[attempts],
  E[trading days to funded] (+ calendar conversion via the source stream's trading-days-per-
  calendar-day ratio), P(funded bust ≤ horizon), E[# payouts], **pipeline-EV** = E[Σ payouts +
  refund − Σ fees] with replicate SD, median, P5/P95. MC sampling error SE_MC = SD/√2000
  pasted with every EV.
- **Two-sided reporting is LAW (D1):** every report prints, next to pipeline-EV, the pooled
  daily-$ stationary-bootstrap 95% CI (2,000 reps, mean block 10) and MDE = 2.8016×SE_boot for
  the SAME stream. Passing-shaped numbers never appear without the expectancy line.
- Sizing caveat (stated, accepted): stream $ are as-executed (0.5% fixed-fractional of a fresh
  $100k per census pass); the MC does not re-scale trade $ with simulated balance.

**PR-E1-2 — Retry policies (owner ratifies ONE at GE2):**
- RP-A single attempt: one fee, stop on first evaluation fail.
- RP-B persistent: retry on evaluation fail with a fresh fee, max 6 attempts total; stop when
  funded account busts (no re-entry after funding).
- RP-C two-consecutive-bust stop: as RP-B but also stop after 2 consecutive evaluation fails.

**PR-E1-3 — Zero-edge floor (anti-lottery guard, D1).** Floor stream = a real profile stream
with its per-trade mean removed (every trade's Net $ shifted by −mean(Net); day structure,
frequency, dispersion, and clustering preserved; E[net] = 0 by construction). Two profiles:
- ZP-I (intraday): pooled V4 session-family stream (measured trades/day pasted at extraction).
- ZP-S (swing): pooled V2 ema-alignment stream (~0.5 trades/day class).
Floors = full PR-E1-1 pipeline-EV of each zero-edge stream × both products × all three retry
policies, same seeds/reps. **A candidate must beat the matching-profile floor with CI
clearance** (Δ = EV_cand − EV_floor; SE_Δ = √(SE_c² + SE_f²); require Δ − 1.96·SE_Δ > 0).
This floor line appears in every candidate report from now on (PLAN §7).

**Amendment A1 to PR-E1-3 (recorded BEFORE any floor computation; reason, not result-driven):**
at extraction time the pooled family superpositions measure ~92 trades/day (V4 pooled, 80
cells) and ~3.3/day (V2 ema-alignment pooled) — these are 80-cell/28-cell notional books, not
frequencies any single deployed account trades, and PLAN D1 requires floors at "matched
vol/frequency" to the real families (the brief's ~2.3/day intraday and ~0.5/day swing are the
per-account census measurements). Floor profiles therefore SUBSAMPLE the pooled stream to the
deployment-realistic frequency before de-meaning: Bernoulli per-trade thinning with
p = target/actual, seed 20260727, targets ZP-I = 2.3 trades/day (from V4 pooled), ZP-S = 0.5
trades/day (from V2 ema-alignment pooled); then per-trade de-mean to E[net]=0; then daily
bucketing. The machinery-demo streams (PR-E1-4) stay pooled-superposition as pre-registered.

**PR-E1-4 — Machinery-validation streams (NOT verdicts).** session-breakout and ema-alignment
family streams from the V2 census (`4F56B1AE`), 2019–2023, all cells of the family pooled by
calendar-day superposition onto one notional $100k book (the E6.2 approximation, stated).
Costs inside these streams are the census's FLAT-SPREAD costs (F87 not yet executed) ⇒ every
number from them is machinery demonstration only; E2 re-scores before any verdict-grade use.

**PR-E1-5 — the E2-vs-census control-layer comparison (Deliverable 1's product).** When E2
runs its targeted re-runs (research mode: governor OFF + regime gate OFF + maxDdEnabled off,
D2; true costs, D3) on cells that exist in the V2/V4 censuses, the control-layer suppression
is measured per cell as: Δentries = n_research − n_census (count of TradeResults rows) and
Δnet$/pos, reported per family with a stationary-bootstrap CI on the daily-$ delta. Stated
attribution caveat: the E2 arm differs from the census in BOTH control layer and cost model;
entry-count differences are control-layer-dominated (entries are bar-close signal events;
costs move fills/exits, not signal emission — LimitOffset fill sensitivity noted), while
$/pos differences are jointly caused and are NOT decomposed by this comparison. If GE2 wants
the decomposition, E2 adds ONE paired flat-spread+research-mode arm on ONE family (owner
call; costs one census-cell re-run per cell compared).

### Deliverable 1 — Assessment inventory (evidence pasted below, computed read-only)

**The owner's question:** "does the way we assess FTMO passing kill good strategies?" Answered
as: which instrument produced each historical verdict, and which of PLAN §1.3's three kill
mechanisms it was exposed to — (K1) stationarity demand, (K2) composite measurement
(strategy+control layer in one number), (K3) DD approximation / composite triage shaping.

| Verdict phase | Instrument that produced the verdict | K1 stationarity | K2 composite | K3 DD/triage |
|---|---|---|---|---|
| alpha-loop R1'/X census (`075d5240`, 252 cells) | sv2 composite (incl. 30-day PassRate velocity + challenge survival) for triage; pooled dollars for claims | EXPOSED (pooled window) | EXPOSED (governor+regime ON) | EXPOSED (daily-close sim; F63-era composite shaped attention) |
| alpha-loop R4 (survivor velocity) | rolling 30-day `ChallengeSimulator` windows on real equity paths — "0/12 windows hit +10%/30d" | partially (windowed!) — but | EXPOSED | EXPOSED + **obsolete rule model**: 30-day TIMED windows, while FTMO had already removed time limits (V0 verified later) — R4's "too slow" verdict was measured against a deadline the real product no longer has |
| structural-edge G1 (exit components) | pooled $ paired/whole-system factorials (F70-corrected) | EXPOSED | EXPOSED | daily-close DD in scoring context |
| V2 H-BANK (`4F56B1AE`, 252 runs) | pooled 5-yr position-$ CI + MDE, family level | **EXPOSED — the verdict IS a stationarity test** | **EXPOSED — universal (evidence below)** | sv2 survival triage-only; verdict itself $ CI |
| V4 H-SESSION (`5D06CE0B`, 80 runs) | same | EXPOSED | EXPOSED (governor; regime permissive — below) | same |

**K2 quantification — census-wide control-layer flags (read-only query, pasted):**
```
V2 (gov, regime, spread, comm, n): [(1, 1, 1.0, 30.0, 252)]
V4 (gov, regime, spread, comm, n): [(1, 1, 1.0, 30.0, 80)]
```
Every one of the 332 verdict runs executed with GovernorEnabled=1 AND RegimeEnabled=1 (and,
F87 corroboration in passing: SpreadPips=1.0, CommissionPerMillion=30.0 on all 332).

**Journal archaeology — what the record can and cannot quantify:**
- V4 `5D06CE0B`: **0 of 80 runs have any Journal rows** (census driver ran SkipJournal=true;
  T5 suppresses the per-bar StepRecord journal, which since iter-36 K5 is the ONLY carrier of
  gate rejects — `RecordDecisionEvent` is a no-op in production, `EffectExecutor.cs:104-109`).
  Governor suppression in V4 is **unrecoverable from the record** — stated, not simulated
  around.
- V2 `4F56B1AE`: 2 of 252 runs have journals (151,838 rows total). In those two cells:
```
e156fad5 (AUDUSD h1): governor_rejects=49  accepted=1274 other_rejects=0 regime_mentions=0 -> gov share 3.704%
1f3f3aaa (EURUSD h1): governor_rejects=80  accepted=1308 other_rejects=2 regime_mentions=0 -> gov share 5.764%
```
  All governor rejects are CoolingOff-family (`GOVERNOR:CoolingOff: N consecutive losses >=
  pause 5` / `N bars remaining`). **Honest bound: n=2 self-selected cells — a peek, not a
  census estimate.** The 3.7–5.8% of proposals blocked is the only direct measurement the
  program has ever produced of the cooling-off tax.
- **Regime gating is structurally invisible, verified in code:** `StrategyBankService.cs:28`
  filters regime-disallowed strategies out of `GetActive` BEFORE evaluation — a gated
  strategy emits no proposal, hence no journal row ever (0 regime_mentions above is exactly
  this). Suppressed-entry archaeology for regime is impossible for ALL runs regardless of
  SkipJournal.
- **How much of each census was regime-gated (EffectiveConfigJson, F69 discipline — run
  truth, not config docs):**
```
V2: regime-GATED runs=168, permissive=84, unparsed=0   (67% of the H-BANK census)
V4: regime-GATED runs=0,   permissive=80, unparsed=0
```
  I.e. **two-thirds of the V2 verdict census measured strategy×regime-gate composites** with
  the regime gate's suppression unmeasurable; V4's strategies were all regime-permissive, so
  its composite exposure is governor-only.
- The clean quantification arrives free at E2 via PR-E1-5 (pre-registered above): same cells,
  research mode, Δentries + Δnet$/pos with CI. That comparison — not journal archaeology — is
  the instrument that measures the control layer's suppression.

**Inventory verdict (for GE2):** the program's historical instruments were exposed to all
three kill mechanisms in every verdict phase; the one phase that used windowed challenge
economics (R4) did so with an obsolete timed-rule model. Nothing in this inventory resurrects
any parked family (their costs were also mismodeled in the OPTIMISTIC direction for
metals/crypto and the toll dominates regardless) — it establishes that the assessment
instrument could not have SEEN a time-varying, control-layer-suppressed, or
challenge-economics-viable edge even if one existed. E1's new objective (PR-E1-1/2/3) plus
D2's separation is the fix; E2/E3 produce the numbers.

### Deliverable 2 — Challenge-pipeline-EV machinery (built, validated, demonstrated)

**Code (additive only; disjoint from F87's files):**
- `TradingEngine.Domain/RiskAndEquity/PropFirmRuleSet.cs`: optional `BestDayMaxShare` (null =
  rule absent — all existing rule sets unchanged).
- `TradingEngine.Risk/Compliance/ChallengeSimulator.cs`: `DrawdownType = "TrailingEod"` (EOD
  high-water-mark Max Loss) + Best Day pass-eligibility. Static path byte-identical when the
  new fields are absent (all 11 pre-existing simulator tests pass untouched).
- `TradingEngine.Risk/Compliance/ChallengePipelineSimulator.cs` (new): stationary-bootstrap
  day-tuple resampler + pipeline walker (fee → evaluation phase(s) via `ChallengeSimulator` →
  funded payout cycles → retry policy), deterministic seeds, PR-E1-1 outputs, and the D1
  two-sided companion (daily-$ CI + MDE) computed inside the same call.
- `TradingEngine.ResearchCli`: new OFFLINE verb `research challenge-pipeline` (CSV + config
  JSONs only — no HTTP, no DB); products `swing-2step` ($540, split 0.80, refund) and
  `standard-1step` ($499, split 0.90, refund; `--no-refund` sensitivity).
- `config/prop-firms/ftmo-1step.json` AUTHORED (GV0): TrailingEod, 3% MDL, 10% ML, target 10%,
  minTradingDays 0, bestDayMaxShare 0.5, evaluation restriction-free. Auto-loaded by
  `ConfigLoader` (directory scan). NOTE: `PropFirmRuleSetSeeder` seeds only an EMPTY DB store,
  so the live web DB does not pick it up until a reseed/upsert — irrelevant to E1 (offline
  verb reads the JSON directly); E6 wires it into the store when the control-layer work needs it.
- `tools/research/e1_extract_streams.py` (new): read-only extractor → committed daily CSVs at
  `docs/iterations/iter-pass-economics/data/` (+ full result JSONs under `data/e1-results/`).

**Gates (pasted):** `scripts/gates.ps1`: build 0 err · Unit **816/0/6** (was 805 — 11 new
tests) · Integration 156/0/0 · Sim-fast 144/0/0 (45 s).

**Validation beyond unit pins (all pasted from output):**
1. Deterministic probe (10×+1000 then −4000 periodic): funded phase must always bust →
   `PFundedBust: 1` exactly.
2. **Independent Python re-implementation** of the full 1-step pipeline spec (different RNG,
   same semantics), 2,000 reps on the session-breakout stream, vs the C# verb:
   `PY: PFunded=0.177 PbustF=1.000 meanPay(uncond)=0.053 EV=-141.3` vs
   C# `PFunded=0.1655 PbustF=1.0 meanPay=0.061 EV=-117.8±47` — agreement within MC error on
   every statistic.
3. Parameter bisection (`--payout-cycle 999999` kills payouts ⇒ PbustF→1 identically;
   exact-arithmetic constant-stream unit tests pin fees/refund/payout to the dollar).
4. Session note for the audit trail: a false alarm ("PFundedBust=0.06") was chased and turned
   out to be a mis-read column in a throwaway tabulation script — the committed JSONs were
   correct all along; the chase produced validations 1–3, which stand.

**Stream extraction (guards pasted by the extractor):** all four streams 2019–2023 only
(min/max Closed pasted in output above the CSVs; extractor hard-fails outside [2019, 2024)).
Trading-day→calendar-day conversion ratios (for E[time] reading): v2-session-breakout 0.81,
v2-ema-alignment 0.76, ZP-I 0.64, ZP-S 0.33 trading days per calendar day.

**Machinery demonstration on the two PR-E1-4 streams (2,000 reps, seed 20260727) — flat-spread
census costs, MACHINERY DEMO ONLY, no verdicts (E2 re-scores first):**

| stream (pooled book) | product | policy | pipeline-EV ± SE_MC | P(funded) | E[td→funded] | P(funded bust) | daily $ CI / MDE |
|---|---|---|---|---|---|---|---|
| session-breakout (10.9 tr/d) | swing-2step | RP-A | **+272 ± 79** | 0.164 | 51 | 1.00 | mean −70.0, CI [−163.8, +24.0], MDE 133.7 |
| session-breakout | swing-2step | RP-B | **+847 ± 153** | 0.652 | 118 | 1.00 | same |
| session-breakout | swing-2step | RP-C | +449 ± 103 | 0.301 | 66 | 1.00 | same |
| session-breakout | standard-1step | RP-A | −118 ± 47 | 0.166 | 20 | 1.00 | same |
| session-breakout | standard-1step | RP-B | −749 ± 91 | 0.670 | 48 | 1.00 | same |
| session-breakout | standard-1step | RP-C | −312 ± 62 | 0.300 | 27 | 1.00 | same |
| ema-alignment (3.0 tr/d) | swing-2step | RP-A | −170 ± 48 | 0.108 | 146 | 1.00 | mean −55.2, CI [−108.1, −0.1], MDE 76.6 |
| ema-alignment | swing-2step | RP-B | −488 ± 112 | 0.480 | 370 | 1.00 | same |
| ema-alignment | swing-2step | RP-C | −212 ± 72 | 0.207 | 193 | 1.00 | same |
| ema-alignment | standard-1step | RP-A | +86 ± 57 | 0.172 | 66 | 1.00 | same |
| ema-alignment | standard-1step | RP-B | +699 ± 118 | 0.704 | 183 | 1.00 | same |
| ema-alignment | standard-1step | RP-C | +315 ± 81 | 0.330 | 93 | 1.00 | same |

The demo shows exactly the two things it was built to show: (1) **the variance lottery is
real** — session-breakout at flat costs gets funded 65% of the time under RP-B and shows
POSITIVE pipeline-EV on the 2-step, while its pooled expectancy is negative-to-zero and every
single funded account busts (P(funded bust) = 1.00 across all 12 rows); a passing-shaped
number without the expectancy line next to it would have been a lie. (2) The objective
discriminates products and policies (2-step favors the high-vol book; 1-step's 3% MDL +
trailing ML punish it).

### Deliverable 3 — Zero-edge floors (the anti-lottery guard, PR-E1-3 + A1)

Floors (2,000 reps, seed 20260727, path 2,500 td; per-trade de-meaned streams, E[net] = 0 by
construction — extractor pastes mean 0.0000 for both):

| profile | product | RP-A | RP-B | RP-C | P(funded) RP-B | P(funded bust) |
|---|---|---|---|---|---|---|
| **ZP-I** (2.6 tr/d intraday, from V4 pooled ss.) | swing-2step | +1,611 ± 97 | +3,018 ± 123 † | +2,409 ± 112 | 0.75 | 0.53–0.56 |
| ZP-I | standard-1step | +2,141 ± 110 | +4,974 ± 139 | +3,360 ± 131 | 0.92 | 0.60 |
| **ZP-S** (1.05 tr/d swing, from V2 ema ss.) | swing-2step | +1,714 ± 107 | +4,107 ± 144 † | +2,860 ± 131 | 0.83 | 0.66–0.70 |
| ZP-S | standard-1step | +2,412 ± 125 | +5,810 ± 157 | +4,027 ± 147 | 0.94 | 0.74–0.75 |

† 2-step floor cells carry path-exhaustion censoring at 2,500 td (466/2000 and 230/2000
replicates in the RP-B cells — zero-edge 2-step grinds are SLOW, E[td→funded] up to 1,110).
Sensitivity at `--path-days 5000`: ZP-I swing-2step RP-B floor rises **3,018 → 3,945** (cens.
bias was DOWNWARD, i.e. lenient). **GE2 recommendation: use the 5,000-td figures for 2-step
floor comparisons; always compare candidate and floor at the SAME path length.**
Other pre-registered sensitivities: 1-step `--no-refund` lowers floors ~$150–400 (RP-B
4,974 → 4,576; 5,810 → 5,408) — the unverified 1-step refund is second-order; 2-step at 90%
scaling-plan split raises floors ~10% (3,018 → 3,524; 4,107 → 4,755).

**What the floors mean (the headline for GE2):** under this model a ZERO-EDGE trader has
pipeline-EV of **+$1.6k to +$5.8k per pipeline** and gets funded up to 94% of the time under
persistent retries. "Pipeline-EV > 0" is therefore MEANINGLESS as a candidate gate — the D1
floor-clearance test (Δ = EV_cand − EV_floor, require Δ − 1.96·SE_Δ > 0, matched profile,
same path length) is the gate, exactly as PLAN §5's variance-lottery clause anticipated. The
absolute floor values inherit every optimism in the model (daily-close granularity, no
news/weekend restrictions, frictionless 21-td payout cycles, as-executed sizing) — that
optimism cancels in the Δ because candidate and floor are simulated under the SAME model;
the floors are NOT a claim that farming FTMO fees is a business.
- Floor-clearance demo on the machinery streams (both FAIL, as toll-dominated streams must):
  session-breakout swing-2step RP-B: Δ = 847 − 3,018 = −2,171, SE_Δ = 196 ⇒ z = −11.1;
  ema-alignment standard-1step RP-B: Δ = 699 − 5,810 = −5,111, SE_Δ = 196 ⇒ z = −26.
- **Product-comparison evidence toward GV0 (owner signs at GE2):** the 1-step is the BETTER
  lottery instrument (higher floors everywhere: cheaper ticket, 90% split, single phase —
  despite 3% MDL + trailing ML + Best Day). Consequence: a real edge must clear a HIGHER bar
  on the 1-step; and the funded account behind the 1-step is Standard (no weekend holding),
  which this daily-close MC cannot see — 1-step funded EV is additionally OPTIMISTIC for any
  multi-day-hold candidate. Swing-2step is the structurally honest home for swing-style
  streams; the 1-step's economics advantage is real but partly an artifact of unmodeled
  funded-phase restrictions. Both products stay modeled; owner rules at GE2 with these two
  caveats on the record.

### GE2 status after this session

E1's half of GE2 is DELIVERED: objective definition (PR-E1-1/2/3 + A1), machinery
(validated, gates green), `ftmo-1step` ruleset authored on live-verified terms, floors
computed for both products × three policies with sensitivities, assessment inventory with
the E2 comparison pre-registered (PR-E1-5). GE2 remains OPEN pending E2's true-cost
re-verdict; at GE2 the owner: (1) ratifies the objective + ONE retry policy, (2) signs GV0
account type(s) knowing the 1-step caveats above, (3) rules on PR-E1-5's optional
decomposition arm, (4) re-verifies the exact native fees at purchase time (pricing page is
JS-rendered; $540/$499 are third-party-corroborated).

### RESUME (after Session 2 — E1 COMPLETE)

- **F87 (Lane D)** unchanged: ready to run `F87-COST-TRUTH-PLAN.md` on
  `iter/pass-economics-f87`; GE1 open. File-disjoint from everything this session touched
  (verified: no shared files).
- **E2 (Lane R, next research session):** offline cost-swap on V2/V4 ledgers → targeted
  true-cost re-runs (AFTER GE1 + owner backup). Its re-run cells now also produce the
  PR-E1-5 control-layer comparison and get pipeline-EV + floor lines via
  `research challenge-pipeline` (extract per-candidate streams with the e1 extractor
  pattern).
- **Owner:** off-machine backup before E2 scored re-runs (script ready); GV0 signature +
  retry-policy ratification at GE2; L0 live compare-both smoke at next cTrader session.
