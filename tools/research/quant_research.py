"""Deep quant research over a scored census experiment (default: alpha-loop 075D5240).

Sections:
  A. Scored-cell pool: metrics, velocity distribution.
  B. Daily equity series per scored cell -> pairwise correlation.
  C. Portfolio aggregation (pools A/B/C) -> rolling 30d challenge windows at 1x/2x/3x.
  D. Trade-level entry/exit quality per strategy (all runs' trades in the experiment).

Committed at iter-structural-edge S0 from the 2026-07-16 research-session scratchpad;
outputs are the source of RESEARCH.md sections 2, 4 and 5 (F65 exit truncation, F67 entry
noise floor, F68 family triage).

Honesty notes baked in:
  - Composite score NOT used for ranking (F63: sv1 FtmoSurvival was a placeholder).
  - The census window is IN-SAMPLE for any selection made here; every portfolio number
    below is a best-case in-sample bound, not an OOS claim (see F64 / split_half.py).
  - Combining cells = summing per-run $ deltas onto one $100k baseline (cells were
    backtested solo on $100k; linear-scaling approximation, flagged not hidden).

Usage:
  python quant_research.py --experiment 075D5240
  python quant_research.py --experiment <prefix> [--db path] [--base 100000]
"""
import argparse, sqlite3, json, math, datetime as dt
from collections import defaultdict
from itertools import combinations
from pathlib import Path

DEFAULT_DB = Path(__file__).resolve().parents[2] / "src" / "TradingEngine.Web" / "data" / "trading.db"

# Cells parked by R3 on real evidence (iter-alpha-loop LEDGER) — excluded from the usable pool.
PARKED = {("trend-breakout", "XAGUSD", "h1"), ("mean-reversion", "GBPUSD", "h1")}

ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument("--experiment", required=True, help="experiment id or prefix, e.g. 075D5240")
ap.add_argument("--db", default=str(DEFAULT_DB), help=f"trading.db path (default: {DEFAULT_DB})")
ap.add_argument("--base", type=float, default=100_000.0, help="portfolio baseline (default 100000)")
args = ap.parse_args()

DB, EXP, BASELINE = args.db, f"{args.experiment}%", args.base

con = sqlite3.connect(DB)
con.row_factory = sqlite3.Row

# ---------- A. scored cells ----------
cells = []
for r in con.execute("""
    SELECT er.VariantLabel vl, er.BacktestRunId rid,
           json_extract(er.ScoreJson,'$.Components.ExpectancyR') expR,
           json_extract(er.ScoreJson,'$.Composite') comp,
           br.Symbol sym, br.Period tf, br.TotalTrades n,
           CAST(br.NetProfit AS REAL) net, CAST(br.MaxDrawdownPct AS REAL) dd,
           br.WinRatePct wr, br.BacktestFrom bf, br.BacktestTo bt,
           CAST(br.InitialBalance AS REAL) bal
    FROM ExperimentRuns er JOIN BacktestRuns br ON br.RunId = er.BacktestRunId
    WHERE er.ExperimentId LIKE ? AND json_extract(er.ScoreJson,'$.Composite') IS NOT NULL
""", (EXP,)):
    strat = r["vl"].split("/")[0]
    cells.append(dict(strat=strat, sym=r["sym"], tf=r["tf"], rid=r["rid"], n=r["n"],
                      net=r["net"], dd=r["dd"], wr=r["wr"], expR=r["expR"], comp=r["comp"],
                      bf=r["bf"][:10], bt=r["bt"][:10], bal=r["bal"],
                      parked=(strat, r["sym"], r["tf"]) in PARKED))
if not cells:
    raise SystemExit(f"no scored runs for experiment LIKE '{EXP}' in {DB}")

pos = [c for c in cells if c["net"] > 0 and not c["parked"]]
neg = [c for c in cells if c["net"] <= 0]
print(f"=== A. CENSUS POOL === scored={len(cells)} positive={len([c for c in cells if c['net']>0])} "
      f"(usable non-parked {len(pos)}) negative={len(neg)}")
print(f"window: {cells[0]['bf']} -> {cells[0]['bt']}, balance {cells[0]['bal']:.0f}")
days = (dt.date.fromisoformat(cells[0]["bt"]) - dt.date.fromisoformat(cells[0]["bf"])).days
yr = days / 365.25
print(f"census span days={days}")
pos.sort(key=lambda c: -c["net"])
print(f"\n{'cell':44s} {'n':>4s} {'tr/30d':>6s} {'net$':>7s} {'ret%/30d':>8s} {'maxDD%':>6s} {'expR':>5s} {'wr%':>5s}")
for c in pos:
    per30 = c["net"] / days * 30
    print(f"{c['strat']+'/'+c['sym']+'/'+c['tf']:44s} {c['n']:4d} {c['n']/days*30:6.1f} "
          f"{c['net']:7.0f} {per30/BASELINE*100:8.2f} {c['dd']*100 if c['dd']<1 else c['dd']:6.2f} "
          f"{c['expR']:5.2f} {c['wr']:5.1f}")
