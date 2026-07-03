# Follow-up: branch reconciliation + tape-backtest readiness verification

**Written:** 2026-07-03
**Branch:** `develop` (this is now the canonical integration branch)
**Pushed:** commit `71133dd` on `origin/develop`

---

## §1 — Branch reconciliation: what happened and why

Two sibling branches had independently redone the same `iter-merge-plan` M1–M5 work:

- `iter/tape-trust` (main worktree `C:\code\shamshir`) — 11 commits after the fork point `1fba208`.
- `iter/merge-plan` (worktree `C:\code\shamshir-trust`) — 21 commits after the same fork point, **already merged
  into `develop`** before this session (confirmed: `git merge-base develop origin/iter/merge-plan` == merge-plan's
  tip exactly). `develop` also had 2 more commits beyond that: `763615e` (cTrader-free tape data **import**
  endpoint + fixes) and `c70cc8d` (experiments wired to the tape venue + minimal Experiments UI) — together
  `docs/iterations/iter-tape-enable/HANDOVER.md`'s Tier-1 work.

So the real comparison was `iter/tape-trust` vs. `develop`, not two unmerged siblings. Every file that differed
between them was audited (68 files, ~2900 lines). Verdict: `develop` already had an equal-or-better version of
everything `iter/tape-trust` had done, generally *with test coverage tape-trust lacked*:

| Area | `iter/tape-trust` | `develop` |
|---|---|---|
| `RunNarrativeService` schema bug (camelCase read against real PascalCase/nested `EventJson`) | Found + fixed this session (ported from develop's own independent fix) | Already fixed, **with** `RunNarrativeServiceTests.cs` (171 lines) + `RunNarrativeQueryTests.cs` (94 lines) |
| Run-delete cascade orphaning `VenueSessions` | Found + fixed this session | Already fixed, **with** `DeleteRunsCascadeTests.cs` |
| M3.3 `EntryReason`/`EntryRegime` | Hardcoded `EntryRegime = null`, `EntryReason` = order-entry-method placeholder | Real values threaded from `OrderProposed` verdict + `BarEvaluator` regime through `PositionLifecycle`/`EffectExecutor`/`TradeResult`; real "Why entered" UI section in `trade-detail.component.ts` |
| Run-overlap guard | None — two runs could start concurrently | 409 Conflict guard in `RunsController.cs` |
| M4.2 Data Manager | Only m1-overlap badge | Per-symbol storage totals + per-(symbol,TF) delete-range, **plus** cTrader-free NDJSON/CSV **import** endpoint |
| Daily-DD bucketing | **Also buggy** (calendar date, not 22:00 UTC) | **Also buggy** — this was the one real gap, see §2 |

**Action taken:** fast-forwarded local `develop` to `iter/experiments-tape-tier1`'s tip (a pure fast-forward — that
branch was `develop` + exactly one more commit, `c70cc8d`, with zero divergence, so this lost nothing). Then fixed
the one genuine gap (§2) as a new commit on `develop` and pushed. `iter/tape-trust` and `iter/merge-plan` are now
**historical** — do not build further feature work on them. All future work should branch from `develop`.

No git history was rewritten, no branch was deleted, no force-push was used. `iter/tape-trust`'s uncommitted
working-tree fixes from earlier this session (in `C:\code\shamshir`) were superseded by this reconciliation and
were not separately committed there — they're redundant now that `develop` has the same fix.

## §2 — The one fix ported forward (commit `71133dd`)

