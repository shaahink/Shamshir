# Shamshir — The System in Plain Words

**For:** anyone (owner/agent) who wants a portable mental model to reason about + ask questions against.
**Written:** 2026-06-20 (post iter-36 kernel cutover) · **Branch:** `iter/36-kernel-cutover`
**Companion to:** the technical reference set (`SYSTEM-REFERENCE.md`, `CODE-MAP.md`, `BACKTEST-ARCHITECTURE.md`,
`TEST-ARCHITECTURE.md`, `TEST-AUDIT.md`) — this doc is the plain-English overview, not the spec.

---

## What it is (one breath)
A **prop-firm algorithmic trading engine** (.NET 10 / C# 13). You give it market bars + a strategy config +
prop-firm risk rules; it decides trades, sizes them, enforces the rules, executes against a venue, and
records *everything* in one auditable log. It runs the **same way for backtest and live** — only the data
source differs.

## The one idea to remember: the kernel funnel
Everything flows through a single pump:

> **an event happens → a pure function decides → it returns (new state, a list of effects) → a thin shell
> performs the effects → the venue's response comes back as new events.**

- The **decision core is pure**: no clocks, no randomness, no database — same inputs always give the same
  outputs. (Pattern names: *functional core / imperative shell*, and the *Elm architecture* — a reducer
  `(state, event) → (state', effects)`. If you've seen Redux/`useReducer`, it's that, plus the effects.)
- **Effects are descriptions, not actions**: the core says "submit this order"; a separate executor is the
  *only* thing that touches the outside world.
- The **state is one object** (`EngineState`) — the single source of truth for positions, drawdown,
  protection, governor, account. No second copy anywhere (a second copy is what killed the old design).

Because the core is pure and events flow in one fixed order, the engine is **deterministic**: re-run the
same data + config + seed and you get a **byte-identical** result. That single property buys replay, audit,
and trustworthy tests.

## Life of a bar (how a trade actually happens)
1. A **bar** arrives (from the database in backtest, or from cTrader live).
2. The **evaluator** (`BarEvaluator`) runs indicators → regime → strategies → signal gate, and proposes
   candidate orders.
3. The **pure kernel gate** (`PreTradeGate` + `KernelSizing`) sizes each candidate (risk %, drawdown
   scaling, lot rounding), checks the prop-firm rules (daily/max/weekly/monthly drawdown, exposure, budget,
   news/weekend), and either accepts (→ submit order) or rejects (→ records why).
4. The **executor** (`EffectExecutor`) sends accepted orders to the venue; the venue's fills/closes come
   back as events and re-enter the loop (`KernelFeedback`).
5. End of bar: **trailing/breakeven** stops update, an **equity/drawdown snapshot** is written, and one
   **journal record** (`StepRecord`) is appended per event.

## The modules (and what's real vs placeholder)
- **Alpha (strategies) + regime detector** — *placeholder / scaffolding* to exercise the engine. The real
  edge is future work. **← key caveat: don't read backtest "performance" as validated alpha yet.**
- **Risk engine** — *the crown jewel.* Prop-firm rules, position sizing, drawdown scaling, a "governor"
  (cooling-off / profit-lock / stop), exposure & budget caps. Risk is a first-class part of the kernel.
- **Cost model** — real commission/swap + spread; stops/targets fill at the stop price, not the bar close.
- **Venues (ports/adapters)** — `BacktestReplayAdapter` (replays stored bars, no credentials), a synthetic
  simulator, and **cTrader** (live + a CLI backtester over NetMQ that runs the real compiled cBot).
- **The journal** — *one* lossless, append-only event log (the "StepRecord" stream). The single source for
  the report, the NDJSON download, and the live monitor. It never silently drops the authoritative record.
- **Research layer (`TradingEngine.Experiments`)** — walk-forward splits, out-of-sample test-fold scoring,
  a Monte-Carlo "probability of passing the prop-firm challenge," expectancy in R-multiples, and a composite
  robustness score across folds.
- **Web** — an Angular single-page app served by the same .NET process; pages for runs, monitor, report,
  trades, strategies, risk profiles, governor, settings.

## The properties that make it unusual (its strengths)
- **Determinism + replay** — same data/config/seed → identical journal.
- **One engine for live & backtest** — live inherits every correctness + audit property of the backtest.
- **One lossless journal** — the audit trail can't drop the authoritative record (high-volume telemetry
  like bars/equity *can* drop under extreme load — a deliberate trade-off).
- **Risk-as-kernel** — prop-firm rules are enforced in the pure core, not as an afterthought.
- **No look-ahead by construction** — strategies see only closed bars; a trailed stop only takes effect the
  next bar.
- **Reproduce / "duplicate with a different strategy"** — every run is content-addressed as
  `(Dataset, ConfigSet, Seed)`; you can re-run a past backtest over the same data with a changed
  strategy/profile, with lineage back to the parent (`POST /api/runs/{id}/duplicate`).

## The honest gaps (so you can ask sharp questions)
- **Real alpha & regime** are not built yet (placeholders).
- **Overfitting rigor** — the research loop sweeps up to 64 variants and picks the best; there's no
  statistic (Deflated Sharpe / Probability-of-Backtest-Overfitting) to prove the winner isn't luck. Latent
  until real strategies exist, but the machinery should be ready before real alpha is tuned.
- **Portfolio-level capital allocation** — sizing is per-trade risk %, not correlation-aware allocation
  across strategies/symbols.
- **Frontend/report cutover tail** — the report's funnel still reads now-empty old tables (scheduled for
  the next frontend iteration, iter-37 F2/F4); the unified journal/report on the new data is the remaining
  UI work.
- **Dataset identity** is a hash of the data *window* (symbols/range), not the literal bars — "same data"
  is by spec, not byte content.

## A question bank you can carry
- "Walk the path of a single trade through the kernel for [strategy X]."
- "What exactly happens when daily drawdown is breached mid-run?"
- "How does live differ from backtest, concretely?"
- "Show me where sizing / the prop-firm gate / trailing lives and how to change it."
- "If I change a risk profile and re-run, what's guaranteed identical and what isn't?"
- "What would it take to add a real strategy / real regime detector safely?"
- "How do I trust a backtest result — what could be fooling me?"
- "What breaks if cTrader isn't available, and what still works?"

---

## Pattern glossary (the vocabulary)
- **Functional core / imperative shell** — pure decision core wrapped by a thin I/O shell.
- **Event sourcing** — state is a fold over an immutable event log; the journal is the source of truth.
- **Reducer** — `(state, event) → (state', effects)` (Redux/Elm `update`).
- **Effects as data** — the core *describes* I/O (`SubmitOrder`, `ModifyStopLoss`); the executor performs it.
- **Ports & adapters (hexagonal)** — `IBrokerAdapter`/`IEffectExecutor`/`IEventTape` are ports; venues are
  adapters; the pure core is transport-ignorant.
- **Finite state machines** — `PositionLifecycle`, `GovernorMachine`, `ProtectionState`.
- **Determinism / replay** — pure fold + seeded nondeterminism ⇒ reproducible, time-travelable runs.
