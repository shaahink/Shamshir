"""iter-viability V4: session/time-of-day OOS census harvest → GV4 evidence tables (Lane R clone).

Produces the pre-registered GV4 deliverables (LEDGER.md Session 7) from experiment
`v4-session-tod`. Read-only against trading.db + marketdata.db — safe to run while the census
batch is still writing (point lookups on a UNIQUE index; no writes, no locks held). Cloned from
v2_harvest.py: identical F70 position-folding, block-bootstrap CI, and M1 spread-stress machinery;
the §3 hypotheses are swapped for the V4 pre-registration (there is no frozen in-sample ranking to
compare, so H-MR / H-RANK are dropped and replaced by H-SESSION / per-strategy / H-TF).

Usage:
  python tools/research/v4_harvest.py --experiment <guid> [--out evidence/v4-harvest.md]
  python tools/research/v4_harvest.py --experiment <guid> --quick    # skip spread stress

Deliverables (pre-registration, LEDGER.md Session 7 "Deliverables (gate GV4 evidence)"):
  1. Era x strategy: pooled $/position + n per strategy per calendar year, block-bootstrap 95%
     CIs; strategy totals with CIs at raw + 1.5x + 2x spread stress.
  2. D5' legs 1 (bootstrap CI on pooled strategy dollars), 2 (sign agreement at strategy x
     FX-class: FX-major / JPY-cross), 4 (drop-any-month jackknife sign stability). Leg 3 (stitched
     walk-forward) does not apply — nothing is refit. Reported, not silently skipped.
  3. H-SESSION (family-pooled — the one-shot verdict) + per-strategy verdicts + H-TF (M15 vs H1).
  4. Residence/park recommendation per strategy (survivors -> 2024 era-holdout at V7).
  5. Guards re-pasted (era-holdout + EMBARGO-2 must be 0).

METHOD NOTES — read before trusting a number:

* Position-level dollars (F70 convention). The metric is $/POSITION, not $/row: a PartialTp
  position lands as several TradeResults rows sharing a PositionId, and counting rows is exactly
  the artifact that manufactured R3's 8/8 and v6a's +82% expR. Rows are folded by
  (RunId, PositionId) before any statistic is computed.

* Spread stress uses each trade's REALIZED dollars-per-price-unit rather than re-deriving pip
  value. The pre-registered formula is
      d$ = (k-1) x (s_m / PipSize) x PipValuePerLot x lots
  and since Gross = price_move x (PipValuePerLot x lots / PipSize), the bracketed term is
  recoverable exactly as dpu = |Gross / (ExitPrice - EntryPrice)|, giving the algebraically
  identical
      d$ = (k-1) x s_m x dpu.
  This is deliberate: pip value has three cases (quote-ccy fixed, base-ccy price-dependent,
  cross rate-dependent) and re-implementing it here would fork the engine's own economics and
  silently misprice every JPY-cross, USDCAD and metal trade (cf. F48 cross-rate timing).
  Verified exact (0.000000 error) on EURUSD/USDJPY/GBPJPY/USDCAD/XAUUSD/BTCUSD.
  Fallback when ExitPrice == EntryPrice: symbol-median dpu-per-lot x lots.

* s_m = dukascopy M1 bar Spread (PRICE units) at the ask-side fill minute — entry for Long,
  exit for Short (P0.2 full-spread convention: the tape charges full spread on one leg).
  Fallback chain: exact minute -> nearest prior M1 within 2 h -> symbol median. Measured on the
  census: 99.6% exact hits, 0.35% fallback.

* Analytic stress prices the COST channel only, not path effects (a wider spread would touch
  different SLs/TPs). Pre-registered limitation. Escalation rule: any family whose raw verdict
  sits within +/-1 MDE of flipping under 1.5x stress is flagged cost-fragile, and an in-run
  stressed rerun is PROPOSED at GV2 (owner call).

* "Weekly-scale blocks" (pre-registered wording) is operationalised here as
  mean_block = median positions per ISO week for that family, floored at 2. The pre-registered
  MDE line was computed ad-hoc and never pinned this in committed code — pinning it now. MDE is
  restated at ACTUAL n throughout (the pre-registration requires this; the n_proj=6x projection
  was an assumption, not a result).
"""
import argparse
import json
import sqlite3
import statistics
import sys
from collections import defaultdict
from datetime import datetime, timedelta
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from block_bootstrap import stationary_bootstrap, mde  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DB = ROOT / "src" / "TradingEngine.Web" / "data" / "trading.db"
DEFAULT_MD = ROOT / "src" / "TradingEngine.Web" / "data" / "marketdata.db"

