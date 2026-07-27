"""iter-pass-economics E1 — extract challenge-pipeline day streams from the census ledgers.

Read-only (mode=ro URI; F84: no checkpoint, no VACUUM). Produces the daily CSVs the
`research challenge-pipeline` verb consumes (PR-E1-1/PR-E1-3/PR-E1-4, LEDGER Session 2):

  v2-session-breakout.daily.csv  machinery-demo stream: V2 family pooled by calendar-day
  v2-ema-alignment.daily.csv     superposition onto one notional book (E6.2 approximation;
                                 flat-spread costs — NOT verdict grade until E2 re-scores)
  zp-i.daily.csv                 zero-edge floor profile, intraday: V4 pooled subsampled to
                                 ~2.3 trades/day (per-account-realistic), per-trade de-meaned
  zp-s.daily.csv                 zero-edge floor profile, swing: V2 ema-alignment subsampled
                                 to ~0.5 trades/day, per-trade de-meaned

Daily bucketing is CE(S)T (Europe/Prague, DST-aware): day PnL = sum of Net over trades CLOSED
that day; TradesOpened = trades OPENED that day (V0 trading-day semantics). Holdout guard: the
extractor hard-fails if any trade closes outside [2019-01-01, 2024-01-01).

Usage:
  python tools/research/e1_extract_streams.py \
      [--db src/TradingEngine.Web/data/trading.db] [--out docs/iterations/iter-pass-economics/data]
"""
import argparse
import csv
import os
import random
import sqlite3
import statistics
import sys
from collections import defaultdict
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

sys.path.insert(0, os.path.dirname(__file__))
from block_bootstrap import stationary_bootstrap_se, mde  # noqa: E402

PRAGUE = ZoneInfo("Europe/Prague")
V2 = "4F56B1AE-7269-41CC-8D6C-60E920742EE7"
V4 = "5D06CE0B-DDB2-49EF-B93E-43FCBF7828C8"
SEED = 20260727
HOLDOUT_LO = datetime(2019, 1, 1)
HOLDOUT_HI = datetime(2024, 1, 1)  # 2024 era-holdout + beyond stays untouched


def cest_date(utc_str):
    dt = datetime.fromisoformat(utc_str).replace(tzinfo=timezone.utc)
    return dt.astimezone(PRAGUE).date()


def fetch_trades(cur, experiment_id, strategy_id=None):
    q = ("SELECT t.OpenedAtUtc, t.ClosedAtUtc, t.NetPnLAmount FROM ExperimentRuns er "
         "JOIN TradeResults t ON t.RunId = er.BacktestRunId WHERE er.ExperimentId = ?")
    args = [experiment_id]
    if strategy_id:
        q += " AND t.StrategyId = ?"
        args.append(strategy_id)
    trades = [(o, c, float(n)) for o, c, n in cur.execute(q, args)]
    for _, closed, _ in trades:
        d = datetime.fromisoformat(closed)
        if not (HOLDOUT_LO <= d < HOLDOUT_HI):
            raise SystemExit(f"HOLDOUT GUARD VIOLATION: trade closed at {closed}")
    return trades


def to_days(trades):
    """(dayNet, tradesOpened) per CE(S)T trading day (>=1 open or close)."""
    net = defaultdict(float)
    opened = defaultdict(int)
    for o, c, n in trades:
        net[cest_date(c)] += n
        opened[cest_date(o)] += 1
    days = sorted(set(net) | set(opened))
    return [(d, net.get(d, 0.0), opened.get(d, 0)) for d in days]


def subsample(trades, target_per_day, seed):
    """Bernoulli per-trade thinning to a deployment-realistic frequency (LEDGER amendment A1)."""
    n_days = len({cest_date(c) for _, c, _ in trades})
    actual = len(trades) / n_days
    p = min(1.0, target_per_day / actual)
    rng = random.Random(seed)
    kept = [t for t in trades if rng.random() < p]
    return kept, actual, p


def demean(trades):
    m = statistics.fmean(n for _, _, n in trades)
    return [(o, c, n - m) for o, c, n in trades], m


def write_csv(path, days):
    with open(path, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["Date", "NetPnL", "TradesOpened"])
        for d, n, t in days:
            w.writerow([d.isoformat(), f"{n:.2f}", t])


def report(label, trades, days, out_path):
    daily = [n for _, n, _ in days]
    per_trade = [n for _, _, n in trades]
    closes = [c for _, c, _ in trades]
    se, lo, hi = stationary_bootstrap_se(daily, reps=2000, mean_block=10, seed=SEED)
    print(f"\n== {label} -> {out_path}")
    print(f"   trades={len(trades)}  trading_days={len(days)}  trades/day={len(trades)/len(days):.3f}")
    print(f"   per-trade net $: mean={statistics.fmean(per_trade):.4f} sd={statistics.pstdev(per_trade):.2f}")
    print(f"   daily net $: mean={statistics.fmean(daily):.2f} sd={statistics.pstdev(daily):.2f}")
    print(f"   daily-mean bootstrap: SE={se:.3f} CI95=[{lo:.2f},{hi:.2f}] MDE={mde(se):.2f}")
    print(f"   holdout guard: min(Closed)={min(closes)} max(Closed)={max(closes)} (all in [2019, 2024))")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="src/TradingEngine.Web/data/trading.db")
    ap.add_argument("--out", default="docs/iterations/iter-pass-economics/data")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    db = sqlite3.connect(f"file:{args.db}?mode=ro", uri=True)
    cur = db.cursor()

    # PR-E1-4 machinery-demo streams (pooled superposition, stated approximation)
    for family in ("session-breakout", "ema-alignment"):
        trades = fetch_trades(cur, V2, family)
        days = to_days(trades)
        path = os.path.join(args.out, f"v2-{family}.daily.csv")
        write_csv(path, days)
        report(f"V2 {family} (pooled, machinery demo)", trades, days, path)

    # PR-E1-3 zero-edge floor profiles (amendment A1: subsampled to deployment-realistic
    # frequency BEFORE de-meaning; the pooled superpositions run ~92/day and ~3.3/day, which
    # no single account trades)
    v4_trades = fetch_trades(cur, V4)
    zp_i, actual_i, p_i = subsample(v4_trades, 2.3, SEED)
    zp_i, mean_i = demean(zp_i)
    days_i = to_days(zp_i)
    path_i = os.path.join(args.out, "zp-i.daily.csv")
    write_csv(path_i, days_i)
    print(f"\nZP-I subsample: source={len(v4_trades)} trades @ {actual_i:.2f}/day, keep p={p_i:.4f}, removed per-trade mean={mean_i:.4f}")
    report("ZP-I zero-edge intraday (~2.3/day target)", zp_i, days_i, path_i)

    ema_trades = fetch_trades(cur, V2, "ema-alignment")
    zp_s, actual_s, p_s = subsample(ema_trades, 0.5, SEED)
    zp_s, mean_s = demean(zp_s)
    days_s = to_days(zp_s)
    path_s = os.path.join(args.out, "zp-s.daily.csv")
    write_csv(path_s, days_s)
    print(f"\nZP-S subsample: source={len(ema_trades)} trades @ {actual_s:.2f}/day, keep p={p_s:.4f}, removed per-trade mean={mean_s:.4f}")
    report("ZP-S zero-edge swing (~0.5/day target)", zp_s, days_s, path_s)


if __name__ == "__main__":
    main()
