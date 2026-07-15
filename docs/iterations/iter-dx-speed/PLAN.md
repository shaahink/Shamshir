# Iter DX-Speed — Remove Development-Cycle Bottlenecks (PLAN)

**Status:** D1 DONE (executed 2026-07-15 on `iter/alpha-loop` at the owner's direct instruction —
see `web-ui/tests/e2e/KNOWN-FAILURES.md` for the full triage; suite now exits 0 in ~30s, 31 passed /
17 skipped). D0 partially covered by D1's measured before/after (7 min + 15 failures → 30s + 0).
D2–D6 remain for the fresh agent; D5's "interim note" in the shamshir-ui skill is already updated.
Written 2026-07-15 from the X2/X3 session retrospective.
**Executor:** a fresh agent. This doc is self-contained; you do not need the X2/X3 transcript.
**Branch:** execute on `iter/dx-speed` off `iter/alpha-loop`.
**Scope guard:** this iteration touches ONLY `web-ui/tests/**`, `web-ui/package.json`,
`web-ui/playwright.config.ts`, `scripts/**`, `docs/**`, `.claude/skills/**`, and (optionally)
`src/TradingEngine.Web/appsettings.Development.json` logging levels. **Zero engine/kernel/domain
changes.** Golden stays 63/63 byte-identical by construction.

---

## Why this iteration

The X2/X3 delivery session (2026-07-15) lost most of its wall-clock to tooling, not thinking:

| Cost | Measured | Cause |
|---|---|---|
| ~21 min | 3 full Playwright runs (7.0m / 6.7m / 7.4m) | 15 failures are pre-existing but undocumented → forced a stash → rebuild-old-SPA → rerun baseline dance to prove "not a regression" |
| ~4–6 min | repeated kill → `dotnet build` → relaunch → re-warm | Windows file locks (MSB3021) forbid rebuilding under a running host; done by hand each time |
| 1 extra build cycle | full build failed on IDE0011 (braces) style analyzer | analyzer errors surface only at build time |
| ~10 min | re-deriving environment facts | lightweight-charts v5 API removal, EF `--context` flag, cold inventory scan, etc. were re-discovered, not read |
| ~15 min | manual live verification | polling run status by hand; non-negotiable in value (it caught F55–F58) but automatable in mechanics |

The model finds root causes in 1–3 probes; the cycle time is the tooling. Every fix below states
what verification value it preserves — **speed that costs signal is a regression here** (see
AGENTS.md "Research integrity" — this repo has been burned by exactly that).

## Ground rules — value that must NOT be lost

- **V1.** The full Playwright suite continues to exist and run green. Quarantine ≠ delete unless the
  UI a spec tests no longer exists.
- **V2.** The live compare-both requirement for venue-touching changes is untouched (AGENTS.md).
- **V3.** Analyzer severities are NOT downgraded. We surface them earlier, not less.
- **V4.** Unit / Integration / Sim-fast gates unchanged. Baseline: Unit 759/0/6 · Integration 121/0/0 · Sim-fast 144/0/0.
- **V5.** No engine code changes. `git diff --stat` at the end must show only the paths in the scope guard.

---

## Phases

### D0 — Measure and pin the baseline (½ session)

Re-measure on the current tree and commit the numbers, so every later phase proves its win:

- `dotnet build` (cold + warm), `npm run build --prefix web-ui`, each dotnet test suite,
  `npx playwright test` full (from `web-ui/`), and one targeted spec
  (`npx playwright test tests/e2e/x2-x3.spec.ts`).
- Record command + wall time + pass/fail counts in `docs/iterations/iter-dx-speed/EVIDENCE.md`.

**Gate:** EVIDENCE.md exists with a timing table and the exact commands used.

### D1 — E2E triage: kill the baseline dance forever (1 session) ← biggest win

The suite: `web-ui/tests/e2e/` = `ui-smoke.spec.ts` (484 lines), `iter-redesign.spec.ts` (179),
`live-monitor-links.spec.ts` (148), `x2-x3.spec.ts` (127). ~15 tests fail on a healthy tree.

For EVERY failing test, classify with evidence (run it, read it, check the UI it targets):

1. **KEEP** — tests current UI, failure is a real bug → file it in the tracker, fix if trivial.
2. **QUARANTINE** — needs data/services this environment lacks (cTrader, seeded bars, live run) →
   `test.fixme(..., 'reason + unquarantine condition')` so intent is preserved and it reports as
   skipped, not failed.
