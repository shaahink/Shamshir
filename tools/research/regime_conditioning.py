"""Old-S2(b) regime conditioning, absorbed into iter-viability Session 1 (zero new runs).

Pre-registered in docs/iterations/iter-viability/LEDGER.md (Session 1, Pre-registration B):
is the F64 H1->H2 bank-wide shift (38->13 positive cells) predictable by EXTERNAL regime
variables (RV20 realized-vol percentile, ER20 Kaufman efficiency ratio), tested as a 2x2
family-class x census-half interaction on the existing 4,461 census trades of experiment
075D5240? Class labels are fixed by strategy construction, never by observed performance.

Inference: cluster bootstrap over ISO weeks (ClosedAtUtc), percentile CIs. --mde-only runs the
blinded variance pass (prints SE/MDE, no point estimates) so the MDE can be recorded in the
ledger before unblinding (D1).

Usage:
  python regime_conditioning.py --mde-only
  python regime_conditioning.py
"""
import argparse
import datetime as dt
import math
import random
import sqlite3
import statistics
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TRADING_DB = ROOT / "src" / "TradingEngine.Web" / "data" / "trading.db"
MARKET_DB = ROOT / "src" / "TradingEngine.Web" / "data" / "marketdata.db"

SPLIT = dt.date(2025, 12, 3)          # F64 half boundary
CENSUS_FROM = dt.date(2025, 7, 4)
CENSUS_TO = dt.date(2026, 5, 5)
LOOKBACK = 20
BOOT_REPS = 2000
SEED = 20260716

CONTINUATION = {"trend-breakout", "ema-alignment", "super-trend", "mtf-trend",
                "macd-momentum", "session-breakout", "bb-squeeze"}
CONTRARIAN = {"mean-reversion", "rsi-divergence"}

ap = argparse.ArgumentParser(description=__doc__)
ap.add_argument("--mde-only", action="store_true",
                help="blinded variance pass: print bootstrap SE + MDE only, no point estimates")
ap.add_argument("--experiment", default="075D5240")
args = ap.parse_args()


def parse_ts(s):
    return dt.datetime.fromisoformat(s.replace("Z", "").split("+")[0])


# ---------- trades ----------
con = sqlite3.connect(str(TRADING_DB))
con.row_factory = sqlite3.Row
trades = con.execute("""
  SELECT t.StrategyId, t.Symbol, t.OpenedAtUtc, t.ClosedAtUtc,
         CAST(t.NetPnLAmount AS REAL) pnl, CAST(t.RMultiple AS REAL) r
  FROM TradeResults t
  WHERE t.RunId IN (SELECT er.BacktestRunId FROM ExperimentRuns er
                    WHERE er.ExperimentId LIKE ?)
""", (args.experiment + "%",)).fetchall()
con.close()
assert len(trades) == 4461, f"expected the 4,461 census trades, got {len(trades)}"

# ---------- daily closes per symbol (H1 tape -> last close per UTC date) ----------
mcon = sqlite3.connect(str(MARKET_DB))
mcon.row_factory = sqlite3.Row
symbols = sorted({t["Symbol"] for t in trades})
daily_close = {}
for sym in symbols:
    rows = mcon.execute("""
      SELECT substr(OpenTimeUtc, 1, 10) d, Close
      FROM MarketDataBars
      WHERE Symbol = ? AND Timeframe = 'H1' AND OpenTimeUtc >= '2025-05-01'
      ORDER BY OpenTimeUtc
    """, (sym,)).fetchall()
    closes = {}
    for r in rows:                       # last H1 close of each UTC date wins
        closes[r["d"]] = float(r["Close"])
    daily_close[sym] = sorted((dt.date.fromisoformat(d), c) for d, c in closes.items())
mcon.close()

# ---------- external regime series: RV20 + ER20, labeled High vs per-symbol census median ----------
regime = {}                              # (sym, date) -> {"rv": High?, "er": High?}
for sym in symbols:
    series = daily_close[sym]
    if len(series) < LOOKBACK + 2:
        continue
    dates = [d for d, _ in series]
    closes = [c for _, c in series]
    rv, er = {}, {}
    for i in range(LOOKBACK, len(series)):
        rets = [math.log(closes[j] / closes[j - 1]) for j in range(i - LOOKBACK + 1, i + 1)]
        rv[dates[i]] = statistics.stdev(rets)
        denom = sum(abs(closes[j] - closes[j - 1]) for j in range(i - LOOKBACK + 1, i + 1))
        er[dates[i]] = abs(closes[i] - closes[i - LOOKBACK]) / denom if denom > 0 else 0.0
    in_window = [d for d in rv if CENSUS_FROM <= d <= CENSUS_TO]
    rv_med = statistics.median(rv[d] for d in in_window)
    er_med = statistics.median(er[d] for d in in_window)
    for d in rv:
        regime[(sym, d)] = {"rv": rv[d] >= rv_med, "er": er[d] >= er_med,
                            "rv_raw": rv[d], "er_raw": er[d]}

