"""V1 backfill importer (iter-viability Session 2; pre-registered in LEDGER.md Session 2).

Downloads Dukascopy's free per-day M1 bid/ask candle files (raw-LZMA .bi5), decodes them with
empirically pinned field order + per-symbol price scale, derives engine timeframes, and imports
2019-01-01 -> 2024-12-31 into MarketDataBars with a per-bar Spread column (price units,
ask = bid + spread — the TapeReplayAdapter P0.2/D3 convention). The 2025+ overlap never enters
the engine DB; it exists only in the raw archive for reconciliation against the recorded
cTrader tape.

Venue bucket conventions (measured from the recorded tape, 2026-07-16): H1/M15 are UTC-aligned;
H4/D1 follow venue midnight at UTC+3 in EU summer time / UTC+2 in winter (EU DST: last Sunday
of March/October, 01:00 UTC) — D1 opens 21:00/22:00 UTC, H4 at 01/05/09/13/17/21 (+1 in
winter). Derivation replicates that so backfilled bars are indistinguishable in shape from
synced ones.

Subcommands:
  download   --from --to [--symbols ...] [--workers N]     resumable, into data/backfill/dukascopy-raw.db
  probe      --date YYYY-MM-DD [--symbols ...]             decode evidence: field order, scale, offsets
  reconcile  --from --to                                   overlap validation vs recorded tape (H1 + H4)
  import     --from --to --timeframes M15,H1,H4,D1         INSERT OR IGNORE into MarketDataBars
  status                                                   archive coverage summary
"""
import argparse
import datetime as dt
import http.client
import lzma
import math
import sqlite3
import statistics
import struct
import sys
import threading
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MARKET_DB = ROOT / "src" / "TradingEngine.Web" / "data" / "marketdata.db"
TRADING_DB = ROOT / "src" / "TradingEngine.Web" / "data" / "trading.db"
# The raw archive lives OUTSIDE the repo on purpose (owner call, 2026-07-16): downloads run at
# ~6 files/s (server throttle), so the archive must survive git clean / worktree removal.
# Override with DUKASCOPY_RAW_DB; falls back to the legacy in-repo path if only that exists.
import os  # noqa: E402

RAW_DB = Path(os.environ.get("DUKASCOPY_RAW_DB", r"C:\ShamshirData\backfill\dukascopy-raw.db"))
_LEGACY_RAW_DB = ROOT / "data" / "backfill" / "dukascopy-raw.db"
if not RAW_DB.exists() and _LEGACY_RAW_DB.exists():
    RAW_DB = _LEGACY_RAW_DB

SYMBOLS = ["EURUSD", "GBPUSD", "USDJPY", "USDCHF", "USDCAD", "AUDUSD", "NZDUSD",
           "EURGBP", "EURJPY", "GBPJPY", "XAUUSD", "XAGUSD", "BTCUSD", "ETHUSD"]
SIDES = ("BID", "ASK")
HEADERS = {"User-Agent": "Mozilla/5.0 (research backfill; single-user)"}
REC = struct.Struct(">IIIIIf")   # t_offset_sec, p1, p2, p3, p4, volume — order pinned by probe


def raw_con():
    RAW_DB.parent.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(str(RAW_DB), timeout=60)
    con.execute("PRAGMA journal_mode=WAL")
    con.execute("""CREATE TABLE IF NOT EXISTS RawFiles(
        Symbol TEXT, Date TEXT, Side TEXT, Status TEXT, Bytes BLOB,
        PRIMARY KEY(Symbol, Date, Side))""")
    con.execute("""CREATE TABLE IF NOT EXISTS Meta(
        Symbol TEXT PRIMARY KEY, Scale REAL, FieldOrder TEXT, Evidence TEXT)""")
    return con


def daterange(d0, d1):
    d = d0
    while d <= d1:
        yield d
        d += dt.timedelta(days=1)


def url_for(sym, date, side):
    # Dukascopy datafeed months are 0-based. Plain HTTP on purpose: from this network HTTPS to
    # the datafeed CDN stalls ~75 s/request while HTTP answers in ~0.1 s (measured 2026-07-16);
    # the feed is public data and every bar is validated against the recorded venue tape anyway.
    return (f"http://datafeed.dukascopy.com/datafeed/{sym}/{date.year:04d}/"
            f"{date.month - 1:02d}/{date.day:02d}/{side}_candles_min_1.bi5")


_tls = threading.local()


