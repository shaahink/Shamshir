using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TradingEngine.Adapters.CTrader;

/// <summary>
/// In-cBot trade/equity logger. Writes our OWN report.json + events.json from the cBot's view of
/// cTrader's positions, INSTEAD of relying on cTrader-cli's <c>--report-json</c> (which crashes its
/// BacktestReportSavingState and even suppresses the default report.html/events.json).
///
/// Design goals (per owner):
///   • Resilient to shutdown — flushed on OnStop (before NetMQ teardown) AND at periodic checkpoints,
///     so the files survive a cTrader report-saving crash or a hard stop.
///   • No hot-path cost — recording an event is an in-memory list add; serialization happens only on
///     checkpoint/stop (batched).
///   • Reconciliation-ready — every record carries the engine <c>clientOrderId</c> (Guid) so the venue
///     ledger joins directly to our DB Trades; gross/net/commission/swap/pips are cTrader's own values.
///
/// The JSON schema mirrors cTrader's report.json / events.json so the existing
/// CtraderSummaryReport / CtraderJsonReport parsers and the diff harness work unchanged.
/// </summary>
public sealed class ShamshirTradeLogger
{
    // Distinct from cTrader's own report.json / events.json so the two can never be confused or
    // collide. Readers (CtraderReportHarvester) reference these via its mirror constants.
    public const string ReportFileName = "shamshir-report.json";
    public const string EventsFileName = "shamshir-events.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private sealed class OpenInfo
    {
        public string Direction = "";
        public double EntryPrice;
        public double Quantity;
        public long EntryTime;
    }

    private sealed class HistoryItem
    {
        public long id { get; set; }
        public string clientOrderId { get; set; } = "";
        public string symbol { get; set; } = "";
        public string direction { get; set; } = "";
        public double net { get; set; }
        public double gross { get; set; }
        public double commissions { get; set; }
        public double swaps { get; set; }
        public double entryPrice { get; set; }
        public double closePrice { get; set; }
        public double pips { get; set; }
        public double quantity { get; set; }
        public long entryTime { get; set; }
        public long closeTime { get; set; }
    }

    private sealed class EventItem
    {
        public int serial { get; set; }
        public long positionId { get; set; }
        public string clientOrderId { get; set; } = "";
        public string @event { get; set; } = "";
        public long time { get; set; }
        public string type { get; set; } = "";
        public double entryPrice { get; set; }
        public double? closePrice { get; set; }
        public double grossProfit { get; set; }
        public double netProfit { get; set; }
        public double commission { get; set; }
        public double swap { get; set; }
        public double pips { get; set; }
        public double quantity { get; set; }
        public double balance { get; set; }
        public double equity { get; set; }
    }

    private sealed class EquityPoint
    {
        public double balance { get; set; }
        public double equity { get; set; }
        public long timestamp { get; set; }
    }

    private sealed class OrderSubmit
    {
        public string clientOrderId { get; set; } = "";
        public string orderType { get; set; } = "";
        public string direction { get; set; } = "";
        public double requestedPrice { get; set; }
        public double bid { get; set; }
        public double ask { get; set; }
        public long venueTime { get; set; }
        /// <summary>"pending" (venue rested it), "immediate" (venue filled on submit), or "rejected".</summary>
        public string outcome { get; set; } = "";
        public string error { get; set; } = "";
    }

    private readonly Dictionary<long, OpenInfo> _open = new();
    private readonly List<HistoryItem> _history = new();
    private readonly List<EventItem> _events = new();
    private sealed class BarClock
    {
        public long barOpenTime { get; set; }
        public long venueTime { get; set; }
    }

    private readonly List<OrderSubmit> _submits = new();
    private readonly List<BarClock> _barClock = new();
    private readonly List<EquityPoint> _equity = new();
    private readonly object _gate = new();
    private int _serial;

    public string Symbol { get; set; } = "";
    public string Period { get; set; } = "";
    public double StartingCapital { get; set; }