REPS = 2000
SEED = 42
ERAS = {2019: "2019", 2020: "2020-vol", 2021: "2021", 2022: "2022-trend", 2023: "2023-chop"}

# V4 is FX-only (pre-reg): metals/crypto excluded, so only two instrument classes for leg-2.
CLASSES = {
    "AUDUSD": "FX-major", "EURGBP": "FX-major", "EURUSD": "FX-major", "GBPUSD": "FX-major",
    "NZDUSD": "FX-major", "USDCAD": "FX-major", "USDCHF": "FX-major",
    "EURJPY": "JPY-cross", "GBPJPY": "JPY-cross", "USDJPY": "JPY-cross",
}

N_CELLS = 80  # 4 strategies x 10 FX symbols x {M15, H1}


class Position:
    """One position = the F70 unit of account. Rows are the partial-TP splits."""
    __slots__ = ("family", "symbol", "tf", "opened", "net", "rows", "stressed")

    def __init__(self, family, symbol, tf, opened, net, rows):
        self.family, self.symbol, self.tf = family, symbol, tf
        self.opened, self.net, self.rows = opened, net, rows

    @property
    def year(self):
        return int(self.opened[:4])

    @property
    def iso_week(self):
        d = datetime.strptime(self.opened[:10], "%Y-%m-%d").isocalendar()
        return (d[0], d[1])

    @property
    def month(self):
        return self.opened[:7]


def load_cells(db, exp):
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    rows = con.execute("""SELECT er.VariantLabel, er.BacktestRunId, er.ScoreJson, br.Status
        FROM ExperimentRuns er JOIN BacktestRuns br ON br.RunId = er.BacktestRunId
        WHERE er.ExperimentId = ? COLLATE NOCASE""", (exp,)).fetchall()
    con.close()
    cells = {}
    for label, rid, sj, status in rows:
        parts = label.split("/")
        if len(parts) != 4 or parts[0] != "census":
            continue
        cells[label] = {"family": parts[1], "symbol": parts[2], "tf": parts[3],
                        "run": rid, "status": status,
                        "composite": json.loads(sj).get("Composite"),
                        "null_reason": json.loads(sj).get("NullReason")}
    return cells


def census_integrity(db, cells, end="2023-12-31"):
    """F82 — which cells stopped trading before the window ended, and why it matters.

    PreTradeGate.cs:174 rejects any entry whose worst case would breach the overall max-DD floor
    (`WorstCaseDDWouldBreachOverall`). Once an account grinds to within one worst-case of that
    floor, EVERY entry is rejected — and since no trade can open, the balance can never recover,
    so every future entry is rejected too. The cell is silently dead for the rest of the window:
    status `completed`, no error, no warning, all bars processed.

    Same structural trap as F78 (cooling-off deadlock) and F79 (daily-DD latch), but this guard
    is arguably CORRECT — the defect is the census DESIGN (5 years, one $100k account, no reset),
    not the gate. It matters because the truncation is NOT random: it selects the losing cells,
    so every later era is survivorship-biased upward.
    """
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    rows = []
    for label, c in cells.items():
        last, n = con.execute(
            "SELECT MAX(ClosedAtUtc), COUNT(*) FROM TradeResults WHERE RunId=?",
            (c["run"],)).fetchone()
        dd = con.execute("SELECT MaxDrawdownPct FROM BacktestRuns WHERE RunId=?",
                         (c["run"],)).fetchone()[0]
        try:
            dd = float(dd) * 100.0          # stored TEXT on some rows (money-as-TEXT legacy)
        except (TypeError, ValueError):
            dd = 0.0
        rows.append({"label": label, "family": c["family"], "n": n or 0,
                     "last": (last or "")[:7], "dd": dd,
                     "dead": bool(n) and (last or "") < end[:4] + "-07-01"})
    con.close()
    return rows