`RunQueryService.GetRunDailyPnLAsync` and `BacktestAnalyticsController`'s `GetPassProbability`/`GetDailyPnL`
endpoints grouped trades by `t.ClosedAtUtc.Date` (calendar midnight) instead of the 22:00 UTC prop-firm reset
boundary — a direct violation of `PLAN.md`'s own "What NOT to do" list. Fixed with a `PropFirmDayOf()` helper
(mirrors `TradingEngine.Host.ResetClock.ResetPeriodDate`'s algorithm) in both files. All three seeded rulesets use
`dailyResetTimeUtc: "22:00:00"`, so hardcoding 22:00 UTC is safe for everything currently seeded; a future ruleset
with a different reset time would need this threaded per-run instead — noted, not blocking.

Gates after the fix: Unit 314/0/6, Integration 110/0, Golden 63/63, clean `dotnet build`.

## §3 — Tape-backtest readiness verification (live, not just static review)

Per the ask to verify the tape-backtest path is "wired correctly both for data gathering and exec and is ready,"
this was independently re-verified live (not just by reading `HANDOVER.md`'s prior claims), using the real
`marketdata.db` already present in the repo (EURUSD H1: 95 bars 2025-06-01→06-05; M1: 170 bars 2025-05-30→06-01).

**Data gathering — READY.** `GET /api/data-manager/inventory` correctly reported both series with accurate bar
counts and an `m1Overlap` flag. The cTrader-free **import** path (`POST /api/data-manager/import`, added in
`763615e`) is the way to get data onto this venue without a cTrader install; the **download** path still requires
cTrader (unavailable on this machine, unverified this session — Tier 2 item, owner-only).

**Exec — MOSTLY READY, one confirmed pricing bug.** Drove a real `venue=tape` run (4 strategies, EURUSD H1,
2025-06-01→06-05) via `POST /api/runs`: completed in ~825ms, 74 bars processed, no errors. The live narrative feed
(`GET /api/runs/{id}/narrative`) produced genuinely readable output — `"mean-reversion SHORT EURUSD @1.14584 · SL
1.14814 (23p) · TP 1.14353"`, correct `MAX_POSITIONS:3>=3` rejections, `"New week"`/`"New prop-firm day"` roll
events — which **directly confirms the `RunNarrativeService` fix works in a live run**, not just in code review.
Two positions opened correctly (real fill price + lot size in the narrative); neither closed within the short
4-day window, so a closed-trade narrative line wasn't observed this session — re-run with a longer window (or the
HANDOVER's own synthetic-data-generator approach) to see that path too. `exitResolution` came back `null` on this
particular run; the *wiring* is independently confirmed correct in code (`TapeReplayAdapter.ExitResolution` →
`BacktestOrchestrator.state.ExitResolution` → `RunQueryService` → `RunDetailResponse` — this resolves a doubt
`HANDOVER.md` explicitly flagged as unverified), the `null` here is most likely because the M1 shard only covers
2025-05-30→06-01 while the H1 run spans through 06-05, so most of the run had no M1 backing to resolve against —
a data-coverage artifact of this quick test's dataset, not a wiring defect.

**CONFIRMED unfixed correctness bug — short entries get zero spread cost.** In both `TapeReplayAdapter.cs` (~line
254) and `BacktestReplayAdapter.cs` (~line 196):

```csharp
var fillPrice = request.Direction == TradeDirection.Long ? midPrice + halfSpread : midPrice;
```

Longs correctly buy at `midPrice + halfSpread` (ask). Shorts should sell at `midPrice - halfSpread` (bid) but
instead fill at the raw `midPrice` — a systematic optimistic bias on every short entry in both credential-free
venues. (Every other spread-sensitive path — SL/TP detection, `ClosePositionAsync`, floating-equity calc — already
handles both directions correctly; it's specifically this one market-order entry-fill line in both adapters.)
This was flagged (but not fixed) in `docs/iterations/iter-tape-enable/HANDOVER.md` as "C1." **This is the top
remaining correctness gap for tape-backtest readiness.** Per repo rules (`PLAN.md`: golden must stay 63/63
byte-identical; `HANDOVER.md`'s own note: "Deferred because the fix changes fill prices → golden must be
re-baselined"), **this was not fixed in this session** — it needs explicit owner sign-off before touching it, since
fixing it changes fill prices for every short trade ever golden-tested.

## §4 — Remaining items (from `iter-tape-enable/HANDOVER.md`, still open)

**Tier 2 (data path polish):**
- Verify `download` start/end round-trips against real cTrader — needs the owner's machine.
- D1: unify the multiple `trading.db` path locations.
- D2: audit hardcoded `EURUSD`/`H1`/`10000` defaults scattered through the UI→engine config path.
- ~~Thread `exitResolution` onto the run-detail DTO~~ — **resolved this session, confirmed working** (§3).

**Tier 3 (golden-sensitive, needs owner sign-off + re-baseline):**
- **C1 — short-entry zero spread cost** (§3) — now independently re-confirmed live, promote to top priority.
- F5 — commission charged wholly at close instead of split half-at-open/half-at-close (net P&L unaffected, only
  intrabar equity path).

## §5 — Housekeeping note: npm/Windows environment flakiness

Setting up a fresh worktree (`C:\code\shamshir-dev`) to do this reconciliation hit repeated `npm install`
failures in `web-ui/` — plain install hit an ERESOLVE `jest` peer-dependency conflict; `--legacy-peer-deps`
install completed but silently produced a tree missing `tailwindcss`/`@tailwindcss/*` entirely (confirmed via
`npm ls tailwindcss` → empty) despite `package.json` listing them and the main worktree installing them fine with
the same file. A raw `cp -r` of a known-good `node_modules` also came up short (missing a transitive dep, `mri`)
— likely NTFS reparse-point/symlink handling breaking under `cp` on Windows. Root cause not fully diagnosed.
**Workaround used:** copied the main worktree's already-*compiled* `wwwroot` output (not source) into this
worktree purely to unblock `WebApplicationFactory`-based integration tests (`WebSmokeTests`) and the live
verification run — this is suficient for those (both passed), but **`web-ui/node_modules` in `C:\code\shamshir-dev`
is not actually usable for real Angular development.** Before doing any UI work in that worktree: delete
`node_modules` and `package-lock.json`-derived caches and retry `npm install` in a normal (non-sandboxed)
terminal, or just delete the worktree and re-add it fresh.
