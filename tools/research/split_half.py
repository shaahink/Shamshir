"""Split-half selection honesty test (F64) + cost drag (F66) + weekly correlation.

Question: if we'd picked 'positive cells' using only H1 (census first half),
what would H2 have paid? This estimates the selection-on-noise haircut that
any portfolio-of-cells thesis must survive. Uses per-trade PnL by ClosedAtUtc.

Committed at iter-structural-edge S0 from the 2026-07-16 research-session scratchpad;
outputs are the source of RESEARCH.md sections 1 and 3. The F64 block is also available
without python as `research persistence --experiment <id> --split <date>` (CLI verb,
served by SplitHalfPersistenceService — same math, one command).

Usage:
  python split_half.py --experiment 075D5240 --split 2025-12-03
  python split_half.py --experiment <prefix> [--split yyyy-mm-dd] [--db path] [--base 100000]

--split defaults to the census midpoint (what the original research session used,
which for experiment 075D5240 lands on 2025-12-03).
"""
import argparse, sqlite3, math, datetime as dt
from collections import defaultdict
from itertools import combinations
from pathlib import Path

DEFAULT_DB = Path(__file__).resolve().parents[2] / "src" / "TradingEngine.Web" / "data" / "trading.db"

ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument("--experiment", required=True, help="experiment id or prefix, e.g. 075D5240")
ap.add_argument("--split", default=None, help="H1/H2 boundary date yyyy-mm-dd (default: census midpoint)")
ap.add_argument("--db", default=str(DEFAULT_DB), help=f"trading.db path (default: {DEFAULT_DB})")
ap.add_argument("--base", type=float, default=100_000.0, help="challenge account base (default 100000)")
args = ap.parse_args()

DB, EXP, BASE = args.db, f"{args.experiment}%", args.base
con = sqlite3.connect(DB); con.row_factory = sqlite3.Row

runs = con.execute("""
  SELECT er.VariantLabel vl, er.BacktestRunId rid, br.TotalTrades n,
         CAST(br.NetProfit AS REAL) net, CAST(br.GrossPnL AS REAL) gross,
         CAST(br.CommissionTotal AS REAL) comm, CAST(br.SwapTotal AS REAL) swap,
         br.BacktestFrom bf, br.BacktestTo bt
  FROM ExperimentRuns er JOIN BacktestRuns br ON br.RunId=er.BacktestRunId
  WHERE er.ExperimentId LIKE ? AND json_extract(er.ScoreJson,'$.Composite') IS NOT NULL
""", (EXP,)).fetchall()
if not runs:
    raise SystemExit(f"no scored runs for experiment LIKE '{EXP}' in {DB}")

bf = dt.date.fromisoformat(runs[0]["bf"][:10]); bt = dt.date.fromisoformat(runs[0]["bt"][:10])
mid = args.split or (bf + (bt - bf) / 2).isoformat()
h2_days = (bt - dt.date.fromisoformat(mid)).days
print(f"census {bf} -> {bt}, split {mid}, H2 span {h2_days}d")

# --- cost drag on the positive pool (F66) ---
pos = [r for r in runs if r["net"] > 0]
g = sum(r["gross"] for r in pos); c = sum(r["comm"] for r in pos); s = sum(r["swap"] for r in pos)
print(f"\n=== COST DRAG ({len(pos)} positive cells) === gross={g:,.0f} commission={c:,.0f} swap={s:,.0f} net={sum(r['net'] for r in pos):,.0f}")
print(f"costs eat {100*(c+s)/g:.1f}% of gross" if g else "")

# --- per-cell H1/H2 PnL from trades ---
h1pnl, h2pnl, h2daily = {}, {}, {}
for r in runs:
    t1 = con.execute("SELECT COALESCE(SUM(NetPnLAmount),0) FROM TradeResults WHERE RunId=? AND substr(ClosedAtUtc,1,10) < ?", (r["rid"], mid)).fetchone()[0]
    t2rows = con.execute("SELECT substr(ClosedAtUtc,1,10) d, SUM(NetPnLAmount) p FROM TradeResults WHERE RunId=? AND substr(ClosedAtUtc,1,10) >= ? GROUP BY d", (r["rid"], mid)).fetchall()
    h1pnl[r["vl"]] = t1
    h2pnl[r["vl"]] = sum(x["p"] for x in t2rows)
    h2daily[r["vl"]] = {x["d"]: x["p"] for x in t2rows}

