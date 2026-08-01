using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using cAlgo.API;
using NetMQ;
using NetMQ.Sockets;

namespace TradingEngine.Adapters.CTrader;

[Robot(AccessRights = AccessRights.FullAccess)]
public class TradingEngineCBot : Robot
{
    private const string Version = "2.0.0";
    private static readonly string BuildDate = "2026-06-18";

    [Parameter("DataPort", DefaultValue = "15555")]
    public int DataPort { get; set; } = 15555;

    [Parameter("CommandPort", DefaultValue = "15556")]
    public int CommandPort { get; set; } = 15556;

    [Parameter("TickEveryN", DefaultValue = "10")]
    public int TickEveryN { get; set; } = 10;

    [Parameter("SymbolString", DefaultValue = "EURUSD")]
    public string SymbolString { get; set; } = "EURUSD";

    [Parameter("Periods", DefaultValue = "H1")]
    public string Periods { get; set; } = "H1";

    // Directory where the cBot writes its OWN report.json + events.json (our resilient venue ledger,
    // replacing cTrader-cli's crashing --report-json). Empty = disabled.
    [Parameter("ReportPath", DefaultValue = "")]
    public string ReportPath { get; set; } = "";

    private PublisherSocket? _pub;
    private DealerSocket? _dealer;
    private NetMQPoller? _poller;
    private readonly BlockingCollection<string> _inbox = new();
    private readonly ConcurrentQueue<Action> _mainActions = new();
    private int _tickCounter;
    private int _barEventCount;
    private int _duplicateCount;
    private int _cmdsReceived;
    private int _ordersExecuted;
    private int _execsSent;
    private readonly HashSet<(string symbol, string tf, DateTime openTime)> _publishedBars = new();
    private readonly List<Bars> _subscriptions = new();
    private readonly Dictionary<long, Guid> _positionMap = new();
    private readonly HashSet<long> _commandCloses = new();
    private volatile bool _connected;

    private readonly ShamshirTradeLogger _tradeLog = new();
    private int _reportCheckpoint;
    private const int ReportCheckpointEveryNBars = 50;

    private static long EpochMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static readonly JsonSerializerOptions JsonOpts = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void Diag(string msg)
    {
        if (_pub is null) return;
        _pub.SendMoreFrame("diag").SendFrame(msg);
    }

    protected override void OnStart()
    {
        Print($"CBOT|START|symbol={SymbolName}|tf={TimeFrame.ShortName}|dataPort={DataPort}|cmdPort={CommandPort}|v={Version}|build={BuildDate}");

        _tradeLog.Symbol = SymbolName;
        _tradeLog.Period = TimeFrame.ShortName;
        _tradeLog.StartingCapital = Account.Balance;
        _tradeLog.RecordEquity(Account.Balance, Account.Equity, EpochMs(Server.TimeInUtc));
        if (!string.IsNullOrWhiteSpace(ReportPath))
            Print($"CBOT|REPORT_PATH|{ReportPath}");

        _pub = new PublisherSocket();
        _pub.Bind($"tcp://*:{DataPort}");
        Print($"CBOT|PUB_BOUND|dataPort={DataPort}");

        _dealer = new DealerSocket();
        _dealer.Connect($"tcp://127.0.0.1:{CommandPort}");
        _dealer.ReceiveReady += OnDealerReceive;

        _poller = new NetMQPoller { _dealer };
        _poller.RunAsync();

        var symbols = SymbolString.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var periods = Periods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var subs = new List<(string sym, string tf)>();
        for (int i = 0; i < symbols.Length; i++)
        {
            var sym = symbols[i];
            var period = i < periods.Length ? periods[i] : periods[^1];
            var tf = ParseTimeFrame(period);
            var bars = MarketData.GetBars(tf, sym);
            _subscriptions.Add(bars);
            subs.Add((sym, period));
        }

        Print($"CBOT|SUBS|{string.Join(", ", subs.Select(s => $"{s.sym}:{s.tf}"))}");

        // V1/V2 — rebuild the Guid↔venue-id map from existing positions (durable Guid in Comment)
        // and announce the open-position snapshot in the hello so the engine can reconcile.
        RebuildPositionMap();
        var positionSnapshot = BuildPositionSnapshot();

        var helloMsg = Serialize("hello", new
        {
            v = 1,
            symbols = symbols,
            periods = periods,
            subs = subs.Select(s => new { s.sym, tf = s.tf }).ToArray(),
            barsLoaded = _subscriptions.Sum(s => s.Count),
            account = new { balance = Account.Balance, equity = Account.Equity },
            positions = positionSnapshot
        });
        _dealer.SendFrame(helloMsg);
        Print($"CBOT|HELLO_SENT|subs={subs.Count}|barsLoaded={_subscriptions.Sum(s => s.Count)}");

        for (int retry = 0; retry < 50 && !_connected; retry++)
        {
            if (retry > 0 && retry % 10 == 0)
            {
                _dealer.SendFrame(helloMsg);
                Print($"CBOT|HELLO_RESEND|retry={retry}");
            }
            System.Threading.Thread.Sleep(100);
        }

        if (!_connected)
        {
            Print("CBOT|HELLO_TIMEOUT|engine did not acknowledge hello — stopping");
            Stop();
            return;
        }

        Print($"CBOT|HANDSHAKE_COMPLETE|connected");

        foreach (var (sym, period) in subs)
        {
            var tf = ParseTimeFrame(period);
            var bars = MarketData.GetBars(tf, sym);
            bars.BarClosed += OnBarClosed;
        }

        Diag($"SUBSCRIBED|subs={subs.Count}");
        Print($"CBOT|SUBSCRIBED|subs={subs.Count}");

        Positions.Closed += OnPositionClosed;

        PublishAccount();

        Print($"CBOT|READY|dataPort={DataPort}|cmdPort={CommandPort}");
    }

