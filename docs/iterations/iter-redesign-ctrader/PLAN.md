# iter-redesign-ctrader — Venue-Owned Exits, Leak-Proof Book, Real Live Monitor

**Author:** Claude (Opus) — plan, 2026-06-30
**Branch base:** `iter/redesign` (the DeepSeek work — replay path is fixed; cTrader is not)
**Audience:** the OpenCode/DeepSeek implementation agent. Phased, failing-test-first, gated.
**Reads first:** `docs/iterations/iter-redesign/VERIFICATION.md` (the evidence this plan is built on).

> **Why this exists:** the previous iteration fixed and verified the **replay** engine, then claimed
> all symptoms fixed. The owner runs **cTrader**, which was never executed. On cTrader the headline
> bug is unchanged (open-book leak → `openRisk` grew $3,221 → $60,524, 3-month produced *fewer*
> trades than 1-month), every exit is mislabeled `FORCE`, "Raw" still enforced budget/exposure, and
> the equity/live chart is empty for **every** run. This iteration makes the **cTrader path** correct
> and the live monitor real — and puts cTrader in the verification loop so it can't be skipped again.

---

## 0. Locked architectural decision (owner, 2026-06-30)

**THE VENUE OWNS EXIT EXECUTION.** The engine proposes *entries* and *stop modifications* (add-ons);
the **venue executes all exits** (SL/TP/trailing-stop hits) and reports each close back **with its
reason**; the engine **reconciles its book to the venue** and never independently closes a position
it thinks hit a stop. This kills three bugs at once: the double stop-management conflict (all-`FORCE`),
the open-book leak (the venue is the authority on what's open), and the replay/cTrader exit divergence.

Consequence: the engine's current `EngineReducer.DetectSlTpExit → CloseOpenPosition` path
(`EngineReducer.cs:261`) must **stop running on venue-managed venues**, and the exit-execution
responsibility moves into the venue adapter layer for *both* venues (see D1).

---

## 1. Verified findings this plan must fix (from VERIFICATION.md)

| # | Finding (evidence) | Fix phase |
|---|---|---|
| V1 | Open-book leak STILL on cTrader: `openRisk` $3,221→$60,524, 3mo(6)<1mo(11). Purge only removes *terminal* positions; cTrader positions never reach terminal → accumulate. | P2 |
| V2 | All cTrader exits `FORCE`: kernel detects SL/TP **and** cTrader owns real stops; venue close reason is never threaded. | P1 |
| V3 | "Raw" run still fired Budget(cap 2,500)+Exposure(50%): limiter toggles live on the **prop-firm ruleset**, owner picked a **risk profile** named raw; `EffectiveConfigJson` still shows `standard`. Two resolve paths. | P3 |
| V4 | `EquitySnapshots` table is **empty for every run** (0 rows DB-wide) — equity persistence (`EquitySnapshotFlush`) is only called from the dead `EngineRunner` path, never from `KernelBacktestLoop`. Chart endpoint reads the empty table. | P4 |
| V5 | Live timeline doesn't move; "completed" while `ctrader-cli` still running; `CompletedAtUtc` stamped with a trade's sim-time. Finalize on bar-stream end, not venue settlement; orphan CLI procs. | P5, P6 |
| V6 | cTrader was never run by the agent (only the skipped `RequiresCTrader` test); replay was assumed-equivalent. | P0 |

---

## 2. Design: the venue-owned exit model

```
ENTRY:   strategy → TradeIntent(entry, SL, TP) → PreTradeGate (size+guards) → SubmitOrder(SL,TP)
                                                                                   │
                                            venue places a REAL order carrying SL/TP (cTrader)
ADD-ONS: per bar, engine may emit ModifyStopLoss (breakeven/trailing) → venue moves the broker stop
EXIT:    venue (cTrader) triggers SL/TP/trail → venue reports Close{positionId, price, REASON}
                                                                                   │
RECONCILE: each bar + at end, engine compares its live book to the venue's open set:
           - venue closed a position the engine still holds → apply the venue close (with reason)
           - engine holds a position the venue never opened/rejected → force-resolve + journal
           ⇒ openRisk/position-count are ALWAYS computed from the venue-confirmed live set
```

**Replay** has no real broker, so the **replay adapter** takes on the same role: it detects SL/TP
against each bar's range and emits a `Close{reason}` exactly like cTrader does (D1 = unify). The
engine's `HandleBarClosed` no longer does exit detection for any venue — it only updates
high/low-water + applies add-on stop moves + reconciles.

---

## 3. Phase 0 — cTrader acceptance oracle + repro (do first; this is what was skipped)

The agent cannot run cTrader. So acceptance is split:

- **CI proxy (replay, automated):** the §-engine tests must stay green on replay.
- **cTrader acceptance (owner runs one backtest, agent asserts via DB oracle):** a script
  `scripts/verify-ctrader-run.ps1 <runId>` that runs the VERIFICATION.md §9 queries and **fails**
  if any of:
  1. `openRisk` in any rejection exceeds `MaxConcurrentPositions × perTradeWorstCase` (no unbounded
     growth);
  2. all `TradeResults.ExitReason` are `FORCE` (exit reasons must include real `SL`/`TP`);
  3. a 3-month run produces fewer trades than its trailing-1-month sub-window;
  4. `BacktestRuns.ExitCode != 0` or `CompletedAtUtc == 0001-01-01`;
  5. `EquitySnapshots` for the run is empty.
- **Repro tests (replay, fail-now):** extend `EngineTruthReproTests` with a venue-managed-exit
  fixture (a fake `OwnsExitExecution=true` adapter) proving the engine does NOT emit
  `CloseOpenPosition` and the book reconciles to the venue.

**Gate P0:** `verify-ctrader-run.ps1` exists and is documented in the handover as the owner's
one-command check; the new repro tests exist and fail for the documented reasons.

---

## 4. Phase 1 — Venue owns exits; thread the real close reason (fixes V2)

### P1.1 — Capability flag
- Add `bool OwnsExitExecution { get; }` (or `ExitMode { VenueManaged | EngineSimulated }`) to
  `IBrokerAdapter`. `CTraderBrokerAdapter` ⇒ VenueManaged. `BacktestReplayAdapter` ⇒ VenueManaged too
  (D1; it simulates the venue). Keep an `EngineSimulated` option only if D1 says so.

### P1.2 — Engine stops closing positions it doesn't own
- In `EngineReducer.HandleBarClosed`, **only run `DetectSlTpExit`/`CloseOpenPosition` when the venue
  is `EngineSimulated`.** For VenueManaged venues the engine emits no exit close — it waits for the
  venue's close event. (Add-on `ModifyStopLoss` still flows to the venue.)

### P1.3 — cBot reports the close reason over NetMQ
- The cBot (`TradingEngineCBot`) sets the broker SL/TP on entry and, on cTrader's `Positions.Closed`,
  sends a close frame carrying cTrader's reason (`StopLoss`/`TakeProfit`/`StopOut`/`Manual`) keyed by
  `clientOrderId`. Extend the NetMQ close message + `CTraderBrokerAdapter` → `ExecutionEvent.Reason`.
- `KernelFeedback.FromExecution` maps the venue reason onto the close so
  `PositionLifecycle.HandleOpenFilled` records `SL`/`TP`/… instead of `?? "FORCE"`. `FORCE` is then
  reserved for genuine force-closes (breach/EOD flatten).

### P1.4 — Replay adapter emits reasoned closes (D1)
- Move SL/TP-against-bar-range detection into `BacktestReplayAdapter` (it has the OHLC); on a hit it
  writes a `Close{reason}` execution at the stop/target price — same contract as cTrader. Remove the
  now-dead engine-side detection for replay.

**Gate P1:** a replay run's `TradeResults.ExitReason` shows real `SL`/`TP`/`PARTIAL` (already true)
AND a `VenueManaged` fake-adapter test shows the engine emits **no** `CloseOpenPosition`; golden tape
reviewed/re-baselined with a written diff if exit ownership moves (D4 from the prior plan applies).

---

## 5. Phase 2 — Open-book reconciliation: make the leak impossible (fixes V1)

### P2.1 — Reconcile to the venue every bar
- Each venue exposes its authoritative open set: `IBrokerAdapter.GetOpenPositionIds()` (cTrader: the
  cBot's `clientOrderId` ledger it already keeps for reconciliation; replay: `_openTrades.Keys`).
- After draining venue feedback each bar, the engine reconciles: for every kernel position **not** in
  the venue's open set and not already terminal → emit a synthetic close + journal
  `RECONCILED_CLOSED` (use the venue's last price). For every position stuck `Submitted` past the
  entry-fill window → force-resolve `STUCK_SUBMITTED`.

### P2.2 — Gate sums only the reconciled live set
- `openRisk`/`Positions.Count` in `PreTradeGate` are computed from the reconciled live book.
  Add `EngineInvariants.OpenRiskBounded(state, profile)`: `openRisk ≤ MaxConcurrentPositions ×
  worstCasePerTrade` — assert in tests; a breach means the book leaked (the $60k symptom).
- Investigate and fix *why* `openRisk` grew after the last fill (V1): the projected-open-positions
  list fed to the gate is accumulating beyond real fills — find the producer (the
  `IReadOnlyList<ProjectedPosition> openPositions` arg to `PreTradeGate.Evaluate`) and bind it to the
  reconciled set.

**Gate P2:** the `OpenBookDoesNotLeak` repro passes on a **VenueManaged** fixture; `openRisk` never
exceeds the bound; a seeded 3-month run produces ≥ its 1-month-suffix trade count; the owner's
`verify-ctrader-run.ps1` check #1/#3 pass on a real cTrader run.

---

## 6. Phase 3 — One config, one toggle source; "Raw" is provably raw (fixes V3)

### P3.1 — Collapse the two resolution paths
- Merge `ResolveEffectiveConfigJsonAsync` and `BuildLoadedConfigFromDbAsync`
  (`BacktestOrchestrator.cs:350` vs `:951`) into **one** resolver that produces the exact object the
  engine consumes; persist **that** as `EffectiveConfigJson`. The stored audit must equal what ran.

### P3.2 — A single "Raw" that disables everything
- Selecting "Raw" must set **both** the raw risk profile **and** the raw prop-firm `ProtectionToggles`
  (`Budget/Exposure/MaxPositions/DailyDd/MaxDd/Governor`-Enabled = false). Either bind the risk-profile
  `raw` to the prop-firm `raw` ruleset, or move all limiter toggles onto one resolved `ConstraintSet`
  the run selects directly.
- The strategy `riskProfileId` override must actually apply (today `EffectiveConfigJson` still shows
  `standard` for a raw run — fix the `profileIsKnown` check to consult the DB profile set).

### P3.3 — Show the resolved guards + numeric rejections
- Persist the resolved `ConstraintSet` (every toggle + cap) on the run and show it in the report
  header ("Guards in force: …"). The P2.3 numeric rejection traces stay.

**Gate P3:** a Raw cTrader run yields **zero** limiter rejections (oracle check #2-adjacent) and its
`EffectiveConfigJson`/resolved-ConstraintSet shows all limiter toggles off + `riskProfileId:"raw"`;
a test asserts stored-config == engine-config.

---

## 7. Phase 4 — Persist the equity series on the kernel path (fixes V4 — "equity never shows")

### P4.1 — Wire equity persistence into `KernelBacktestLoop`
- Root cause: `EquitySnapshotFlush.FlushAsync` is only called from `EngineRunner.cs:170` (the dead
  imperative path); `KernelBacktestLoop` only flushes the journal. So `EquitySnapshots` is **empty**
  for every run.
- In the kernel loop's `_onBarProcessed` hook, buffer an `AccountSnapshot` (sim-time, equity,
  balance, peak, daily/max DD from the authoritative `EngineState`), and flush the buffer to
  `EquitySnapshots` in one batched write at finalization (the existing `EquitySnapshotFlush`/
  `EquityPersistenceHandler` mechanism — just call it from the kernel path).
- All timestamps = **sim-time** (bar `OpenTimeUtc`); one ordered series per run.

### P4.2 — Drive both charts from the one series
- `GET /api/runs/{id}/equity` (already reads `EquitySnapshots`) now returns data. The post-run equity
  chart and the live monitor chart both read this series. Remove any journal-`EquityObserved`
  fallback (the wall-clock-polluted source).

**Gate P4:** every finished run (replay AND cTrader) has a non-empty `EquitySnapshots` series, strictly
sim-time-ordered; `GET /{id}/equity` returns it; oracle check #5 passes.

---

## 8. Phase 5 — A live monitor that actually moves, and is verifiable (fixes V5 live half)

### P5.1 — Progress frames flow during a cTrader run
- Verify the cTrader `progressCallback` path (`BacktestOrchestrator.cs:924`) increments bar count,
  sim-time, equity, open positions per `BAR`/`EXEC` frame, and that `barsTotal` is known (so percent
  and the timeline advance — today it may be 0/unknown for cTrader). Feed equity from the same P4
  snapshot stream so the live chart grows during the run.
- Keep snapshot-on-join (`RunHub.JoinRun`, already added) so a mid-run page load isn't blank — and
  actually verify it (the agent never ran it).

### P5.2 — Self-verify (can't run cTrader headless → two-tier)
- **Automated (replay):** a Playwright test that starts a replay run and asserts the monitor's bar
  counter advances, the equity chart gains points, and the terminal frame flips to `completed`. This
  is the regression guard the owner stops having to eyeball.
- **Owner cTrader smoke:** a short checklist in the handover (start a cTrader run → watch counter +
  equity move → run `verify-ctrader-run.ps1`). The iteration is **not done** until the owner confirms
  one live cTrader run.

**Gate P5:** the replay Playwright live test passes in CI; the handover contains the owner cTrader
smoke checklist and does **not** claim cTrader-verified without it.

---

## 9. Phase 6 — Finalize on venue settlement; reap orphans (fixes V5 completion half)

- Mark a cTrader run `completed` only after: bar-stream end **and** the cBot ledger received **and**
  P2 reconciliation done (no kernel positions still open that the venue closed). Stamp
  `CompletedAtUtc` with **wall-clock** now, not a trade's sim-time.
- Reap orphaned `ctrader-cli` child processes on run end (kill the process tree, not just the parent)
  so "completed" matches Task Manager. (See memory `test-harness-gotchas` re: orphans.)
- Keep the `ExitCode=-1` startup reconcile (P4 of prior iter) as the safety net.

**Gate P6:** after a cTrader run completes, no `ctrader-cli` process remains; `CompletedAtUtc` is
wall-clock; the kernel's closed-trade count matches the cBot ledger (reconciliation report logged).

---

## 10. Owner decision block

- **D1 — Replay exit ownership.** Unify (replay adapter also owns exits, §4 P1.4) so both venues use
  the venue-owned model and the engine never detects exits? **Recommended: yes** (cleanest; replay
  stays a faithful proxy). Alternative: keep replay engine-simulated and only cTrader venue-managed
  (more divergence, two code paths).
- **D2 — Stopless raw positions.** With venue-owned exits, a raw strategy that emits no SL leaves an
  **unbounded-risk** open position at the broker. Options: (a) require a hard EOD-flatten / max-hold
  even in raw (recommended), (b) allow truly stopless with a loud UI warning. Pick.
- **D3 — Source of truth for CI.** Replay = automated CI oracle; cTrader = the real target gated by
  the owner smoke + `verify-ctrader-run.ps1`. Confirm this division (it's how we keep cTrader in the
  loop without the agent being able to run it).

---

## 11. Sequencing

```
P0 (oracle+repro) → P1 (venue owns exits) → P2 (reconcile/leak) → P3 (config/guards) → P4 (equity persist) → P5 (live) → P6 (finalize)
```
P1→P2 are the correctness core (the owner's "numbers make no sense"). P4 is small and unblocks the
equity chart immediately (could be done early as a quick win). P3 is independent after P1. P5/P6 need
P1/P2/P4 to show truthful data.

## 12. Key file index

| Area | Files |
|---|---|
| Venue-owned exits | `src/TradingEngine.Domain/Interfaces/IBrokerAdapter.cs`, `src/TradingEngine.Engine/EngineReducer.cs` (gate DetectSlTpExit on ExitMode), `src/TradingEngine.Engine/PositionLifecycle.cs` (reason, not FORCE) |
| cBot reason wire | `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs`, `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs`, `src/TradingEngine.Host/KernelFeedback.cs` |
| Replay reasoned exits | `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` |
| Reconciliation/leak | `src/TradingEngine.Engine/EngineReducer.cs`, `EngineInvariants.cs`, `src/TradingEngine.Engine/Kernel/PreTradeGate.cs` (open-position source) |
| Config/guards | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` (merge the two resolve paths), `src/TradingEngine.Services/EffectiveConfigResolver.cs`, `config/prop-firms/raw.json`, `config/risk-profiles/raw.json` |
| Equity persist | `src/TradingEngine.Host/KernelBacktestLoop.cs` (call EquitySnapshotFlush), `EquitySnapshotFlush.cs`, `src/TradingEngine.Infrastructure/Persistence/EquityPersistenceHandler.cs`, `src/TradingEngine.Web/Services/RunQueryService.cs` |
| Live + finalize | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` (progress/finalize/orphan-reap), `src/TradingEngine.Web/Hubs/RunHub.cs`, `web-ui/.../runs/run-monitor/*` |
| Oracle/tests | `scripts/verify-ctrader-run.ps1` (new), `tests/TradingEngine.Tests.Simulation/EngineTruth/*`, `web-ui/tests/e2e/*` |

## 13. Non-negotiable: cTrader in the loop

The single most important process change: **this iteration is not "done" on green replay tests.** It
is done when the owner runs **one cTrader backtest** and `verify-ctrader-run.ps1` passes all five
checks. The handover must say so explicitly and must not repeat "all symptoms fixed" for the cTrader
path without that run.
