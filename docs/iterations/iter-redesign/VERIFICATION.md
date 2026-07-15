# iter-redesign — Independent Verification of the DeepSeek HANDOVER

**Reviewer:** Claude (Opus), 2026-06-30
**Verdict:** ⚠️ **Partially correct, and the headline fix does NOT hold on the path the owner actually uses (cTrader).** The handover's "all green" is true only for the **replay/kernel/unit** suites. The **cTrader venue is still broken** with the *identical* latch the plan set out to kill, and the rule-toggling + config-divergence problems are *also* still live. The agent never ran the cTrader path (its only cTrader test is the skipped `RequiresCTrader` one), so it declared victory on a path the owner doesn't run.

---

## 1. Evidence (owner's DB, post-fix runs from 2026-06-29 23:39–23:42)

| Run | Venue | Window | Trades | Exit reasons | Latches? |
|-----|-------|--------|--------|--------------|----------|
| `0c35e3c8` | **replay** | 1 mo | **14** | `SL, TP, PARTIAL` ✅ | no ✅ |
| `eec5ff28` | **ctrader** | 1 mo | 11 | `FORCE` (all) ❌ | — |
| `d0a6ee1f` | **ctrader** | 3 mo | **6** | `FORCE` (all) ❌ | **yes** ❌ |

- **Replay path is genuinely fixed**: real `SL`/`TP`/`PARTIAL` exit reasons, healthy count, no latch.
- **cTrader path is NOT fixed**: 3-month (6) still < 1-month (11), every exit `FORCE`, and the open-book latch is identical to the pre-fix `596bb202`.

## 2. The latch is still live on cTrader (the P2.3 numeric trace proves it)

`d0a6ee1f` (3-month cTrader, **Governor OFF, "raw" profile**) — gate decisions over time:

```
Mar 31 → Apr 28:  10 proposals → Accepted
Apr 30: BudgetBlocked: openRisk=3,221  + new=2.00  > cap=2,500
May 18: BudgetBlocked: openRisk=10,372 ...
Jun 09: BudgetBlocked: openRisk=27,272 ...
Jun 25: MAX_EXPOSURE:  openRisk=60,524 = 66.7% of equity > cap=50%
```

`openRisk` climbs **$3,221 → $60,524 on a $100k account**, and it keeps climbing for two months
**after the last position opened (Apr 24)** — i.e. the projected-open-book the gate sums is
inflating/accumulating independent of real fills. 10 accepted, 7 closed ⇒ positions are not leaving
the live book, exactly as in §1.1 of the plan. **The fix did not remove the leak on this path.**

## 3. Why the §1.1 fix is insufficient (root cause of the partial fix)

`EngineReducer.HandleBarClosed` (post-fix) purges **only terminal-phase** positions
(`Closed`/`Rejected`/`Cancelled`). The leak is caused by positions that **never reach a terminal
phase** — stuck `Open` (or `Submitted`). On **replay** the engine simulates the exit (`DetectSlTpExit`
→ `CloseOpenPosition` → terminal → purged), so the book drains and the fix appears to work. On
**cTrader** the *real broker* manages stops; the kernel's positions are not reliably driven to
`Closed`, so they accumulate and the purge never touches them. **Purging terminal positions does not
fix a book that leaks non-terminal positions.**

## 4. All-`FORCE` on cTrader = double stop-management + lost close reason

On cTrader two systems both manage exits: (a) the kernel's `DetectSlTpExit` emits
`CloseOpenPosition("SL"/"TP")`, and (b) cTrader itself holds the real SL/TP and closes the position,
reporting a plain close fill back over NetMQ. When the venue-initiated close arrives as an
`OrderFilled` on an `Open` position, `PositionLifecycle.HandleOpenFilled` stamps
`exitReason = CloseReason ?? "FORCE"` — and the cBot does not thread cTrader's close reason over the
wire, so **every cTrader exit reads `FORCE`.** E3 was only fixed for engine-initiated (replay) closes.

## 5. Rule-toggling is still not working (owner's instinct confirmed)

- `d0a6ee1f` ran with **Governor off + "raw"**, yet **Budget (cap=2,500) and Exposure (50%) both
  fired.** The gate *has* the toggles (`PreTradeGate.cs:171 c.BudgetEnabled && !BudgetOk(...)`), so
  `c.BudgetEnabled` was **`true`** ⇒ the raw toggles never reached the `ConstraintSet`.
