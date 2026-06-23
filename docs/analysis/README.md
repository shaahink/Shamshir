# Shamshir Analysis Reports

**Generated:** 2026-06-23
**Branch:** `iter/31-costs-journal` (iter-38 add-ons + iter-39 cleanup)
**For:** LLM-based analysis, issue identification, and change requests

---

## Files

### `SYSTEM-MODEL.md`
The complete system and entity model in one document:
- Architecture overview (kernel, engine, venues)
- Backtest modes (replay vs cTrader — when each is used)
- Full entity catalog (16 DB entities, 40+ domain types, 15 engine events, 10 engine effects)
- Strategy system (9 strategies, config storage, per-run overrides)
- Add-on system (5 add-on types, auto-tuner, packs)
- Risk profiles, governor, FTMO rules
- Cost & journal system
- API endpoints + DTOs
- Kernel flow (PreTradeGate 8-step walk, sizing, equity, breach)
- Code map

### `UI-FLOWS.md`
Visual UX description of every screen, designed to be read as if seeing the UI:
- **Before backtest:** New Backtest form (what fields, what data sources, what defaults)
- **During backtest:** Live Monitor (SignalR feed, progress bar, counters, equity chart, journal stream)
- **After backtest:** Run Report (summary tiles, equity/DD chart, daily PnL, trades table, funnel, journal)
- **Side pages:** Run List, Run Analyzer, Strategy Detail, Add-on Packs, Risk Profiles, FTMO Rules, Governor
- **When replay vs cTrader is used** — marked at every decision point

### `CODE-MAP.md` (in `docs/reference/`)
The existing CODE-MAP.md covers feature-to-file mapping. This analysis folder supplements it.

---

## How to Use

1. Start with `SYSTEM-MODEL.md` to understand the architecture, entities, and data flow
2. Read `UI-FLOWS.md` to understand what the user sees and where each piece of data comes from
3. Use `docs/reference/CODE-MAP.md` to find specific source files
4. Reference `DECISIONS.md` (root) for all design decisions D1-D84
5. Reference `docs/OPEN-ISSUES.md` for known bugs and carry-forward items
