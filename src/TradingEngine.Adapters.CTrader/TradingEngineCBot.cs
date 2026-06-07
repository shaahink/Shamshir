using System;
using System.Collections.Concurrent;
using System.Text.Json;
using cAlgo.API;
using NetMQ;
using NetMQ.Sockets;

namespace TradingEngine.Adapters.CTrader;

[Robot(AccessRights = AccessRights.FullAccess)]
public class TradingEngineCBot : Robot
{
    [Parameter("Data Port", DefaultValue = "15555")]
    public int DataPort { get; set; } = 15555;

    [Parameter("Command Port", DefaultValue = "15556")]
    public int CommandPort { get; set; } = 15556;

    [Parameter("Tick Every N", DefaultValue = "10")]
    public int TickEveryN { get; set; } = 10;

    private PublisherSocket? _pub;
    private DealerSocket? _dealer;
    private NetMQPoller? _poller;
    private readonly ConcurrentQueue<Action> _mainActions = new();
    private int _tickCounter;

    private static readonly JsonSerializerOptions JsonOpts = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    protected override void OnStart()
    {
        Print($"CBOT|START|symbol={SymbolName}|tf={TimeFrame.ShortName}|dataPort={DataPort}|cmdPort={CommandPort}");

        _pub = new PublisherSocket();
        _pub.Bind($"tcp://*:{DataPort}");

        _dealer = new DealerSocket();
        _dealer.Connect($"tcp://127.0.0.1:{CommandPort}");
        _dealer.ReceiveReady += OnDealerReceive;

        _poller = new NetMQPoller { _dealer };
        _poller.RunAsync();

        System.Threading.Thread.Sleep(600);

        _dealer.SendFrame(Serialize("hello", new { }));

        var bars = MarketData.GetBars(TimeFrame, SymbolName);
        bars.BarClosed += OnBarClosed;

        PublishAccount();

        Print($"CBOT|READY|dataPort={DataPort}|cmdPort={CommandPort}");
    }

    protected override void OnTick()
    {
        _tickCounter++;

        while (_mainActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Print($"CBOT|CMD_ERR|{ex.Message}"); }
        }

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
        var bars = args.Bars;
        var bar = bars.Last(1);
        if (bar.Open == 0 && bar.High == 0) return;

        Publish("bar", new
        {
            symbol = bars.SymbolName,
            period = TimeFrame.ShortName,
            openTime = bar.OpenTime.ToString("o"),
            open = bar.Open,
            high = bar.High,
            low = bar.Low,
            close = bar.Close,
            volume = (long)bar.TickVolume
        });

        Print($"CBOT|BAR|{bars.SymbolName}|{TimeFrame.ShortName}|{bar.OpenTime:yyyy-MM-dd HH:mm}|close={bar.Close:F5}");
    }

    private void OnDealerReceive(object? sender, NetMQSocketEventArgs e)
    {
        if (!e.Socket.TryReceiveFrameString(out var json) || json is null) return;
        var captured = json;
        _mainActions.Enqueue(() => HandleCommand(captured));
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
                case "submit_order":   HandleSubmitOrder(doc.RootElement);   break;
                case "close_position": HandleClosePosition(doc.RootElement); break;
                case "modify_order":   HandleModifyOrder(doc.RootElement);   break;
                case "cancel_order":   HandleCancelOrder(doc.RootElement);   break;
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
        var symbol        = cmd.GetProperty("symbol").GetString()!;
        var direction     = cmd.GetProperty("direction").GetString()!;
        var lots          = cmd.GetProperty("lots").GetDouble();
        var slPrice       = cmd.GetProperty("slPrice").GetDouble();
        var tpPrice       = cmd.GetProperty("tpPrice").GetDouble();

        var sym = Symbols.GetSymbol(symbol);
        if (sym is null)
        {
            PublishExec(clientOrderId, "Rejected", 0, 0, "Unknown symbol: " + symbol);
            return;
        }

        var tradeType = direction == "Long" ? TradeType.Buy : TradeType.Sell;
        var volumeInUnits = Math.Floor(lots * sym.LotSize / sym.VolumeInUnitsStep) * sym.VolumeInUnitsStep;
        var slPips = slPrice > 0 ? (double?)Math.Abs(slPrice - (sym.Bid + sym.Ask) / 2.0) / sym.PipSize : null;
        var tpPips = tpPrice > 0 ? (double?)Math.Abs(tpPrice - (sym.Bid + sym.Ask) / 2.0) / sym.PipSize : null;

        var result = ExecuteMarketOrder(tradeType, symbol, volumeInUnits, "Shamshir", slPips, tpPips);
        if (result?.IsSuccessful == true)
        {
            var pos = result.Position;
            PublishExec(clientOrderId, "Filled", pos.EntryPrice, pos.VolumeInUnits / sym.LotSize, null);
            PublishAccount();
        }
        else
        {
            PublishExec(clientOrderId, "Rejected", 0, 0, result?.Error.ToString() ?? "Null result");
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
        var newSl   = cmd.GetProperty("newSl").GetDouble();
        var newTp   = cmd.GetProperty("newTp").GetDouble();
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

    protected override void OnStop()
    {
        Print($"CBOT|STOP|ticks={_tickCounter}");
        _poller?.StopAsync();
        _dealer?.Dispose();
        _pub?.Dispose();
        _poller?.Dispose();
        NetMQConfig.Cleanup(false);
    }

    private void Publish(string topic, object payload)
    {
        if (_pub is null) return;
        var json = Serialize(topic, payload);
        _pub.SendMoreFrame(topic).SendFrame(json);
    }

    private void PublishAccount()
    {
        Publish("acct", new
        {
            balance    = Account.Balance,
            equity     = Account.Equity,
            floatingPnL = Account.Equity - Account.Balance,
            time       = Server.TimeInUtc.ToString("o")
        });
    }

    private void PublishExec(Guid clientOrderId, string state, double fillPrice, double filledLots, string? reason)
    {
        Publish("exec", new
        {
            clientOrderId = clientOrderId.ToString(),
            state,
            fillPrice,
            filledLots,
            reason,
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
