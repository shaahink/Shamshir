"""V0 pre-registered read-only re-examination (LEDGER.md Session 1, Pre-registration A).

Question on record: R4's verdict was 'safe but too slow' (0/12 windows hit +10%/30d). Under the
verified unlimited-period FTMO rule, does slow convert to viable — low P(bust), finite E[time]?

Replicates the CORRECTED ChallengeSimulator semantics (V0) over each R4 candidate full-year
run's real daily equity path: anchored untimed windows from every trading-day start to the end
of history; Incomplete = censored. Also reports the 30d PassRate velocity index for reference.
READ-ONLY: no ExperimentRuns writes, no new runs.
"""
import sqlite3
import statistics
from datetime import datetime

DB = r"C:/code/shamshir/src/TradingEngine.Web/data/trading.db"
RUNS = ["9c98ce41", "baf739ad", "38b4d82f", "6d8c8fa0"]

MAX_DAILY = 0.05
MAX_TOTAL = 0.10
MIN_TRADING_DAYS = 4

con = sqlite3.connect(DB)
con.row_factory = sqlite3.Row


def parse_ts(s):
    return datetime.fromisoformat(s.replace("Z", "").split("+")[0])


def build_daily_points(run_id):
    snaps = con.execute(
        "SELECT TimestampUtc, Balance, Equity, DailyStartEquity FROM EquitySnapshots "
        "WHERE RunId=? ORDER BY TimestampUtc", (run_id,)).fetchall()
    trades = con.execute(
        "SELECT OpenedAtUtc, ClosedAtUtc FROM TradeResults WHERE RunId=?", (run_id,)).fetchall()
    if not snaps:
        return []
    opens = sorted(parse_ts(t["OpenedAtUtc"]) for t in trades)

    buckets = []
    start = 0
    n = len(snaps)
    for i in range(1, n + 1):
        boundary = (i == n
                    or snaps[i]["DailyStartEquity"] != snaps[start]["DailyStartEquity"]
                    or parse_ts(snaps[i]["TimestampUtc"]).date() != parse_ts(snaps[i - 1]["TimestampUtc"]).date())
        if not boundary:
            continue
        buckets.append((snaps[start], snaps[i - 1]))
        start = i

    points = []
    for k, (first, last) in enumerate(buckets):
        lo = parse_ts(first["TimestampUtc"])
        hi = parse_ts(buckets[k + 1][0]["TimestampUtc"]) if k + 1 < len(buckets) else datetime.max
        opened = sum(1 for o in opens if lo <= o < hi)
        points.append({
            "date": lo.date(),
            "start_eq": float(first["DailyStartEquity"]),
            "end_eq": float(last["Equity"]),
            "end_bal": float(last["Balance"]) if last["Balance"] is not None else float(last["Equity"]),
            "opened": opened,
        })
    return points


def simulate(days, target_pct):
    """Corrected V0 semantics; verdicts: ('pass', day_idx) / ('fail', reason) / ('censored',)."""
    init = days[0]["start_eq"]
    target = init * (1 + target_pct)
    max_floor = init * (1 - MAX_TOTAL)
    daily_limit = init * MAX_DAILY
    trading_days = 0
    target_reached = False
    prev_bal = init
    for i, d in enumerate(days):
        if d["opened"] > 0:
            trading_days += 1
        day_min = min(d["start_eq"], d["end_eq"])
        if day_min <= max_floor:
            return ("fail", "max-loss", i)
        if day_min <= prev_bal - daily_limit:
            return ("fail", "daily-loss", i)
        if d["end_eq"] >= target:
            target_reached = True
        if target_reached and trading_days >= MIN_TRADING_DAYS:
            return ("pass", i)
        prev_bal = d["end_bal"]
    return ("censored",)


def untimed_metrics(days, target_pct):
    passes, busts, censored, times = 0, 0, 0, []
    for s in range(len(days)):
        w = days[s:]
        r = simulate(w, target_pct)
        if r[0] == "pass":
            passes += 1
            times.append((w[r[1]]["date"] - w[0]["date"]).days + 1)
        elif r[0] == "fail":
            busts += 1
        else:
            censored += 1
    resolved = passes + busts
    return {
        "passes": passes, "busts": busts, "censored": censored,
        "p_bust": busts / resolved if resolved else None,
        "e_time": statistics.mean(times) if times else None,
        "med_time": statistics.median(times) if times else None,
    }


def velocity_30d(days, target_pct):
    n = len(days)
    if n < 30:
        return None
    p = f = i_ = 0
    for s in range(n - 30 + 1):
        r = simulate(days[s:s + 30], target_pct)
        if r[0] == "pass":
            p += 1
        elif r[0] == "fail":
            f += 1
        else:
            i_ += 1
    return p, f, i_, p / (p + f + i_)


for prefix in RUNS:
    row = con.execute(
        "SELECT RunId, TotalTrades, CAST(NetProfit AS REAL) net, BacktestFrom, BacktestTo "
        "FROM BacktestRuns WHERE RunId LIKE ?", (prefix + "%",)).fetchone()
    if row is None:
        print(f"{prefix}: RUN NOT FOUND")
        continue
    days = build_daily_points(row["RunId"])
    print(f"=== {prefix} ({row['TotalTrades']} trades, net ${row['net']:,.0f}, "
          f"{row['BacktestFrom'][:10]} -> {row['BacktestTo'][:10]}, {len(days)} daily buckets) ===")
    for label, tgt in (("Phase1 10%", 0.10), ("Phase2  5%", 0.05)):
        m = untimed_metrics(days, tgt)
        v = velocity_30d(days, tgt)
        pb = f"{m['p_bust']:.3f}" if m["p_bust"] is not None else "n/a (0 resolved)"
        et = f"{m['e_time']:.0f}" if m["e_time"] is not None else "-"
        md = f"{m['med_time']:.0f}" if m["med_time"] is not None else "-"
        vel = f"30d velocity: {v[0]}P/{v[1]}F/{v[2]}I rate={v[3]:.2%}" if v else "30d velocity: n/a"
        print(f"  {label}: untimed {m['passes']}P/{m['busts']}B/{m['censored']}C  "
              f"P(bust)={pb}  E[t]={et}d med={md}d  | {vel}")
con.close()
