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
    private volatile bool _connected;

    private static readonly JsonSerializerOptions JsonOpts = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void Diag(string msg)
    {
        if (_pub is null) return;
        _pub.SendMoreFrame("diag").SendFrame(msg);
    }

    protected override void OnStart()
    {
        Print($"CBOT|START|symbol={SymbolName}|tf={TimeFrame.ShortName}|dataPort={DataPort}|cmdPort={CommandPort}");

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

        var helloMsg = Serialize("hello", new
        {
            v = 1,
            symbols = symbols,
            periods = periods,
            subs = subs.Select(s => new { s.sym, tf = s.tf }).ToArray(),
            barsLoaded = _subscriptions.Sum(s => s.Count)
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
                                else if (cmdType == "shutdown")
                                {
                                    Print("CBOT|SHUTDOWN|received via bar_done");
                                    Stop();
                                    return;
                                }
                            }
                        }

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

        Diag($"CMD_RECV|submit_order|{clientOrderId}|{symbol}|{direction}|lots={lots:F4}");

        var sym = Symbols.GetSymbol(symbol);
        if (sym is null)
            return MakeExecResult(clientOrderId, "entry_fill", 0, "Rejected", 0, lots, "Unknown symbol: " + symbol);

        var tradeType = direction == "Long" ? TradeType.Buy : TradeType.Sell;
        var volumeInUnits = Math.Floor(lots * sym.LotSize / sym.VolumeInUnitsStep) * sym.VolumeInUnitsStep;

        var result = ExecuteMarketOrder(tradeType, symbol, volumeInUnits, "Shamshir", null, null);
        if (result?.IsSuccessful == true)
        {
            var pos = result.Position;
            _positionMap[pos.Id] = Guid.Parse(clientOrderId);
            _ordersExecuted++;

            if (slPrice > 0 || tpPrice > 0)
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

    private object ExecuteClosePosition(JsonElement cmd)
    {
        var positionIdStr = cmd.GetProperty("positionId").GetString()!;
        foreach (var pos in Positions)
        {
            if (_positionMap.TryGetValue(pos.Id, out var orderGuid) && orderGuid.ToString() == positionIdStr)
            {
                var clientOrderId = orderGuid;
                var result = ClosePosition(pos);
                Diag($"CLOSE_POS|{positionIdStr}|success={result?.IsSuccessful}");
                if (result?.IsSuccessful == true) PublishAccount();
                return MakeExecResult(clientOrderId.ToString(), "close", pos.Id,
                    result?.IsSuccessful == true ? "Filled" : "Rejected",
                    pos.CurrentPrice > 0 ? pos.CurrentPrice : 0d, pos.VolumeInUnits / (Symbols.GetSymbol(pos.SymbolName)?.LotSize ?? 100000.0), null);
            }
        }
        Print($"CBOT|CLOSE_NOT_FOUND|positionId={positionIdStr}");
        return MakeExecResult(Guid.Empty.ToString(), "close", 0, "Rejected", 0, 0, "Position not found: " + positionIdStr);
    }

    private static object MakeExecResult(string clientOrderId, string kind, long positionId,
        string state, double fillPrice, double filledLots, string? reason)
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
            simTime = DateTime.UtcNow.ToString("o"),
            grossProfit = 0.0,
            netProfit = 0.0
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
                simTime = Server.TimeInUtc.ToString("o"),
                grossProfit = pos.GrossProfit,
                netProfit = pos.NetProfit
            });
            try { _dealer?.SendFrame(execJson); } catch { }
            _execsSent++;
            Diag($"EXEC_SENT|{clientOrderId}|Filled|kind=close|fill={closePrice:F5}|lots={lots:F4}|pnl={pos.NetProfit:F2}");
            _positionMap.Remove(pos.Id);
            PublishAccount();
        }
    }

    protected override void OnStop()
    {
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
