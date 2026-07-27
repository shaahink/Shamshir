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

### RESUME (next session)

- **Lane D (implementation agent):** execute `F87-COST-TRUTH-PLAN.md` on a branch
  (`iter/pass-economics-f87`), P0→P6, handover to `F87-HANDOVER.md`.
- **Lane R (next research session):** E1 objective truth can start NOW, in parallel, offline —
  assessment inventory (quantify governor/regime-blocked entries in V2/V4 journals),
  challenge-pipeline-EV MC on `ChallengeSimulator`/`PassProbabilityEstimator`, zero-edge floor.
  GV0 resolves inside E1 (owner picks account type; author `ftmo-1step` ruleset if wanted).
- **Owner:** run the backup (above); GE2 will need your account-type pick (E1.2).
- Standing debt unchanged: L0 live compare-both smoke at next cTrader session.
