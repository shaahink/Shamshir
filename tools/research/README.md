# tools/research — committed research analysis scripts

Committed at **iter-structural-edge S0** (PLAN §3 S0.2) from the 2026-07-16 research-session
scratchpad, so every number in `docs/iterations/iter-structural-edge/RESEARCH.md` is one
command away from re-verification. Both scripts read the live DB directly
(`src/TradingEngine.Web/data/trading.db` by default) and take an experiment id **prefix**
(e.g. `075D5240`).

## The one-command F64 check (no python needed)

The F64 split-half table is also served by the app + `research` CLI
(`SplitHalfPersistenceService`, same math as `split_half.py`):

```
research persistence --experiment 075D5240 --split 2025-12-03
```

This is the owner's 5-minute verification from PLAN §5 and the G0 gate command. The app must
be running (like every `research` verb).

## split_half.py — F64 selection persistence + F66 cost drag

```
python tools/research/split_half.py --experiment 075D5240 --split 2025-12-03
```

Reproduces RESEARCH.md §1 (split-half selection test, top-8, reverse check, H2 rolling 30d
challenge windows at 1x/2x/3x, weekly-bucket correlations) and §3 (cost drag). `--split`
defaults to the census midpoint, which for 075D5240 is exactly `2025-12-03`.

## quant_research.py — census pool, portfolio pools, exit quality

```
python tools/research/quant_research.py --experiment 075D5240
```

Reproduces RESEARCH.md §2 (F65 exit truncation / MFE capture), §4 (F67 entry noise floor) and
§5 (F68 family triage), plus the in-sample portfolio pools A/B/C that F64 dispels — the
script's own header states the honesty caveats (in-sample selection, linear-scaling
approximation).

Options for both: `--db <path>` (default resolves relative to the repo), `--base <amount>`
(default 100000).
