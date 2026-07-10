# iter-parity-pipeline — Trust the comparison, then automate the research loop

**Written:** 2026-07-07 by Fable 5. **Read `AUDIT.md` first** — every phase here traces to an audited
finding (F1–F16) or retrospective item (R1–R10). This plan supersedes nothing in iter-quant-model's
delivered code; it repairs its seams and builds the next capability on top.

**Branch:** start from `iter/quant-model--p1-tf-agnostic` after P-0 lands the working tree. New branch
`iter/parity-pipeline`.

**The thesis:** iter-quant-model built the measurement machine but the owner cannot yet trust a single
number from a cTrader comparison (F1 ¼-sizing, F2 entry lag, F5 fake-failed, F6 vanishing trades, F9
inert configs), cannot operate the labs (F11), and cannot delegate the research loop to a local agent
(no non-UI driving surface). This iteration: (1) make one paired run produce IDENTICAL sizing, honest
status, and a committed reconcile verdict; (2) turn the research workflow into an agent-driveable,
owner-reviewable pipeline; (3) make the UI tell the truth. Wild-list features come only after the
pipeline can measure them honestly.

**Meta-rule (unchanged, now 4× vindicated): a deferred gate means the phase is NOT done.**

---

## 0. Owner decisions (defaults locked unless overridden — same convention as iter-quant-model §2)

| # | Decision | Default (locked) |
|---|---|---|
| Q1 | The agent's blanket 8-strategy switch to LimitOffset (uncommitted JSON) | **Revert the 8 JSONs to Market** (keep mean-reversion Limit). Entry tactic must be chosen by the entry lab per cell (D10), not by default-flip. Keep the F5 kernel code fix + tests — they're correct and needed. |
| Q2 | Tape+cTrader concurrent runs | **Serialize cTrader runs behind a queue** (one ctrader-cli at a time; tape runs stay parallel). UI shows "queued". True concurrency is not worth the port/DB contention risk now. |
| Q3 | Pipeline driver shape | **Standalone CLI (`TradingEngine.ResearchCli`) speaking HTTP to the running Web app** — one engine instance, UI sees everything live, agent never touches Angular. Not a second in-process runner. |
| Q4 | cTrader entry latency (F2) | **Measure first** (P0.4 instrumentation into reconcile), then decide M1-cadence command drain as a follow-up. Do not rebuild the cBot loop in this iteration unless the measurement shows >1-bar or variable lag. |
| Q5 | Run status vocabulary | Add **`completed-with-warnings`** (engine result complete; teardown/persistence anomalies attached as warnings). `failed` is reserved for "no trustworthy result". |
| Q6 | Where pipeline state lives | **DB table (`ResearchPipelines` + `ResearchPipelineSteps`)**, artifacts on disk under `docs/research/pipelines/{id}/`. Resumable by pipeline id. |

---

## 1. Phase map (dependency order)

```
P-0  Land the working tree deliberately (½ session)
P0   Parity truth repair: sizing ¼ (F1), status truth (F5), trade persistence (F6), latency instrument (F2)   ← the spine
P1   Config & DB truth: one DB (F10), config propagation + drift (F9, F7)
P2   Lifecycle: run state machine, cancel, queue, compare-both first-class (F8, F16) → THE P6.1 GATE FINALLY
P3   Research pipeline: ResearchCli + playbook engine + UI review page   ← the centerpiece feature
P4   Lab golden paths: exploration→exit-lab funnel (F11), MAE/MFE units doctrine (F12), entry lab (P3.6/D10)
P5   UI truth + Angular refactor (F13–F15 + refactor list)
P6   Wild list (carried from iter-quant-model §7) — only what the pipeline can measure
```

P0 → P1 → P2 are strictly ordered. P3 can start after P2.2 (needs honest run lifecycle to drive).
P4 needs P0 (trusted excursions) + P3 (playbook to walk it). P5 anytime after P0. P6 last.

---

## 2. P-0 — Land the working tree deliberately (½ session)

The tree has ~24 modified/3 new files (F5 kernel fix, P7/P3.3 tests, compare-both UI, strategy JSONs,
docs). Do NOT batch-commit blind (R4).