def load_positions(db, cells):
    """Fold TradeResults rows into positions (F70). Returns (positions, dropped_rows)."""
    by_run = {c["run"]: c for c in cells.values()}
    if not by_run:
        return [], 0
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    marks = ",".join("?" * len(by_run))
    rows = con.execute(f"""SELECT RunId, PositionId, Symbol, Direction, Lots, EntryPrice,
        ExitPrice, GrossPnLAmount, NetPnLAmount, OpenedAtUtc, ClosedAtUtc
        FROM TradeResults WHERE RunId IN ({marks})""", list(by_run)).fetchall()
    con.close()

    grouped = defaultdict(list)
    for r in rows:
        grouped[(r[0], r[1])].append(r)

    out = []
    for (rid, _pid), rs in grouped.items():
        c = by_run[rid]
        out.append(Position(
            family=c["family"], symbol=c["symbol"], tf=c["tf"],
            opened=min(r[9] for r in rs),
            net=sum(r[8] for r in rs),
            rows=[{"dir": r[3], "lots": r[4], "entry": r[5], "exit": r[6],
                   "gross": r[7], "opened": r[9], "closed": r[10]} for r in rs],
        ))
    return out, len(rows)


class SpreadSource:
    """M1 ask-side spread with the pre-registered fallback chain."""

    def __init__(self, md):
        self.con = sqlite3.connect(f"file:{md}?mode=ro", uri=True)
        self.cache = {}
        self.median = {}
        self.stats = defaultdict(int)

    def _symbol_median(self, sym):
        if sym not in self.median:
            row = self.con.execute(
                "SELECT Spread FROM MarketDataBars WHERE Symbol=? AND Timeframe='M1' "
                "AND Spread IS NOT NULL ORDER BY Spread LIMIT 1 "
                "OFFSET (SELECT COUNT(*)/2 FROM MarketDataBars WHERE Symbol=? AND "
                "Timeframe='M1' AND Spread IS NOT NULL)", (sym, sym)).fetchone()
            self.median[sym] = row[0] if row else 0.0
        return self.median[sym]

    def get(self, sym, ts):
        key = (sym, ts[:16] + ":00")
        if key in self.cache:
            return self.cache[key]
        row = self.con.execute(
            "SELECT Spread FROM MarketDataBars WHERE Symbol=? AND Timeframe='M1' "
            "AND OpenTimeUtc=?", key).fetchone()
        if row and row[0] is not None:
            self.stats["exact"] += 1
            val = row[0]
        else:
            lo = (datetime.strptime(key[1], "%Y-%m-%d %H:%M:%S")
                  - timedelta(hours=2)).strftime("%Y-%m-%d %H:%M:%S")
            prior = self.con.execute(
                "SELECT Spread FROM MarketDataBars WHERE Symbol=? AND Timeframe='M1' "
                "AND OpenTimeUtc<=? AND OpenTimeUtc>=? AND Spread IS NOT NULL "
                "ORDER BY OpenTimeUtc DESC LIMIT 1", (sym, key[1], lo)).fetchone()
            if prior:
                self.stats["prior_2h"] += 1
                val = prior[0]
            else:
                self.stats["symbol_median"] += 1
                val = self._symbol_median(sym)
        self.cache[key] = val
        return val