tot_net = sum(c["net"] for c in pos); tot_n = sum(c["n"] for c in pos)
print(f"\nALL {len(pos)} positive cells summed: net=${tot_net:.0f} trades={tot_n} "
      f"-> {tot_net/days*30/BASELINE*100:.2f}%/30d at 1x, {tot_n/days*30:.0f} trades/30d")

# ---------- B. daily equity series ----------
def daily_equity(rid):
    rows = con.execute("""SELECT substr(TimestampUtc,1,10) d, Equity e
                          FROM EquitySnapshots WHERE RunId=? ORDER BY TimestampUtc""", (rid,)).fetchall()
    out = {}
    for r in rows: out[r["d"]] = r["e"]   # last snapshot per date wins
    return out

series = {}
for c in pos:
    eq = daily_equity(c["rid"])
    if len(eq) < 30:
        print(f"WARN: {c['strat']}/{c['sym']}/{c['tf']} has only {len(eq)} equity days -> excluded")
        continue
    series[(c["strat"], c["sym"], c["tf"])] = (c, eq)

# common calendar (union), forward-fill, deltas
all_dates = sorted({d for _, eq in series.values() for d in eq})
def deltas(eq):
    out, prev = {}, None
    for d in all_dates:
        cur = eq.get(d, prev if prev is not None else BASELINE)
        if prev is not None: out[d] = cur - prev
        prev = cur
    return out
dser = {k: deltas(eq) for k, (c, eq) in series.items()}

def pearson(xs, ys):
    n = len(xs)
    mx, my = sum(xs)/n, sum(ys)/n
    vx = sum((x-mx)**2 for x in xs); vy = sum((y-my)**2 for y in ys)
    if vx == 0 or vy == 0: return 0.0
    return sum((x-mx)*(y-my) for x, y in zip(xs, ys)) / math.sqrt(vx*vy)

def avg_pairwise_corr(keys):
    cors = []
    for a, b in combinations(keys, 2):
        da, db = dser[a], dser[b]
        common = [d for d in all_dates[1:] if d in da and d in db]
        xs = [da[d] for d in common]; ys = [db[d] for d in common]
        cors.append((pearson(xs, ys), a, b))
    return cors

# ---------- C. portfolio pools ----------
R4 = [("trend-breakout","XAUUSD","h4"), ("mean-reversion","AUDUSD","h1"),
      ("ema-alignment","EURJPY","h1")]
poolA = [k for k in R4 if k in series]
# pool B: top-8 by expectancyR among positive, diversity cap 2 per symbol
byExp = sorted(series.keys(), key=lambda k: -series[k][0]["expR"])
poolB, symcnt = [], defaultdict(int)
for k in byExp:
    if symcnt[k[1]] >= 2: continue
    poolB.append(k); symcnt[k[1]] += 1
    if len(poolB) == 8: break
poolC = list(series.keys())

def challenge(pool_keys, k_scale, target=0.10, daily_cap=0.05, max_loss=0.10, win_days=30):
    """Aggregate deltas, roll windows over every start date. Returns stats."""
    agg = {d: sum(dser[k].get(d, 0.0) for k in pool_keys) * k_scale for d in all_dates[1:]}
    dates = all_dates[1:]
    # equity curve
    eq, cur, peak, maxdd, worst = [], BASELINE, BASELINE, 0.0, 0.0
    for d in dates:
        cur += agg[d]; peak = max(peak, cur)
        maxdd = max(maxdd, (peak-cur)/peak)
        worst = min(worst, agg[d])
        eq.append((d, cur))
    # rolling windows
    passes = fails = inc = 0
    idx = {d: i for i, d in enumerate(dates)}
    for si, sd in enumerate(dates):
        end = (dt.date.fromisoformat(sd) + dt.timedelta(days=win_days)).isoformat()
        if end > dates[-1]: break
        start_eq = BASELINE if si == 0 else eq[si-1][1]
        cum, verdict = start_eq, "inc"
        for d, e in eq[si:]:
            if d > end: break
            if agg[d] < -daily_cap * start_eq: verdict = "fail"; break
            if e < start_eq * (1 - max_loss): verdict = "fail"; break
            if e >= start_eq * (1 + target): verdict = "pass"; break
        if verdict == "pass": passes += 1
        elif verdict == "fail": fails += 1
        else: inc += 1
    tot = passes + fails + inc
    ret30 = (eq[-1][1] - BASELINE) / days * 30 / BASELINE * 100
    return dict(final=eq[-1][1], ret30=ret30, maxdd=maxdd*100, worst=worst/BASELINE*100,
                p=passes, f=fails, i=inc, tot=tot)