3. **DELETE** — asserts UI from a dead iteration that no longer exists. Deletion requires naming
   the removed route/component in the commit message.

Create `web-ui/tests/e2e/KNOWN-FAILURES.md`: one row per quarantined/deleted test — name, reason,
unquarantine condition. This file is now the authority; **no future session may stash-rebuild a
baseline to prove a Playwright failure is pre-existing.**

**Gate:** `npx playwright test` exits **0** on a clean tree (fixmes report as skipped).
**Value preserved:** every quarantined test keeps its assertion text + a written revival condition.

### D2 — Test tiers + config hygiene (½ session)

- Add npm scripts in `web-ui/package.json`:
  `"e2e:fast": "playwright test --grep @fast"` and keep `"e2e"` as the full suite.
  Tag the cheap, load-bearing specs (`x2-x3.spec.ts`, the render-only parts of `ui-smoke`) `@fast`.
- `playwright.config.ts`: `screenshot: 'on'` → `'only-on-failure'` (every passing test currently
  pays a screenshot). Evaluate `fullyParallel`/workers — specs share one app + one SQLite DB, so
  only enable it if specs are read-only or isolated; otherwise document WHY it stays serial.
- Workflow rule (goes in AGENTS.md, already drafted there): iterate on `e2e:fast`; full `e2e`
  before calling a phase done, run **in the background** while writing tracker/ledger updates.

**Gate:** `npm run e2e:fast` exits 0 in **< 90s**; full `npm run e2e` still runs every non-fixme test.

### D3 — One-command backend restart (½ session)

Write `scripts/dev-restart.ps1`: kill hosts whose commandline matches `TradingEngine.Web.dll`
(CIM pattern already in `.claude/skills/run-shamshir/SKILL.md`), `dotnet build
src/TradingEngine.Web -c Debug`, relaunch with cwd `src/TradingEngine.Web`, poll
`GET /api/runs` until 200, print PID + port. Flags: `-NoBuild`, `-Port`.

Then evaluate `dotnet watch run --project src/TradingEngine.Web --non-interactive` as the
iterating alternative (watch owns the process, so the lock problem vanishes). It must ignore
`wwwroot/**` churn (SPA builds would trigger useless restarts) — if that can't be configured
cleanly, keep the script as the blessed path and say so in the skill.

**Gate:** warm-tree round-trip (kill → build → healthy 200) **< 60s**, demonstrated twice in EVIDENCE.md.

### D4 — Scripted live verification (½ session)

Automate the *mechanics* of the live gate, not the gate itself: `scripts/verify-live.ps1` —
POST a tape run (symbol/window with known bars via `Invoke-RestMethod`), poll to terminal state
with a hard timeout, assert: terminal ≠ stuck, trades > 0, progress advanced monotonically.
Nonzero exit on any failure. Environment-aware: in a container with no `marketdata.db`, print
`SKIPPED (no bars)` and exit 0 — never fake a pass.

**Gate:** script drives a real tape run to terminal on the owner machine and fails correctly when
pointed at a dead port.
**Value preserved:** live verification still happens on every UI/orchestrator change — it just
costs one command instead of fifteen manual polls.

### D5 — Keep the banked knowledge true (¼ session)

The 2026-07-15 session already banked gotchas into `.claude/skills/shamshir-ui/SKILL.md`,
`.claude/skills/run-shamshir/SKILL.md`, and AGENTS.md ("Dev-cycle speed rules"). After D1–D4:
update those sections with the final script names/npm commands, and replace the interim
"KNOWN-FAILURES.md does not exist yet" note in the shamshir-ui skill.

**Gate:** every command named in the two skills executes as written.

### D6 — Close out

Fast suites green (V4), `git diff --stat` respects the scope guard (V5), EVIDENCE.md has
before/after timings for D1–D3, tracker row updated per the AGENTS.md tracker rule.

---

## Out of scope (explicitly)

- Downgrading any analyzer severity, deleting the full e2e suite, or weakening the live-verify
  doctrine — see V1–V3.
- Engine/kernel performance (covered by iter-marketdata-tape work).
- CI setup — there is no CI here yet; if the owner wants one, that is its own decision (file it,
  don't build it).

## Success criteria (whole iteration)

A backend-touching UI session should pay: **< 60s** per backend restart, **< 90s** per e2e
iteration loop, **one** full suite run (green, background) before done, **zero** baseline
re-derivation. That converts the measured ~50 min of session tax into roughly 10.
