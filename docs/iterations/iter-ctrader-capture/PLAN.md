# iter-ctrader-capture — Run a backtest FROM the cTrader desktop UI, captured live + after

**Written:** 2026-07-01 (for the OpenCode/DeepSeek agent). **Branch base:** `iter/ux-unify`.
**Owner decisions locked (see the person, 2026-07-01):**
- **Capture model = "our engine, launched from desktop."** The user adds our `TradingEngineCBot` to a
  chart in the cTrader **desktop** app and runs a backtest there. The cBot connects to *our* engine over
  NetMQ exactly as it does headless; **our strategies/risk decide the trades.** We are NOT building a
  passive recorder of a cTrader-owned session.
- **Session type = backtest first.** Capture desktop *backtest* runs (defined start/end, finalize on the
  cBot's STOP). Live/demo capture (continuous, no defined end) is a later iteration — do NOT build it here.

---

## The one-paragraph summary

Today `BacktestOrchestrator.RunEngineNetMqAsync` (`src/TradingEngine.Web/Services/BacktestOrchestrator.cs:929`)
**allocates ports → starts the engine host (which binds the ROUTER + connects the SUB via
`NetMqMessageTransport` wrapped in `CTraderBrokerAdapter`) → then launches `ctrader-cli` pointed at those
ports.** Everything downstream of a `RunId` — the `RunProgressBroadcaster`, the `BacktestJournal`, the
StepRecord sink, equity persistence, `VenueSessions`, the summary finalize, the live Monitor, and the
after-report — is keyed by that `RunId` and is **fully reusable**. This feature keeps all of that and
replaces only the front of the pipeline: instead of *we* launching cli with known ports, the engine enters
a **listen mode** on known ports and **waits for an inbound cBot** started by the user in the desktop app;
on the first session handshake it **mints a `RunId`**, creates the run row, and runs the identical kernel
loop. The desktop app becomes just a different *launcher* for the same NetMQ session.

## What is reused verbatim (do NOT rebuild)

| Concern | Reused component |
|---|---|
| Engine host + kernel loop | `EngineHostFactory.Create(EngineHostOptions{…})` (`BacktestOrchestrator.cs:999`) |
| Transport | `NetMqMessageTransport(dataEndpoint [SUB connect], commandEndpoint [ROUTER bind])` (`:1005`) |
| Venue adapter + status→journal/VenueSessions | `CTraderBrokerAdapter` + `OnStatusChange` (`:1009`) |
| Live push | `RunProgressBroadcaster` (`_broadcaster`), SignalR Monitor |
| Journal / decisions | `BacktestJournal` + StepRecord sink → Monitor + `/runs/{id}/bar-decisions` |
| Equity + summary finalize | equity flush + settled-ledger summary recompute (iter-ux-unify P1) |
| Session history | `VenueSessions` table (also iter-ux-unify P7) |
| After-view | existing run report / gallery |

## Topology (measured from the current code — build on this, don't re-derive)

- **DataPort:** cBot **binds** a `PublisherSocket` (`TradingEngineCBot.cs:94-96`); engine **connects** its
  `SubscriberSocket` (`NetMqMessageTransport.cs:62`). Bars/account/diag flow cBot → engine.
- **CommandPort:** engine **binds** a `RouterSocket` (`NetMqMessageTransport.cs:67`); cBot **connects** a
  `DealerSocket` to `tcp://127.0.0.1:{CommandPort}` (`TradingEngineCBot.cs:98-99`). Commands/replies flow
  engine ↔ cBot.
- Ports are cBot **parameters** (`DataPort`/`CommandPort`, default `15555`/`15556`). Headless, the
  orchestrator injects allocated ports via CLI args; **in the desktop UI the user types them in the cBot
  parameter panel** — so the engine's listen ports and the cBot's params MUST match.
- The cBot is **bar-driven**: one synchronous NetMQ round-trip per closed bar (`OnBarClosed`,
  `TradingEngineCBot.cs:193`), NOT per tick. `OnTick` publishes nothing unless `Verbose=true`.

---

## Phases (failing-test-first + machine-checkable gates where possible; P0 is owner-in-the-loop)

### P0 — Spike: prove the desktop app can host the cBot over NetMQ  *(RISK GATE — do first)*
**Why first:** the whole approach dies if the cTrader **desktop sandbox blocks socket bind/connect** or
won't grant the cBot `FullAccess`. Confirm before building anything.
- Manual procedure (owner runs, needs the desktop app): start a throwaway engine listener bound on
  `127.0.0.1:15555/15556` (a tiny console or a temporary listen endpoint from P2), then in the desktop app
  add `TradingEngineCBot` to an EURUSD H1 chart, grant Full Access, set `DataPort=15555 CommandPort=15556`,
  and run a **backtest**.
- **Gate (manual):** engine logs `NETMQ_CONNECTED` + receives `bar` frames; cBot logs `CBOT|PUB_BOUND` +
  round-trips. Capture the exact desktop steps into `docs/iterations/iter-ctrader-capture/DESKTOP-SETUP.md`.
- **If bind/connect is blocked:** STOP and escalate — the design changes (e.g. cBot writes a file/ledger the
  engine tails). Do not proceed to P1+ until P0 passes.

### P1 — cBot session handshake (requires `.algo` rebuild)
The desktop app does not tell our engine what/when it started; the cBot's `Print` never reaches us. Add
explicit NetMQ frames the engine can consume:
- On `OnStart`: publish a **`session`** frame on the PUB carrying `{ v, symbol, period, startingCapital,
  mode ("backtest"|"live" from `RunningMode`/`IsBacktesting`), startUtc, endUtc(if backtest), cbotVersion }`.
- On `OnStop`: publish a **`session-end`** frame `{ reason, barEvents, ticks }` before sockets close.
- Keep **backward compatible**: headless path still works (it already pre-creates the RunId; the `session`
  frame is additive and can be ignored there, or used to enrich).
- **Gate:** extend the existing FakeTransport cTrader **contract test** to assert the `session`/`session-end`
  frames serialize/round-trip; rebuild `src.algo`; determinism gate stays green (this path is not a golden
  replay but must not regress the byte-identical journal).

### P2 — Engine listen mode (`CTraderListenService`)
A new service that binds the transport on configured **listen ports** and waits, WITHOUT launching cli:
- Reuse `EngineHostFactory.Create` + `NetMqMessageTransport` + `CTraderBrokerAdapter` (copy the wiring from
  `RunEngineNetMqAsync`, minus the cli launch and minus pre-known symbol/range).
- On first inbound **`session`** frame → mint a `RunId`, create the `BacktestRuns` row with `Mode` from the
  handshake + the config chosen in P3, wire `_broadcaster`/`_journal`/equity/StepRecord sink, start the
  kernel loop. Bars already flowing are processed identically to headless.
- **Gate:** an integration test drives a `session` frame + a few `bar` frames through a `FakeTransport`/loopback
  into the listen service and asserts a run row is created, StepRecords persist, and the broadcaster fires.

### P3 — Web control + RunId minting + config selection
- `POST /api/ctrader/listen/start` — body: chosen strategy config / risk profile (reuse
  `EffectiveConfigResolver`); response: the **ports to type into the desktop cBot** + a full-access reminder.
  Binds the listener (P2). Persist an "awaiting-session" placeholder run so the UI has something to show.
- `POST /api/ctrader/listen/stop` — unbinds the listener.
- On `session` frame, promote the placeholder to a real run (mint `RunId`, stamp config/mode).
- **Gate:** API test — start listen → returns ports; simulate a session → run appears via the existing
  `/api/runs` list; stop → listener released.

### P4 — Finalize on session-end (reuse + fix the known cTrader-path bugs)
- On `session-end` (or `CBOT|STOP`), finalize: recompute summary from the **settled ledger** (reuse the
  iter-ux-unify P1 trade-count-finalize fix — cTrader summaries undercount because stats run before late
  settlement), **write wall-clock `CompletedAtUtc`** (the cTrader path currently persists sim-time-in-the-past
  — see iter-ux-unify DB findings / iter-redesign-ctrader P6), flush equity, broadcast `Done`, write a
  `VenueSessions` STOP. Then reset the listener to await the next session.
- **Gate:** integration test — after a simulated `session-end`, the run's `Status=Completed`,
  `CompletedAtUtc` is wall-clock, `TotalTrades` == distinct persisted ledger rows.

### P5 — UI: "cTrader Session" page (live + after reuse)
- A page with Start/Stop-listening, the expected ports, a Full-Access reminder, and live status. When a
  session mints a run, deep-link to the **existing** live Monitor (no new live UI). After completion, reuse
  the existing report / gallery. Fold into the iter-ux-unify **P7 cTrader session-history** page rather than
  a parallel one.
- **Gate:** component/e2e smoke — start listen renders ports; a minted run links to the Monitor.

### P6 — Docs, owner smoke, hardening
- `DESKTOP-SETUP.md` (from P0), update the `ctrader-e2e` skill, document the **single-session** limitation
  and **localhost-only** constraint. Owner smoke checklist. Note the JSON-verdict crash fix (committed
  `4f4a41b`) is a prerequisite for the after-view `/bar-decisions`.

---

## Owner decisions to lock before/while building (Q-block)

- **Q1 — Listen ports.** Fixed well-known `15555/15556` (match cBot defaults, simplest) **[recommend]** vs.
  configurable per `listen/start`. *(Affects P2/P3.)*
- **Q2 — Config source.** Web page picks the strategy config/risk profile before the desktop run **[recommend
  v1]** vs. a cBot parameter carrying a config id (extra desktop friction, needs `.algo` param). *(P1/P3.)*
- **Q3 — Same machine only?** Desktop + engine on one box, `127.0.0.1` **[recommend v1]** vs. remote desktop
  (needs real host/IP, bind on `0.0.0.0`, firewall — security review). *(P0/P2.)*
- **Q4 — Concurrency.** Single concurrent session **[recommend v1]** vs. multi-session (port range +
  discovery, more moving parts). *(P2.)*
- **Q5 — Listener lifecycle.** On-demand via the page **[recommend]** vs. always-listening background service
  that auto-starts with the Web app. *(P2/P3.)*

## Risks / prerequisites (read before estimating)

1. **P0 is a genuine risk gate** — if the desktop sandbox blocks NetMQ bind/connect or full-access, the
   architecture changes fundamentally. Nothing downstream is worth building until P0 passes.
2. **`.algo` rebuild** is required (P1 cBot change). The redesign-ctrader work already rebuilds it; follow
   that build path. The rebuilt cBot must stay compatible with the headless orchestrator path.
3. **Not a deterministic replay.** Desktop-driven runs are user-driven, so they are exempt from the golden
   determinism gate — but they MUST persist correctly and must not regress the byte-identical journal for the
   replay path.
4. **Reuse the cTrader-path fixes**, don't reinvent: settled-ledger summary recompute + wall-clock
   `CompletedAtUtc` (both are known-broken on the cTrader path per the iter-ux-unify DB findings).

## Definition of done
Owner opens the cTrader desktop app, starts the Web "cTrader Session" listener (picks a config, sees the
ports), adds `TradingEngineCBot` to a chart with those ports + Full Access, runs a backtest, and watches it
**live in the Shamshir Monitor**; on completion the run appears in the run list with a correct summary,
equity curve, trades, and `/bar-decisions` — identical to a headless run, just launched from the desktop UI.