    /// <summary>
    /// F34: the venue account's deposit currency. Every figure in this report — gross, net,
    /// commission, swap, equity — is denominated in it. The engine models USD, so a report in any
    /// other currency is scaled by an FX rate and is not comparable to a tape run. It goes in the
    /// report (not just a Print) because the cBot's stdout does not survive the cTrader CLI.
    /// </summary>
    public string AccountCurrency { get; set; } = "";

    /// <summary>
    /// F33: how many positions the venue did NOT hold at the stop-loss/take-profit price the engine
    /// asked for. Must be zero. Reported in the ledger rather than via Print for the same reason as
    /// <see cref="AccountCurrency"/> — the cBot's stdout does not survive the cTrader CLI.
    /// </summary>
    public int ProtectionMismatches { get; set; }

    /// <summary>
    /// F38: what the venue did with each entry order at the instant we submitted it — venue clock, the
    /// bid/ask we were quoted, the price we asked for, and whether the venue took it as a resting order
    /// or filled it on the spot. Without this the engine can only see the eventual position and has to
    /// guess how it came about; a limit that fills THROUGH its own limit price (the F38 signature) is
    /// only explicable from the submit-time quote, which lives nowhere else.
    /// </summary>
    public void RecordOrderSubmit(string clientOrderId, string orderType, string direction,
        double requestedPrice, double bid, double ask, long venueTime, string outcome, string error)
    {
        lock (_gate)
            _submits.Add(new OrderSubmit
            {
                clientOrderId = clientOrderId, orderType = orderType, direction = direction,
                requestedPrice = requestedPrice, bid = bid, ask = ask,
                venueTime = venueTime, outcome = outcome, error = error,
            });
    }

    /// <summary>
    /// F38: the venue clock at the moment we hand a bar to the engine, against that bar's own open time.
    /// For an H4 bar the gap must be exactly one bar (we publish a bar when it closes). Two bars means we
    /// are feeding the engine a stale bar and every order it returns is placed a bar late. Sampled (first
    /// few bars) — this is a clock check, not a data feed.
    /// </summary>
    public void RecordBarClock(long barOpenTime, long venueTime)
    {
        lock (_gate)
        {
            if (_barClock.Count < 5)
                _barClock.Add(new BarClock { barOpenTime = barOpenTime, venueTime = venueTime });
        }
    }

    public void RecordEquity(double balance, double equity, long time)
    {
        lock (_gate)
            _equity.Add(new EquityPoint { balance = balance, equity = equity, timestamp = time });
    }

    public void RecordOpen(long posId, string clientOrderId, string direction, double entryPrice,
        double quantity, long time, double balance, double equity)
    {
        lock (_gate)
        {
            _open[posId] = new OpenInfo
            {
                Direction = direction, EntryPrice = entryPrice, Quantity = quantity, EntryTime = time
            };
            _events.Add(new EventItem
            {
                serial = _serial++, positionId = posId, clientOrderId = clientOrderId,
                @event = "Create Position", time = time, type = direction,
                entryPrice = entryPrice, closePrice = null, quantity = quantity,
                balance = balance, equity = equity
            });
        }
    }

    public void RecordClose(long posId, string clientOrderId, string eventName, double closePrice,
        double gross, double net, double commission, double swap, double pips, long time,
        double balance, double equity)
    {
        lock (_gate)
        {
            _open.TryGetValue(posId, out var open);
            var dir = open?.Direction ?? "";
            var entry = open?.EntryPrice ?? 0;
            var qty = open?.Quantity ?? 0;
            var entryTime = open?.EntryTime ?? time;

            _history.Add(new HistoryItem
            {
                id = posId, clientOrderId = clientOrderId, symbol = Symbol, direction = dir,
                net = net, gross = gross, commissions = commission, swaps = swap,
                entryPrice = entry, closePrice = closePrice, pips = pips, quantity = qty,
                entryTime = entryTime, closeTime = time
            });
            _events.Add(new EventItem
            {
                serial = _serial++, positionId = posId, clientOrderId = clientOrderId,
                @event = eventName, time = time, type = dir, entryPrice = entry, closePrice = closePrice,
                grossProfit = gross, netProfit = net, commission = commission, swap = swap, pips = pips,
                quantity = qty, balance = balance, equity = equity
            });
            _open.Remove(posId);
        }
    }

