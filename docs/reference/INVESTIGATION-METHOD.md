# How to investigate a venue-parity defect

**Status:** normative. Written 2026-07-12, derived from the session that found F38/F40 — after four
previous sessions, all with green gates, had failed to find them.

This is not a style guide. Every rule below is here because breaking it cost this project real time,
and because following it produced a root cause in a single session. Read it before you open a parity
investigation, and cite it in your plan.

## 0. The one-sentence version

**Make the venue tell you what it did. Do not infer what it did from what happened afterwards.**

Almost every wasted session in this project's history was spent reasoning backwards from an outcome
(a trade's price, a PnL number) toward a cause, when the cause could have been *measured directly* by
asking the venue to record what it saw at the moment it acted.

---

## 1. The failure pattern that keeps repeating

Four sessions of parity work (P0–P3) each found a real bug, fixed it, and declared parity closed.
None of them found the bug that was actually causing the divergence. The shape was identical each time:

1. Observe an output divergence (PnL differs, trade counts differ).
2. Form a plausible story about the cause.
3. Find *a* real defect consistent with the story.
4. Fix it. Gates go green. Declare victory.
5. The divergence is still there next session.

Step 3 is the trap. **A codebase this size will always yield a real defect consistent with almost any
story.** Finding one proves nothing about your hypothesis. The story must be *tested*, not
*corroborated*.

### The concrete cost

- **F33** (every cTrader stop at the wrong distance) survived four sessions because every parity test
  cell was XAUUSD, the one symbol where the bug produces a plausible-looking number.
- **F38** (every cTrader bar delivered one bar stale) survived because its *documented* diagnosis —
  "the LimitOffset is never applied" — was a misreading of a journal field, and nobody re-derived it.
- **F40** (rested orders crashed the exec parser) sat in a `LogWarning` that nobody read.

---

## 2. Rules

### R1 — Distrust the previous session's diagnosis before you distrust the code

The handoff said: *"F38: the journal shows `LimitPrice == SignalPriceMid`, therefore the configured
5.25-pip LimitOffset is never applied."*

It took ten minutes to disprove. `BarEvaluator.cs` passes `entryPrice` (which *is* the limit price) into
the `SignalPriceMid` field of the `OrderProposed` event. Those two fields are equal **by construction**
for every limit order. The evidence was an artifact of a mislabelled field, not a finding.

Had I "fixed the LimitOffset", I would have broken working code and left the real bug in place.

> **Rule:** a handoff's *facts* (run IDs, numbers, file:line) are inputs. A handoff's *conclusions* are
> hypotheses. Re-derive the conclusion from the facts before you act on it. If you can't re-derive it,
> that is your first finding.

### R2 — Read the effect payload, not the config

To answer "is the offset applied?", the config file is worthless (it says what we *intended*) and the
strategy code is nearly worthless (it says what *should* happen). The `SubmitOrder` effect in the journal
is the truth, because it is what was actually sent:

```json
{"OrderId":"00000001-…","LimitPrice":{"Value":1.15973},"OrderType":"Limit",
 "Entry":{"Method":"LimitOffset","LimitOffsetPips":5.25, …}}
```

The offset is right there. One query closed the question permanently, and proved *both* venues received
an identical order — which relocated the entire investigation from the engine to the venue.

> **Rule:** trace the value at the boundary it crosses, not at the layer that computes it.

### R3 — When two systems disagree, find the fact that only ONE story can explain

Both venues got an identical sell limit at 1.15973. The tape filled at exactly 1.15973. cTrader opened
at **1.16156 — 18 pips through the limit**.

That single number is a discriminator. A resting limit order *cannot* fill 18 pips better than its limit.
So exactly one story survives: **the order was not resting when price crossed it.** Every hypothesis that
required the order to be resting (bad offset, wrong expiry, spread convention, gap handling) is dead
without further testing.

> **Rule:** hunt for the observation that is *impossible* under all but one hypothesis. One such fact is
> worth a hundred consistent ones. If your evidence is merely *consistent* with your story, you have not
> tested it.

### R4 — Instrument the venue; never simulate your way to a conclusion

At this point I knew the order arrived late but not *why*, and I could have spent hours reading cBot
code building a theory. Instead I made the cBot record, per order, what only the cBot could know:

```csharp
_tradeLog.RecordOrderSubmit(clientOrderId, orderType, direction,
    limitPrice, sym.Bid, sym.Ask, EpochMs(Server.TimeInUtc), outcome, error);
```

The venue's own clock, the venue's own quote, at the instant of submit. One run produced this:

| engine proposed (bar open) | cBot actually submitted | bid at submit | our sell limit |
|---|---|---|---|
| 05-28 **01:00** | 05-28 **09:00** | 1.16156 | 1.15973 |

The limit arrives *below the bid* — already marketable. Not a theory. A measurement.

> **Rule:** if a claim about the venue cannot be backed by a number the venue itself emitted, it is a
> guess. Add the instrument, take the reading. It is almost always cheaper than the reasoning it replaces,
> and unlike the reasoning it is reusable.

### R5 — Never "fix" an off-by-one you have not measured, especially near lookahead

The prime suspect was `bars.Last(1)` (publish the previous bar) vs `bars.Last(0)`. Both are one-character
changes. One of them is correct; **the other silently introduces lookahead bias** — feeding the engine a
bar that has not finished forming, which would make every backtest in this repo optimistic and worthless
in a way no test would catch.

I did not guess. I recorded the venue clock at bar-publish time and compared it to the bar's own open time:

```
bar 05-11 01:00 -> published 05-11 09:00    gap 8h     <- H4 bar published at its close must be 4h
```

A steady **8h** on an H4 bar. Publishing at the close is 4h. So `Last(1)` was one bar stale, therefore
`Last(0)` is the just-closed bar, therefore the fix is safe — *and* the measurement doubles as the
verification: after the change the same instrument read exactly **4h**, on every bar.

> **Rule:** when the two candidate fixes are "correct" and "catastrophic and invisible", the cost of
> measuring is always lower than the cost of being wrong. Build the instrument so that it *also* proves
> the fix. A number that was 8 and is now 4 is a verification; "it looks right now" is not.

### R6 — One test symbol proves nothing; pick the extremes

Every parity cell for four sessions was XAUUSD. The stop bug's magnitude scaled with the instrument's
price, so on gold it produced a ~$330 stop — a plausible gold stop. The same bug on EURUSD produced a
**1.2-pip** stop and on BTCUSD a **7,708-point** stop, either of which is instantly, obviously insane.

**The one symbol the team standardised on was the one symbol that hid the bug.**

> **Rule:** any parity or economics claim must be measured on at least a currency pair *and* a high-priced
> instrument — the two extremes of `pipSize`. A scale-dependent bug is invisible at exactly one scale, and
> you do not know in advance which scale that is.

### R7 — A green gate battery is not evidence about a venue

Build, Unit (747), Integration (121), Sim-fast (144) were green through every one of these defects.
**None of them place an order at a real venue.** They cannot see a stale bar, a mispriced stop, or an
order that arrives late.

> **Rule:** green credential-free gates permit a claim about the venue; they never support one. If your
> conclusion is about cTrader, your evidence must come from a run against cTrader. State which.

### R8 — Read the warnings. They are usually the answer, in plain language

- `TRADES_LOST:7:0` — *"journal had 7 closes, 0 persisted"*. That run was presented as a clean result.
- `CTRADER|ROUTER_PARSE_ERR: Requested value 'Pending' was not found` — this appeared on **every run with
  a resting limit order**, and named F40 exactly. It sat in a `LogWarning` for an entire iteration.

> **Rule:** grep the run log for `WARN`/`ERR` *before* forming a hypothesis, not after one fails. A
> warning nobody reads is a defect nobody fixed. And a run that cannot account for its own trades must
> not be presentable as a result — make it fail.

### R9 — Prove the abstraction, don't assert it

The claim "the account currency is now a config value, so GBP will be easy" is unfalsifiable until
someone flips it. So I flipped it to EUR (the venue's actual currency) and ran the whole pipeline.

It surfaced two real bugs that no amount of re-reading would have:
1. Two of three `EngineHostOptions` construction sites never got the currency, so the cTrader leg still
   modelled USD while the tape modelled EUR.
2. Commission was computed in USD and *labelled* in the account currency — a 17% error, exactly the
   EURUSD rate.

Both were fixed, and the flip then produced **identical lots on both venues** — closing the
never-explained "F6 position-size divergence" that had been open for three iterations.

> **Rule:** exercise the new degree of freedom at least once, end to end. An abstraction that has only
> ever run in its default configuration is not an abstraction; it is a default with extra syntax.

---

## 3. The order to work in

1. **Re-derive the previous diagnosis** from its own cited evidence (R1). Expect to kill it.
2. **Read the run's warnings** (R8).
3. **Find the boundary payload** — what was actually sent to the venue (R2).
4. **Find the discriminating fact** — the observation only one story explains (R3).
5. **Instrument the venue** to capture what only it knows, at the instant it acts (R4).
6. **Take the reading. Fix. Re-take the same reading as verification** (R5).
7. **Re-measure on both `pipSize` extremes** (R6).
8. Only now update the plan/tracker — with the number, not the narrative.

---

## 4. What "evidence" means in this repo

A finding is not real until it cites one of:

- a **RunId** and the query that reads it,
- a **venue-emitted number** (`shamshir-report.json`: `orderSubmits`, `barClock`, `accountCurrency`,
  `protectionMismatches`),
- a **journal effect payload** (what crossed the boundary),
- a **before/after reading of the same instrument** (the verification).

"The code looks right", "the tests pass", and "the previous session said so" are none of these.

---

## 5. Instruments that now exist (use them; extend them)

The cBot's stdout **does not survive the cTrader CLI** — any `Print()` is lost, which is why the
orchestrator's old `CBOT|TIMING` scan was dead code for months. Anything the venue must tell us goes into
`shamshir-report.json`:

| Field | Answers |
|---|---|
| `orderSubmits[]` | venue clock, bid/ask, requested price, and whether the venue **rested** or **immediately filled** each entry |
| `barClock[]` | bar open time vs the venue clock when we handed it over — catches stale-bar feeds (must equal one bar) |
| `accountCurrency` | the venue's actual denomination — checked against `Account:Currency`, mismatch is loud |
| `protectionMismatches` | positions whose venue-held SL/TP ≠ what the engine asked for. Must be 0 |

Add to this file whenever you build a new instrument, and say what question it answers.