regime_dates = defaultdict(list)         # sym -> sorted regime dates (for prior-day lookup)
for (sym, d) in regime:
    regime_dates[sym].append(d)
for sym in regime_dates:
    regime_dates[sym].sort()


def prior_regime(sym, entry_dt):
    """Regime of the last completed UTC day strictly before entry (no lookahead)."""
    days = regime_dates.get(sym, [])
    lo, hi = 0, len(days)
    target = entry_dt.date()
    while lo < hi:
        mid = (lo + hi) // 2
        if days[mid] < target:
            lo = mid + 1
        else:
            hi = mid
    return regime[(sym, days[lo - 1])] if lo > 0 else None


# ---------- label trades ----------
rows = []
unlabeled = 0
for t in trades:
    cls = "cont" if t["StrategyId"] in CONTINUATION else "contr"
    assert t["StrategyId"] in CONTINUATION | CONTRARIAN, t["StrategyId"]
    opened = parse_ts(t["OpenedAtUtc"])
    closed = parse_ts(t["ClosedAtUtc"])
    reg = prior_regime(t["Symbol"], opened)
    if reg is None:
        unlabeled += 1
        continue
    iso = closed.isocalendar()
    rows.append({
        "cls": cls, "half": "H1" if closed.date() < SPLIT else "H2",
        "er": reg["er"], "rv": reg["rv"], "pnl": t["pnl"], "r": t["r"],
        "week": (iso[0], iso[1]),
    })
print(f"trades labeled: {len(rows)} (unlabeled, no prior regime day: {unlabeled})")


# ---------- bootstrap machinery: per-week cell aggregates ----------
def cell_agg(rows, key):
    """week -> {cellkey: (sum_pnl, n)}"""
    per_week = defaultdict(lambda: defaultdict(lambda: [0.0, 0]))
    for r in rows:
        c = per_week[r["week"]][key(r)]
        c[0] += r["pnl"]
        c[1] += 1
    return per_week


def contrast(cells, a, b, c, d):
    """(mean[a] - mean[b]) - (mean[c] - mean[d]); None if any cell empty."""
    means = {}
    for k in (a, b, c, d):
        s, n = cells.get(k, (0.0, 0))
        if n == 0:
            return None
        means[k] = s / n
    return (means[a] - means[b]) - (means[c] - means[d])


def boot(per_week, stat, reps=BOOT_REPS, seed=SEED):
    weeks = sorted(per_week.keys())
    rng = random.Random(seed)
    out = []
    for _ in range(reps):
        sample = [weeks[rng.randrange(len(weeks))] for _ in weeks]
        cells = defaultdict(lambda: [0.0, 0])
        for w in sample:
            for k, (s, n) in per_week[w].items():
                cells[k][0] += s
                cells[k][1] += n
        v = stat({k: (s, n) for k, (s, n) in cells.items()})
        if v is not None:
            out.append(v)
    out.sort()
    se = statistics.stdev(out)
    lo = out[int(0.025 * len(out))]
    hi = out[int(0.975 * len(out)) - 1]
    return se, lo, hi


def full_cells(per_week):
    cells = defaultdict(lambda: [0.0, 0])
    for w in per_week.values():
        for k, (s, n) in w.items():
            cells[k][0] += s
            cells[k][1] += n
    return {k: (s, n) for k, (s, n) in cells.items()}


# (i) class x half interaction
pw_half = cell_agg(rows, lambda r: (r["cls"], r["half"]))
stat_half = lambda cells: contrast(cells, ("cont", "H2"), ("cont", "H1"), ("contr", "H2"), ("contr", "H1"))
se_i, lo_i, hi_i = boot(pw_half, stat_half)

# (iii) class x regime interaction (full window), ER primary / RV secondary
pw_er = cell_agg(rows, lambda r: (r["cls"], r["er"]))
stat_er = lambda cells: contrast(cells, ("cont", True), ("cont", False), ("contr", True), ("contr", False))
se_er, lo_er, hi_er = boot(pw_er, stat_er)

pw_rv = cell_agg(rows, lambda r: (r["cls"], r["rv"]))
stat_rv = lambda cells: contrast(cells, ("cont", True), ("cont", False), ("contr", True), ("contr", False))
se_rv, lo_rv, hi_rv = boot(pw_rv, stat_rv)