    /// <summary>Atomically (write-temp-then-move) writes report.json + events.json into <paramref name="dir"/>.</summary>
    public void Write(string dir, double endingBalance, double endingEquity)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;

        string eventsJson, reportJson;
        lock (_gate)
        {
            eventsJson = JsonSerializer.Serialize(_events, JsonOpts);
            reportJson = JsonSerializer.Serialize(BuildReport(endingBalance, endingEquity), JsonOpts);
        }

        Directory.CreateDirectory(dir);
        WriteAtomic(Path.Combine(dir, EventsFileName), eventsJson);
        WriteAtomic(Path.Combine(dir, ReportFileName), reportJson);
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        // Move is atomic on the same volume; overwrite the previous checkpoint in one step so a crash
        // mid-write never leaves a truncated report.
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    private object BuildReport(double endingBalance, double endingEquity)
    {
        var net = _history.Sum(h => h.net);
        var grossWin = _history.Where(h => h.gross > 0).Sum(h => h.gross);
        var grossLoss = Math.Abs(_history.Where(h => h.gross < 0).Sum(h => h.gross));
        var pf = grossLoss > 0 ? grossWin / grossLoss : 0;
        var (maxDdPct, maxDdAbs) = ComputeDrawdown();

        return new
        {
            main = new
            {
                symbol = Symbol,
                period = Period,
                netProfit = net,
                startingCapital = StartingCapital,
                endingEquity,
                endingBalance,
                accountCurrency = AccountCurrency,
                protectionMismatches = ProtectionMismatches,
            },
            tradeStatistics = new
            {
                netProfit = Directional(h => h.net),
                totalTrades = DirectionalCount(_ => true),
                winningTrades = DirectionalCount(h => h.net > 0),
                losingTrades = DirectionalCount(h => h.net < 0),
                commissions = Directional(h => h.commissions),
                swaps = Directional(h => h.swaps),
                profitFactor = new { all = pf, @long = 0.0, @short = 0.0 },
            },
            equity = new
            {
                maxEquityDrawdownPercent = maxDdPct,
                maxEquityDrawdownAbsolute = maxDdAbs,
                points = _equity,
            },
            history = new { items = _history },
            orderSubmits = new { items = _submits },   // F38: see RecordOrderSubmit
            barClock = new { items = _barClock },      // F38: see RecordBarClock
        };
    }

    private static bool IsLong(string d) => d is "Long" or "Buy";
    private static bool IsShort(string d) => d is "Short" or "Sell";

    private object Directional(Func<HistoryItem, double> sel) => new
    {
        all = _history.Sum(sel),
        @long = _history.Where(h => IsLong(h.direction)).Sum(sel),
        @short = _history.Where(h => IsShort(h.direction)).Sum(sel),
    };

    private object DirectionalCount(Func<HistoryItem, bool> f) => new
    {
        all = _history.Count(f),
        @long = _history.Count(h => f(h) && IsLong(h.direction)),
        @short = _history.Count(h => f(h) && IsShort(h.direction)),
    };

    private (double pct, double abs) ComputeDrawdown()
    {
        var peak = StartingCapital > 0 ? StartingCapital
            : _equity.Count > 0 ? _equity[0].equity : 0;
        double maxAbs = 0, maxPct = 0;
        foreach (var p in _equity)
        {
            if (p.equity > peak) peak = p.equity;
            var dd = peak - p.equity;
            if (dd > maxAbs) maxAbs = dd;
            var pct = peak > 0 ? dd / peak * 100.0 : 0;
            if (pct > maxPct) maxPct = pct;
        }
        return (maxPct, maxAbs);
    }
}
