# Conductor — Shamshir-Parity run report

_Updated 2026-07-08 04:09 UTC · branch `iter/parity-pipeline` · HEAD `c89f17a`_

**Status:** Idle
**Stage:** P0 — Parity truth repair (the spine) · attempts used 0 · working ▸ P0.2
**Checkpoints:** 3/17 done · **Sessions run:** 2 · **Cost:** $0.1656 · **Tokens:** 126,016 in / 42,661 out / 20,571 think

## Stage progress

| Stage | Title | Done | State |
|---|---|---|---|
| P0 | Parity truth repair (the spine) | 3/6 | **← active** |
| P1 | Config & DB truth | 0/2 | todo |
| P2 | Lifecycle robustness + headline gate | 0/2 | todo |
| P3 | Research pipeline (ResearchCli + playbooks) | 0/4 | todo |
| P4 | Lab golden paths | 0/1 | todo |
| P5 | UI truth + Angular refactor | 0/1 | todo |
| P6 | Wild list (pipeline-gated) | 0/1 | todo |

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | P0 | Deliver | 1 | 07-08 02:17 | 0:19 | Advanced | P0.0 | 5 | build:OK | $0.0273 | 1,510/12,485 |
| 2 | P0 | Deliver | 1 | 07-08 02:37 | 1:30 | Advanced | P0.1 P0.5 | 8 | build:OK | $0.1384 | 124,506/30,176 |

### Commits by session

- **s1 (P0 Deliver)** — 5 commit(s):
  - b5e06d4 docs(P0.0): tracker + AGENTS RESUME handoff — P0.0 done, next P0.1
  - 9686242 feat(ui): compare-both toggle + signal migration; revert 8 JSONs to Market (Q1)
  - b57d913 chore(conductor): s1 P0 working ▸P0.0 @ 03:32
  - bf74d4b test(P7,P3.3): DD-guard/weekend-flatten/replayer validation tests
  - 9570ad6 fix(F5): thread OrderEntryOptions through kernel; isLimit from request.Type
- **s2 (P0 Deliver)** — 8 commit(s):
  - c89f17a chore(conductor): s2 P0 working ▸P0.1 @ 05:07
  - c55d4a2 docs(P0.1): tracker + AGENTS RESUME handoff — P0.1/P0.5 done, next P0.2
  - a6aa08c fix(P0.1,F1): backtest sizes off config balance, not venue hello (¼-sizing) + sizing journal (R7)
  - f3c9e19 chore(conductor): s2 P0 working ▸P0.1 @ 04:52
  - cc7070e chore(conductor): s2 P0 working ▸P0.1 @ 04:37
  - 601e625 chore(conductor): s2 P0 working ▸P0.1 @ 04:22
  - c8357f5 chore(conductor): s2 P0 working ▸P0.1 @ 04:07
  - b73d250 chore(conductor): s2 P0 working ▸P0.1 @ 03:52

## Last gate run

build:OK

## Last session result

> I'll begin the pre-session ritual. Let me read the required docs in order, starting with the key handoff files.
> The RESUME block reconciles: HEAD is at `9686242` + conductor commits (`b5e06d4`, `e42aac6`), working tree only has the expected `BuildInfo.g.cs` drift + `.conductor/.gitignore`. Now let me read the stage plan, audit findings, and standards docs.
> Now let me read the standards docs and evidence file from P0.0, plus start locating the key code files.
> Now I understand the context. Let me QA the previous session (P0.0). First, verify the runtime store claim (DB) and read the key P0.1 files, while kicking off the build gate.
> R5 claim confirmed: DB shows Method:0 (Market) ×8 + Method…

## Tracker handoff

```
last: **P0.1 + P0.5 DONE** — a6aa08c fix(P0.1,F1): backtest sizes off CONFIG balance (not venue hello);
      EngineRunner.ResolveInitialBalance (pure); sizing DetailJson (R7); VenueSizingParityTests
      [Category=VenueParity] 5/5 incl. ×0.25 repro + equal-lots-after-fix. QA-previous(P0.0): confirmed.
stage: **P0 IN PROGRESS** — P0.0/P0.1/P0.5 done; P0.2 (run-status truth F5) is next, NOT started.
gate: GREEN — build 0 err/5 warn; Unit 508/0/6; fast Sim 144/0/0 (was 139; +5 VenueParity);
      golden 61/61 byte-identical (NO rebaseline — see evidence §5); Integration 101/0/0.
next: **P0.2** — separate engine-result from transport-teardown (Q5 `completed-with-warnings`); reproduce
      the NetMQPoller disposal race (poller/queue Dispose ordering), one repro before + clean after (R3).
      See PLAN §3 P0.2 + AUDIT F5. NOTE P0.1 golden did NOT move (forecast was wrong) — no rebaseline.
trap: real paired cTrader mini-run for P0.1 is OWNER-PENDING (needs creds). tsc 2 PRE-EXISTING errors
      (P5). BuildInfo.g.cs regenerates every build → re-dirties (committed generated metadata). P2.2 OWNER-GATE.
```