for name, pool in [("A: R4 carries (3 cells)", poolA), ("B: top-8 expR, <=2/symbol", poolB),
                   ("C: ALL positive cells", poolC)]:
    print(f"\n=== C. PORTFOLIO {name} ===")
    for k in pool:
        c = series[k][0]
        print(f"   {k[0]}/{k[1]}/{k[2]}  net={c['net']:.0f} n={c['n']} expR={c['expR']:.2f}")
    cors = avg_pairwise_corr(pool)
    if cors:
        vals = [c[0] for c in cors]
        print(f" pairwise corr: avg={sum(vals)/len(vals):+.3f} min={min(vals):+.3f} max={max(vals):+.3f}")
        hi = sorted(cors, key=lambda t: -abs(t[0]))[:3]
        for v, a, b in hi: print(f"   highest |corr|: {a[0]}/{a[1]} vs {b[0]}/{b[1]}: {v:+.3f}")
    sum_dd = sum(series[k][0]["dd"]*(100 if series[k][0]["dd"]<1 else 1) for k in pool)
    for ks in (1, 2, 3):
        r = challenge(pool, ks)
        print(f" k={ks}x: final=${r['final']:,.0f} ret={r['ret30']:+.2f}%/30d maxDD={r['maxdd']:.2f}% "
              f"worstDay={r['worst']:+.2f}% | 30d windows: {r['p']} pass / {r['f']} fail / {r['i']} incomplete (of {r['tot']})")
    print(f" (sum of solo maxDDs: {sum_dd:.2f}% -> diversification check)")

# ---------- D. trade-level entry/exit quality ----------
print("\n=== D. ENTRY/EXIT QUALITY (all census-run trades) ===")
trades = con.execute("""
    SELECT t.StrategyId s, t.RMultiple r, t.MaeR mae, t.MfeR mfe, t.ExitReason ex,
           t.DurationSeconds dur, t.NetPnLAmount pnl
    FROM TradeResults t
    WHERE t.RunId IN (SELECT BacktestRunId FROM ExperimentRuns WHERE ExperimentId LIKE ?)
""", (EXP,)).fetchall()
print(f"census trades: {len(trades)}")
by = defaultdict(list)
for t in trades: by[t["s"]].append(t)
print(f"\n{'strategy':18s} {'n':>5s} {'wr%':>5s} {'expR':>6s} {'avgWinR':>7s} {'avgLossR':>8s} "
      f"{'mfe>=1&net<=0':>13s} {'neverRan(mfe<.3)':>16s} {'medDur(h)':>9s}")
for s, ts in sorted(by.items(), key=lambda kv: -len(kv[1])):
    rs = [t["r"] for t in ts]
    wins = [r for r in rs if r > 0]; losses = [r for r in rs if r <= 0]
    mfes = [t for t in ts if t["mfe"] is not None]
    giveback = [t for t in mfes if t["mfe"] >= 1.0 and t["pnl"] <= 0]
    ranfrac = [t for t in mfes if t["mfe"] < 0.3]
    durs = sorted(t["dur"] for t in ts)
    med = durs[len(durs)//2]/3600 if durs else 0
    print(f"{s:18s} {len(ts):5d} {100*len(wins)/len(rs):5.1f} {sum(rs)/len(rs):6.2f} "
          f"{sum(wins)/len(wins) if wins else 0:7.2f} {sum(losses)/len(losses) if losses else 0:8.2f} "
          f"{100*len(giveback)/max(1,len(mfes)):12.1f}% {100*len(ranfrac)/max(1,len(mfes)):15.1f}% {med:9.1f}")
# exit reasons
print("\nExit reasons (census trades):")
exr = defaultdict(lambda: [0, 0.0])
for t in trades: exr[t["ex"]][0] += 1; exr[t["ex"]][1] += t["r"]
for ex, (n, rsum) in sorted(exr.items(), key=lambda kv: -kv[1][0]):
    print(f"  {ex:28s} n={n:5d}  avgR={rsum/n:+.2f}")
# MFE capture on winners
mfes = [t for t in trades if t["mfe"] is not None and t["mfe"] > 0.5]
cap = [max(0.0, t["r"]) / t["mfe"] for t in mfes]
print(f"\nMFE capture (trades with MfeR>0.5): n={len(mfes)}, mean captured R/MFE = {sum(cap)/len(cap):.2f}")