if args.mde_only:
    print("\n=== BLINDED VARIANCE PASS (D1) — no point estimates ===")
    print(f"(i)   class x half interaction:  SE_boot = ${se_i:,.0f}/trade   MDE(2.8xSE) = ${2.8 * se_i:,.0f}/trade")
    print(f"(iii) class x ER20 interaction:  SE_boot = ${se_er:,.0f}/trade  MDE = ${2.8 * se_er:,.0f}/trade")
    print(f"(iii) class x RV20 interaction:  SE_boot = ${se_rv:,.0f}/trade  MDE = ${2.8 * se_rv:,.0f}/trade")
    raise SystemExit(0)

# ---------- full results ----------
print("\n=== (i) 2x2 class x half — $/trade (n) [expR] ===")
cells = full_cells(pw_half)
er_by = defaultdict(list)
for r in rows:
    er_by[(r["cls"], r["half"])].append(r["r"])
for cls in ("cont", "contr"):
    line = f"  {cls:5s}"
    for half in ("H1", "H2"):
        s, n = cells[(cls, half)]
        line += f"  {half}: {s / n:+8.1f} $/t (n={n:4d}) [{statistics.mean(er_by[(cls, half)]):+.3f}R]"
    print(line)
beta = stat_half(cells)
print(f"  interaction beta = ${beta:,.1f}/trade   95% CI [{lo_i:,.1f}, {hi_i:,.1f}]   SE {se_i:,.1f}")

print("\n=== (ii) regime mix shift H1 -> H2 ===")
for var in ("er", "rv"):
    # external day-share: fraction of symbol-days High per half (unweighted across symbols)
    d1 = [1 if regime[(s, d)][var] else 0 for (s, d) in regime if CENSUS_FROM <= d < SPLIT]
    d2 = [1 if regime[(s, d)][var] else 0 for (s, d) in regime if SPLIT <= d <= CENSUS_TO]
    # trade-entry share (endogenous to entries, reported for completeness)
    t1 = [1 if r[var] else 0 for r in rows if r["half"] == "H1"]
    t2 = [1 if r[var] else 0 for r in rows if r["half"] == "H2"]
    pw_share = cell_agg([{**r, "pnl": 1.0 if r[var] else 0.0} for r in rows], lambda r: r["half"])
    stat_share = lambda cells: (cells["H2"][0] / cells["H2"][1] - cells["H1"][0] / cells["H1"][1]
                                if cells.get("H1", (0, 0))[1] and cells.get("H2", (0, 0))[1] else None)
    se_s, lo_s, hi_s = boot(pw_share, stat_share)
    print(f"  {var.upper()}20-High day-share:   H1 {statistics.mean(d1):.3f} -> H2 {statistics.mean(d2):.3f}"
          f"   (delta {statistics.mean(d2) - statistics.mean(d1):+.3f})")
    print(f"  {var.upper()}20-High trade-share: H1 {statistics.mean(t1):.3f} -> H2 {statistics.mean(t2):.3f}"
          f"   delta CI [{lo_s:+.3f}, {hi_s:+.3f}]")

print("\n=== (iii) class x regime interaction, full window — $/trade (n) [expR] ===")
for var, pw, se_, lo_, hi_ in (("ER20", pw_er, se_er, lo_er, hi_er),
                               ("RV20", pw_rv, se_rv, lo_rv, hi_rv)):
    cells = full_cells(pw)
    r_by = defaultdict(list)
    for r in rows:
        r_by[(r["cls"], r[var[:2].lower()])].append(r["r"])
    for cls in ("cont", "contr"):
        line = f"  [{var}] {cls:5s}"
        for hi_flag, lab in ((True, "High"), (False, "Low ")):
            s, n = cells.get((cls, hi_flag), (0.0, 0))
            m = s / n if n else float("nan")
            line += f"  {lab}: {m:+8.1f} $/t (n={n:4d}) [{statistics.mean(r_by[(cls, hi_flag)]):+.3f}R]"
        print(line)
    gamma = contrast(cells, ("cont", True), ("cont", False), ("contr", True), ("contr", False))
    print(f"  [{var}] gamma = ${gamma:,.1f}/trade   95% CI [{lo_:,.1f}, {hi_:,.1f}]   SE {se_:,.1f}")

print("\n=== conditional family-class expR split (run-budget threshold: >= 0.10R) ===")
for var in ("er", "rv"):
    for cls in ("cont", "contr"):
        hi_r = [r["r"] for r in rows if r["cls"] == cls and r[var]]
        lo_r = [r["r"] for r in rows if r["cls"] == cls and not r[var]]
        print(f"  [{var.upper()}20] {cls:5s}: High {statistics.mean(hi_r):+.3f}R (n={len(hi_r)})"
              f"  Low {statistics.mean(lo_r):+.3f}R (n={len(lo_r)})"
              f"  split {abs(statistics.mean(hi_r) - statistics.mean(lo_r)):.3f}R")
