# P1 evidence — venue-declared symbol specs, live-verified

**Date:** 2026-07-11 (QA session, iter-alpha-loop)
**Method:** static audit of commits `de52441`/`393ff67`/`56871de`/`83519da` + live cTrader compare-both runs on this machine (creds: `seankiaa`/5834367). Every claim below cites a RunId or a file:line.

---

## 0. Verdict

**P0 is sound. P1's core commission/symbol-spec model is sound, but it shipped with one critical live bug (F24, found and fixed this session) and two real gaps (F25, F26) that were undetected because the previous session's gate battery never included a live cTrader run.** All four are documented below with reproduction evidence. Gate battery re-verified green after the fix: build 0err/5warn · Unit 721/0/6 · Integration 121/0/0 · Sim-fast 144/0/0.

---

## 1. F24 — CRITICAL (found + fixed live this session)

**`SymbolInfoRegistry.MergeVenueSpec` overwrote `TypicalSpread` from the venue's live/backtest-CLI spread, which silently collapsed the ATR-based stop-loss ceiling and made cTrader reject every wide-stop trade.**

### Reproduction (before fix)

Compare-both, XAUUSD/H4/trend-breakout, 2025-08-01→2025-10-01, market entries, `config/compare-both/xauusd-h4-tb-p1-verify.json`:

| Leg | RunId | Trades | OrderProposed | OrderFilled |
|---|---|---|---|---|
| tape | `e907e647` | 12 | 14 | 24 |
| cTrader | `921ce1e4` | **0** | **17** | **0** |

Every one of the 17 cTrader-leg proposals was rejected. Journal `EventJson` for a rejected proposal (`RunId='921ce1e4'`):

```
GuardResult: "SL_TOO_WIDE:2603.0>175.0"
Profile.MaxSlPips: 175.00000000382028
```

Tape-leg proposal for the same strategy/window (`RunId='e907e647'`):

```
SlPips: 2614
Profile.MaxSlPips: 5250
```

### Root cause

`RiskProfile.MaxSlPips = MaxSlAtrMultiple × ReferenceAtrPips`, and
`ReferenceAtrPips = TypicalSpreadPips × timeframeFactor` (`AddOnAutoTuner.ReferenceAtrPips`, H4 factor = 35 — pure linear scaling, no `ReferenceScales` DB entry exists for XAUUSD/H4 so the heuristic fallback always fires).

- **Tape** (never touched by `MergeVenueSpec`): `TypicalSpread` = `config/symbols.json`'s static `0.3` → `TypicalSpreadPips = 0.3/0.01 = 30` → `MaxSlPips = 5 × 30 × 35 = 5250`.
- **cTrader** (post-merge): `TypicalSpread` was overwritten with the venue's captured *live* spread — which for a CLI backtest reflects the `--spread=1` (1 pip) test argument, not a realistic "typical" spread → `TypicalSpreadPips ≈ 1.0` → `MaxSlPips = 5 × 1 × 35 = 175`.

A 30× shrink in the sizing-heuristic reference collapses the SL ceiling from a realistic 5250 pips (gold routinely needs $20–30 stops) to 175 pips (~$1.75) — so `SL_TOO_WIDE` fires on every trend-following signal. This is **not** what D10 asked for: D10's field list is "commission + type, swap long/short + calc type, lot size, pip/tick size, digits, triple-swap day" — spread was never in scope, yet the implementation captured it and merged it into the same `TypicalSpread` field several other subsystems use as a *stable reference constant* (risk sizing) as well as a *live cost input* (tape fill spread simulation — irrelevant to cTrader, since cTrader fills itself).

### Fix

`src/TradingEngine.Infrastructure/SymbolInfoRegistry.cs` — `MergeVenueSpec` no longer copies `spec.TypicalSpread` into the registry's `SymbolInfo.TypicalSpread`. Commission/swap/lot/pip/tick-size merging (the fields D10 actually names) is unchanged.

### Reproduction (after fix)

Same config, fresh run:

| Leg | RunId | Trades | Net | Gross | Commission | Swap |
|---|---|---|---|---|---|---|
| tape | `f22e51bb` | 12 | 2735.79 | 2850.51 | −133.80 | 19.08 |
| cTrader | `261bb748` | 14 | 4531.05 | 4593.18 | −51.88 | −10.25 |

Trade counts are now comparable (12 vs 14 — consistent with the pre-registered F23 entry-latency effect: cTrader median entry lag 2 bars vs tape 1.004 bars, `GET /api/backtest/analytics/reconcile?left=f22e51bb&right=261bb748`), not a 12-vs-0 total rejection. Gate battery re-verified green post-fix (see §0).

### Residual — commission/swap deltas still large

