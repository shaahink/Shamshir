# iter-viability — From trustworthy negatives to a funded-account path

**Status: ADOPTED (2026-07-16, at the iter-structural-edge S1 owner gate).** Drafted by the
external quant review (`docs/QUANT-REVIEW-RESPONSE-2026-07.md`) as the successor to
`iter-structural-edge`; adopted when the owner accepted **G1** (S1 CLOSED early-with-reason @
`68aa53e`: **no D5-surviving exit component** — trend-breakout and ema-alignment both refuted
on position-level dollars; R3's star cell v6a reproduced its +82% ExpectancyR while losing
$1,109 vs its own control, proving the F70 artifact on its own cell; remaining families
skipped with reason per the power analysis) and ratified the **D7 park** (mtf-trend, 4 cells,
reversible). Nothing here re-touches EMBARGO-2 or loosens any inherited gate.

**Session 1 (next):** V0 (rule-model truth, ~½ session) + the absorbed regime-conditioning
analysis (old S2(b) — zero new runs; the 2×2 family-class × census-half interaction over
existing trades, external regime variables only). **Session 2:** V1 backfill. This is the
S1-gate vote (regime analysis first, backfill in parallel) with V0 added because it is cheap
and recalibrates every metric the downstream stages report.

**Read order for a fresh session:** this file → `docs/QUANT-REVIEW-RESPONSE-2026-07.md` (the
why) → `iter-structural-edge/LEDGER.md` tail (latest results: F69–F72, corrected no-TP
refutation, S1.2 pre-reg) → `iter-structural-edge/RESEARCH.md` (F64–F68) →
`iter-alpha-loop/HANDOVER.md`.

---

## 0. Lineage (how this plan tracks history)

| Inherited | From | Status here |
|---|---|---|
| Findings ledger F1–F72 (next free: **F73**) | alpha-loop + structural-edge | Continues, same numbering |
| Session protocol (QA prior claims → pre-register → append ledger → paste gates) | alpha-loop §4, verbatim | Non-negotiable, unchanged |
| EMBARGO-2 (post-2026-07-05, one touch, ≥45 accrued days) | structural-edge D3 | Sacred; final gate for anything V1–V4 produces |
| Park-never-delete, D13 one-cell-per-run, null-with-reason | alpha-loop D13 / structural-edge | Unchanged |
| Position-level dollars as the family metric | F70 | Unchanged |
| sv2 scoring + `research persistence` tooling | structural-edge S0 | Extended in V0/V5, never in-place edited |
| Parity doctrine (tape-only research; verdict ≤14d for owner-facing) | alpha-loop D12 / structural-edge D8 | Unchanged |
| Structural-edge S2 (regime conditioning on recorded trades) + S3 (cost knobs) | structural-edge | Absorbed into V4/V3 as cheap analyses, same pre-reg discipline |
| Structural-edge stop rule ("bank exhausted → new material") | structural-edge §6 | Superseded in part: V4 starts new material in parallel — the power analysis (review §2.1) shows S1-style tests could not have fired the stop rule meaningfully |

## 1. Decisions (owner ratifies/edits before launch)