1. Revert the 8 strategy JSONs' `orderEntry.method` to `"Market"` per Q1 (keep the added normalized
   companion fields; keep mean-reversion as-is).
2. Commit in 3 separate commits: (a) `fix(F5): thread OrderEntryOptions through kernel; isLimit from
   request.Type` + its 2 tests; (b) `test(P7,P3.3): DD-guard/weekend-flatten/replayer validation tests`;
   (c) `feat(ui): compare-both toggle + signal-migration on new-backtest` + docs.
3. Gate: build 0 errors; Unit; fast Simulation (`RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ`);
   `npx tsc --noEmit` in web-ui. Paste outputs in commit bodies.

---

## 3. P0 — Parity truth repair (1.5–2 sessions; the spine)

### P0.1 — The ¼-sizing bug (F1)

**Instrument first (this is also the permanent fix for R7):**
- Extend the `OrderSubmitted` DecisionRecord's `DetailJson` (currently `"{}"`, see Kernel.cs
  `DecideProposed`) to carry the gate's actual inputs+outputs:
  `{equityAtGate, drawdownScale, lotSizingMethod, riskPct, slPips, pipValuePerLot, rawLots, clampedLots, riskAmount}`.
  Pure change inside the kernel — additive JSON, golden fixtures WILL move (DetailJson is journaled):
  re-baseline is expected and must be a dedicated commit per the golden protocol.
- Also log `EngineRunner` startup reconciliation values at INFO with the run id (they exist at
  `EngineRunner.cs:114-123` — verify they reach the per-run engine log).

**Reproduce (failing test first):**
- New Simulation test class `VenueSizingParityTests`: drive `KernelBacktestLoop` twice over the same
  synthetic H1 bars — once with `TapeReplayAdapter`, once with the FakeTransport-backed
  `CTraderBrokerAdapter` (the FakeTransport harness exists — see `FakeTransportTests`), same run config,
  profile `standard`. Assert: per matched proposal, **equal Lots and equal RiskAmount**.
- Variant: FakeTransport hello reports `account.balance = 25000` while config balance = 100000 → this
  is expected to reproduce ×0.25 if AUDIT hypothesis 1 is right. If it doesn't, chase hypothesis 2
  (Kelly misroute) — add a unit test pinning `KernelSizing.Calculate` branch selection for every
  `LotSizingMethod` value round-tripped through the profile store.

**Fix (once the mechanism is proven):**
- If hello-balance adoption: startup reconciliation must not override an explicitly configured backtest
  balance — rule: in backtest mode, `initialBalance` comes from config; venue balance is *recorded* as a
  drift warning if it disagrees (never silently adopted). Live mode keeps adoption.
- Regression gate: the parity test above + one REAL paired mini-run (tape + ctrader, EURUSD H1, 1 week)
  showing equal lots in the DB. Paste both.

### P0.2 — Run status truth + the NetMQPoller teardown crash (F5)

- **Separate engine-result from transport-teardown.** `BacktestOrchestrator.RunAsync` finalization:
  if a `BacktestResult` with stats exists, a teardown exception downgrades to
  `completed-with-warnings` (Q5) with the exception recorded in a new `WarningsJson`; `failed` only
  when there is no result. This alone restores UI trust even before the race is fixed.
- **Then actually fix the race:** reproduce with a test that connects/disconnects
  `NetMqMessageTransport` under load (poller stop vs queue disposal ordering); the committed B4 fix
  unsubscribes handlers but the crash persists — suspect the `NetMQQueue`/poller `Dispose` ordering or
  a second poller reference in the adapter. One observed repro before, one clean run after (R3).
- Gate: real headless cTrader run ends `completed` (not failed); fault-injection test yields
  `completed-with-warnings`; run list UI shows the new status (chip added in P5 but API now).

### P0.3 — Trade persistence integrity barrier (F6)

- Finalization must reconcile counts: closed positions in journal vs TradeResults rows for the run.
  Flush `TradePersistenceHandler` (await channel drain) before writing the end record. On mismatch:
  attach warning `TRADES_LOST:{expected}:{persisted}` (never silently report fewer trades) and attempt
  a journal-based backfill of the missing TradeResults (all data needed is in StepRecords — same
  technique as the P0.1 R-backfill).