    protected override void OnTick()
    {
        _tickCounter++;

        if (_tickCounter % TickEveryN == 0)
        {
            Publish("tick", new
            {
                symbol = SymbolName,
                bid = Symbol.Bid,
                ask = Symbol.Ask,
                time = Server.TimeInUtc.ToString("o")
            });

            PublishAccount();
        }
    }

    private void OnBarClosed(BarClosedEventArgs args)
    {
        _barEventCount++;
        var bars = args.Bars;
        var bar = bars.Last(1);
        if (bar.Open == 0 && bar.High == 0) return;

        var key = (bars.SymbolName, bars.TimeFrame.ShortName, bar.OpenTime);
        if (!_publishedBars.Add(key)) { _duplicateCount++; return; }

        Print($"CBOT|BAR_EVENT|seq={_barEventCount}|" +
              $"count={bars.Count}|" +
              $"openTime={bar.OpenTime:yyyy-MM-dd HH:mm}|" +
              $"open={bar.Open:F5}|high={bar.High:F5}|low={bar.Low:F5}|close={bar.Close:F5}|" +
              $"dup=false");

        var openTimeUtc = DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc);

        _tradeLog.RecordEquity(Account.Balance, Account.Equity, EpochMs(openTimeUtc));
        if (++_reportCheckpoint % ReportCheckpointEveryNBars == 0)
            TryWriteReport();

