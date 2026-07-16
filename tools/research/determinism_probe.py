"""Concurrency determinism probe (iter-structural-edge S1 speed work).

Question: are tape runs byte-identical when several execute CONCURRENTLY? Gate for
exit_factorial_driver.py --parallel N (N>1). Method: pick reference cells with known
sequential results, launch each twice concurrently (unscored throwaway runs), then compare
TotalTrades + NetProfit of every probe run against the sequential reference run.

Usage: python tools/research/determinism_probe.py --reference <runId,runId,...> [--base-url ...]
Exit 0 = PASS (safe to parallelize), 1 = DRIFT DETECTED (stay sequential; file a finding).
"""
import argparse, json, time, urllib.request

TERMINAL = ("completed", "completed-with-warnings", "failed", "cancelled")

def make_http(base):
    def http(method, path, body=None, timeout=120):
        req = urllib.request.Request(base + path, method=method)
        data = json.dumps(body).encode() if body is not None else None
        if data: req.add_header("Content-Type", "application/json")
        with urllib.request.urlopen(req, data, timeout=timeout) as resp:
            return json.loads(resp.read().decode() or "null")
    return http

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--reference", required=True, help="comma list of reference runIds (sequential, completed)")
    ap.add_argument("--base-url", default="http://localhost:5134")
    args = ap.parse_args()
    http = make_http(args.base_url)

    refs = []
    for rid in args.reference.split(","):
        run = http("GET", f"/api/runs/{rid}")
        refs.append(run)
        print(f"reference {rid[:8]}: {run.get('symbol')}/{run.get('period')} trades={run.get('totalTrades')} net={run.get('netProfit')}")

    # relaunch every reference twice, all POSTs up-front -> maximum concurrency
    probes = []
    for ref in refs:
        plan = json.loads(ref.get("runPlanJson") or "[]")
        for copy in (1, 2):
            body = {
                "start": ref["backtestFrom"], "end": ref["backtestTo"], "balance": 100000,
                "commissionPerMillion": 30, "spreadPips": 1, "venue": "tape",
                "riskProfileId": ref.get("riskProfileId") or "standard",
                "rows": plan, "speed": 10, "honestFills": True,
                "idempotencyKey": f"determinism-probe-{ref['runId'][:8]}-{copy}-{int(time.time())}",
            }
            resp = http("POST", "/api/runs", body)
            probes.append((ref, resp["runId"]))
            print(f"probe launched: {resp['runId'][:8]} (copy {copy} of {ref['runId'][:8]})")

    drift = 0
    pending = dict.fromkeys(rid for _, rid in probes)
    deadline = time.time() + 1800
    while pending and time.time() < deadline:
        time.sleep(3)
        for rid in list(pending):
            run = http("GET", f"/api/runs/{rid}")
            if (run.get("status") or "") in TERMINAL:
                del pending[rid]
    for ref, rid in probes:
        run = http("GET", f"/api/runs/{rid}")
        same = (run.get("totalTrades") == ref.get("totalTrades")
                and abs(float(run.get("netProfit") or 0) - float(ref.get("netProfit") or 0)) < 0.01
                and run.get("status") == "completed")
        mark = "OK " if same else "DRIFT"
        if not same: drift += 1
        print(f"{mark} probe {rid[:8]} vs ref {ref['runId'][:8]}: trades {run.get('totalTrades')} vs {ref.get('totalTrades')}, net {run.get('netProfit')} vs {ref.get('netProfit')}")

    print("VERDICT:", "PASS - concurrent tape runs are deterministic" if drift == 0 else f"FAIL - {drift} drifted")
    raise SystemExit(0 if drift == 0 else 1)

if __name__ == "__main__":
    main()