- Repro test: simulate the BTC scenario (fills journaled, kill venue before closes settle) → run must
  end `completed-with-warnings` with the count mismatch surfaced, not `TotalTrades=0`.

### P0.4 — Entry-latency instrumentation (F2, per Q4 measure-first)

- Reconcile output gains per-trade `entryDelayBars` (+ seconds) computed proposal→fill for both runs,
  and per-run distribution summary. No cBot behavior change this phase.
- Gate: reconcile of the paired mini-run from P0.1 shows tape delay ≈ 1 M1 bar and quantifies the
  cTrader delay — the number goes in `docs/audit/RECONCILE-FINDINGS.md` as F2 evidence.

### P0.5 — Venue-parity test tier (R8, permanent)

- Promote `VenueSizingParityTests` into a small suite `Category=VenueParity` (fast, FakeTransport, no
  credentials): same bars in ⇒ same proposals, same lots, same fills-modulo-documented-latency. This is
  the missing test class that would have caught F1 the day it shipped. Wire into the standard gate
  filter.

---

## 4. P1 — Config & DB truth (1 session)

### P1.1 — One database (F10)

- Single source for the DB path (env/appsettings shared by Web AND Host CLI). Host startup: pending EF
  migrations ⇒ fail loud with the exact path it opened. Delete or archive the stale root `data/trading.db`
  (owner confirms first — it may hold nothing the Web copy lacks; check `BacktestRuns` count before delete).
- Gate: `dotnet run --project src/TradingEngine.Host -- lint-config` runs green against the SAME DB the
  Web app uses; the compute-reference-scales verb finally executes (populate the 84 cells — closes the
  iter-quant-model carry-forward).

### P1.2 — Config propagation + drift (F9, F7)

- Seeder policy: content-hash each `config/strategies/*.json` + `config/risk-profiles/*.json`; on
  startup, if JSON hash ≠ DB row's stored hash AND the DB row wasn't hand-edited via UI since seed
  (compare UpdatedAtUtc vs a new SeededHash column) → upsert + bump Version + log. If both changed →
  startup warning + `GET /api/system/config-drift` reports the diff; UI chip on Strategies page.
- Persist per-run effective config properly on the cTrader path too (`StrategyParamsJson` was `{}` —
  F7): the orchestrator already resolves effective configs for tape; unify.
- Gate: edit a JSON value → restart → run → journal `OrderProposed` reflects it; hand-edit via UI →
  restart → NOT clobbered, drift chip shows.

---

## 5. P2 — Lifecycle robustness (1–1.5 sessions)

### P2.1 — Run state machine + tests (F8)

- Enumerate states: `queued → starting → running → finalizing → completed | completed-with-warnings |
  cancelled | failed` and forbid illegal jumps in ONE place (orchestrator state property today is
  stringly and multi-writer).
- Cancel semantics: cancel kills the child ctrader-cli process tree (verify no orphans —
  `CtraderTestHarness` gotcha), releases ports, finalizes with partial stats + `cancelled`. Test each
  transition including double-cancel and cancel-during-finalize.
- Watchdog: CLI process exit without run completion ⇒ finalize within 30 s with real status +
  diagnostics (kills the "stuck running forever" class). Test with a fake CLI that dies.

### P2.2 — cTrader run queue (Q2) + compare-both first-class (F16) → **execute P6.1 at last**

- Queue: at most one cTrader run active; others `queued` (API + UI status). Tape runs unaffected.
- Compare-both: child run row is created UP FRONT with `ParentRunId` + `queued` status (visible in UI
  immediately); parent completion triggers auto-reconcile
  (`GET /api/backtest/analytics/reconcile?left&right` already exists) and stores the verdict on the
  parent (`ReconcileJson`).
- **GATE (the inherited P6.1 debt, now unblocked):** one real compare-both run (EURUSD H1, 1 month)
  on the post-P0 build; reconcile output committed to `docs/audit/RECONCILE-FINDINGS.md` with the V4
  template; sizing equal (P0.1), statuses truthful (P0.2), trade counts reconciled or explained
  (P0.3/F3). **This is the iteration's headline gate. Nothing in P3+ ships until it's green.**