Aggregate commission: tape −133.80 vs cTrader −51.88 (2.6×); swap: tape +19.08 vs cTrader −10.25 (opposite sign). **This does not yet meet P1's own tolerance budget (commission ≤2%, swap ≤5%).** However, the reconcile's per-trade matcher (`LedgerReconciler.ComputeTradeDeltas`) pairs trades by `|OpenedAtUtc_left − OpenedAtUtc_right| < 5 minutes` — and the two venues' entries are ~4–8+ hours apart by design (F23), so **zero trade pairs matched** on this run (`"text"` field shows only the aggregate divergence table, no per-trade rows). The aggregate commission/swap comparison is therefore comparing two *different, unmatched trade sets*, not the same trades priced twice — it is not yet a clean signal on the commission model itself. Filed as F29 below; the real per-trade commission comparison needs either (a) wider/bar-aware matching tolerance, or (b) P2's limit entries (deterministic, near-identical fill timestamps), whichever lands first.

---

## 2. F25 — MAJOR: `VenueSymbolSpecs` DB table is migrated but never written

`git grep VenueSymbolSpecEntity` outside migrations/DbContext/snapshot returns nothing — no repository or service ever calls `_db.VenueSymbolSpecs.Add(...)`. Confirmed empty after live runs that successfully captured+merged specs:

```sql
SELECT COUNT(*) FROM VenueSymbolSpecs;  -- 0 (in src/TradingEngine.Web/data/trading.db, post live cTrader runs)
```

`SymbolInfoRegistry.UpsertVenueSpec` only writes to an in-memory `ConcurrentDictionary`. This is process-lifetime only: a fresh app restart starts blind (falls back to `symbols.json`, loudly-warned) until the next cTrader connection re-captures specs. TRACKER.md's "engine persists VenueSymbolSpec" claim is not accurate as shipped. Not fixed this session (scope: QA + P2/P3); flagged for a follow-up phase.

---

## 3. F26 — MODERATE: `PreTradeGate.CandidateWorstCase` doesn't dispatch on `CommissionType`

`src/TradingEngine.Engine/Kernel/PreTradeGate.cs:243-246` — `Math.Abs(symbol.CommissionPerLotPerSide) * lots * 2m` treats the field as a flat per-lot dollar figure regardless of `CommissionType`. After a venue merge, `CommissionPerLotPerSide` for `UsdPerMillionUsdVolume` symbols (XAUUSD, likely most/all symbols on this account) holds a per-million-notional *rate*, not a per-lot dollar amount — the worst-case pre-trade risk estimate is wrong order-of-magnitude for exactly the symbols P1 targets. Already flagged as a TODO in the code; did not cause the F24 failure (that was `TypicalSpread`/`MaxSlPips`, a completely separate guard). Not fixed this session — needs entry price at gate time to compute notional correctly (per the existing TODO).

---

## 4. F27 — MODERATE: zero unit-test coverage on the new notional commission math

No test in `tests/` references `UsdPerMillionUsdVolume`, `ComputeEntryCommission`, or `BaseToUsd`. The `BaseToUsd`/`ComputePerSideCommission` dispatch logic was verified correct by (a) static derivation matching the worked XAUUSD/USDCAD examples in `PARITY-TRUTH.md` F4, and (b) the live run above producing sane (not 3,300×-off) commission figures. Still a coverage gap for regression safety.

---

## 5. F28 — MINOR: `SwapCalculationType` captured but never dispatched on

`VenueSymbolSpec.SwapCalculationType` is captured from the cBot and persisted in the record, but `TradeCostCalculator` always computes swap as a flat `nights × rate × lots`, regardless of whether cTrader's calc type is Pips/Points/Percentage/Absolute. Not confirmed to have caused a live divergence this session (swap deltas in the F24 repro were affected by the unmatched-trade-set issue, F29), but worth a follow-up since P1's own approach text (§P1(f)) called for calc-type-aware swap.

---

## 6. F29 — MINOR: reconcile per-trade matcher's 5-minute window is too tight for market entries

