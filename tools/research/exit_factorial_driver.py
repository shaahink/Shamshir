"""iter-structural-edge S1: exit-layer factorial batch driver (committed tooling).

Runs the 8 pre-registered exit variants x given cells for one strategy family, one cell per
run (D13), tape venue, census window; sv2-scores each run into the given experiment.

Usage:
  python tools/research/exit_factorial_driver.py --experiment <guid> --strategy ema-alignment \
      --cells EURJPY/H1,USDJPY/H1 [--only no-tp-trail] [--parallel 3] [--base-url http://localhost:5134]

Operational facts learned the hard way (S1.1, 2026-07-16):
  - The app's run idempotency-key store is IN-MEMORY: after an app restart the same key
    creates a NEW run. Resume therefore skips by variantLabel against the DB (a completed,
    scored run for the label = done), not by idempotency key.
  - --parallel N submits up to N runs concurrently. ONLY use N>1 after the determinism probe
    (determinism_probe.py) has shown concurrent tape runs byte-identical to sequential ones
    on this machine/build.
"""
import argparse, json, sqlite3, sys, time, urllib.request, urllib.error
from pathlib import Path

DEFAULT_DB = Path(__file__).resolve().parents[2] / "src" / "TradingEngine.Web" / "data" / "trading.db"
START, END = "2025-07-04T00:00:00", "2026-05-05T00:00:00"

SL = {"method":"AtrMultiple","atrMultiple":1.5,"fixedPips":0,"maxPips":100,"maxSlAtrMultiple":None}
TP = {"method":"RrMultiple","rrMultiple":2,"fixedPips":0,"atrMultiple":0}
BE_OFF = {"enabled":False,"mode":0,"triggerRMultiple":1,"offsetPips":1,"offsetSpreadMultiple":None}
TRAIL_1 = {"enabled":True,"mode":0,"method":"AtrMultiple","stepPips":10,"atrMultiple":1,
           "activateAfterBreakeven":False,"structureLookbackBars":10,"steppedRLevels":[1,2,3],"stepAtrFraction":None}
TRAIL_OFF = {"enabled":False,"mode":0,"method":"None","stepPips":10,"atrMultiple":1,
             "activateAfterBreakeven":False,"structureLookbackBars":10,"steppedRLevels":[1,2,3],"stepAtrFraction":None}
RIDE = {"enabled":True,"mode":0,"adxFloor":25,"relaxedAtrMultiple":3}
PARTIAL = {"enabled":True,"mode":0,"triggerRMultiple":1,"closeFraction":0.5}

PACKS = {
    "s1-trail-only": {"id":"s1-trail-only","name":"S1 Trail Only","description":
        "iter-structural-edge S1: runner-aggressive's ATR-1.0 trail alone (BE off, no Ride/Partial).",
        "addOns":{"stopLoss":SL,"takeProfit":TP,"breakeven":BE_OFF,"trailing":TRAIL_1,"ride":None,"partialTp":None,"dynamicSlTp":None},
        "regimeDetectionEnabled":True},
    "s1-trail-ride": {"id":"s1-trail-ride","name":"S1 Trail + Ride","description":
        "iter-structural-edge S1: ATR-1.0 trail + Ride relax (ADX>=25 -> 3.0), BE off, no Partial.",
        "addOns":{"stopLoss":SL,"takeProfit":TP,"breakeven":BE_OFF,"trailing":TRAIL_1,"ride":RIDE,"partialTp":None,"dynamicSlTp":None},
        "regimeDetectionEnabled":True},
    "s1-partial-only": {"id":"s1-partial-only","name":"S1 Partial Only","description":
        "iter-structural-edge S1: PartialTp 50% @ 1R alone (BE off, trail off).",
        "addOns":{"stopLoss":SL,"takeProfit":TP,"breakeven":BE_OFF,"trailing":TRAIL_OFF,"ride":None,"partialTp":PARTIAL,"dynamicSlTp":None},
        "regimeDetectionEnabled":True},
}

# label -> (packId, stripAddOns, tpNone)
VARIANTS = [
    ("control",      None,                False, False),
    ("bare",         None,                True,  False),
    ("be-only",      "breakeven-only",    False, False),
    ("trail-only",   "s1-trail-only",     False, False),
    ("trail-ride",   "s1-trail-ride",     False, False),
    ("partial-only", "s1-partial-only",   False, False),
    ("runner-full",  "runner-aggressive", False, False),
    ("no-tp-trail",  "s1-trail-only",     False, True),
]

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