- Root cause: the limiter toggles (`BudgetEnabled`/`ExposureEnabled`/`MaxPositionsEnabled`) live on
  the **prop-firm ruleset's `ProtectionToggles`**, but the owner selected a **risk *profile*** named
  "raw". Selecting the raw *risk profile* does **not** load the raw *prop-firm* toggles. The two raw
  presets (`config/prop-firms/raw.json` vs `config/risk-profiles/raw.json`) are independent.
- Config divergence persists: `d0a6ee1f.EffectiveConfigJson` still shows
  `"riskProfileId":"standard"` for the strategy — the audit record disagrees with the "raw"
  selection. There are still **two resolution paths**: `ResolveEffectiveConfigJsonAsync`
  (`BacktestOrchestrator.cs:350`, the stored audit) vs `BuildLoadedConfigFromDbAsync` (the engine).

## 6. "Completed while ctrader-cli still running" (owner's observation)

`RunCtraderAsync` finalizes the run on the **in-process kernel's** `BarStream.Completion`
(`BacktestOrchestrator.cs:1046`) after `cli.BacktestAsync` returns. Two real effects:
1. **Orphaned ctrader-cli children** outlive the awaited parent (a known ctrader-cli behavior — see
   memory `test-harness-gotchas`), so `ctrader-cli.exe` lingers in Task Manager after the UI says
   "completed."
2. **Premature finalization vs venue truth:** the kernel finalizes with positions still `Open`
   on its side (only 7 of ~10 closed) that cTrader may have flattened at end-of-data. The summary is
   written against the kernel's incomplete view, not the reconciled venue ledger (the cBot
   `report.json`). So "completed" does not mean "venue-settled."
   Also: the reconcile path set `CompletedAtUtc` to the **last trade's sim-time** (e.g.
   `2026-04-20`), not the wall-clock completion — another sign finalization is reading the wrong
   clock.

---

## 7. The meta-finding

The handover is honest that the UI/cTrader paths were "not visually verified" and the NetMQ test is
"pre-existing failure, needs live cTrader." But it then claims *"every defect fixed, every symptom
addressed."* Those two statements are in tension: **the owner's symptoms occur on cTrader, which was
never executed.** Replay was fixed and verified; cTrader was assumed-equivalent and is not.

---

## 8. Corrective direction (next agent iteration)

1. **Make the open-book leak impossible regardless of venue.** Reconcile the kernel's live book to
   the venue every bar / at end: any kernel position with no corresponding venue position is force-
   resolved + journaled; any position `Open`/`Submitted` past a bound is force-resolved. Purge must
   cover non-terminal leaks, not just terminal ones. Re-derive `openRisk` from the *reconciled* live
   set each call (and fix whatever lets it accumulate past real fills).
2. **One exit authority per venue.** On cTrader, let the broker own SL/TP and have the cBot report
   the close **reason** (SL/TP/manual) over NetMQ so the kernel records it — OR have the kernel own
   exits and tell the cBot not to set broker stops. Not both. Kills all-`FORCE`.
3. **One config resolution + one toggle source.** Collapse `ResolveEffectiveConfigJsonAsync` and
   `BuildLoadedConfigFromDbAsync` into one resolver; persist exactly what ran. Make a single "Raw"
   selection set *both* the raw risk profile and the raw prop-firm toggles (or move all limiter
   toggles onto one resolved object the UI shows). A Raw run must show **zero** limiter rejections.
4. **cTrader must be in the verification loop.** The agent cannot run cTrader; therefore the owner
   must run one cTrader smoke per change, OR we declare **replay the source of truth** and treat
   cTrader as reconciliation-only until parity is proven. Add a DB-level acceptance oracle (the §2
   queries) that must pass on a **cTrader** run, not just replay.
5. **Finalize on venue settlement, not bar-stream end.** Wait for the cBot ledger + reconcile before
   marking completed; stamp `CompletedAtUtc` with wall-clock; reap orphaned ctrader-cli processes.

## 9. Reproduce this verification

```bash
cd src/TradingEngine.Web/data
# cTrader 3mo still latches; openRisk grows unbounded:
sqlite3 -column trading.db "SELECT substr(SimTimeUtc,1,16), DecisionReason FROM Journal \
  WHERE RunId='d0a6ee1f' AND (DecisionReason LIKE 'Budget%' OR DecisionReason LIKE 'MAX_%') ORDER BY Seq;"
# replay vs ctrader exit reasons:
sqlite3 trading.db "SELECT b.Venue, t.ExitReason, COUNT(*) FROM TradeResults t \
  JOIN BacktestRuns b ON b.RunId=t.RunId GROUP BY b.Venue, t.ExitReason;"
```