See §1 residual. `LedgerReconciler.ComputeTradeDeltas` matches trades within 5 minutes of `OpenedAtUtc`; real venue entry-latency divergence (F23) is measured in hours. On the F24-repro run, 0 of 12/14 trades matched. The per-trade delta feature (P0's stated deliverable) is currently non-functional on any market-entry compare-both. Likely self-resolves once P2 lands (limit entries fill at a named price/time), but the matcher should probably become bar-aware regardless of P2's timeline.

---

## 7. Environmental (not a code regression) — cTrader Desktop CLI report-generation crash

`CtraderE2EHarnessSmokeTests.EurUsd_H1_3Days_ProducesTrades_UsingRunAsync` and `TradeLedger_ClientOrderIdReconciliation_NoMissingTrades` both fail with "0 trades" against **current HEAD**. Reproduced **identically against the pre-P0 baseline** (`e0583e6`, isolated worktree, same test, same failure) — **confirmed not a P0/P1 regression.**

Root cause: cTrader Desktop CLI (installed build `5.7.14.51420`) throws internally after the backtest logic completes:

```
System.InvalidOperationException: Message expected
   at cTrader.Console.Infrastructure.StateMachine.Strategies.BacktestReportSavingStateStrategy.DoEnter()
System.NotImplementedException: The method or operation is not implemented.
   at cTrader.Console.Infrastructure.Configuration.ConsoleApplicationDirectories.GetOrCreateJournalLogDirectoryPerBrokerName()
```

Exit code 1. The cBot's own resilient ledger (`shamshir-report.json`) shows the trade actually executed (1 trade, net $72.5, commission −$4.40 for 0.74 lots EURUSD) — but our engine's DB never receives it (0 rows across `BacktestRuns`/`TradeResults`/`Positions`/`Orders` for that harness run) because the CLI's own crash tears down the process abnormally before/during our finalization. This affects the `ctrader-e2e` skill's harness specifically; it did **not** reproduce on the app's own `BacktestOrchestrator`-driven compare-both path used for the F24 repro above (both of those completed with `exitCode=0`).

**This is a standing environment risk, not something to fix in this repo.** See §8 for the pattern (this is the Nth time "cTrader testing struggles" have shown up across sessions/models) and proposed mitigations.

---

## 8. Recurring cTrader-testing friction — pattern across sessions/models, and proposed fixes

The owner flagged that this class of problem ("struggles with cTrader testing dealing with trades") recurs across different sessions and different models, and that AGENTS.md updates haven't stuck. Concrete instances from this session alone:

1. The CLI's own report-generation crash (§7) silently drops a fully-valid completed run's engine-side data — no error surfaces anywhere in our own logs; you only find it by cross-checking the cBot's *own* `shamshir-report.json` against our DB.
2. F24 (§1) was invisible to every credential-free gate (build/Unit/Integration/Sim-fast all stayed green) — it only manifests when a *real* cTrader connection merges a venue spec into the shared registry. The previous P0/P1 session's "gate battery" never ran a live cTrader test, so a 100%-signal-rejection bug shipped as "DONE."
3. `SymbolInfoRegistry` is a process-wide singleton with no reset between runs — a bad/stale venue spec captured by run N silently changes the behavior of run N+1, even a *tape* run on a completely different symbol/session, for the lifetime of the app process. This makes bugs like F24 nondeterministic depending on run order within a session, which is exactly the kind of thing that produces "worked yesterday, fails today" reports.

### Proposed fixes (not implemented this session — flagging for owner decision)

- **P-X: Add a mandatory live-cTrader smoke gate to the phase protocol.** AGENTS.md §"Session protocol" already says "background everything, kill by PID" etc., but nothing requires a *live* cTrader compare-both before a phase can be marked DONE when the phase touches `CTraderBrokerAdapter`, `SymbolInfoRegistry`, or anything in the venue-spec/cost path. Static+credential-free-gate-only sign-off is precisely how F24 shipped. Suggest: any phase touching those files must include one compare-both run + a `SELECT COUNT` sanity check on the affected DB table, with the RunId pasted into the ledger (matching R3 in AGENTS.md's existing "Research integrity" rules — this is the same rule, just not yet applied to code phases, only to research sessions).
- **Isolate or reset `SymbolInfoRegistry` per run** (or at minimum, per distinct process invocation used for testing) rather than a single app-lifetime singleton — this would make F24-class bugs deterministic and reproducible instead of order-dependent, and is a bounded, mechanical change (scope the registry to the DI scope already created per run, matching how other per-run state is handled).
- **Surface the CLI's own report-generation exit code/crash into our run record** instead of leaving it silent. Right now a crashed CLI leaves zero trace in `BacktestRuns`/`ErrorMessage` for the E2E harness path — cross-referencing the cBot's own `shamshir-report.json` is the only way to tell "the venue actually traded but we lost it" apart from "nothing happened." A cheap fix: if the CLI exits non-zero AND the cBot's own report.json shows trades, log a loud `CTRADER|CLI_CRASHED_BUT_TRADED` warning with the report path, instead of silently returning a 0-trade result indistinguishable from a genuinely quiet market.
- **Track the installed cTrader Desktop version against a known-good baseline.** The report-generation crash may be specific to build `5.7.14.51420` (vs whatever version the `ctrader-e2e` skill's "9/10 pass" baseline was last validated against) — auto-updates are outside repo control, but `research doctor` (already in this plan, R0.2b) could at least print the installed cTrader version so a sudden E2E regression is immediately attributable to "cTrader auto-updated" rather than re-litigated as a code bug every session.

None of these are implemented in this session (out of the QA+P2/P3 scope as given); recorded here per the owner's explicit request to log the pattern and propose solutions regardless of the session's primary deliverable.