def _conn():
    c = getattr(_tls, "conn", None)
    if c is None:
        c = http.client.HTTPConnection("datafeed.dukascopy.com", timeout=30)
        _tls.conn = c
    return c


def _drop_conn():
    c = getattr(_tls, "conn", None)
    if c is not None:
        try:
            c.close()
        except Exception:  # noqa: BLE001
            pass
        _tls.conn = None


def fetch_one(sym, date, side):
    # Persistent per-thread HTTP connection — a fresh TCP handshake per file capped the
    # naive urllib version at ~5 files/s aggregate.
    path = url_for(sym, date, side).replace("http://datafeed.dukascopy.com", "")
    last = "?"
    for attempt in range(4):
        try:
            c = _conn()
            c.request("GET", path, headers=HEADERS)
            r = c.getresponse()
            data = r.read()
            if r.status == 200:
                return (sym, date.isoformat(), side, "ok" if data else "empty", data)
            if r.status == 404:
                return (sym, date.isoformat(), side, "e404", b"")
            last = f"http{r.status}"
        except Exception as e:  # noqa: BLE001 — network retry loop
            last = type(e).__name__
        _drop_conn()
        time.sleep(0.5 + 1.5 * attempt)
    return (sym, date.isoformat(), side, "err:" + last, b"")


def cmd_download(args):
    con = raw_con()
    have = {(s, d, side) for s, d, side in con.execute(
        "SELECT Symbol, Date, Side FROM RawFiles WHERE Status NOT LIKE 'err%'")}
    tasks = [(sym, d, side)
             for sym in args.symbols
             for d in daterange(args.date_from, args.date_to)
             for side in SIDES
             if (sym, d.isoformat(), side) not in have]
    print(f"download: {len(tasks)} files missing ({len(have)} already archived), workers={args.workers}",
          flush=True)
    if not tasks:
        return
    t0 = time.time()
    done = 0
    buf = []
    with ThreadPoolExecutor(args.workers) as ex:
        futs = [ex.submit(fetch_one, *t) for t in tasks]
        for f in as_completed(futs):
            buf.append(f.result())
            done += 1
            if len(buf) >= 500:
                con.executemany("INSERT OR REPLACE INTO RawFiles VALUES (?,?,?,?,?)", buf)
                con.commit()
                buf = []
                rate = done / (time.time() - t0)
                eta = (len(tasks) - done) / rate / 60 if rate > 0 else -1
                print(f"  {done}/{len(tasks)} ({rate:.0f}/s, eta {eta:.0f}m)", flush=True)
    if buf:
        con.executemany("INSERT OR REPLACE INTO RawFiles VALUES (?,?,?,?,?)", buf)
        con.commit()
    for row in con.execute("SELECT Status, COUNT(*) FROM RawFiles GROUP BY Status"):
        print("  status", tuple(row))