def attach_stress(positions, md, ks=(1.5, 2.0)):
    """Per-position stressed net for each k. Ask-side leg only (entry Long / exit Short)."""
    src = SpreadSource(md)
    dpu_per_lot = defaultdict(list)
    for p in positions:
        for r in p.rows:
            if r["exit"] != r["entry"] and r["lots"]:
                dpu_per_lot[p.symbol].append(abs(r["gross"] / (r["exit"] - r["entry"])) / r["lots"])
    med_dpu = {s: statistics.median(v) for s, v in dpu_per_lot.items() if v}

    for p in positions:
        p.stressed = {}
        cost = 0.0
        for r in p.rows:
            leg = r["opened"] if r["dir"] == "Long" else r["closed"]
            s_m = src.get(p.symbol, leg)
            if r["exit"] != r["entry"]:
                dpu = abs(r["gross"] / (r["exit"] - r["entry"]))
            else:
                dpu = med_dpu.get(p.symbol, 0.0) * r["lots"]
            cost += s_m * dpu
        for k in ks:
            p.stressed[k] = p.net - (k - 1.0) * cost
    return src.stats


def weekly_block(positions):
    """mean_block = median positions per ISO week (the pre-registered 'weekly-scale blocks')."""
    per = defaultdict(int)
    for p in positions:
        per[p.iso_week] += 1
    return max(2, int(statistics.median(per.values()))) if per else 2


def pooled(positions, attr=None, k=None):
    if k is not None:
        return [p.stressed[k] for p in positions]
    return [p.net for p in positions]


_CI_CACHE = {}


def ci(series, block, reps=REPS):
    """(mean, lo, hi, se) — stationary block bootstrap, 95% percentile CI.

    Memoised: the same family series is bootstrapped by §1, §1b and §4, and the bank series by
    §1 and §3. The bootstrap is O(reps x n) pure Python, so recomputing it is the whole runtime.
    Deterministic (fixed seed) ⇒ caching cannot change a result.
    """
    if len(series) < 2:
        return (statistics.fmean(series) if series else 0.0), None, None, None
    key = (block, reps, tuple(series))
    if key not in _CI_CACHE:
        dist = stationary_bootstrap(series, statistics.fmean, reps=reps, mean_block=block,
                                    seed=SEED)
        _CI_CACHE[key] = (statistics.fmean(series), dist[int(0.025 * len(dist))],
                          dist[int(0.975 * len(dist)) - 1], statistics.stdev(dist))
    return _CI_CACHE[key]