def already_done(db, exp_id):
    con = sqlite3.connect(db)
    rows = con.execute("""SELECT er.VariantLabel FROM ExperimentRuns er
        JOIN BacktestRuns br ON br.RunId = er.BacktestRunId
        WHERE er.ExperimentId = ? AND br.Status = 'completed'""", (exp_id,)).fetchall()
    con.close()
    return {r[0] for r in rows}


def run_body(strategy, sym, tf, label, pack, strip, tp_none):
    body = {
        "start": START, "end": END, "balance": 100000,
        "commissionPerMillion": 30, "spreadPips": 1,
        "venue": "tape", "riskProfileId": "standard",
        "rows": [{"strategyId": strategy, "symbol": sym, "timeframe": tf,
                  "packId": pack, "enabled": True}],
        "speed": 10, "honestFills": True,
        "idempotencyKey": f"s1-{strategy}-{label}-{sym}-{tf}".lower(),
    }
    if strip: body["stripAddOns"] = True
    if tp_none:
        body["strategyOverrides"] = {strategy: {"positionManagement": {"takeProfit": {"method": "None"}}}}
    return body


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--experiment", required=True)
    ap.add_argument("--strategy", required=True)
    ap.add_argument("--cells", required=True, help="SYM/TF,SYM/TF,...")
    ap.add_argument("--only", default=None, help="comma list of variant labels to run")
    ap.add_argument("--parallel", type=int, default=1)
    ap.add_argument("--base-url", default="http://localhost:5134")
    ap.add_argument("--db", default=str(DEFAULT_DB))
    args = ap.parse_args()

    http = make_http(args.base_url)
    cells = [tuple(c.split("/")) for c in args.cells.split(",")]
    only = set(args.only.split(",")) if args.only else None

    for pid, body in PACKS.items():
        st, resp = http("PUT", f"/api/addons/packs/{pid}", body)
        print(f"PACK {pid}: {st}", flush=True)
        if st != 200:
            print(f"ABORT: pack creation failed: {resp}", flush=True); sys.exit(2)

    done = already_done(args.db, args.experiment)
    print(f"RESUME: {len(done)} variants already completed+scored", flush=True)

    todo = []
    for sym, tf in cells:
        for label, pack, strip, tp_none in VARIANTS:
            if only and label not in only: continue
            vl = f"{label}/{args.strategy}/{sym}/{tf}"
            if vl in done:
                print(f"SKIP {vl} (already done)", flush=True)
                continue
            todo.append((vl, run_body(args.strategy, sym, tf, label, pack, strip, tp_none)))

    total, finished = len(todo), 0
    inflight = {}   # runId -> (variantLabel, startedAt)
    queue = list(todo)
    consecutive_failures = 0
    print(f"TODO {total} runs, parallel={args.parallel}", flush=True)

    while queue or inflight:
        while queue and len(inflight) < args.parallel:
            vl, body = queue.pop(0)
            st, resp = http("POST", "/api/runs", body, timeout=120)
            rid = (resp or {}).get("runId")
            if not rid:
                finished += 1
                print(f"RUN {finished}/{total} {vl} START-FAILED {st} {resp}", flush=True)
                consecutive_failures += 1
                if consecutive_failures >= 3:
                    print("ABORT: three consecutive start failures", flush=True); sys.exit(3)
                continue
            inflight[rid] = (vl, time.time())

        time.sleep(3)
        for rid in list(inflight):
            vl, t0 = inflight[rid]
            st2, run = http("GET", f"/api/runs/{rid}", timeout=30)
            status = (run or {}).get("status") if st2 == 200 else None
            if status in TERMINAL:
                del inflight[rid]
                finished += 1
                consecutive_failures = 0
                trades = (run or {}).get("totalTrades") or 0
                st3, score = http("POST", "/api/experiments/score",
                                  {"backtestRunId": rid, "experimentId": args.experiment, "variantLabel": vl},
                                  timeout=120)
                verdict = (score or {}).get("verdict", f"HTTP{st3}")
                extra = (score or {}).get("score") if verdict == "PASS" else (score or {}).get("reason")
                print(f"RUN {finished}/{total} {vl} id={rid[:8]} status={status} trades={trades} score={verdict}({extra})", flush=True)
            elif time.time() - t0 > 1800:
                del inflight[rid]
                finished += 1
                print(f"RUN {finished}/{total} {vl} id={rid[:8]} TIMEOUT after 30m", flush=True)

    print("BATCH DONE", flush=True)


if __name__ == "__main__":
    main()