| # | Decision | Rationale |
|---|---|---|
| D1 | **Every pre-registration includes a power line**: minimum detectable effect (MDE) at the planned n, stated next to the hypothesis. A null from an underpowered test is recorded as "not detectable at n", never "no effect". | S1.1's arms had MDE ≈ 0.17R vs plausible effects of 0.03–0.10R (review §2.1). The corrected no-TP result (−$25.8k swing) shows large effects ARE detectable — the distinction must be explicit. |
| D2 | **The challenge model is verified before it drives another decision** (V0): time limits, account type (standard vs Swing), daily-loss definition (intraday floating equity, fixed-$ base, reset time), scaling terms — against FTMO's current contract, not memory. | R4's headline inverts if Phase 1 is untimed; the bank holds multi-day and Swing may remove the news/weekend tax; sv2's daily-close sim is optimistic on intraday breaches. |
| D3 | **Historical backfill (2019–2024, bid/ask M1) is research-legal under three conditions:** era-conservative costs (recorded per-bar spread where available, else ≥ today's), rule×family-level interpretation only, and **2024 is an era-holdout** (untouched until a V-final gate) with 2025+ remaining the terminal holdout. | Pre-2025 data predates all strategy development — the cheapest clean OOS in existence. Era-holdout mints years, not weeks, of honest validation. |
| D4 | **Exit questions move to paired statistics** (excursion recorder + offline replayer). No further whole-system exit factorials after S1.2 completes; whole-system runs are reserved for effects expected to be large (MDE-checked per D1). If a whole-system factorial must run, research-mode `MaxConcurrent` is set high enough never to bind. | Pairing ≈ 20× power (σ_d ≈ 0.4R vs √2·σ ≈ 1.5R); S1.1 documented the MaxConcurrent entry-admission confound (position counts 253–647 across arms). |
| D5 | **Anti-overfit, amended (D5′):** pre-registration + ≤8 variants/session (unchanged); survival = stationary-block-bootstrap 95% CI on pooled family dollars excluding zero, **and** sign agreement at family×instrument-class level (not per-cell), **and** stitched walk-forward OOS positive with ≥6-month train windows (once backfill exists), **and** drop-any-month jackknife sign stability. Per-cell sign counting is demoted to a descriptive. | The old leg (a) passed a TRUE +0.05R effect only ~30% of the time (review §3 Q1); per-cell signs at n=20–90 are coin flips. |
| D6 | **No trailing-performance selection at any level, ever** — no dynamic strategy/cell picker, no re-ranking on recent results, no adaptation window < 6 months. What MAY adapt (the auto-tune doctrine, review §5): Tier-1 risk structure (vol targeting, DD scaling, challenge-state risk policy, portfolio intraday stop) freely; Tier-2 exit-calibration tables only with walk-forward proof that refit beats frozen; Tier-3 portfolio weights EB-shrunk, turnover-capped, quarterly. | F64: 24% persistence — trailing performance anti-selects. The doctrine gives "dynamic" a safe, testable meaning. |
| D7 | **Parallel execution is embraced with two guards:** idempotency keys persisted to DB (S1.1's in-memory keys caused 23 duplicate runs across restarts), and either per-run scoping of `SymbolInfoRegistry` or process-per-run isolation before in-process parallel tape runs. | Kernel purity makes parallelism safe by design; the two known hazards are operational (S1.1 execution record, F24). |
| D8 | **New-material families enter as bank members under identical discipline** (config-seeded, pre-registered, family-level pooled evaluation) — the 9 incumbents are not rewritten on new research (development contamination of the only data is the cost); dead knobs get fixed (F71-style), residence is decided by V2's OOS verdict + parks (D7 of structural-edge carries over for `mtf-trend`). | Churning incumbent logic re-contaminates; membership decisions belong to out-of-sample evidence. |
| D9 | **Two-lane worktree concurrency** (detail in §8): Lane R = the DB-owning worktree on `iter/viability`, sole writer of scored runs + ledger result entries, one app instance per DB file, parallelism *inside* the app/driver only; Lane D = separate worktree on a branch off `iter/viability` for code stages (importer, exit lab, control layer, L1 fixes), credential-free gates there, merge at stage gates. Docs-only lanes always safe. | Kernel purity makes parallel runs safe (determinism probe PASS); SQLite single-writer + the append-only ledger make multi-writer *sessions* unsafe. Quicker development without truth-risk. |

## 2. Phase map

```
V0 rule-model truth (½–1 session)   → FTMO terms verified; sv2 metrics corrected      [gate GV0, OWNER]
V1 backfill + importer (1–2)        → 2019–2024 bid/ask tape, overlap-validated       [gate GV1]
V2 frozen-bank pure OOS (1)         → F68 family ranking tested on 6 years            [gate GV2, OWNER]
V3 exit lab (1–2)                   → excursion recorder + offline replayer; paired
                                      exit verdicts for all families (S1.2 folds in)  [gate GV3]
V4 new material (2–3, parallel-ok)  → session/time-of-day, cross-sectional FX,
                                      indices, gap family; S2/S3 analyses absorbed    [gate GV4, OWNER]
V5 gate upgrade (1)                 → bootstrap + power into D5′; EB shrinkage tool   [gate GV5]
V6 control layer (1–2)              → intraday equity envelope; portfolio −3% stop;
                                      challenge-state risk policy (MC-optimized)      [gate GV6]
V7 era-holdout + EMBARGO-2 + portfolio + audit → structural-edge S5–S7 shape          [gates GV7a/b, OWNER]
```

Sessions are ceilings. V1/V3/V4 can interleave; V2 must precede any V4 *claim* (V4 build work
may start earlier). V6 is edge-independent and can run any time after V0. The **L-track (§7)**
interleaves throughout — L0 is a standing debt executable immediately; L3 deliberately fills
the EMBARGO-2 accrual wait.

## 3. Stages

### V0 — Challenge-model truth [OWNER GATE]
Verify against FTMO's current published terms: Phase-1/2 time limits (removed in 2022?),
**Swing** account (weekend holding + news trading permitted?), max daily loss (fixed-$ of
initial balance? breached intraday on floating equity? reset at midnight CE(S)T vs the
config's 22:00 Prague), min trading days, scaling plan, payout terms. Diff every item against
`config/prop-firms/ftmo-standard.json`; correct config + `ChallengeSimulator` semantics; add
**P(bust before target)** and **E[time-to-target]** as first-class sv2 outputs (30d PassRate
retained as a velocity index). Decide the target account type.
**Gate GV0:** rule-diff table pasted; corrected metric definitions unit-pinned; owner signs
the account-type decision.

### V1 — Backfill + importer
Dukascopy (or equivalent free archive) M1 **bid/ask** 2019-01-01 → 2024-12-31 for the 14
symbols + a shortlisted 2–4 index CFDs. Importer derives the tape's bar schema incl. a per-bar
spread column (null = legacy constant). **Validation:** import the overlap year (2025) and
reconcile derived H1/H4 bars against the recorded cTrader tape (counts, OHLC deltas, spread
sanity); any systematic divergence is a finding, not a shrug.
**Gate GV1:** overlap reconciliation table pasted; per-bar spread present; 2024 partition
flagged era-holdout in the DB (a `BacktestRuns` check like the embargo query).

### V2 — Frozen-bank pure OOS census [OWNER GATE — the decisive experiment]
Pre-register, then run the R1'-shaped census (D13, sv2) for the frozen bank exactly as
configured today over 2019–2023 (2024 held out). Family-level pooled dollars with block-
bootstrap CIs, per-era breakdown (2019, 2020-vol, 2022-trend, 2023-chop). Primary questions:
does mean-reversion's +0.10R survive? does the F68 ranking hold? is the bank's +0.02R mean
real? **This gate decides the program's center of gravity:** MR holds → V6+V7 fast-path to a
challenge candidate; nothing holds → V4 is the program.
**Gate GV2:** era × family table + CIs pasted; residence/park decisions recorded; owner
directs weighting of V3/V4.

### V3 — Exit lab (paired)
F71-fix pattern extended if needed; excursion recorder (wide-SL exploration mode, one
recording run per family×cell) + offline `ExitReplayer` (SL/TP/BE/trail/Ride/partial grids
against recorded paths; conservative first-touch semantics identical to `VenueFillModel`).
Paired family-level verdicts for all four S1 families (S1.2's ema-alignment whole-system
result is cross-checked against its paired equivalent — a free calibration of the two
methods). Interim analysis available immediately: common-entry paired re-evaluation of the
existing S1.1/S1.2 run data. Cost knobs (old S3: swap-aware hold caps, expectancy floors)
evaluated on the same recorded paths.
**Gate GV3:** paired delta tables with bootstrap CIs per family; exit-calibration table format
committed (versioned, fit-window-stamped) for any survivor.

### V4 — New material [OWNER GATE]
Built on backfilled data (2019–2023 IS, 2024 era-holdout, 2025+ untouched), each family
pre-registered with MDE per D1: (a) **session/time-of-day family** — London ORB, NY-open
drive/fade, Asia-range, day-of-week; clock-keyed, few knobs; M15 *execution* allowed where
per-bar spread exists (honesty gate satisfied by V1). (b) **Cross-sectional FX
momentum/carry** — weekly rank of the pair universe, basket long/short; needs the multi-row
run plan (exists) + a portfolio risk profile (built here, also serves V7). (c) **Indices
session strategies** on the V1 shortlist. (d) **Weekend-gap family** (Monday gap-fade /
gap-and-go — entered Monday, no weekend hold required; Swing terms from V0 decide more).
(e) Old-S2 analyses: regime conditioning (2×2 family-class × era interaction, external regime
variables — realized-vol percentile + efficiency ratio) and the F67 never-ran filter — on
recorded trades first, run-budget only if the conditional split is large.
**Gate GV4:** per-family pooled verdicts under D5′; owner picks the V7 candidate set.

### V5 — Gate upgrade (tooling)
Stationary block bootstrap (block ≈ 5–10 trading days) into `tools/research/` + gates; MDE
calculator (D1); EB/partial-pooling shrinkage across cells (τ̂ reported per family); stitched
walk-forward with ≥6-month trains replacing the OOS ratio. Wire `PassProbabilityEstimator`
(or sv2's simulator in MC mode over bootstrap-resampled trade streams) into candidate
reporting.
**Gate GV5:** each tool reproduces a hand-checked synthetic case; D5′ text finalized in this
plan by ledger amendment.

### V6 — Control layer (edge-independent)
Intraday equity envelope → honest intraday daily-cap detection in `ChallengeSimulator`;
portfolio-level intraday stop (flatten + halt-for-day at −3%) in the governor, simulated
identically; challenge-state risk policy (risk/trade as f(distance-to-target, DD headroom,
phase)) optimized by MC over bootstrap-resampled streams — reported as policy-vs-constant-risk
P(pass)/P(bust)/E[time] tables at 3 risk tiers.
**Gate GV6:** MC tables pasted; policy is deterministic config, kernel-pure, golden-safe.

### V7 — Era-holdout → EMBARGO-2 → portfolio → audit [OWNER GATES]
(a) V4/V2 survivors run once on the 2024 era-holdout; survivors of that run once on EMBARGO-2
(≥45 accrued days, ~Sep 2026) with the V6 control layer active — R4-format candidate cards
plus P(bust)/E[time] under the verified rule set. (b) Portfolio phase per structural-edge S6
(joint-tail sizing: bootstrap 99th-pct daily loss × 1.5 < 5%), conditional on ≥1 survivor.
(c) R5-style final audit.
**Stop rule:** if nothing survives the 2024 era-holdout, the conversation is data/market
class, not more search — same spirit as structural-edge §6, now with 6× the evidence behind it.

## 4. Session protocol — inherited verbatim from alpha-loop §4 / structural-edge §4

QA prior session's claims against artifacts first; pre-register (now incl. MDE, D1) before
anything scored; append LEDGER.md; paste gate outputs, never assert; fast suites green before
"done"; leave RESUME in AGENTS.md.

## 5. Verification matrix (owner, 5 minutes)
- Embargo + era-holdout untouched: no `BacktestRuns` row in 2024 or post-2026-07-05 windows
  before their gate ledger entries exist.
- `research persistence` on any experiment; D13 no-lies query = 0 (unchanged).
- Every V-stage gate table exists in LEDGER.md with the query/script that produced it.
- MDE line present in every pre-registration (grep the ledger).

## 6. Owner's asks → where they land (traceability)

| Owner ask (2026-07-16) | Where it lands |
|---|---|
| Why 30m/15m aren't successful; tested enough? | Never scored (constant-spread honesty gate). V1's bid/ask backfill delivers per-bar spread → V4 allows M15 *execution* for clock-keyed families; D6 of the roadmap ("no shorter-TF hunts on frequency alone") stays |
| Dynamic system picking strategy/symbol from the bank | Rejected as a picker (F64: trailing performance anti-selects, 24%); built as a **risk allocator** instead — D6 doctrine: Tier-1 risk freely, Tier-2 with WF proof, Tier-3 slow weights; never selection on recent results |
| Does the bank need review/update for new research? | D8: incumbents are not rewritten (contamination); dead knobs fixed (F71 pattern + dead-knob audit test); residence decided by V2's OOS verdict; new material enters as new members (V4) |
| Vision: train on current data now, repeat on tick-quality later | Pipeline-first confirmed; "trains on accuracy" = Tier-2 walk-forward-proven calibration only; tick data is already free (Dukascopy) — bar-level questions first, tick fidelity when M15/tight-trail execution becomes real |
| Have we avoided overfitting / hidden costs / solo-trader mistakes? | Largely yes (audit in review §1/§3 Q1); remaining exposures closed by D1 (power line), D3 (era-holdout), V1 (per-bar spread), V6 (intraday DD fidelity) |
| Embrace automation + parallel calc | D7: idempotency keys persisted to DB + SymbolInfoRegistry per-run scoping or process isolation; speed kit (parallel `gates.ps1`, `--parallel` factorial driver, determinism probe PASS) already delivered in S1 |
| Economic factors, noise, gaps, gap exploitation | V4: weekend-gap family (Monday entry, no weekend hold), post-news family (needs historical calendar data), F67 never-ran filter analysis; regime covariates kept simple (vol percentile + efficiency ratio) |
| Runner / maximiser — chase big profits when available | Exit-layer uncapping is refuted (no-TP: −$25.8k; the 2R cap protects). Conditional runner (Ride) gets its fair **paired** test in V3; "chase when available" is built as the V6 challenge-state risk policy — opportunism as optimized policy, not exit hope |
| Benefit from the kernel/engine | Kernel purity → parallel tape (D7), byte-identical replay (determinism probe), deterministic policy layer (V6) — all stages lean on it |
| Live-ready on cTrader, and when | §7 L-track: L0 parity smoke now → L1 correctness-before-money (folds into V0/V6) → L2 demo forward-run at first V2 candidate → L3 ops hardening during the embargo wait → L4 challenge after GV7 |
| Concurrent worktrees for speed | D9 + §8: research lane owns the DB, dev lane builds code in a separate worktree, merge at gates |

## 7. L-track — live readiness on cTrader (interleaved; mostly edge-independent)

| Phase | What | When | Gate |
|---|---|---|---|
| **L0** | Live compare-both parity smoke — the standing debt from the god-classes merge | Next cTrader session, before any venue-path change ships | GL0: smoke verdict pasted |
| **L1** | Correctness-before-money: **F26** (pre-trade gate commission estimate must dispatch on `CommissionType`), **F28** (`SwapCalculationType` dispatch), **F25** (persist `VenueSymbolSpecs` to DB so spec truth survives restarts), the UNIQUE start-record bug, daily-reset timezone (with V0), and a **sub-bar account heartbeat** (cBot → engine, 30–60 s equity/positions event) so the governor and the V6 portfolio stop see intraday troughs, not bar closes | Folds into the V0/V6 sessions | GL1: fixes + tests green; heartbeat event golden-tested |
| **L2** | Continuous **demo forward-run** (cTrader demo / FTMO free trial, desktop-capture listen mode): candidate(s) run unattended; weekly oracle reconcile (roadmap Q4 habit); daily automated report (equity, trades, tape-expectation drift) | Starts the moment V2 yields any candidate — calendar time is unfakeable, start early | GL2: first weekly reconcile green |
| **L3** | Ops hardening, timed to the EMBARGO-2 accrual wait: always-on box/VPS with cTrader Desktop + engine (unattended live = **listen mode**; headless CLI is backtest-only), process supervision + reconnect, **crash-recovery drill** (kill engine mid-position → restart → journal rebuild + venue reconcile verified), alerting on disconnect / order-rejection rate (the F24 lesson) / breach proximity / missed heartbeat, runbook | Aug–Sep 2026 (the embargo wait **is** the window) | GL3: drill + alert-test evidence pasted |
| **L4** | Funded challenge, V6 sizing policy active, parity verdict ≤ 14 days old | After GV7 | GL4: candidate card + owner go |

## 8. Concurrency / worktree protocol (D9 detail)

- **Lane R (research/truth):** the DB-owning worktree (currently `C:\Code\Shamshir`) checks out
  `iter/viability`. The census/experiment DB (`src/TradingEngine.Web/data/trading.db`) lives
  ONLY there — fresh worktrees start with no research data. All scored runs, sv2 scoring, and
  ledger scored-result entries happen in this lane, **one app instance per DB file**;
  parallelism happens *inside* the app/driver (`exit_factorial_driver.py --parallel`,
  determinism-probe-validated) — never as two apps on one SQLite file.
- **Lane D (dev):** a separate worktree (e.g. `C:\Code\shamshir-viability-dev`) on a branch off
  `iter/viability` (`iter/viability-dev` or per-stage). Builds V1 importer, V3
  recorder/replayer, V5 tools, V6 control layer, L1 fixes — with the credential-free gates
  (Unit / Integration / Sim-fast) run there. Anything needing research data reads the Lane-R
  DB path explicitly (`--db`) read-only, or waits for merge.
- **Merge discipline:** Lane D merges into `iter/viability` at stage gates; Lane R pulls before
  any scored batch. `LEDGER.md` stays append-only with one writer at a time — a dev session
  appends its entry at merge time, never concurrently with a research batch entry.
- **Docs lanes** (like `docs/quant-review`) are always safe in parallel; merge to `main` at
  owner gates.