def report(cells, positions, stress_stats, guard_vals, quick, exp, integrity):
    L = []
    W = L.append
    fams = sorted({p.family for p in positions})
    blocks = {f: weekly_block([p for p in positions if p.family == f]) for f in fams}
    bank_block = weekly_block(positions)

    W("# V4 session/time-of-day OOS census — GV4 evidence tables\n")
    W(f"Experiment `{exp}` · generated by `tools/research/v4_harvest.py` (read-only).\n")
    scored = [c for c in cells.values() if c["composite"] is not None]
    nulls = [c for c in cells.values() if c["composite"] is None]

    if len(cells) < N_CELLS:
        done_tf = defaultdict(int)
        for c in cells.values():
            done_tf[c["tf"]] += 1
        W(f"\n> ## ⚠ PARTIAL CENSUS — NOT GATE NUMBERS\n"
          f"> Only **{len(cells)} of {N_CELLS}** cells exist ("
          + ", ".join(f"{k}={v}" for k, v in sorted(done_tf.items())) + "). The census runs "
          "M15 first, so a partial harvest is timeframe-skewed and every strategy mean below is "
          "weighted toward whichever timeframe happens to be finished. These tables are a "
          "PIPELINE CHECK only — do not read a verdict off them and do not take them to GV4. "
          "Re-run after `BATCH DONE` + `v4_census_driver.py --rescore-nulls`.\n")
    W(f"**Coverage:** {len(cells)} cells scored-or-null of {N_CELLS} pre-registered "
      f"({len(scored)} scored, {len(nulls)} null) · {len(positions)} positions "
      f"(F70 position-level, not rows).\n")
    infra = [c for c in nulls if "not completed" in (c["null_reason"] or "")]
    W(f"**Null audit:** {len(nulls)} null, of which {len(infra)} infrastructure (F80 finalize "
      f"race — must be 0 at the gate; recover with `census_driver.py --rescore-nulls`).\n")

    # ---- 0. Census integrity (F82) ---------------------------------------------------
    dead = [r for r in integrity if r["dead"]]
    traded = [r for r in integrity if r["n"]]
    W("\n## 0. Census integrity — F82 silent truncation (READ FIRST)\n")
    if dead:
        near = [r for r in dead if r["dd"] >= 9.0]
        W(f"\n> **{len(dead)} of {len(traded)} trading cells ({100*len(dead)/max(1,len(traded)):.0f}%) "
          f"STOPPED TRADING before the window ended** — status `completed`, no error, no warning, "
          f"all bars processed. {len(near)}/{len(dead)} of them are pinned at ≥9% drawdown "
          f"(median {statistics.median([r['dd'] for r in dead]):.2f}%), i.e. parked just above the "
          f"$90k FTMO max-loss floor where `PreTradeGate.cs:174` "
          f"(`WorstCaseDDWouldBreachOverall`) rejects every entry. No trade can open ⇒ the balance "
          f"cannot recover ⇒ the rejection is permanent. **Absorbing.**\n")
        W("> \n> This is NOT random attrition — it selects precisely the cells that were losing, "
          "so every later era below is **survivorship-biased upward**: the worst cells stop "
          "contributing trades from their death date onward. Era columns are therefore NOT "
          "comparable across time, and any family whose cells died early has its late-era mean "
          "computed from the survivors only.\n")
        W("> \n> The gate itself is defensible (never risk breaching a terminal floor). The defect "
          "is the census DESIGN: 5 years, one $100k account, no reset. **GV2 owner decision "
          "required** — (a) research mode with the overall-floor gate disabled, (b) per-era "
          "account reset, or (c) accept and report truncation explicitly. Until then these tables "
          "measure *time-until-first-10%-drawdown* as much as edge.\n")
        W("\n| strategy | cells | died early | median death | median maxDD% at death |")
        W("|---|---|---|---|---|")
        for f in sorted({r["family"] for r in traded}):
            fr = [r for r in traded if r["family"] == f]
            fd = [r for r in fr if r["dead"]]
            if not fd:
                W(f"| {f} | {len(fr)} | 0 | — | — |")
                continue
            W(f"| {f} | {len(fr)} | **{len(fd)}** | {sorted(r['last'] for r in fd)[len(fd)//2]} "
              f"| {statistics.median([r['dd'] for r in fd]):.2f} |")
        W("\nDead cells (last trade → maxDD% → cell):\n")
        W("```")
        for r in sorted(dead, key=lambda x: x["last"]):
            W(f"{r['last']}  {r['dd']:5.2f}%  n={r['n']:<5} {r['label']}")
        W("```")
    else:
        W("\nNo cell stopped trading early — F82 truncation absent in this census.\n")

    # ---- 1. Era x family -------------------------------------------------------------
    W("\n## 1. Era × strategy — pooled $/position (F70), block-bootstrap 95% CI\n")
    W("`n` = positions. CI excluding 0 ⇒ the sign is detectable at that n.\n")
    W("\n| strategy | " + " | ".join(ERAS[y] for y in sorted(ERAS)) + " | ALL |")
    W("|---|" + "---|" * (len(ERAS) + 1))
    for f in fams:
        cols = []
        for y in sorted(ERAS):
            ps = [p for p in positions if p.family == f and p.year == y]
            if len(ps) < 2:
                cols.append(f"n={len(ps)}")
                continue
            m, lo, hi, _ = ci(pooled(ps), blocks[f])
            cols.append(f"{m:+.1f} (n={len(ps)})<br>[{lo:+.0f}, {hi:+.0f}]")
        ps = [p for p in positions if p.family == f]
        m, lo, hi, _ = ci(pooled(ps), blocks[f])
        cols.append(f"**{m:+.1f}** (n={len(ps)})<br>[{lo:+.0f}, {hi:+.0f}]")
        W(f"| {f} | " + " | ".join(cols) + " |")
    m, lo, hi, se = ci(pooled(positions), bank_block)
    W(f"\n**BANK-POOLED:** {m:+.2f} $/position (n={len(positions)}), 95% CI "
      f"[{lo:+.2f}, {hi:+.2f}], SE {se:.2f}, MDE@n {mde(se):.1f}\n")

    # ---- 1b. Spread stress -----------------------------------------------------------
    W("\n## 1b. Family totals — raw vs 1.5× vs 2× spread stress\n")
    if quick:
        W("_skipped (--quick)_\n")
    else:
        tot = sum(stress_stats.values()) or 1
        W(f"Ask-side M1 spread lookups: exact {stress_stats['exact']} "
          f"({100 * stress_stats['exact'] / tot:.1f}%), nearest-prior-2h "
          f"{stress_stats['prior_2h']}, symbol-median {stress_stats['symbol_median']}. "
          f"Analytic cost channel only (no path effects) — pre-registered limitation.\n")
        W("\n| strategy | n | raw $/pos [CI] | 1.5× [CI] | 2× [CI] | MDE@n | cost-fragile? |")
        W("|---|---|---|---|---|---|---|")
        for f in fams + ["BANK-POOLED"]:
            ps = positions if f == "BANK-POOLED" else [p for p in positions if p.family == f]
            b = bank_block if f == "BANK-POOLED" else blocks[f]
            if len(ps) < 2:
                continue
            r_m, r_lo, r_hi, r_se = ci(pooled(ps), b)
            s15 = ci(pooled(ps, k=1.5), b)
            s20 = ci(pooled(ps, k=2.0), b)
            m_de = mde(r_se)
            fragile = "**YES**" if (r_m > 0) != (s15[0] > 0) or abs(s15[0]) < m_de else "no"
            W(f"| {f} | {len(ps)} | {r_m:+.1f} [{r_lo:+.0f}, {r_hi:+.0f}] | "
              f"{s15[0]:+.1f} [{s15[1]:+.0f}, {s15[2]:+.0f}] | "
              f"{s20[0]:+.1f} [{s20[1]:+.0f}, {s20[2]:+.0f}] | {m_de:.0f} | {fragile} |")
        W("\nCost-fragile ⇒ propose an in-run stressed rerun of that strategy's cells at GV4 "
          "(owner call; needs a spread-multiplier knob, not built).\n")

    # ---- 2. D5' legs -----------------------------------------------------------------
    W("\n## 2. D5′ legs (frozen configs — nothing is refit)\n")
    W("\n**Leg 1 — bootstrap CI on pooled strategy dollars:** the ALL column of §1. "
      "Survive = CI excludes 0.\n")
    W("\n**Leg 2 — sign agreement at strategy × FX-class (FX-major / JPY-cross):**\n")
    klasses = ["FX-major", "JPY-cross"]
    leg2 = {}
    W("\n| strategy | " + " | ".join(klasses) + " | agree? |")
    W("|---|" + "---|" * (len(klasses) + 1))
    for f in fams:
        row, signs = [], []
        for k in klasses:
            ps = [p for p in positions if p.family == f and CLASSES.get(p.symbol) == k]
            if not ps:
                row.append("—")
                continue
            mm = statistics.fmean(pooled(ps))
            signs.append(mm > 0)
            row.append(f"{mm:+.1f} (n={len(ps)})")
        # A single populated class cannot "agree" with anything — one sign trivially matches
        # itself. That is absence of evidence, not sign stability; report it as n/a.
        if len(signs) < 2:
            leg2[f] = None
            verdict2 = f"n/a ({len(signs)} class)"
        else:
            leg2[f] = all(s == signs[0] for s in signs)
            verdict2 = "YES" if leg2[f] else "no"
        W(f"| {f} | " + " | ".join(row) + f" | {verdict2} |")
    W("\n**Leg 3 — stitched walk-forward: DOES NOT APPLY.** V2 scores frozen bank configs; "
      "nothing is refit, so there is no in-sample plateau to stitch. Reported, not skipped.\n")
    W("\n**Leg 4 — drop-any-month jackknife (sign stability across the 60 months):**\n")
    leg4 = {}
    W("\n| strategy | pooled $/pos | months | sign flips on dropping 1 month | stable? |")
    W("|---|---|---|---|---|")
    for f in fams:
        ps = [p for p in positions if p.family == f]
        if len(ps) < 2:
            continue
        full = statistics.fmean(pooled(ps))
        months = sorted({p.month for p in ps})
        flips = []
        for mo in months:
            keep = [p for p in ps if p.month != mo]
            if keep and (statistics.fmean(pooled(keep)) > 0) != (full > 0):
                flips.append(mo)
        leg4[f] = not flips
        extra = (" (" + ", ".join(flips[:3]) + ")") if flips else ""
        W(f"| {f} | {full:+.1f} | {len(months)} | {len(flips)}{extra} | "
          f"{'YES' if not flips else '**no**'} |")

    # ---- 3. Hypotheses ---------------------------------------------------------------
    W("\n## 3. Primary hypotheses\n")

    # H-SESSION (PRIMARY, the pre-registered one-shot verdict): pooled across all 4 strategies.
    m, lo, hi, se = ci(pooled(positions), bank_block)
    if lo is not None and lo > 0:
        v = "SUPPORTED — the family carries edge (CI > 0)"
    elif hi is not None and hi < 0:
        v = "REFUTED — CI strictly < 0"
    else:
        v = f"NOT DETECTABLE at n={len(positions)} (MDE@n {mde(se):.0f})"
    W(f"\n**H-SESSION (PRIMARY)** — session/time-of-day family-pooled $/position 2019–2023 > 0?  "
      f"{m:+.2f} (n={len(positions)}), 95% CI [{lo:+.2f}, {hi:+.2f}] → **{v}**\n")
    W("\n_Pre-registered one-shot verdict. **Stop rule (binding, LEDGER Session 7):** a REFUTED "
      "or NOT-DETECTABLE family verdict is the program's clean stop — not a prompt for a fifth "
      "strategy or a knob sweep._\n")

    # Per-strategy verdicts (D5' leg-1 + residence input).
    W("\n**Per-strategy** (D5′ leg-1 — survive = CI excludes 0 on the positive side):\n")
    W("\n| strategy | pooled $/pos | n | 95% CI | MDE@n | verdict |")
    W("|---|---|---|---|---|---|")
    for f in fams:
        ps = [p for p in positions if p.family == f]
        if len(ps) < 2:
            W(f"| {f} | — | {len(ps)} | — | — | n<2 |")
            continue
        m, lo, hi, se = ci(pooled(ps), blocks[f])
        vv = "SURVIVE" if lo > 0 else "REFUTED" if hi < 0 else "not detectable"
        W(f"| {f} | {m:+.1f} | {len(ps)} | [{lo:+.0f}, {hi:+.0f}] | {mde(se):.0f} | {vv} |")

    # H-TF: M15-pooled vs H1-pooled per strategy (the owner's 15m question).
    W("\n**H-TF** — M15 vs H1 pooled $/position per strategy (does finer execution help, or does "
      "the spread eat it? — descriptive, both reported):\n")
    W("\n| strategy | M15 $/pos (n) | H1 $/pos (n) | M15 − H1 |")
    W("|---|---|---|---|")
    for f in fams:
        m15 = [p for p in positions if p.family == f and p.tf == "M15"]
        h1 = [p for p in positions if p.family == f and p.tf == "H1"]
        m15m = statistics.fmean(pooled(m15)) if m15 else None
        h1m = statistics.fmean(pooled(h1)) if h1 else None
        m15s = f"{m15m:+.1f} ({len(m15)})" if m15m is not None else "—"
        h1s = f"{h1m:+.1f} ({len(h1)})" if h1m is not None else "—"
        diff = f"{m15m - h1m:+.1f}" if (m15m is not None and h1m is not None) else "—"
        W(f"| {f} | {m15s} | {h1s} | {diff} |")
    m15all = [p for p in positions if p.tf == "M15"]
    h1all = [p for p in positions if p.tf == "H1"]
    if m15all and h1all:
        W(f"\nFamily M15-pooled {statistics.fmean(pooled(m15all)):+.2f} (n={len(m15all)}) vs "
          f"H1-pooled {statistics.fmean(pooled(h1all)):+.2f} (n={len(h1all)}).\n")

    # ---- 4. Residence/park -----------------------------------------------------------
    W("\n## 4. Residence / park recommendation (owner decides at GV4)\n")
    W("\n| strategy | pooled $/pos | CI | leg 2 | leg 4 | recommendation |")
    W("|---|---|---|---|---|---|")
    for f in fams:
        ps = [p for p in positions if p.family == f]
        if len(ps) < 2:
            continue
        m, lo, hi, se = ci(pooled(ps), blocks[f])
        if lo > 0:
            rec = "**RESIDE** — positive, CI excludes 0"
        elif hi < 0:
            rec = "**PARK** — negative, CI excludes 0"
        else:
            rec = f"hold — not detectable at n={len(ps)} (MDE {mde(se):.0f})"
        l2 = leg2.get(f)
        W(f"| {f} | {m:+.1f} | [{lo:+.0f}, {hi:+.0f}] | "
          f"{'n/a' if l2 is None else ('YES' if l2 else 'no')} | "
          f"{'YES' if leg4.get(f) else 'no'} | {rec} |")
    W("\n_Recommendations are mechanical from leg 1 and are INPUT to the owner's GV2 call, "
      "not a substitute for it._\n")

    # ---- 5. Guards -------------------------------------------------------------------
    W("\n## 5. Guards (re-pasted)\n")
    W("```")
    for k, val in guard_vals.items():
        W(f"{k} = {val}")
    W("```")
    W("\nBoth must be 0: V4 windows end 2023-12-31T00:00 by construction (2024 era-holdout "
      "untouched); EMBARGO-2 untouched.\n")
    return "\n".join(L)


