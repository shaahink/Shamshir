# Engine Truth — owner audit, 2026-07-10

**Lineage:** the real iteration is **`docs/iterations/iter-parity-pipeline/`** (mega plan,
P0–P7 marked DONE). `iter-land-fix` is only its landing queue — the A1–D1 fixes spun off the
parity-pipeline AUDIT and the P2.2 headline gate. This doc closes A1 and scopes what the NEXT
mega plan must contain; read it alongside `iter-parity-pipeline/PLAN.md` + `AUDIT.md`.

**Author:** Claude (owner session). Everything here was verified live against HEAD `d14bdfe`
(+ my working-tree verification run), the real `trading.db`, and the conductor session logs —
not inferred from docs. Companion evidence: `evidence/a1-f17-verified-20260710.md`.

This document answers three questions:
1. What is actually true about backtests right now (what works, what still lies)?
2. What does "reliable tape vs cTrader" require — the parity ladder for the next mega plan?
3. Why did 11 agent sessions burn on a fixed bug, and what must the pipeline change?

---

## 1. State of truth (verified today)

### TRUE and healthy
- **Tape venue produces trades again (F17 RESOLVED).** Verified by live run `8bd9cedb`:
  3 trades, 272 journal rows, 244 equity snapshots, NetProfit **byte-identical** to the Jul 7
  known-good run (`-1575.852`). Determinism across 3 days of commits held.
- **The DB does not lie.** `TotalTrades` == actual `TradeResults` rows for every run in the DB
  (verified by SQL join across all 33 recent runs). The runs LIST endpoint returns the same
  numbers as the DB. The long-suspected "list vs details mismatch" does not exist.
- **The kernel journal is superb.** Every working run has full BarClosed/EquityObserved/
  OrderProposed/OrderFilled records. This is your best forensic asset — protect it.
- **cTrader path produces trades and truthful statuses** since the Jul 8 fixes
  (`6533c7e` idempotent disconnect, `de4c8e7` completed-with-warnings).

