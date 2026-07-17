"""iter-viability V2: frozen-bank pure-OOS census driver (committed tooling).

Runs the 252-cell frozen-bank census (9 strategies x 14 symbols x {H1,H4}, D13 one cell per
run) over 2019-01-01 -> 2023-12-31 (2024 era-holdout untouched by construction: BacktestTo <
2024-01-01), tape venue on the Session-2 Dukascopy backfill, and sv2-scores each run into the
V2 experiment. Config replicates census 075D5240 exactly (LEDGER.md Session 3 pre-registration).

Usage:
  python tools/research/census_driver.py --create-experiment          # prints new experiment id
  python tools/research/census_driver.py --experiment <guid> [--pilot] [--parallel 3] \
      [--base-url http://localhost:5134]

Operational facts inherited from exit_factorial_driver (S1.1, 2026-07-16):
  - App idempotency-key store is IN-MEMORY: resume skips by VariantLabel against the DB
    (completed run for the label = done), not by idempotency key.
  - --parallel N only after the determinism probe has passed on this machine/build (it has:
    S1 speed kit, determinism_probe.py PASS).
  - Per-run timeout 90 min (5-year windows; census anchor 124 s / 10-month H1 cell; crypto H1
    is the long pole at ~44k decision bars).

Disk discipline (Session 3 pre-registration, owner decision 2026-07-17 "Both"):
  - --prune-journal deletes each run's kernel Journal rows AFTER the run completes AND scores.
    Journal is a replay/debug artifact for backtests — nothing in sv2 scoring, verdict tables,
    or `research persistence` reads it. Results audit (TradeResults, EquitySnapshots, Bars,
    scores) is kept in full. Unscored/failed runs keep their journal for debugging. Freed pages
    recycle inside trading.db, capping batch growth at the results-only ~3.5 GB.
  - The driver refuses to submit new runs when free disk < MIN_FREE_GB (resume later).
"""
import argparse, json, shutil, sqlite3, sys, time, urllib.request, urllib.error, uuid
from pathlib import Path

DEFAULT_DB = Path(__file__).resolve().parents[2] / "src" / "TradingEngine.Web" / "data" / "trading.db"
START, END = "2019-01-01T00:00:00", "2023-12-31T00:00:00"   # To < 2024-01-01: era-holdout clean
RUN_TIMEOUT_S = 90 * 60
MIN_FREE_GB = 1.5

STRATEGIES = ["bb-squeeze", "ema-alignment", "macd-momentum", "mean-reversion", "mtf-trend",
              "rsi-divergence", "session-breakout", "super-trend", "trend-breakout"]
SYMBOLS = ["AUDUSD", "BTCUSD", "ETHUSD", "EURGBP", "EURJPY", "EURUSD", "GBPJPY", "GBPUSD",
           "NZDUSD", "USDCAD", "USDCHF", "USDJPY", "XAGUSD", "XAUUSD"]
TIMEFRAMES = ["H1", "H4"]

# 2 pilot cells to measure 5-year wall time before committing to the ~30-40 h batch
PILOT = [("mean-reversion", "EURUSD", "H1"), ("trend-breakout", "XAUUSD", "H4")]

EXPERIMENT_NAME = "v2-frozen-bank-oos"
HYPOTHESIS = ("V2 frozen-bank pure OOS census 2019-2023 (D13, one cell per run); pre-registered "
              "in iter-viability LEDGER.md Session 3. H-MR / H-RANK / H-BANK; spread policy = "
              "raw per-bar dukascopy + 1.5x/2x post-hoc stress.")
# Same scoring weights as 075D5240 so sv2 composites stay comparable (D4).
SPEC = {"Name": EXPERIMENT_NAME, "Hypothesis": HYPOTHESIS,
        "Symbols": SYMBOLS, "Timeframes": TIMEFRAMES, "Strategies": STRATEGIES,
        "From": "2019-01-01", "To": "2023-12-31", "WalkForward": None, "MaxRuns": 252,
        "Variants": [],
        "Scoring": {"PassProbability": 0.4, "ExpectancyR": 0.3, "MaxDrawdown": 0.2,
                    "FoldConsistency": 0.1}}

TERMINAL = ("completed", "completed-with-warnings", "failed", "cancelled")


def make_http(base):
    def http(method, path, body=None, timeout=60):
        req = urllib.request.Request(base + path, method=method)
        data = json.dumps(body).encode() if body is not None else None
        if data: req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, data, timeout=timeout) as resp:
                return resp.status, json.loads(resp.read().decode() or "null")
        except urllib.error.HTTPError as e:
            try: detail = e.read().decode()[:300]
            except Exception: detail = ""
            return e.code, {"error": f"HTTP {e.code}: {detail}"}
        except Exception as e:
            return 0, {"error": str(e)}
    return http


def create_experiment(db):
    """One-shot Experiments row insert (Session-1 PropFirmRuleSets-upsert precedent: Lane R
    owns the DB; the doctrine forbids a second APP instance, not a scripted one-row write)."""
    eid = str(uuid.uuid4()).upper()
    now = time.strftime("%Y-%m-%d %H:%M:%S")
    con = sqlite3.connect(db)
    con.execute("""INSERT INTO Experiments
        (Id, Name, Hypothesis, SpecJson, Status, CreatedUtc, CreatedAtUtc, UpdatedAtUtc)
        VALUES (?,?,?,?,?,?,?,?)""",
        (eid, EXPERIMENT_NAME, HYPOTHESIS, json.dumps(SPEC), "Running", now, now, now))
    con.commit(); con.close()
    print(f"EXPERIMENT CREATED: {eid}  name={EXPERIMENT_NAME}")
    return eid


