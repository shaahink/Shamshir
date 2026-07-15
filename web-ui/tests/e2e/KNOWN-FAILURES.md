# E2E Known Failures — triage of 2026-07-15 (iter-dx-speed D1)

**This file is the authority on the Playwright suite's expected state.** On a healthy tree,
`npx playwright test` (from `web-ui/`) exits **0**: quarantined tests report as *skipped/fixme*,
everything else passes. **No session may stash-rebuild a baseline to prove a Playwright failure is
pre-existing — check this file first.** If the suite fails with something not listed here, it is a
regression on YOUR change.

Baseline this triage resolved: 15 tests failed on a healthy tree (12 in `ui-smoke.spec.ts`, 3 in
`live-monitor-links.spec.ts`), verified pre-existing on 2026-07-15 by re-running the pre-X2/X3 SPA.
Classification per iter-dx-speed PLAN D1: KEEP (fix test or product) / QUARANTINE (`test.fixme` +
revival condition) / DELETE (asserted UI that no longer exists; named below).

## Fixed (KEEP — these now pass; the failures were stale tests or one real product gap)

| Test | What was wrong | Fix |
|---|---|---|
| ui-smoke › Run Report › navigation links exist | **Real product gap:** `/runs/:id/analyzer` route+page exist but the report header lost its link — the analyzer was unreachable from the UI | Added the Analyzer link back to the report header (product fix, run-report.component.ts) |
| ui-smoke › Add-on Packs › card links navigate to detail | Packs moved under the Risk hub; `/addon-packs` redirects to `/risk/packs`, so `waitForURL('**/addon-packs/**')` never matched | Test now navigates to `/risk/packs` and waits for that URL |
| ui-smoke › Add-on Packs › detail page has editable fields | Same redirect | Same |
| ui-smoke › Strategy Detail add-ons › Baseline & Add-ons labels | First `<a>` in the list is now **New Strategy** → test landed on the CREATE page, which has no add-ons section | Selector excludes `/strategies/new` |
| ui-smoke › Strategy CRUD › delete button visible | Same first-link problem (create page has no Delete) | Same selector fix |
| ui-smoke › Live Monitor journal polling › journal section | Section renamed **Narrative** (M3.1 server-side narrative projection) | Test asserts the new label |
| ui-smoke › Risk Profile create modal › opens and closes | `<app-create-modal>` host is a zero-size inline element (its only child is `position:fixed`), so `toBeVisible()` on the host can never pass | Test asserts the inner `h2` overlay content |

## Quarantined (`test.fixme` — intent preserved, revival condition written)

| Test | Reason | Unquarantine condition |
|---|---|---|
| live-monitor-links › Tier 2 › running status within 15s | Drives the PRE-redesign backtest form: never selects a strategy, so the row-builder's Start stays disabled; assumes a yesterday→today replay window has bars | Rewrite against the row builder (strategy chip + symbol + TF, venue=tape, known-bars window) — or retire in favour of `scripts/verify-live.ps1` (D4) + `x2-x3.spec.ts` live list test |
| live-monitor-links › Tier 3 › full chain completes with trades | Same stale form contract | Same |
| ui-smoke › Live Backtest Chain › monitor updates, report shows trades | Same stale form contract ("EURUSD is the default" is also no longer true) | Same |

## Deleted (asserted UI that no longer exists — removed component/label named)

| Test | Removed UI it asserted |
|---|---|
| live-monitor-links › Tier 1 › SignalR connects (console log) | The `[SignalR] connected` console.log in `run-hub.service.ts` no longer exists; intent covered by the sibling status-tile test + `x2-x3.spec.ts` live updates |
| ui-smoke › iter-37 › resolved-config preview + overrides | The iter-37 "Resolved config preview" panel and per-strategy override textareas — removed by the iter-strategy-system row-based builder |
| ui-smoke › New-Backtest pack + regime (iter-38 S10 U3) | The single "Add-on Pack" dropdown and "Disable Regime Detection" checkbox — replaced by per-row pack selects + the "Regime" protection chip |
| ui-smoke › Nav bar Packs link (iter-38) | The top-nav Packs link — packs live under the Risk hub at `/risk/packs` (old URL redirects); reachability covered by the Add-on Packs tests |
| ui-smoke › New-Backtest per-strategy pack (32-P5) | The "Pack: strategy default" per-strategy dropdown — superseded by the run-plan rows' per-row pack selects |

## Environment-conditional skips (not failures — these `test.skip` themselves)

- Anything gated on `SEEDED_RUN_ID` (duplicate-dialog modal, seeded report checks).
- Data-dependent checks that skip with a reason when the DB lacks trades/journal for the chosen run.