### STILL LYING (each with root cause, ordered by user-facing pain)
| # | Lie | Root cause | Fix size |
|---|---|---|---|
| F19 | Healthy tape runs show scary `TRADES_PARTIALLY_UNRECONSTRUCTABLE` warning + degraded status | P0.3 persistence barrier's close/open pairing doesn't recognize the tape journal shape; fires on runs whose trades persisted perfectly | S (fix pairing or scope barrier to ctrader venue) |
| F18 | compare-both silently runs only the tape leg | `RunCompareBothAsync` child registration regressed by P2.1 RunStateMachine refactor; child state torn down in `finally`, so no post-mortem | M (A2's job; add child-run row visible in UI from the moment of spawn) |
| F22 | `CompletedAtUtc` shows sim-time for cTrader runs (e.g. `77e37dee` "completed" in January) | completion timestamp taken from bar-stream clock, not wall clock | S |
| — | Jul 6–7 cTrader rows still say `failed` (NetMQPoller) despite valid trades | crash fixed Jul 8; historical rows never healed | S (one-off heal script or UI note) |
| F23 | Every cTrader run "completes" only via the 30s BAR_STREAM_TIMEOUT forced disconnect | the "channels complete naturally" path never actually fires; the safety net is the de-facto shutdown | M (make deliberate shutdown the designed path; keep net as true fallback) |
| F20 | Listen-mode will re-create F17 | `CTraderListenService.cs:105` still has the old 5-levels-up dbPath fallback | XS (one line → DbPathResolver) |
| F21 | Agent docs lie: quickstart says port 5000 (real: **5134**), and advises `Stop-Process` ALL dotnet (kills unrelated repos' work — s9 did exactly this) | doc drift | XS |

**Rule to adopt: a run status may only claim what a query can prove.** Every status/warning
shown in the UI must be derivable from DB rows; anything else is a display invention.

---

## 2. The real parity question (next mega plan's core)

F17 was noise. The signal — measured on the Jul 6–7 paired runs and pre-registered in
`docs/audit/RECONCILE-FINDINGS.md` — is:

| Gap | Measured | Status |
|---|---|---|
| **F6 trade-count divergence** | tape 28 vs ct 24; 8 vs 7; 3 vs 3 — historically 34–83% more on tape | **unexplained — the #1 parity bug** |
| F2 entry latency | tape fills +1 M1 bar; cTrader +1 full H1 bar — constant 1-decision-bar gap | measured, fix deferred (Q4) |
| F1 spread cost | tape fills at mid | modelled gap, known direction |
| F2b intrabar DD | tape MaxDD blind to intrabar floating equity | known ("DB 0 vs venue 4.6%") |
| F4 gap-through fills | fills at stop price, not worse open | known, D1's job |
| RawMoney reconcile | DIVERGENCES flagged on both P2.2 workaround pairs | never triaged — blocked on F18 |

### The parity ladder (proposed acceptance ladder for the next mega plan)
Climb in order; each rung is a **machine-checkable gate** (ResearchCli verdict), and a rung may
not be attempted until the one below is green on a fresh paired run:

1. **L0 — both venues run and persist truthfully** (F17 done; F18/F19 to close).
2. **L1 — same signals**: proposals per (bar, strategy) identical across venues.
   The venue-parity equivalence test tier (AUDIT R8) is the harness; F6 dies on this rung —
   count divergence means *signals* differ (cooldown/config drift), not fills.
3. **L2 — same intents**: lots, SL/TP, order type identical (F1-lots, F5 limit-vs-market).
4. **L3 — fills within model**: entry deltas ≤ spread + 1-bar lag model; RawMoney delta
   explained item-by-item (spread × lots × trades ± swap timing).
5. **L4 — aggregates within model**: MaxDD/win-rate deltas explained by intrabar blindness;
   or engine adopts venue DD on cTrader runs.
6. **L5 — drift watch**: weekly compare-both auto-reconcile (already spec'd in
   RECONCILE-FINDINGS "weekly drift check") becomes a conductor gate, not a human chore.

**Only after L3 is green does calibration/research output mean anything.** That is the honest
prerequisite for the FTMO config-search program (exit lab, walk-forward, entry-tactic lab all
inherit tape fidelity).

---

## 3. cTrader as a first-class component (design gaps)

What exists is close: CTraderCliLocator, NetMQ transport, cBot sidecar report/events files,
30-min CTS, 30s stream safety net, completed-with-warnings truthfulness. What's missing is
*ownership semantics*:

- **One supervisor owns every child process.** Web app, ctrader-cli, cBot handshake — spawn
  through a single `ProcessSupervisor` that records PID + purpose + owner run-id, redirects
  output to per-run log files, kills by handle (never by name), and reaps orphans on startup
  (two opencode processes from Jul 8/9 are still alive on this machine right now, plus the
  historical ctrader-cli orphan problem).
- **A `doctor` verb** (ResearchCli): checks Web app up (right port), DB migrated, marketdata
  coverage for requested window, cTrader CLI resolvable, creds file present — prints one
  VERDICT line. Every agent session starts with `research doctor` instead of rediscovering the
  environment (this exact rediscovery burned sessions 1, 3, 6, 7, 9, 10).
- **An `app ensure` verb**: idempotently starts/attaches the Web app in the background with
  logs to a file, waits for /api/health, prints the port. Agents must never hand-roll
  `dotnet run` again — that is the single biggest stall cause in conductor history.
- **Truthful lifecycle:** deliberate shutdown of cTrader runs (F23) + one state machine
  (already exists per P2.1) with each transition journaled — the "child removed from `_runs`
  in finally makes post-mortem impossible" pattern (F18) is banned.

---

## 4. Why 11 sessions burned on a fixed bug (pipeline findings)

Full retrospective feeds `conductor-baton/docs/CONDUCTOR-VNEXT-PLAN.md`. The Shamshir-side facts:

1. **The fix landed in session 9 (`9962432` 02:16) and nobody knew.** The session was
   stall-killed while verifying; its discovery lived only in its dying context. Sessions 10–11
   then followed the stale tracker handoff. → *Knowledge must be checkpointed continuously
   (ledger file per session, updated per finding), not at session end.*
2. **Verification required a long-running app, and the stall policy killed exactly that.**
   The 15-minute no-output kill turned "start web app, run backtest, poll" into a death
   sentence. → *The pipeline needs sanctioned background-run primitives (`app ensure`,
   `run await`) that emit heartbeats, so verification is stall-proof by construction.*
3. **Human injection helped and hurt.** The 003 injection recovered history (good) but ordered
   "do NOT run another tape backtest until DB paths verified" (bad — verification was exactly
   what was needed) and carried the RunPlanJson red herring forward.
4. **Gates were green the whole time.** build/unit/integration/sim-fast never caught F17
   because no gate runs an actual end-to-end tape run and asserts trades > 0. → *Add a truth
   gate: `research run start … && research run validate --min-trades 1` as a per-phase gate on
   engine-touching stages.*
5. **Docs drift is an agent trap** (port 5000, kill-all-dotnet). → *Docs that agents follow
   verbatim are code; gate them like code.*

---

## 4b. Test doctrine (owner decision, 2026-07-10)

**cTrader/NetMQ tests exist to test cTrader/NetMQ — never trading logic.** Now that the tape
venue + kernel + golden fixtures exist, every trading-logic assertion belongs in the fast tiers
(Unit / kernel golden / tape sim). The cTrader/NetMQ suite shrinks to a transport contract:
handshake, bar/exec framing, clientOrderId ledger reconciliation, disconnect/timeout paths —
nothing that a FakeTransport or tape test can express. The P7.7 audit already classified the 19
`RequiresCTrader` tests (12 KEEP / 1 MERGE / 1 REPLACEABLE / 5 RETIRED) — execute that
classification, then re-audit the KEEPs against this doctrine: any KEEP that asserts strategy
or risk behavior gets ported to tape and demoted. Expected payoff: the slow suite stops being
the long pole in every gate battery (see also `docs/CTRADER-TEST-POLICY.md`).

## 5. Recommended next steps (in order)

1. **Finish iter-land-fix with the corrected scope** (this branch, conductor-run):
   A2 (F18 + P2.2 headline gate + F19/F20/F21/F22 small fixes bundled), B1, C1, C2, D1
   unchanged. A1 is DONE — tracker updated with evidence.
2. **Next mega plan = the parity ladder (§2) + process ownership (§3).** Name suggestion:
   `iter-parity-ladder`. Its D1-equivalent final gate: a fresh compare-both pair passes L3 with
   an auto-generated reconcile report, and the weekly drift check runs as a playbook.
3. **Only then** resume the quant program (entry/exit labs, walk-forward, config search) — its
   results are un-trustable until L3.
4. Run the next iteration through the upgraded conductor pipeline (see
   `conductor-baton/docs/CONDUCTOR-VNEXT-PLAN.md`) with the truth gate from §4.4 wired in.