def already_done(db, exp_id):
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    rows = con.execute("""SELECT er.VariantLabel FROM ExperimentRuns er
        JOIN BacktestRuns br ON br.RunId = er.BacktestRunId
        WHERE er.ExperimentId = ? COLLATE NOCASE AND br.Status LIKE 'completed%'""",
        (exp_id,)).fetchall()
    con.close()
    return {r[0] for r in rows}


def run_body(strategy, sym, tf):
    return {
        "start": START, "end": END, "balance": 100000,
        "commissionPerMillion": 30, "spreadPips": 1,
        "venue": "tape", "riskProfileId": "standard",
        "rows": [{"strategyId": strategy, "symbol": sym, "timeframe": tf,
                  "packId": None, "enabled": True}],
        "speed": 10, "honestFills": True,
        "idempotencyKey": f"v2-census-{strategy}-{sym}-{tf}".lower(),
    }


def prune_journal(db, run_id):
    """Post-score disk discipline: drop the completed run's kernel event journal (see header)."""
    con = sqlite3.connect(db, timeout=60)
    try:
        n = con.execute("DELETE FROM Journal WHERE RunId = ?", (run_id,)).rowcount
        con.commit()
        return n
    finally:
        con.close()


def free_gb(db):
    return shutil.disk_usage(Path(db).anchor).free / 1e9


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--experiment", default=None)
    ap.add_argument("--create-experiment", action="store_true")
    ap.add_argument("--pilot", action="store_true", help="run only the 2 pre-registered pilot cells")
    ap.add_argument("--parallel", type=int, default=1)
    ap.add_argument("--timeframes", default=None, help="comma filter, e.g. H4 (tranche the batch)")
    ap.add_argument("--prune-journal", action="store_true",
                    help="delete each run's Journal rows after completed+scored (pre-registered)")
    ap.add_argument("--base-url", default="http://localhost:5134")
    ap.add_argument("--db", default=str(DEFAULT_DB))
    args = ap.parse_args()

    if args.create_experiment:
        create_experiment(args.db)
        return
    if not args.experiment:
        print("--experiment <guid> required (or --create-experiment first)"); sys.exit(2)

    http = make_http(args.base_url)
    tfs = args.timeframes.split(",") if args.timeframes else TIMEFRAMES
    cells = PILOT if args.pilot else [
        (s, sym, tf) for tf in tfs for sym in SYMBOLS for s in STRATEGIES]

    done = already_done(args.db, args.experiment)
    print(f"RESUME: {len(done)} cells already completed", flush=True)

    todo = []
    for s, sym, tf in cells:
        vl = f"census/{s}/{sym}/{tf}"
        if vl in done:
            print(f"SKIP {vl} (already done)", flush=True)
            continue
        todo.append((vl, run_body(s, sym, tf)))

    total, finished = len(todo), 0
    inflight = {}   # runId -> (variantLabel, startedAt)
    queue = list(todo)
    consecutive_failures = 0
    print(f"TODO {total} runs, parallel={args.parallel}", flush=True)

    while queue or inflight:
        while queue and len(inflight) < args.parallel:
            if free_gb(args.db) < MIN_FREE_GB:
                print(f"DISK GUARD: free < {MIN_FREE_GB} GB — not submitting further runs; "
                      f"{len(queue)} cells left, resume-safe by label", flush=True)
                queue.clear()
                break
            vl, body = queue.pop(0)
            # 360s: run submission re-validates data coverage; at 35M-row scale a cold inventory
            # cache costs ~2 min (the pilot's second POST timed out at 120s and orphaned a run).
            st, resp = http("POST", "/api/runs", body, timeout=360)
            rid = (resp or {}).get("runId")
            if not rid:
                finished += 1
                print(f"RUN {finished}/{total} {vl} START-FAILED {st} {resp}", flush=True)
                consecutive_failures += 1
                if consecutive_failures >= 3:
                    print("ABORT: three consecutive start failures", flush=True); sys.exit(3)
                continue
            inflight[rid] = (vl, time.time())
            print(f"START {vl} id={rid[:8]}", flush=True)

        time.sleep(5)
        for rid in list(inflight):
            vl, t0 = inflight[rid]
            st2, run = http("GET", f"/api/runs/{rid}", timeout=30)
            status = (run or {}).get("status") if st2 == 200 else None
            if status in TERMINAL:
                del inflight[rid]
                finished += 1
                consecutive_failures = 0
                trades = (run or {}).get("totalTrades") or 0
                wall = (run or {}).get("wallElapsedMs") or 0
                st3, score = http("POST", "/api/experiments/score",
                                  {"backtestRunId": rid, "experimentId": args.experiment,
                                   "variantLabel": vl}, timeout=120)
                verdict = (score or {}).get("verdict", f"HTTP{st3}")
                extra = (score or {}).get("score") if verdict == "PASS" else (score or {}).get("reason")
                pruned = ""
                if args.prune_journal and status.startswith("completed") and st3 == 200:
                    pruned = f" journal-pruned={prune_journal(args.db, rid)}"
                print(f"RUN {finished}/{total} {vl} id={rid[:8]} status={status} trades={trades} "
                      f"wall={wall/60000:.1f}m score={verdict}({extra}){pruned} "
                      f"free={free_gb(args.db):.1f}GB", flush=True)
            elif time.time() - t0 > RUN_TIMEOUT_S:
                del inflight[rid]
                finished += 1
                print(f"RUN {finished}/{total} {vl} id={rid[:8]} TIMEOUT after {RUN_TIMEOUT_S//60}m", flush=True)

    print("BATCH DONE", flush=True)


if __name__ == "__main__":
    main()