def decode_records(blob):
    """-> list of (t_off_sec, p1, p2, p3, p4, vol) raw ints/float."""
    if not blob:
        return []
    data = lzma.decompress(blob)
    if len(data) % REC.size:
        raise ValueError(f"blob length {len(data)} not a multiple of {REC.size}")
    return [REC.unpack_from(data, i * REC.size) for i in range(len(data) // REC.size)]


def ohlc_from(rec, order):
    _, p1, p2, p3, p4, _ = rec
    if order == "toclh":                      # t, open, close, low, high
        return p1, p4, p3, p2
    return p1, p2, p3, p4                     # t, open, high, low, close


def violations(recs, order):
    v = 0
    for r in recs:
        o, h, l, c = ohlc_from(r, order)
        if h < max(o, c) or l > min(o, c):
            v += 1
    return v


def tape_close_median(sym, date):
    con = sqlite3.connect(str(MARKET_DB))
    rows = [r[0] for r in con.execute(
        "SELECT Close FROM MarketDataBars WHERE Symbol=? AND Timeframe='H1' "
        "AND OpenTimeUtc LIKE ?", (sym, date.isoformat() + "%"))]
    con.close()
    return statistics.median(rows) if rows else None


def cmd_probe(args):
    con = raw_con()
    print(f"probe date {args.date} — field-order + scale evidence")
    for sym in args.symbols:
        row = con.execute("SELECT Bytes, Status FROM RawFiles WHERE Symbol=? AND Date=? AND Side='BID'",
                          (sym, args.date.isoformat())).fetchone()
        if row is None or row[1] != "ok":
            print(f"  {sym:8s} no archived BID file for probe date (status={row[1] if row else 'missing'})")
            continue
        recs = decode_records(row[0])
        offs = [r[0] for r in recs]
        v_oclh = violations(recs, "toclh")
        v_ohlc = violations(recs, "tohlc")
        order = "toclh" if v_oclh <= v_ohlc else "tohlc"
        med_raw = statistics.median(r[1] for r in recs)
        tape_med = tape_close_median(sym, args.date)
        if tape_med:
            k = round(math.log10(med_raw / tape_med))
            scale = 10.0 ** k
        else:
            k, scale = None, None
        print(f"  {sym:8s} n={len(recs):4d} t_off[min..max]=[{min(offs)}..{max(offs)}] all%60==0:{all(o % 60 == 0 for o in offs)}"
              f"  viol(t,o,c,l,h)={v_oclh} viol(t,o,h,l,c)={v_ohlc} -> {order}"
              f"  medRaw={med_raw:.0f} tapeMed={tape_med} -> scale=1e{k}")
        if scale:
            ev = (f"probe {args.date}: order={order} (viol {v_oclh} vs {v_ohlc}), "
                  f"scale=1e{k} (medRaw {med_raw:.0f} / tape {tape_med})")
            con.execute("INSERT OR REPLACE INTO Meta VALUES (?,?,?,?)", (sym, scale, order, ev))
    con.commit()


def meta_for(con, sym):
    row = con.execute("SELECT Scale, FieldOrder FROM Meta WHERE Symbol=?", (sym,)).fetchone()
    if row is None:
        raise SystemExit(f"no Meta for {sym} — run probe first")
    return float(row[0]), row[1]


def m1_stream(con, sym, d0, d1):
    """Yield (utc_datetime, bidO, bidH, bidL, bidC, spread_or_None, vol) ordered by time."""
    scale, order = meta_for(con, sym)
    for d in daterange(d0, d1):
        rows = {side: con.execute(
            "SELECT Bytes, Status FROM RawFiles WHERE Symbol=? AND Date=? AND Side=?",
            (sym, d.isoformat(), side)).fetchone() for side in SIDES}
        bid = rows["BID"]
        if bid is None or bid[1] != "ok":
            continue
        ask_recs = {}
        ask = rows["ASK"]
        if ask is not None and ask[1] == "ok":
            for r in decode_records(ask[0]):
                ask_recs[r[0]] = r
        base = dt.datetime(d.year, d.month, d.day)
        for r in decode_records(bid[0]):
            # Zero-volume records are Dukascopy filler (verified 2026-07-16: Saturdays are 1,440
            # flat zero-vol rows; Sunday pre-open 1,260; real weekdays ~6). cTrader emits no bar
            # for a tickless minute, so skipping them also matches venue bar-emission behavior.
            if r[5] == 0.0:
                continue
            o, h, l, c = ohlc_from(r, order)
            spread = None
            a = ask_recs.get(r[0])
            if a is not None:
                ao, ah, al, ac = ohlc_from(a, order)
                spread = (ac - c) / scale
            yield (base + dt.timedelta(seconds=r[0]),
                   o / scale, h / scale, l / scale, c / scale, spread, r[5])


def last_sunday(year, month):
    d = dt.date(year, month, 31 if month in (3, 10) else 30)
    while d.weekday() != 6:
        d -= dt.timedelta(days=1)
    return d


def venue_offset_hours(t):
    """cTrader venue midnight = UTC+3 in EU summer, UTC+2 in winter (measured from tape)."""
    mar = dt.datetime.combine(last_sunday(t.year, 3), dt.time(1, 0))
    oct_ = dt.datetime.combine(last_sunday(t.year, 10), dt.time(1, 0))
    return 3 if mar <= t < oct_ else 2


def bucket_open(t, tf):
    if tf == "M15":
        return t.replace(minute=t.minute - t.minute % 15, second=0)
    if tf == "H1":
        return t.replace(minute=0, second=0)
    off = venue_offset_hours(t)
    v = t + dt.timedelta(hours=off)
    if tf == "H4":
        vb = v.replace(hour=v.hour - v.hour % 4, minute=0, second=0)
    elif tf == "D1":
        vb = v.replace(hour=0, minute=0, second=0)
    else:
        raise ValueError(tf)
    return vb - dt.timedelta(hours=off)


def aggregate(stream, timeframes):
    """Streaming aggregation. Yields (tf, open_time, o, h, l, c, vol, spread_median)."""
    state = {tf: None for tf in timeframes}  # tf -> [open_time, o, h, l, c, vol, [spreads]]
    for (t, o, h, l, c, spread, vol) in stream:
        for tf in timeframes:
            bo = bucket_open(t, tf)
            s = state[tf]
            if s is not None and s[0] != bo:
                yield (tf, s[0], s[1], s[2], s[3], s[4], s[5],
                       statistics.median(s[6]) if s[6] else None)
                s = None
            if s is None:
                state[tf] = [bo, o, h, l, c, vol, [spread] if spread is not None else []]
            else:
                s[2] = max(s[2], h)
                s[3] = min(s[3], l)
                s[4] = c
                s[5] += vol
                if spread is not None:
                    s[6].append(spread)
    for tf, s in state.items():
        if s is not None:
            yield (tf, s[0], s[1], s[2], s[3], s[4], s[5],
                   statistics.median(s[6]) if s[6] else None)


def cmd_import(args):
    if args.date_to >= dt.date(2025, 1, 1):
        raise SystemExit("import refuses dates >= 2025-01-01: the recorded cTrader tape is the "
                         "sole 2025+ truth in MarketDataBars (LEDGER Session 2 pre-registration)")
    con = raw_con()
    out = sqlite3.connect(str(MARKET_DB), timeout=120)
    now = dt.datetime.utcnow().strftime("%Y-%m-%d %H:%M:%S.%f0")
    total = 0
    for sym in args.symbols:
        rows = []
        inserted = 0
        for (tf, bo, o, h, l, c, vol, spread) in aggregate(
                m1_stream(con, sym, args.date_from, args.date_to), args.timeframes):
            rows.append((sym, tf, bo.strftime("%Y-%m-%d %H:%M:%S"), o, h, l, c, vol,
                         "dukascopy", 0, now, spread))
            if len(rows) >= 20000:
                cur = out.executemany(
                    "INSERT OR IGNORE INTO MarketDataBars "
                    "(Symbol,Timeframe,OpenTimeUtc,Open,High,Low,Close,Volume,Source,Quality,IngestedAtUtc,Spread) "
                    "VALUES (?,?,?,?,?,?,?,?,?,?,?,?)", rows)
                inserted += cur.rowcount
                out.commit()
                rows = []
        if rows:
            cur = out.executemany(
                "INSERT OR IGNORE INTO MarketDataBars "
                "(Symbol,Timeframe,OpenTimeUtc,Open,High,Low,Close,Volume,Source,Quality,IngestedAtUtc,Spread) "
                "VALUES (?,?,?,?,?,?,?,?,?,?,?,?)", rows)
            inserted += cur.rowcount
            out.commit()
        print(f"  {sym:8s} inserted {inserted} bars ({','.join(args.timeframes)})", flush=True)
        total += inserted
    print(f"import total: {total} bars")
    out.close()


def cmd_reconcile(args):
    con = raw_con()
    tape = sqlite3.connect(str(MARKET_DB))
    tdb = sqlite3.connect(str(TRADING_DB))
    spec = {r[0]: r[1] for r in tdb.execute(
        "SELECT Symbol, TypicalSpread FROM VenueSymbolSpecs")} if tdb.execute(
        "SELECT name FROM sqlite_master WHERE name='VenueSymbolSpecs'").fetchone() else {}
    print(f"reconcile {args.date_from} -> {args.date_to} (derived vs recorded tape)")
    print(f"{'symbol':8s} {'tf':3s} {'offs':>4s} {'matched':>7s} {'tapeOnly':>8s} {'dukaOnly':>8s} "
          f"{'med|dC|bps':>10s} {'p90bps':>7s} {'sign+%':>6s} {'medSpr':>9s} {'venueSpr':>9s}")
    findings = []
    for sym in args.symbols:
        derived = {}
        for (tf, bo, o, h, l, c, vol, spread) in aggregate(
                m1_stream(con, sym, args.date_from, args.date_to), ["H1", "H4"]):
            derived.setdefault(tf, {})[bo.strftime("%Y-%m-%d %H:%M:%S")] = (c, spread)
        for tf in ("H1", "H4"):
            rec = {r[0]: r[1] for r in tape.execute(
                "SELECT OpenTimeUtc, Close FROM MarketDataBars "
                "WHERE Symbol=? AND Timeframe=? AND OpenTimeUtc>=? AND OpenTimeUtc<=?",
                (sym, tf, args.date_from.isoformat(), args.date_to.isoformat() + " 23:59:59"))}
            der = derived.get(tf, {})
            if not der or not rec:
                print(f"{sym:8s} {tf:3s}  no data (derived={len(der)}, tape={len(rec)})")
                continue
            best = None
            for off in range(-3, 4):
                dd = dt.timedelta(hours=off)
                deltas = []
                for k, (c, _) in der.items():
                    kk = (dt.datetime.strptime(k, "%Y-%m-%d %H:%M:%S") + dd).strftime("%Y-%m-%d %H:%M:%S")
                    if kk in rec:
                        deltas.append(abs(c - rec[kk]) / rec[kk])
                if len(deltas) > 50:
                    med = statistics.median(deltas)
                    if best is None or med < best[1]:
                        best = (off, med, len(deltas))
            if best is None:
                print(f"{sym:8s} {tf:3s}  <50 joinable bars at any offset")
                findings.append(f"{sym}/{tf}: unjoinable")
                continue
            off = best[0]
            dd = dt.timedelta(hours=off)
            deltas, signs, spreads = [], 0, []
            matched = 0
            for k, (c, spread) in der.items():
                kk = (dt.datetime.strptime(k, "%Y-%m-%d %H:%M:%S") + dd).strftime("%Y-%m-%d %H:%M:%S")
                if kk in rec:
                    matched += 1
                    deltas.append((c - rec[kk]) / rec[kk])
                    if c > rec[kk]:
                        signs += 1
                    if spread is not None:
                        spreads.append(spread)
            absd = sorted(abs(x) * 1e4 for x in deltas)
            med_bps = statistics.median(absd)
            p90 = absd[int(0.9 * len(absd))]
            signpct = 100.0 * signs / matched
            med_spr = statistics.median(spreads) if spreads else float("nan")
            vs = spec.get(sym, float("nan"))
            print(f"{sym:8s} {tf:3s} {off:+4d} {matched:7d} {len(rec) - matched:8d} {len(der) - matched:8d} "
                  f"{med_bps:10.2f} {p90:7.2f} {signpct:6.1f} {med_spr:9.5f} {vs:9.5f}")
            if off != 0:
                findings.append(f"{sym}/{tf}: best offset {off:+d}h (timestamp-base divergence)")
            if signpct > 60 or signpct < 40:
                findings.append(f"{sym}/{tf}: sign bias {signpct:.0f}% (systematic level offset)")
    print("\nfindings:" if findings else "\nfindings: none")
    for f in findings:
        print("  -", f)


def cmd_status(args):
    con = raw_con()
    print("archive:", RAW_DB)
    for r in con.execute("""
        SELECT Symbol, MIN(Date), MAX(Date), SUM(Status='ok'), SUM(Status='e404'),
               SUM(Status='empty'), SUM(Status LIKE 'err%')
        FROM RawFiles GROUP BY Symbol ORDER BY Symbol"""):
        print(f"  {r[0]:8s} {r[1]} -> {r[2]}  ok={r[3]} e404={r[4]} empty={r[5]} err={r[6]}")
    n = con.execute("SELECT COUNT(*), COALESCE(SUM(LENGTH(Bytes)),0)/1e6 FROM RawFiles").fetchone()
    print(f"  files={n[0]} blobMB={n[1]:.0f}")


def parse_date(s):
    return dt.date.fromisoformat(s)


ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
sub = ap.add_subparsers(dest="cmd", required=True)
for name in ("download", "probe", "reconcile", "import", "status"):
    p = sub.add_parser(name)
    p.add_argument("--symbols", nargs="*", default=SYMBOLS)
    if name in ("download", "reconcile", "import"):
        p.add_argument("--from", dest="date_from", type=parse_date, required=True)
        p.add_argument("--to", dest="date_to", type=parse_date, required=True)
    if name == "download":
        p.add_argument("--workers", type=int, default=16)
    if name == "probe":
        p.add_argument("--date", type=parse_date, required=True)
    if name == "import":
        p.add_argument("--timeframes", type=lambda s: s.split(","), default=["M15", "H1", "H4", "D1"])

args = ap.parse_args()
{"download": cmd_download, "probe": cmd_probe, "reconcile": cmd_reconcile,
 "import": cmd_import, "status": cmd_status}[args.cmd](args)
