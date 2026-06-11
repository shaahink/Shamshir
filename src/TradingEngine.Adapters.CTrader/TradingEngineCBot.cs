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
    private readonly ConcurrentQueue<Action> _mainActions = new();
    private int _tickCounter;
    private int _barEventCount;
    private int _duplicateCount;
    private readonly HashSet<(string symbol, string tf, DateTime openTime)> _publishedBars = new();
    private readonly List<Bars> _subscriptions = new();
    private readonly Dictionary<long, Guid> _positionMap = new();

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

        // PUB/SUB slow-joiner: give engine subscriber time to complete handshake
        System.Threading.Thread.Sleep(600);

        // Heartbeat: send periodic diag via PUB to prove data channel works
        for (int i = 1; i <= 10; i++)
        {
            System.Threading.Thread.Sleep(500);
            Diag($"HEARTBEAT|{i}");
        }
        Print($"CBOT|HEARTBEATS_DONE");

        _dealer = new DealerSocket();
        _dealer.Connect($"tcp://127.0.0.1:{CommandPort}");
        _dealer.ReceiveReady += OnDealerReceive;

        _poller = new NetMQPoller { _dealer };
        _poller.RunAsync();

        _dealer.SendFrame(Serialize("hello", new { }));
        Print($"CBOT|HELLO_SENT");

        SubscribeAll();
        Print($"CBOT|SUBSCRIBED|subs={_subscriptions.Count}");

        Positions.Closed += OnPositionClosed;

        PublishAccount();

        Print($"CBOT|READY|dataPort={DataPort}|cmdPort={CommandPort}");
    }

    protected override void OnTick()
    {
        _tickCounter++;

        var queued = _mainActions.Count;
        var processed = 0;
        while (_mainActions.TryDequeue(out var action))
        {
            try { action(); processed++; }
            catch (Exception ex) { Print($"CBOT|CMD_ERR|{ex.Message}"); }
        }
        if (queued > 0)
            Diag($"TICK_DRAIN|tick={_tickCounter}|queued={queued}|processed={processed}");

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

        var hat2Open = bars.Count > 2 ? bars.Last(2).OpenTime : DateTime.MinValue;

        Print($"CBOT|BAR_EVENT|seq={_barEventCount}|" +
              $"count={bars.Count}|" +
              $"openTime={bar.OpenTime:yyyy-MM-dd HH:mm}|" +
              $"open={bar.Open:F5}|high={bar.High:F5}|low={bar.Low:F5}|close={bar.Close:F5}|" +
              $"hat2OpenTime={hat2Open:yyyy-MM-dd HH:mm}|" +
              $"firstIdx0={bars.Last(0).OpenTime:yyyy-MM-dd HH:mm}|" +
              $"dup=false");

        var openTimeUtc = DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc).ToString("o");

        Diag($"BAR_SENT|{bars.SymbolName}|{bars.TimeFrame.ShortName}|{openTimeUtc}|close={bar.Close:F5}|seq={_barEventCount}");

        Publish("bar", new
        {
            symbol = bars.SymbolName,
            period = bars.TimeFrame.ShortName,
            openTime = openTimeUtc,
            open = bar.Open,
            high = bar.High,
            low = bar.Low,
            close = bar.Close,
            volume = (long)bar.TickVolume
        });
    }

    private void OnDealerReceive(object? sender, NetMQSocketEventArgs e)
    {
        if (!e.Socket.TryReceiveFrameString(out var json) || json is null) return;
        var captured = json;
        _mainActions.Enqueue(() => HandleCommand(captured));
        Diag($"DEALER_RECV|queueDepth={_mainActions.Count}|jsonLen={captured.Length}");
    }

    private void HandleCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();
            Print($"CBOT|CMD|{type}|{json[..Math.Min(120, json.Length)]}");

            switch (type)
            {
                case "ping": break;
                case "shutdown":
                    Print($"CBOT|SHUTDOWN|received shutdown command, stopping");
                    Stop();
                    break;
                case "submit_order": HandleSubmitOrder(doc.RootElement); break;
                case "close_position": HandleClosePosition(doc.RootElement); break;
                case "modify_order": HandleModifyOrder(doc.RootElement); break;
                case "cancel_order": HandleCancelOrder(doc.RootElement); break;
            }
        }
        catch (Exception ex)
        {
            Print($"CBOT|CMD_PARSE_ERR|{ex.Message}");
        }
    }

    private void HandleSubmitOrder(JsonElement cmd)
    {
        var clientOrderId = cmd.GetProperty("clientOrderId").GetGuid();
        var symbol = cmd.GetProperty("symbol").GetString()!;
        var direction = cmd.GetProperty("direction").GetString()!;
        var lots = cmd.GetProperty("lots").GetDouble();
        var slPrice = cmd.GetProperty("slPrice").GetDouble();
        var tpPrice = cmd.GetProperty("tpPrice").GetDouble();

        Diag($"CMD_RECV|submit_order|{clientOrderId}|{symbol}|{direction}|lots={lots:F4}");

        var sym = Symbols.GetSymbol(symbol);
        if (sym is null)
        {
            PublishExec(clientOrderId, "Rejected", 0, 0, "Unknown symbol: " + symbol);
            Diag($"EXEC_SENT|{clientOrderId}|Rejected|reason=Unknown symbol");
            return;
        }

        var tradeType = direction == "Long" ? TradeType.Buy : TradeType.Sell;
        var volumeInUnits = Math.Floor(lots * sym.LotSize / sym.VolumeInUnitsStep) * sym.VolumeInUnitsStep;
        var midPrice = (sym.Bid + sym.Ask) / 2.0;
        double? slPips = null;
        double? tpPips = null;
        if (slPrice > 0 && midPrice > 0)
        {
            var rawSl = Math.Abs(slPrice - midPrice) / sym.PipSize;
            slPips = rawSl < 500 ? (double?)rawSl : null; // clamp absurd values (M1 mode has bid=ask=0)
        }
        if (tpPrice > 0 && midPrice > 0)
        {
            var rawTp = Math.Abs(tpPrice - midPrice) / sym.PipSize;
            tpPips = rawTp < 500 ? (double?)rawTp : null;
        }

        var result = ExecuteMarketOrder(tradeType, symbol, volumeInUnits, "Shamshir", slPips, tpPips);
        if (result?.IsSuccessful == true)
        {
            var pos = result.Position;
            _positionMap[pos.Id] = clientOrderId;
            PublishExec(clientOrderId, "Filled", pos.EntryPrice, pos.VolumeInUnits / sym.LotSize, null);
            Diag($"EXEC_SENT|{clientOrderId}|Filled|fill={pos.EntryPrice:F5}|lots={pos.VolumeInUnits / sym.LotSize:F4}");
            PublishAccount();
        }
        else
        {
            PublishExec(clientOrderId, "Rejected", 0, 0, result?.Error.ToString() ?? "Null result");
            Diag($"EXEC_SENT|{clientOrderId}|Rejected|reason={result?.Error}");
        }
    }

    private void HandleClosePosition(JsonElement cmd)
    {
        var positionId = cmd.GetProperty("positionId").GetString();
        foreach (var pos in Positions)
        {
            if (pos.Id.ToString() == positionId)
            {
                var result = ClosePosition(pos);
                if (result?.IsSuccessful == true) PublishAccount();
                return;
            }
        }
        Print($"CBOT|CLOSE_NOT_FOUND|positionId={positionId}");
    }

    private void HandleModifyOrder(JsonElement cmd)
    {
        var orderId = cmd.GetProperty("orderId").GetString();
        var newSl = cmd.GetProperty("newSl").GetDouble();
        var newTp = cmd.GetProperty("newTp").GetDouble();
        foreach (var pos in Positions)
        {
            if (pos.Id.ToString() == orderId)
            {
#pragma warning disable CS0618
                ModifyPosition(pos, newSl > 0 ? newSl : pos.StopLoss, newTp > 0 ? newTp : pos.TakeProfit);
#pragma warning restore CS0618
                return;
            }
        }
    }

    private void HandleCancelOrder(JsonElement cmd)
    {
        var orderId = cmd.GetProperty("orderId").GetString();
        foreach (var order in PendingOrders)
        {
            if (order.Id.ToString() == orderId)
            {
                CancelPendingOrder(order);
                return;
            }
        }
    }

    private void OnPositionClosed(PositionClosedEventArgs args)
    {
        var pos = args.Position;
        if (_positionMap.TryGetValue(pos.Id, out var clientOrderId))
        {
            var sym = Symbols.GetSymbol(pos.SymbolName);
            var lots = sym is not null ? pos.VolumeInUnits / sym.LotSize : pos.VolumeInUnits / 100_000.0;
            var exitPrice = pos.EntryPrice + (pos.TradeType == TradeType.Buy ? 1 : -1)
                * (pos.GrossProfit / (pos.VolumeInUnits > 0 ? pos.VolumeInUnits : 1));

            PublishExec(clientOrderId, "Filled", exitPrice, lots, null);
            Diag($"EXEC_SENT|{clientOrderId}|Filled|fill={exitPrice:F5}|lots={lots:F4}|reason=PositionClosed");
            PublishAccount();
            _positionMap.Remove(pos.Id);
        }
    }

    protected override void OnStop()
    {
        Diag($"STOP|ticks={_tickCounter}|barEvents={_barEventCount}|dup={_duplicateCount}");
        Print($"CBOT|STOP|ticks={_tickCounter}|barEvents={_barEventCount}|dup={_duplicateCount}");
        _poller?.StopAsync();
        _dealer?.Dispose();
        _pub?.Dispose();
        _poller?.Dispose();
        NetMQConfig.Cleanup(false);
    }

    private void SubscribeAll()
    {
        var symbols = SymbolString.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var periods = Periods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var sym in symbols)
            foreach (var period in periods)
            {
                var tf = ParseTimeFrame(period);
                var bars = MarketData.GetBars(tf, sym);
                bars.BarClosed += OnBarClosed;
                _subscriptions.Add(bars);
                Diag($"SUBSCRIBED|{sym}|{period}|loaded={bars.Count}");
            }
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

    private void PublishExec(Guid clientOrderId, string state, double fillPrice, double filledLots, string? reason)
    {
        if (_pub is null) return;
        var json = JsonSerializer.Serialize(new
        {
            clientOrderId = clientOrderId.ToString(),
            state,
            fillPrice,
            filledLots,
            reason,
            time = Server.TimeInUtc.ToString("o")
        }, JsonOpts);
        _pub.SendMoreFrame("exec").SendFrame(json);
        Diag($"EXEC_SENT|{clientOrderId}|{state}|fill={fillPrice:F5}|lots={filledLots:F4}");
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