---

## 6. P3 — The research pipeline (agent-driveable, owner-reviewable) (2 sessions)

**Why:** the owner wants to delegate symbol/entry/exit/cell research to a local agent (DeepSeek), but
driving Angular is where agents die (R6 and owner experience). The delegation surface must be CLI +
HTTP + files; the REVIEW surface stays the UI.

### P3.1 — `TradingEngine.ResearchCli` (new console project)

Talks HTTP to the running Web app (Q3). Verbs (all support `--json` for machine consumption, non-zero
exit codes on failure, `--timeout`):

```
research data ensure   --symbols EURUSD,XAUUSD --tfs H1,M15 --from 2026-01-01 --to 2026-07-01
research run start     --plan plan.json --venue tape [--compare-both] [--explore]
research run await     <runId> [--timeout 1800]      # polls status; streams progress lines
research run validate  <runId> --gates gates.json    # trades>0, status=completed, no TRADES_LOST, …
research reconcile     --left <tapeRun> --right <ctraderRun> --tolerances tol.json
research exitlab eval  --run <runId> --grid grid.json
research walkforward   --cell strategy:symbol:tf --windows …
research report        --pipeline <id>               # writes markdown artifact
research pipeline run  playbook.json [--resume <id>] # the driver (P3.2)
research pipeline status <id>
```

Design rules (these are what un-sticks the agent):
- Every command prints a one-line machine verdict last: `VERDICT: PASS|FAIL key=value …`.
- On failure it emits a **diagnostics bundle** (runId, status, ErrorMessage, last 50 journal rows,
  warnings) to stdout — the agent never needs to "go look in the UI".
- No interactive prompts, ever. Idempotent where possible (start with `--idem-key`).

### P3.2 — Playbook engine

- Playbook = JSON list of typed steps (`ensure-data`, `start-run`, `await-run`, `assert-gates`,
  `reconcile`, `exitlab-eval`, `walk-forward`, `apply-calibration`, `owner-gate`, `report`), each with
  params + `continueOnFail` + tolerances. Executed by the CLI against the API; state persisted per Q6
  (`ResearchPipelines`/`ResearchPipelineSteps`: status, startedAt, verdictJson, artifactPath).
- `owner-gate` steps BLOCK: pipeline pauses with status `awaiting-owner`; owner approves in the UI
  (P3.3) or via `research pipeline approve <id> <step>`.
- Resume: `--resume` skips completed steps by recorded verdicts (content-addressed on step params so a
  changed param invalidates downstream steps).

### P3.3 — UI review page (`/research`)

- Pipelines list (status, current step, started, playbook name) → detail: step timeline with verdicts,
  links to run monitors/reports, rendered markdown artifacts, approve/reject buttons on owner-gates.
- This is deliberately thin — read + approve only. All mutation flows through the API the CLI uses.

### P3.4 — Canonical playbooks (shipped as files in `playbooks/`)

1. `venue-parity.json` — paired run → reconcile → tolerance verdict (encodes P2.2's gate as a
   repeatable check; run it after ANY engine/venue change).
2. `explore-exit.json` — exploration run (RecordExcursions on) → validate excursions>0 → exit-lab grid
   → plateau pick → owner-gate → apply calibration → verification run → report.
3. `triage-sweep.json` — cell sweep over strategies×symbols×TFs → scoreboard snapshot → kill/park/keep
   report (replaces the 1-month ad-hoc sweep; window length a param, default 6 months).
4. `walk-forward.json` — per-cell WF with test-leg verdicts.

