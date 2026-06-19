# Phase 1 — Freeze the API Contract

**Track:** SERIAL. Runs after Phase 0 exit gate. This is the **seam** that lets Track A (frontend) and
Track B (backend reorg) proceed in parallel without colliding.
**Output:** `contract/openapi.v1.json` committed to the repo + a documented SignalR envelope + contract
tests that pin both.

**Why a freeze:** Track A generates its TypeScript client from this document and builds the whole SPA
against it. Track B may move every backend file, but as long as the contract holds, the SPA keeps
working. A contract change is a deliberate event (update the doc + a contract test in the same PR), not
an accident.

---

## P1.1 — Inventory the surface the SPA needs

Map current controllers/pages (there are 13 controllers + the Razor pages today — consolidate, don't
copy 1:1) to the SPA's needs:

| Area | Operations the SPA needs |
|------|--------------------------|
| Runs | list, detail, start, cancel, live progress (stream), journal (paged, lossless), report stats, equity curve, trades for run, funnel |
| Strategies | list configs, get config, upsert config, validate config, list risk profiles / prop-firm rules / symbols |
| Live | venue status (current + event timeline), start/stop, account snapshot |
| Trades | list (paged/filtered), detail (+ chart window data) |
| Verification | reconcile run (Layer-1), compare-to-cTrader (Layer-2, on demand) |
| Exports | run report as markdown/JSON |

---

## P1.2 — Define the REST contract (`/api/v1`)

- DTOs are the **stable contract** — independent of EF entities and of internal records. Put them in a
  `Contracts` namespace so Track B can move internals freely beneath them.
- Version the route prefix `/api/v1`.
- Errors: RFC7807 `ProblemDetails`; map the `Result.Error` codes to HTTP consistently (validation→400,
  not-found→404, conflict→409, unexpected→500).
- Generate OpenAPI via Swashbuckle or NSwag; emit `contract/openapi.v1.json` as a build artifact checked
  into the repo.

**Gate:** OpenAPI generates; every declared endpoint has a typed request/response DTO; no EF entity
leaks into a contract DTO (architecture/contract test).

---

## P1.3 — Define the SignalR envelope

- One typed `RunProgress` envelope (already camelCase) — bars processed/total, percent, eta, equity,
  balance, dd%, open positions, governor state, typed counters, recent-journal append.
- Separate typed messages for venue-status transitions and lossless journal-append (replaces the 30-item
  ring — see Track D / 31-B2).
- Document the hub method names, group naming (`run:{runId}`), and message schemas next to the OpenAPI
  doc.

**Gate:** the documented envelope matches what the hub actually sends (contract test extends
`RunProgressContractTests`).

---

## P1.4 — Contract tests

- Extend `WebSmokeTests` → every `/api/v1` endpoint returns the declared shape (not just 200).
- Extend `RunProgressContractTests` → SignalR payloads deserialize into the documented envelope.
- A generation smoke step: NSwag/openapi-generator produces a **compiling** TS client from
  `openapi.v1.json` (this is what Track A consumes).

---

## P1.5 — Publish the artifact

- Commit `contract/openapi.v1.json`.
- Add a short `contract/README.md`: how to regenerate, the versioning rule (additive = same version;
  breaking = `/api/v2` + doc bump), and "change the contract before the code on both sides."

**Phase 1 exit gate:** OpenAPI committed; contract tests green; TS client generates and compiles. Open
the worktrees for Track A and Track B.
