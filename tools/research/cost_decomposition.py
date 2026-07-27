"""Canonical cost-decomposition helper (F87 P5, iter-pass-economics E0/GE1).

The V2 and V4 harvests each discarded GrossPnLAmount independently, so the −$20/pos verdict had
to be re-derived from raw ledgers by offline archaeology (AUDIT-POST-VIABILITY-2026-07 §1). This
module makes `gross / spread / commission / swap / net / signal` standing columns every future
harvest gets from ONE place.

Definitions (R2 conventions — costs are NEGATIVE):
  Net    = Gross + Commission + Swap            (exact per row; violations are reported, not hidden)
  Signal = Gross − SpreadCost                    (bid-to-bid PnL; SpreadCostAmount is F87 P3's
                                                  per-trade column, 0 on pre-F87 rows and on venues
                                                  that don't compute it — Signal degrades to Gross)

Read-only (mode=ro URI; F84: no checkpoint, no VACUUM). Run selection: an explicit --runs list,
or --experiment matched against Experiments.Name (exact) or the hex prefix of Experiments.Id
(TEXT GUIDs, e.g. 4F56B1AE), joined through ExperimentRuns.BacktestRunId.

Usage:
  python tools/research/cost_decomposition.py --db src/TradingEngine.Web/data/trading.db --runs a1b2c3d4,e5f6a7b8
  python tools/research/cost_decomposition.py --db ... --experiment 4F56B1AE --per-cell
  python tools/research/cost_decomposition.py --selftest
"""
import argparse
import sqlite3

TOL = 0.01  # cents-level tolerance on the Net identity, per trade

COLS = ("n", "gross", "spread_cost", "commission", "swap", "net", "signal")


def _connect_ro(db_path):
    return sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)


def resolve_run_ids(cur, runs=None, experiment=None):
    """Explicit run-id list, or every BacktestRunId of the matched experiment."""
    if runs:
        return [r.strip() for r in runs.split(",") if r.strip()]
    if not experiment:
        raise ValueError("pass --runs or --experiment")
    row = cur.execute(
        "SELECT Id FROM Experiments WHERE Name = ? OR UPPER(Id) LIKE UPPER(?) || '%'",
        (experiment, experiment)).fetchall()
    if len(row) != 1:
        raise ValueError(f"experiment '{experiment}' matched {len(row)} experiments (need exactly 1)")
    return [r[0] for r in cur.execute(
        "SELECT DISTINCT BacktestRunId FROM ExperimentRuns WHERE ExperimentId = ?", (row[0][0],))]


def decompose(cur, run_ids, per_cell=False):
    """Aggregate rows keyed by RunId (default) or (StrategyId, Symbol, EntryTimeframe).

    Returns (rows, violations): rows is a dict key -> dict of COLS + per-pos figures; violations
    counts trades whose |Net − (Gross+Commission+Swap)| > TOL — reported, never swallowed.
    """
    placeholders = ",".join("?" * len(run_ids))
    key_cols = ("StrategyId, Symbol, COALESCE(EntryTimeframe,'?')" if per_cell else "RunId")
    q = (f"SELECT {key_cols}, GrossPnLAmount, SpreadCostAmount, CommissionAmount, SwapAmount, "
         f"NetPnLAmount FROM TradeResults WHERE RunId IN ({placeholders})")

    rows, violations = {}, 0
    for row in cur.execute(q, run_ids):
        key = tuple(row[:-5]) if per_cell else row[0]
        gross, spread, comm, swap, net = (float(v) for v in row[-5:])
        if abs(net - (gross + comm + swap)) > TOL:
            violations += 1
        agg = rows.setdefault(key, dict.fromkeys(COLS, 0.0))
        agg["n"] += 1
        agg["gross"] += gross
        agg["spread_cost"] += spread
        agg["commission"] += comm
        agg["swap"] += swap
        agg["net"] += net
        agg["signal"] += gross - spread
    return rows, violations


def emit(rows, violations, out=print):
    header = ("key", "n", "gross", "spread_cost", "commission", "swap", "net", "signal",
              "net_per_pos", "signal_per_pos")
    out("\t".join(header))
    tot = dict.fromkeys(COLS, 0.0)
    for key in sorted(rows, key=str):
        a = rows[key]
        for c in COLS:
            tot[c] += a[c]
        n = int(a["n"])
        out("\t".join([str(key), str(n)]
                      + [f"{a[c]:.2f}" for c in COLS[1:]]
                      + [f"{a['net'] / n:.2f}", f"{a['signal'] / n:.2f}"]))
    n = int(tot["n"]) or 1
    out("\t".join(["TOTAL", str(int(tot['n']))]
                  + [f"{tot[c]:.2f}" for c in COLS[1:]]
                  + [f"{tot['net'] / n:.2f}", f"{tot['signal'] / n:.2f}"]))
    out(f"net-identity violations (>|{TOL}|): {violations}")


