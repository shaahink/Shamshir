# Research playbooks (P3.2 / P3.4)

A **playbook** is a JSON file describing an ordered list of typed steps that the
`TradingEngine.ResearchCli` (`research`) walks sequentially against the **running** Shamshir Web app
over HTTP (Q3). The executor is deliberately dumb: no DAGs, no parallelism, no retry policies
(PLAN §12) — its value is **resumability** and **honest persisted verdicts**.

Run one with:

```
research pipeline run playbooks/venue-parity.json --base-url https://localhost:7108
research pipeline status <pipelineId>
research pipeline approve <pipelineId>          # release a parked owner-gate
research pipeline run playbooks/venue-parity.json --resume <pipelineId>
```

State lives in the DB tables `ResearchPipelines` + `ResearchPipelineSteps` (Q6); artifacts land under
`docs/research/pipelines/{pipelineId}/` when `--artifact-dir` is supplied. The UI review page
(`/research`, P3.3) reads the same rows.

## File shape

```json
{
  "name": "venue-parity",
  "steps": [
    { "kind": "<step-kind>", "continueOnFail": false, ...params }
  ]
}
```

- `name` (required) — the pipeline name shown in the UI + used for the artifact folder.
- `steps` (required, non-empty) — each step is an object with a `kind` and its params.
- `kind` and `continueOnFail` are reserved keys; every other property is that step's params.
- A step's **param hash** (kind + canonicalized params) is the resume invalidation key: on `--resume`,
  a `passed`/`approved`/`skipped` step with an unchanged hash is skipped; the first step with a changed
  hash (or a non-passed status) re-runs, and everything downstream re-runs too.

## Step kinds

| kind | what it does | key params | PASS when |
|------|--------------|-----------|-----------|
| `ensure-data` | checks market-data coverage (optionally downloads) | `symbols`, `tfs`, `from`, `to` | no missing (symbol×tf) cells |
| `start-run` | POST `/api/runs` (params ARE a StartRunRequest) | any StartRunRequest field; `as` (context key), `venue`, `compareBoth`, `explore` | a runId comes back |
| `await-run` | polls a run to a terminal status | `runId` (context key or literal), `timeout` | run reached a terminal state |
| `assert-gates` | validates a run's terminal facts | `runId`, `requireStatus`, `minTrades`, `forbidWarnings`, `forbidWarningCodes` | all gates pass |
| `reconcile` | GET reconcile of two runs; writes `reconcile.json` | `left`, `right` (context keys or ids) | reconcile returns |
| `exitlab-eval` | POST `/api/exit-lab/evaluate` | `runIds`, `positionIds`, `referenceAtrPips`, grid overrides | `totalCells > 0` |
| `walk-forward` | POST `/api/walk-forward/start` | WalkForwardRequest fields | a jobId comes back |
| `apply-calibration` | POST `/api/exit-lab/calibrations` | SaveCalibrationRequest fields | request accepted |
| `owner-gate` | PARKS the pipeline (`awaiting-owner`) | `reason` | approved via UI / `pipeline approve` |
| `report` | summarizes the accumulated context | — | always |

## Referencing earlier runs

A `start-run` step records its runId in the pipeline context under the key given by `as` (default
`runId`). Later steps reference it by that key (`"runId": "tapeRun"`) or by a `$`-prefixed reference
(`"$tapeRun"`); an unknown reference is treated as a literal id.

## Canonical playbooks in this folder

- `venue-parity.json` — paired tape+cTrader run → reconcile → tolerance verdict (the repeatable P2.2 gate).
- `explore-exit.json` — exploration run (excursions on) → validate → exit-lab grid → owner-gate → report.

> **Note (owner-pending):** running these end-to-end needs the app up, market data present, and (for
> `venue-parity`) cTrader credentials. The playbook *shapes* are validated by unit tests; the live
> end-to-end run is the P3 verification-matrix gate and is owner/next-session.