def read_guards(db):
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    era = con.execute("""SELECT COUNT(*) FROM BacktestRuns WHERE BacktestFrom <= '2024-12-31'
        AND BacktestTo >= '2024-01-01' AND StartedAtUtc >= '2026-07-16'""").fetchone()[0]
    emb = con.execute(
        "SELECT COUNT(*) FROM BacktestRuns WHERE BacktestFrom >= '2026-07-06'").fetchone()[0]
    con.close()
    return {"era-holdout (2024 touched since 2026-07-16)": era,
            "EMBARGO-2 (BacktestFrom >= 2026-07-06)": emb}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--experiment", required=True)
    ap.add_argument("--db", default=str(DEFAULT_DB))
    ap.add_argument("--marketdata", default=str(DEFAULT_MD))
    ap.add_argument("--out", default=str(ROOT / "evidence" / "v4-harvest.md"))
    ap.add_argument("--quick", action="store_true", help="skip the M1 spread stress")
    args = ap.parse_args()

    cells = load_cells(args.db, args.experiment)
    print(f"cells: {len(cells)}", flush=True)
    positions, nrows = load_positions(args.db, cells)
    print(f"trade rows: {nrows} -> positions: {len(positions)} "
          f"(F70 split factor {nrows / max(1, len(positions)):.4f})", flush=True)
    if not positions:
        print("no positions — nothing to harvest")
        sys.exit(1)

    stats = defaultdict(int)
    if args.quick:
        for p in positions:
            p.stressed = {}
    else:
        print("spread stress (M1 ask-side join)...", flush=True)
        stats = attach_stress(positions, args.marketdata)
        print(f"  lookups: {dict(stats)}", flush=True)

    md = report(cells, positions, stats, read_guards(args.db), args.quick,
                args.experiment, census_integrity(args.db, cells))
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(md, encoding="utf-8")
    print(f"WROTE {out} ({len(md)} chars)")


if __name__ == "__main__":
    main()