        var barJson = Serialize("bar", new
        {
            v = 1,
            seq = _barEventCount,
            symbol = bars.SymbolName,
            period = bars.TimeFrame.ShortName,
            openTime = openTimeUtc.ToString("o"),
            open = bar.Open,
            high = bar.High,
            low = bar.Low,
            close = bar.Close,
            volume = (long)bar.TickVolume,
            simTime = Server.TimeInUtc.ToString("o"),
            account = new { balance = Account.Balance, equity = Account.Equity }
        });
        _dealer!.SendFrame(barJson);
        Diag($"BAR_SENT|{bars.SymbolName}|{bars.TimeFrame.ShortName}|{openTimeUtc:o}|close={bar.Close:F5}|seq={_barEventCount}");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_inbox.TryTake(out var json, 100))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

                    if (type == "bar_done" && doc.RootElement.TryGetProperty("seq", out var seqEl) && seqEl.GetInt32() == _barEventCount)
                    {
                        var commands = doc.RootElement.TryGetProperty("commands", out var cmds) ? cmds : default;
                        _cmdsReceived += commands.ValueKind == JsonValueKind.Array ? commands.GetArrayLength() : 0;

                        var execs = new List<object>();
                        if (commands.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var cmd in commands.EnumerateArray())
                            {
                                var cmdType = cmd.GetProperty("type").GetString();
                                if (cmdType == "submit_order")
                                {
                                    var result = ExecuteSubmitOrder(cmd);
                                    execs.Add(result);
                                }
                                else if (cmdType == "close_position")
                                {
                                    var result = ExecuteClosePosition(cmd);
                                    execs.Add(result);
                                }
                                else if (cmdType == "close_partial")
                                {
                                    var result = ExecuteClosePartialPosition(cmd);
                                    execs.Add(result);
                                }
                                else if (cmdType == "cancel_order")
                                {
                                    var result = ExecuteCancelOrder(cmd);
                                    execs.Add(result);
                                }
                                else if (cmdType == "modify_order")
                                {
                                    var result = ExecuteModifyOrder(cmd);
                                    execs.Add(result);
                                }
                                else if (cmdType == "shutdown")
                                {
                                    Print("CBOT|SHUTDOWN|received via bar_done");
                                    Stop();
                                    return;
                                }
                            }
                        }

                        _execsSent += execs.Count;
                        var barResult = Serialize("bar_result", new
                        {
                            v = 1,
                            seq = _barEventCount,
                            execs = execs,
                            account = new { balance = Account.Balance, equity = Account.Equity }
                        });
                        _dealer!.SendFrame(barResult);
                        Diag($"BAR_RESULT|seq={_barEventCount}|execs={execs.Count}");
                        return;
                    }
                    else if (type == "shutdown")
                    {
                        Print("CBOT|SHUTDOWN|received during bar processing");
                        Stop();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Print($"CBOT|BAR_PROC_ERR|{ex.Message}");
                }
            }
        }

        Print($"CBOT|BAR_TIMEOUT|seq={_barEventCount}|bar_done not received within 30s");
        Stop();
    }

    private object ExecuteSubmitOrder(JsonElement cmd)
    {
        var clientOrderId = cmd.GetProperty("clientOrderId").GetString()!;
        var symbol = cmd.GetProperty("symbol").GetString()!;
        var direction = cmd.GetProperty("direction").GetString()!;
        var lots = cmd.GetProperty("lots").GetDouble();
        var slPrice = cmd.GetProperty("slPrice").GetDouble();
        var tpPrice = cmd.GetProperty("tpPrice").GetDouble();
        var orderType = cmd.TryGetProperty("orderType", out var ot) ? ot.GetString() : "Market";
        var limitPrice = cmd.TryGetProperty("limitPrice", out var lp) ? lp.GetDouble() : 0.0;

        Diag($"CMD_RECV|submit_order|{clientOrderId}|{symbol}|{direction}|type={orderType}|lots={lots:F4}|limit={limitPrice:F5}");

        var sym = Symbols.GetSymbol(symbol);
        if (sym is null)
            return MakeExecResult(clientOrderId, "entry_fill", 0, "Rejected", 0, lots, "Unknown symbol: " + symbol);

        var tradeType = direction == "Long" ? TradeType.Buy : TradeType.Sell;
        var volumeInUnits = Math.Floor(lots * sym.LotSize / sym.VolumeInUnitsStep) * sym.VolumeInUnitsStep;

        TradeResult? result;
        if (orderType == "Limit" && limitPrice > 0)
        {
#pragma warning disable CS0618
            result = PlaceLimitOrder(tradeType, symbol, volumeInUnits, limitPrice, "Shamshir",
                slPrice > 0 ? slPrice : null, tpPrice > 0 ? tpPrice : null);
#pragma warning restore CS0618
        }
        else
        {
            result = ExecuteMarketOrder(tradeType, symbol, volumeInUnits, "Shamshir",
                slPrice > 0 ? slPrice : null, tpPrice > 0 ? tpPrice : null, clientOrderId);
        }

        if (result?.IsSuccessful == true)
        {
            var pos = result.Position;
            _positionMap[pos.Id] = Guid.Parse(clientOrderId);
            _ordersExecuted++;

            _tradeLog.RecordOpen(pos.Id, clientOrderId, direction, pos.EntryPrice,
                pos.VolumeInUnits / sym.LotSize, EpochMs(Server.TimeInUtc), Account.Balance, Account.Equity);

            if ((slPrice > 0 || tpPrice > 0) && pos.StopLoss != slPrice && pos.TakeProfit != tpPrice)
            {
                try
                {
#pragma warning disable CS0618
                    ModifyPosition(pos, slPrice > 0 ? slPrice : pos.StopLoss, tpPrice > 0 ? tpPrice : pos.TakeProfit);
#pragma warning restore CS0618
                }
                catch { }
            }

            PublishAccount();
            return MakeExecResult(clientOrderId, "entry_fill", pos.Id, "Filled", pos.EntryPrice,
                pos.VolumeInUnits / sym.LotSize, null);
        }

        return MakeExecResult(clientOrderId, "entry_fill", 0, "Rejected", 0, lots, result?.Error.ToString() ?? "Null result");
    }

    private object ExecuteCancelOrder(JsonElement cmd)
    {
        var orderIdStr = cmd.GetProperty("orderId").GetString()!;
        foreach (var pos in Positions)
        {
            if (_positionMap.TryGetValue(pos.Id, out var orderGuid) && orderGuid.ToString() == orderIdStr)
            {
                // C2: close the open position. For pending (limit) orders, cancel via the platform.
                var result = ClosePosition(pos);
                Diag($"CANCEL_ORDER|{orderIdStr}|posId={pos.Id}|success={result?.IsSuccessful}");
                if (result?.IsSuccessful == true) PublishAccount();
                return MakeExecResult(orderIdStr, "entry_cancelled", pos.Id,
                    result?.IsSuccessful == true ? "Cancelled" : "Rejected",
                    pos.CurrentPrice > 0 ? pos.CurrentPrice : 0d, pos.VolumeInUnits / (Symbols.GetSymbol(pos.SymbolName)?.LotSize ?? 100000.0),
                    "ENTRY_EXPIRED");
            }
        }
        Print($"CBOT|CANCEL_NOT_FOUND|orderId={orderIdStr}");
        return MakeExecResult(orderIdStr, "entry_cancelled", 0, "Rejected", 0, 0, "Order not found: " + orderIdStr);
    }

    private object ExecuteClosePosition(JsonElement cmd)
    {
        var positionIdStr = cmd.GetProperty("positionId").GetString()!;
        foreach (var pos in Positions)
        {
            if (_positionMap.TryGetValue(pos.Id, out var orderGuid) && orderGuid.ToString() == positionIdStr)
            {
                var clientOrderId = orderGuid;
                // Capture realized PnL BEFORE closing — at the close instant the position's
                // floating Gross/Net P&L equals what's realized. Previously MakeExecResult
                // hard-coded 0, so every engine-requested close (SL/TP via the engine's exit
                // simulation, force-close) recorded $0 PnL in the ledger while only cTrader's own
                // async closes carried real PnL. This corrupted the ledger for most trades.
                var grossProfit = pos.GrossProfit;
                var netProfit = pos.NetProfit;
                var result = ClosePosition(pos);
                // Read commission/swap AFTER the close: before it, only the entry-side commission has
                // been charged, so the venue-reported figure would be half the round-trip cost.
                var commission = pos.Commissions;
                var swap = pos.Swap;
                _commandCloses.Add(pos.Id);
                Diag($"CLOSE_POS|{positionIdStr}|success={result?.IsSuccessful}|net={netProfit:F2}");
                if (result?.IsSuccessful == true) PublishAccount();
                return MakeExecResult(clientOrderId.ToString(), "close", pos.Id,
                    result?.IsSuccessful == true ? "Filled" : "Rejected",
                    pos.CurrentPrice > 0 ? pos.CurrentPrice : 0d, pos.VolumeInUnits / (Symbols.GetSymbol(pos.SymbolName)?.LotSize ?? 100000.0), null,
                    grossProfit, netProfit, commission, swap);
            }
        }
        Print($"CBOT|CLOSE_NOT_FOUND|positionId={positionIdStr}");
        return MakeExecResult(Guid.Empty.ToString(), "close", 0, "Rejected", 0, 0, "Position not found: " + positionIdStr);
    }

    private object ExecuteClosePartialPosition(JsonElement cmd)
    {
        var positionIdStr = cmd.GetProperty("positionId").GetString()!;
        var closeLots = cmd.TryGetProperty("lots", out var lotsEl) ? (decimal)lotsEl.GetDouble() : 0m;
        if (closeLots <= 0) closeLots = 0.01m;

        foreach (var pos in Positions)
        {
            if (_positionMap.TryGetValue(pos.Id, out var orderGuid) && orderGuid.ToString() == positionIdStr)
            {
                var clientOrderId = orderGuid;
                var lotSize = Symbols.GetSymbol(pos.SymbolName)?.LotSize ?? 100000.0;
                var volumeInUnits = (int)((double)closeLots * lotSize);
                if (volumeInUnits <= 0) volumeInUnits = 1000;

                var fraction = pos.VolumeInUnits > 0 ? Math.Min(1.0, volumeInUnits / (double)pos.VolumeInUnits) : 1.0;
                var grossProfit = pos.GrossProfit * fraction;
                var result = ClosePosition(pos, volumeInUnits);
                // M1 (iter-35 B2): read commission/swap AFTER the close. The full-close path does
                // the same — before close only entry-side commission is charged. After partial
                // close these reflect round-trip costs for the remaining portion; scaling by
                // fraction gives a close estimate for the closed portion.
                var commission = pos.Commissions * fraction;
                var swap = pos.Swap * fraction;
                var netProfit = grossProfit - commission - swap;
                Diag($"CLOSE_PARTIAL|{positionIdStr}|lots={closeLots}|units={volumeInUnits}|success={result?.IsSuccessful}|net={netProfit:F2}");
                if (result?.IsSuccessful == true) PublishAccount();

                return MakeExecResult(clientOrderId.ToString(), "partial_close", pos.Id,
                    result?.IsSuccessful == true ? "Filled" : "Rejected",
                    pos.CurrentPrice > 0 ? pos.CurrentPrice : 0d, (double)closeLots, null,
                    grossProfit, netProfit, commission, swap);
            }
        }
        Print($"CBOT|CLOSE_PARTIAL_NOT_FOUND|positionId={positionIdStr}");
        return MakeExecResult(Guid.Empty.ToString(), "partial_close", 0, "Rejected", 0, 0, "Position not found: " + positionIdStr);
    }

    // V3 — apply a stop-loss/take-profit modification and echo the venue-confirmed levels so the
    // engine can write them back. Previously modify_order commands were silently dropped (the
    // bar_done loop had no branch for them), so trailing-stop updates never reached the venue.
    private object ExecuteModifyOrder(JsonElement cmd)
    {
        var orderIdStr = cmd.GetProperty("orderId").GetString()!;
        var newSl = cmd.TryGetProperty("newSl", out var slEl) ? slEl.GetDouble() : 0.0;
        var newTp = cmd.TryGetProperty("newTp", out var tpEl) ? tpEl.GetDouble() : 0.0;

        foreach (var pos in Positions)
        {
            if (_positionMap.TryGetValue(pos.Id, out var guid) && guid.ToString() == orderIdStr)
            {
                var appliedSl = newSl > 0 ? newSl : (pos.StopLoss ?? 0.0);
                var appliedTp = newTp > 0 ? newTp : (pos.TakeProfit ?? 0.0);
                try
                {
#pragma warning disable CS0618
                    var result = ModifyPosition(pos, appliedSl > 0 ? appliedSl : (double?)null,
                        appliedTp > 0 ? appliedTp : (double?)null);
#pragma warning restore CS0618
                    Diag($"MODIFY|{orderIdStr}|sl={appliedSl:F5}|tp={appliedTp:F5}|success={result?.IsSuccessful}");
                    return MakeModifyResult(guid.ToString(), pos.Id,
                        result?.IsSuccessful == true ? "Filled" : "Rejected", appliedSl, appliedTp);
                }
                catch (Exception ex)
                {
                    Print($"CBOT|MODIFY_ERR|{orderIdStr}|{ex.Message}");
                    return MakeModifyResult(guid.ToString(), pos.Id, "Rejected", 0, 0);
                }
            }
        }
        Print($"CBOT|MODIFY_NOT_FOUND|orderId={orderIdStr}");
        return MakeModifyResult(Guid.Empty.ToString(), 0, "Rejected", 0, 0);
    }

    private object MakeModifyResult(string clientOrderId, long positionId, string state,
        double slPrice, double tpPrice)
    {
        return new
        {
            clientOrderId,
            kind = "modify",
            positionId,
            state,
            slPrice,
            tpPrice,
            fillPrice = 0.0,
            filledLots = 0.0,
            reason = (string?)null,
            simTime = Server.TimeInUtc.ToString("o")
        };
    }

    // V2 — rebuild the venue-id → engine-Guid map from positions that survived a cBot restart,
    // reading the durable Guid stored in each Shamshir position's Comment.
    private void RebuildPositionMap()
    {
        var remapped = 0;
        foreach (var pos in Positions)
        {
            if (pos.Label != "Shamshir") continue;
            if (Guid.TryParse(pos.Comment, out var guid))
            {
                _positionMap[pos.Id] = guid;
                remapped++;
            }
            else
            {
                Print($"CBOT|REMAP_SKIP|posId={pos.Id}|no durable Guid in comment");
            }
        }
        Print($"CBOT|REMAP|positions={remapped}");
    }

    // V1 — snapshot of currently-open Shamshir positions, keyed by the engine clientOrderId Guid,
    // for the engine to reconcile its tracker after a (re)connect.
    private object[] BuildPositionSnapshot()
    {
        var list = new List<object>();
        foreach (var pos in Positions)
        {
            if (!_positionMap.TryGetValue(pos.Id, out var guid)) continue;
            var sym = Symbols.GetSymbol(pos.SymbolName);
            var lots = sym is not null ? pos.VolumeInUnits / sym.LotSize : pos.VolumeInUnits / 100_000.0;
            list.Add(new
            {
                clientOrderId = guid.ToString(),
                symbol = pos.SymbolName,
                direction = pos.TradeType == TradeType.Buy ? "Long" : "Short",
                lots,
                entryPrice = pos.EntryPrice,
                stopLoss = pos.StopLoss ?? 0.0,
                takeProfit = pos.TakeProfit ?? 0.0,
                venuePositionId = pos.Id
            });
        }
        return list.ToArray();
    }

    private object MakeExecResult(string clientOrderId, string kind, long positionId,
        string state, double fillPrice, double filledLots, string? reason,
        double grossProfit = 0.0, double netProfit = 0.0,
        double commission = 0.0, double swap = 0.0)
    {
        return new
        {
            clientOrderId,
            kind,
            positionId,
            state,
            fillPrice,
            filledLots,
            reason,
            simTime = Server.TimeInUtc.ToString("o"),
            grossProfit,
            netProfit,
            commission,
            swap
        };
    }

    private void OnDealerReceive(object? sender, NetMQSocketEventArgs e)
    {
        if (!e.Socket.TryReceiveFrameString(out var json) || json is null) return;
        var captured = json;

        try
        {
            using var doc = JsonDocument.Parse(captured);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "hello_ack")
            {
                _connected = true;
                Diag("HELLO_ACK|received");
                return;
            }
            if (type == "shutdown")
            {
                Print("CBOT|SHUTDOWN|received shutdown, stopping");
                Stop();
                return;
            }
        }
        catch { }

        _inbox.Add(captured);
        Diag($"DEALER_RECV|inboxDepth={_inbox.Count}|jsonLen={captured.Length}");
    }

    private void OnPositionClosed(PositionClosedEventArgs args)
    {
        var pos = args.Position;
        if (_positionMap.TryGetValue(pos.Id, out var clientOrderId))
        {
            var sym = Symbols.GetSymbol(pos.SymbolName);
            var lots = sym is not null ? pos.VolumeInUnits / sym.LotSize : pos.VolumeInUnits / 100_000.0;
            var closePrice = pos.CurrentPrice > 0 ? pos.CurrentPrice : pos.EntryPrice;

            // Log EVERY close (command- and venue-initiated) to our own ledger with cTrader's realized
            // values. This is the single source of truth for the report.json/events.json we write.
            var closeEventName = args.Reason switch
            {
                PositionCloseReason.StopLoss => "Stop Loss Hit",
                PositionCloseReason.TakeProfit => "Take Profit Hit",
                _ => "Closed",
            };
            _tradeLog.RecordClose(pos.Id, clientOrderId.ToString(), closeEventName, closePrice,
                pos.GrossProfit, pos.NetProfit, pos.Commissions, pos.Swap, pos.Pips,
                EpochMs(Server.TimeInUtc), Account.Balance, Account.Equity);

            if (_commandCloses.Remove(pos.Id))
            {
                // Command-initiated close: exec already reported via bar_result.execs[]
                _positionMap.Remove(pos.Id);
                PublishAccount();
                return;
            }

            // Venue-initiated close (server-side SL/TP/stop-out, or manual). Propagate cTrader's
            // own close reason so the engine journals "SL"/"TP" instead of the generic "FORCE" —
            // these close intrabar before the engine's bar-level exit detection runs.
            var closeReason = MapCloseReason(args.Reason);
            var execJson = Serialize("exec", new
            {
                v = 1,
                clientOrderId = clientOrderId.ToString(),
                kind = "close",
                positionId = pos.Id,
                state = "Filled",
                fillPrice = closePrice,
                filledLots = lots,
                reason = (string?)null,
                closeReason,
                simTime = Server.TimeInUtc.ToString("o"),
                grossProfit = pos.GrossProfit,
                netProfit = pos.NetProfit,
                commission = pos.Commissions,
                swap = pos.Swap
            });
            try { _dealer?.SendFrame(execJson); } catch { }
            _execsSent++;
            Diag($"EXEC_SENT|{clientOrderId}|Filled|kind=close|reason={closeReason}|fill={closePrice:F5}|lots={lots:F4}|pnl={pos.NetProfit:F2}");
            _positionMap.Remove(pos.Id);
            PublishAccount();
        }
    }

    private static string MapCloseReason(PositionCloseReason reason) => reason switch
    {
        PositionCloseReason.StopLoss => "SL",
        PositionCloseReason.TakeProfit => "TP",
        PositionCloseReason.StopOut => "STOPOUT",
        _ => "CLOSED",
    };

    private void TryWriteReport()
    {
        if (string.IsNullOrWhiteSpace(ReportPath)) return;
        try { _tradeLog.Write(ReportPath, Account.Balance, Account.Equity); }
        catch (Exception ex) { Print($"CBOT|REPORT_WRITE_ERR|{ex.Message}"); }
    }

    protected override void OnStop()
    {
        // Flush our ledger FIRST — before any NetMQ teardown and before cTrader's own (crash-prone)
        // report-saving runs — so report.json/events.json are always written even if cTrader dies after.
        TryWriteReport();

        Diag($"STOP|ticks={_tickCounter}|barEvents={_barEventCount}|dup={_duplicateCount}");
        Print($"CBOT|STOP|ticks={_tickCounter}|barEvents={_barEventCount}|dup={_duplicateCount}");

        while (_mainActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Print($"CBOT|DRAIN_ERR|{ex.Message}"); }
        }

        var stats = Serialize("stats", new
        {
            v = 1,
            barsSent = _barEventCount,
            cmdsReceived = _cmdsReceived,
            ordersExecuted = _ordersExecuted,
            execsSent = _execsSent
        });
        try { _dealer?.SendFrame(stats); } catch { }

        if (_dealer is not null) _dealer.Options.Linger = TimeSpan.FromSeconds(2);
        if (_pub is not null) _pub.Options.Linger = TimeSpan.FromSeconds(2);

        _poller?.StopAsync();
        _dealer?.Dispose();
        _pub?.Dispose();
        _poller?.Dispose();

        try { NetMQConfig.Cleanup(true); }
        catch { NetMQConfig.Cleanup(false); }
    }

    private static TimeFrame ParseTimeFrame(string s) => s.ToUpperInvariant() switch
    {
        "M1" => TimeFrame.Minute,
        "M5" => TimeFrame.Minute5,
        "M15" => TimeFrame.Minute15,
        "M30" => TimeFrame.Minute30,
        "H1" => TimeFrame.Hour,
        "H4" => TimeFrame.Hour4,
        "D1" => TimeFrame.Daily,
        "W1" => TimeFrame.Weekly,
        _ => throw new ArgumentException($"Unknown timeframe: {s}")
    };

    private void Publish(string topic, object payload)
    {
        if (_pub is null) return;
        try
        {
            var json = Serialize(topic, payload);
            _pub.SendMoreFrame(topic).SendFrame(json);
        }
        catch (Exception ex)
        {
            Print($"CBOT|PUB_ERR|{topic}|{ex.Message}");
        }
    }

    private void PublishAccount()
    {
        Publish("acct", new
        {
            balance = Account.Balance,
            equity = Account.Equity,
            floatingPnL = Account.Equity - Account.Balance,
            time = Server.TimeInUtc.ToString("o")
        });
    }

    private static string Serialize(string type, object payload)
    {
        var dict = new System.Collections.Generic.Dictionary<string, object>(8)
        { ["type"] = type };
        using var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(payload, payload.GetType(), JsonOpts));
        foreach (var prop in payloadDoc.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return JsonSerializer.Serialize(dict, JsonOpts);
    }
}