- **Gate:** playbook (1) and (2) each executed end-to-end by CLI only, pipeline visible in UI,
  artifacts committed under `docs/research/pipelines/`. Playbook (2) finally puts real rows in
  `TradeExcursions` (closes F11's data famine).

---

## 7. P4 — Lab golden paths (1 session + P3.6 as its own follow-on)

- **Exploration funnel (F11):** completed exploration run banner → "Open Exit Lab (pre-filtered)";
  Exit Lab empty-state explains exactly which knob was off (`RecordExcursions`) and links the
  explore-exit playbook. New-backtest exploration preset stays one-click.
- **MAE/MFE units doctrine (F12):** decide + test the unit per asset class (store R-normalized MAE/MFE
  alongside pips; pips stay venue-convention). Table-driven tests for EURUSD/JPY/XAU/BTC mirroring the
  P0.2 spread-test style. Backfill endpoint for existing rows (journal has what's needed).
- **Entry lab (P3.6/D10):** implement per `P3.6-HANDOVER.md` (already fully mapped, 32 files) — but
  ONLY after P2.2's reconcile gate is green, per the owner's original deferral rationale. Its outputs
  become a playbook step (`entry-tactic-eval`) rather than a UI-only surface.

---

## 8. P5 — UI truth + targeted Angular refactor (1–1.5 sessions)

Fixes (each gated on a driven smoke via `run-shamshir`, R6):
- **F13:** never emit equity=0 progress envelopes pre-first-observation; terminal envelopes freeze
  last-known equity; equity chart y-domain fits data (no 0 anchor).
- **F14:** one progress model, server-computed: `{barsDone, barsTotal(from actual inventory count, not
  calendar), percent, eta, elapsed, pass}` — monitor renders ONE bar; timeline keeps its distinct
  sim-time visualization but drops anything that reads as a second progress bar.
- **F15:** start button → pending state on click (disabled + spinner) until server ack or error toast;
  double-submit guarded server-side via idempotency key too.
- **F16:** compare-both child visible (P2.2 API) + run list groups parent/child.
- Status chips: `completed-with-warnings` (amber, hover = warnings), `queued`, `cancelled`.

Refactor list (do WITH the fixes, not as a separate rewrite):
- Finish the signals migration consistently (the tree currently mixes `[(ngModel)]` two-way on plain
  fields and signal getters — pick signals + explicit `set`, as the uncommitted new-backtest diff
  started).
- One run-state store: monitor/list/report currently each derive status/progress independently from
  envelopes + REST; consolidate into `runs.store.ts` with typed envelope contracts in ONE file shared
  with `api.types.ts` (kill drift between `RunProgressEnvelope` and backend DTOs).
- Error surfacing: global HTTP error toast + per-feature empty-states (Exit Lab/Scoreboard silently
  showing nothing was a recurring owner complaint).
- `OnPush` + `inject()` consistency pass on the runs feature only (don't boil the app).

---

## 9. P6 — Wild list (carried; rank re-affirmed, pipeline-gated)

Only start once playbooks (1)–(3) run green. Order by leverage-per-effort:
1. **Data-quality sentinel** (old #10) — cheapest, protects everything; runs as an `ensure-data` gate.
2. **Session fingerprinting** (old #2) — labels into excursions/scoreboard; pipeline report dimension.
3. **Spread/vol no-trade filter** (old #7) — needs P6.2 recorded spread (done); measured via playbook (1) A/B.
4. **MAE loser-triage** (old #6) — needs F12 units + F11 data; becomes an exit-lab grid dimension.
5. **Regime-conditioned calibration** (old #3), **block-bootstrap tapes** (old #5), **meta-allocator**
   (old #4), **entry-quality decomposition** (old #8), **pyramiding policy** (old #9) — backlog, each as
   a playbook extension, never as UI-first features.

---

## 10. Session protocol (bake-in; replaces ad-hoc handovers — from AUDIT §5)

Every agent session on this plan follows this contract. It is checkable, and checking it is the FIRST
task of the NEXT session.

**Session start (30 min, mandatory):**
1. Read `AGENTS.md` → `RESUME` block (bottom), this PLAN, `PROGRESS.md` of this iteration.
2. **QA the previous session:** re-run its stated gate command(s) verbatim; independently verify TWO of
   its claims (one against the DB/runtime, one against tests). Write the outcome in `PROGRESS.md` under
   `## QA of previous session` — confirmed / diverged (with evidence). A diverged claim becomes the
   session's first work item.
3. Only then continue the plan.

**During the session:**
- One subphase = one commit, gate output pasted in the body (R4).
- A gate is a command + its output. "Should work" is not a gate (R1).
- Runtime-propagation rule: any config/JSON/seed change is verified in the RUNTIME store (DB query or
  journal evidence) before being claimed (R5/F9).
- Observability rule: touching a decision path ⇒ journal/log its inputs in the same commit (R7).
- UI rule: any UI change ⇒ one driven smoke via `run-shamshir` (R6).
- Repro rule: a runtime bug fix needs one observed repro before + one observed absence after; otherwise
  the commit message and code comment say `UNVERIFIED` (R3).
- STOP conditions (write findings to PROGRESS and stop rather than thrash): a gate fails twice for the
  same cause; a fix requires touching the kernel reducer semantics; anything needs cTrader credentials
  interactively. Escalate with a short decision block (options + recommendation) for the owner.

**Session end (15 min, mandatory):**
1. Update `PROGRESS.md` (status table + deviations, same format as iter-quant-model).
2. Replace the `RESUME` block at the bottom of `AGENTS.md` with ≤20 lines: branch+commit, exact next
   step, gates currently green (with the command), open traps for the next agent.
3. Nothing may remain uncommitted except work-in-progress explicitly named in RESUME.

---

## 11. Verification matrix

| Phase | Gate (command + expected) |
|---|---|
| P-0 | build 0; Unit green; fast Sim green; tsc clean; 3 commits with pasted outputs |
| P0.1 | `VenueSizingParityTests` green (was red); paired mini-run: equal lots in DB; sizing inputs visible in journal DetailJson; golden re-baselined in dedicated commit |
| P0.2 | real headless ctrader run → `completed`; fault-injection → `completed-with-warnings`; zero NetMQPoller messages in ErrorMessage across 3 consecutive runs |
| P0.3 | BTC-scenario test green; mismatch surfaces as warning, backfill restores trades |
| P0.4 | reconcile output contains entryDelayBars; number recorded in RECONCILE-FINDINGS |
| P1.1 | Host CLI verbs run against the Web DB; 84/84 ReferenceScales rows |
| P1.2 | JSON edit → journal reflects it; UI edit survives restart; drift endpoint diffs |
| P2.1 | state-machine tests incl. cancel/watchdog/orphan-kill green |
| P2.2 | **one real compare-both with committed reconcile verdict (inherited P6.1 gate)** |
| P3 | playbooks (1)+(2) end-to-end via CLI only; pipeline visible in UI; artifacts committed; TradeExcursions > 0 rows |
| P4 | MAE/MFE unit tests per asset class; exit-lab funnel driven smoke; (later) P3.6 gates per its handover §6 |
| P5 | each fix has a driven-smoke note; no equity-0 anchor; single progress surface |
| P6 | each feature ships with a playbook that measures it |

---

## 12. Agent guidance — tricky parts

- **P0.1 golden movement:** adding sizing detail to `DetailJson` WILL move every golden fixture.
  Follow the established protocol: fix + tests first, verify only DetailJson changed (diff one fixture
  by hand), then a separate `REBASELINE` commit. Do not fold rebaseline into the fix commit.
- **P0.2:** resist "catch and swallow ObjectDisposedException" as the whole fix — Q5's status
  separation is the design fix; the race still gets a real root-cause because it can also bite mid-run.
- **FakeTransport harness:** see `tests/.../Infrastructure/FakeTransportTests.cs` (extended in the
  uncommitted tree) — it can impersonate the cBot end of both sockets; hello/bar/bar_result frames are
  plain JSON (shapes in `shamshir-ctrader` skill §2).
- **ResearchCli HTTP base:** the app self-hosts SPA+API single-origin (see `run-shamshir` skill for
  launch); default `https://localhost:7108`, override via `--base-url` / env. Auth: none today — don't
  invent one.
- **Playbook engine:** keep it a dumb sequential executor with persisted verdicts. No DAGs, no
  parallelism, no retry policies in v1 — resumability + honest verdicts are the whole value.
- **DeepSeek-specific:** phases here are sized ≤1 session; every phase names its files; when a phase
  says "measure, don't fix", that is the deliverable — a number in a doc is a valid gate output.