sel = [vl for vl in h1pnl if h1pnl[vl] > 0]           # H1-positive selection
h2_of_sel = sum(h2pnl[vl] for vl in sel)
h1_of_sel = sum(h1pnl[vl] for vl in sel)
persist = [vl for vl in sel if h2pnl[vl] > 0]
print(f"\n=== SPLIT-HALF SELECTION TEST (F64) ===")
print(f"cells positive in H1: {len(sel)}/{len(h1pnl)}  (H1 PnL of selection: ${h1_of_sel:,.0f})")
print(f"same cells in H2:     ${h2_of_sel:,.0f}   -> haircut factor {h2_of_sel/h1_of_sel:.2f}")
print(f"persistence: {len(persist)}/{len(sel)} H1-positive cells stayed positive in H2 ({100*len(persist)/len(sel):.0f}%)")
print(f"H2 return of H1-selected portfolio at 1x: {h2_of_sel/h2_days*30/BASE*100:+.2f}%/30d")

# top-8 by H1 instead of all-positive
top8 = sorted(sel, key=lambda v: -h1pnl[v])[:8]
h2_top8 = sum(h2pnl[vl] for vl in top8)
print(f"top-8 by H1 PnL -> H2: ${h2_top8:,.0f} = {h2_top8/h2_days*30/BASE*100:+.2f}%/30d "
      f"(H1 was ${sum(h1pnl[v] for v in top8):,.0f})")
for vl in top8:
    print(f"   {vl:44s} H1={h1pnl[vl]:8,.0f}  H2={h2pnl[vl]:8,.0f}")

# also: reverse test (H2-positive back-tested on H1) for symmetry
sel2 = [vl for vl in h2pnl if h2pnl[vl] > 0]
h1_of_sel2 = sum(h1pnl[vl] for vl in sel2); h2_of_sel2 = sum(h2pnl[vl] for vl in sel2)
print(f"\nreverse check: H2-positive cells ({len(sel2)}) earned ${h2_of_sel2:,.0f} in H2, ${h1_of_sel2:,.0f} in H1 "
      f"-> factor {h1_of_sel2/h2_of_sel2:.2f}")

# --- H2 challenge windows for the H1-selected portfolio (honest-ish OOS arithmetic) ---
alldates = sorted({d for vl in sel for d in h2daily[vl]})
agg = {d: sum(h2daily[vl].get(d, 0.0) for vl in sel) for d in alldates}
def windows(scale, target=.10, cap=.05, floor=.10, span=30):
    p = f = i = 0
    for si, sd in enumerate(alldates):
        end = (dt.date.fromisoformat(sd) + dt.timedelta(days=span)).isoformat()
        if end > alldates[-1]: break
        eq = BASE; verdict = "i"
        for d in alldates[si:]:
            if d > end: break
            eq += agg[d] * scale
            if agg[d] * scale < -cap * BASE or eq < BASE * (1 - floor): verdict = "f"; break
            if eq >= BASE * (1 + target): verdict = "p"; break
        p += verdict == "p"; f += verdict == "f"; i += verdict == "i"
    return p, f, i
print(f"\nH1-selected portfolio, H2 rolling 30d challenge windows (fresh ${BASE:,.0f} each):")
for k in (1, 2, 3):
    p, f, i = windows(k)
    worst = min(agg.values()) * k / BASE * 100
    print(f" k={k}x: {p} pass / {f} fail / {i} incomplete   worstDay={worst:+.2f}%")

# --- weekly-bucket correlation for the H1-selected pool (sparsity-robust check) ---
def weekly(vl):
    out = defaultdict(float)
    for d, pn in h2daily[vl].items():
        y, w, _ = dt.date.fromisoformat(d).isocalendar()
        out[(y, w)] += pn
    return out
wk = {vl: weekly(vl) for vl in sel}
allwk = sorted({w for v in wk.values() for w in v})
def pearson(xs, ys):
    n=len(xs); mx=sum(xs)/n; my=sum(ys)/n
    vx=sum((x-mx)**2 for x in xs); vy=sum((y-my)**2 for y in ys)
    return 0.0 if vx==0 or vy==0 else sum((a-mx)*(b-my) for a,b in zip(xs,ys))/math.sqrt(vx*vy)
cors = []
for a, b in combinations(sel, 2):
    xs=[wk[a].get(w,0.0) for w in allwk]; ys=[wk[b].get(w,0.0) for w in allwk]
    cors.append(pearson(xs, ys))
cors.sort()
if cors:
    print(f"\nweekly-bucket pairwise corr (H2, {len(sel)} cells): avg={sum(cors)/len(cors):+.3f} "
          f"p10={cors[len(cors)//10]:+.3f} p90={cors[-len(cors)//10]:+.3f} max={cors[-1]:+.3f}")