def _fixture_db():
    """In-memory DB with the TradeResults/Experiments shape this module reads."""
    db = sqlite3.connect(":memory:")
    db.executescript("""
        CREATE TABLE TradeResults (
            RunId TEXT, StrategyId TEXT, Symbol TEXT, EntryTimeframe TEXT,
            GrossPnLAmount REAL, SpreadCostAmount REAL, CommissionAmount REAL,
            SwapAmount REAL, NetPnLAmount REAL);
        CREATE TABLE Experiments (Id TEXT, Name TEXT);
        CREATE TABLE ExperimentRuns (ExperimentId TEXT, BacktestRunId TEXT);
    """)
    # R2 conventions: costs negative; Net = Gross + Comm + Swap; Signal = Gross − SpreadCost.
    trades = [
        # run1: long EURUSD H1 — gross embeds a −15 spread toll
        ("run1", "session-breakout", "EURUSD", "H1", 1185.0, -15.0, -12.41, 0.0, 1172.59),
        ("run1", "session-breakout", "EURUSD", "H1", -215.0, -15.0, -12.00, -10.0, -237.00),
        # run1, different cell (same run — per-cell must split it out)
        ("run1", "ema-alignment", "XAUUSD", "H1", 500.0, -63.0, -19.83, 0.0, 480.17),
        # run2: short — spread paid at exit; swap credit is legal (positive swap)
        ("run2", "session-breakout", "EURUSD", "H1", 1470.0, -30.0, -12.41, 2.5, 1460.09),
        # pre-F87 row: SpreadCostAmount 0 ⇒ Signal degrades to Gross
        ("run2", "session-breakout", "EURUSD", "H1", 100.0, 0.0, -10.0, 0.0, 90.0),
    ]
    db.executemany("INSERT INTO TradeResults VALUES (?,?,?,?,?,?,?,?,?)", trades)
    db.execute("INSERT INTO Experiments VALUES ('AABBCCDD-0000-0000-0000-000000000000', 'fixture-exp')")
    db.executemany("INSERT INTO ExperimentRuns VALUES ('AABBCCDD-0000-0000-0000-000000000000', ?)",
                   [("run1",), ("run2",)])
    # A violation row in a run the selftest does NOT select — must not leak into run1/run2 sums.
    db.execute("INSERT INTO TradeResults VALUES ('run3','x','EURUSD','H1', 100.0, 0.0, -10.0, 0.0, 999.0)")
    return db


def selftest():
    ok = True
    db = _fixture_db()
    cur = db.cursor()

    # (a) run selection by experiment id-prefix and by name resolve to the same two runs.
    by_prefix = sorted(resolve_run_ids(cur, experiment="aabbccdd"))
    by_name = sorted(resolve_run_ids(cur, experiment="fixture-exp"))
    print(f"(a) run selection: prefix={by_prefix} name={by_name}")
    ok &= by_prefix == by_name == ["run1", "run2"]

    # (b) per-run aggregates: hand-computed sums, Net identity, Signal identity.
    rows, violations = decompose(cur, ["run1", "run2"])
    r1, r2 = rows["run1"], rows["run2"]
    print(f"(b) run1: n={int(r1['n'])} gross={r1['gross']:.2f} spread={r1['spread_cost']:.2f} "
          f"net={r1['net']:.2f} signal={r1['signal']:.2f}")
    ok &= int(r1["n"]) == 3 and abs(r1["gross"] - 1470.0) < 1e-9
    ok &= abs(r1["spread_cost"] - (-93.0)) < 1e-9
    ok &= abs(r1["net"] - 1415.76) < 1e-9
    ok &= abs(r1["signal"] - 1563.0) < 1e-9              # 1470 − (−93)
    ok &= abs(r1["net"] - (r1["gross"] + r1["commission"] + r1["swap"])) < TOL
    ok &= abs(r2["signal"] - 1600.0) < 1e-9              # 1570 − (−30); pre-F87 row adds gross only
    ok &= violations == 0
    print(f"(b) identities: net==gross+comm+swap OK, violations={violations}")

    # (c) per-cell grouping splits run1's two cells and pools across runs.
    cells, _ = decompose(cur, ["run1", "run2"], per_cell=True)
    sb = cells[("session-breakout", "EURUSD", "H1")]
    xau = cells[("ema-alignment", "XAUUSD", "H1")]
    print(f"(c) per-cell: session-breakout/EURUSD n={int(sb['n'])} ema-alignment/XAUUSD n={int(xau['n'])}")
    ok &= int(sb["n"]) == 4 and int(xau["n"]) == 1
    ok &= abs(sb["signal"] - (1185 + 15 - 215 + 15 + 1470 + 30 + 100)) < 1e-9

    # (d) the Net-identity violation in unselected run3 is invisible; selected, it is COUNTED.
    _, v3 = decompose(cur, ["run3"])
    print(f"(d) violation accounting: run3 violations={v3}")
    ok &= v3 == 1

    # (e) R2 conventions hold on every fixture row the aggregates consumed.
    neg_ok = all(s <= 0 for (s,) in cur.execute(
        "SELECT SpreadCostAmount FROM TradeResults WHERE RunId IN ('run1','run2')"))
    comm_ok = all(c <= 0 for (c,) in cur.execute(
        "SELECT CommissionAmount FROM TradeResults WHERE RunId IN ('run1','run2')"))
    print(f"(e) R2 conventions: spread_cost<=0 {neg_ok}, commission<=0 {comm_ok}")
    ok &= neg_ok and comm_ok

    print("SELFTEST", "PASS" if ok else "FAIL")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db", default="src/TradingEngine.Web/data/trading.db")
    ap.add_argument("--runs", help="comma-separated BacktestRun ids")
    ap.add_argument("--experiment", help="Experiments.Name (exact) or Id hex prefix")
    ap.add_argument("--per-cell", action="store_true",
                    help="group by (StrategyId, Symbol, EntryTimeframe) instead of RunId")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args()

    if args.selftest:
        raise SystemExit(selftest())

    db = _connect_ro(args.db)
    cur = db.cursor()
    run_ids = resolve_run_ids(cur, args.runs, args.experiment)
    rows, violations = decompose(cur, run_ids, per_cell=args.per_cell)
    emit(rows, violations)


# F81: keep the argparse behind the main guard so importing this module never eats a caller's argv.
if __name__ == "__main__":
    main()
