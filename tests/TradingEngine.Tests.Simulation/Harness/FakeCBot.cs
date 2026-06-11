using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetMQ;
using NetMQ.Sockets;

namespace TradingEngine.Tests.Simulation.Harness;

public sealed class FakeCBot : IAsyncDisposable
{
    private readonly string _dataEndpoint;
    private readonly string _commandEndpoint;
    private readonly CancellationToken _ct;
    private readonly ConcurrentQueue<string> _log = new();

    private PublisherSocket? _pub;
    private DealerSocket? _dealer;
    private NetMQPoller? _poller;
    private readonly BlockingCollection<JsonObject> _inbox = new();
    private volatile bool _connected;

    public IReadOnlyList<string> Log => _log.ToList();
    public int BarsSent { get; private set; }
    public int CommandsReceived { get; private set; }
    public int OrdersExecuted { get; private set; }
    public int ExecsSent { get; private set; }
    public bool IsConnected => _connected;

    public FakeCBot(int dataPort, int commandPort, CancellationToken ct = default)
    {
        _dataEndpoint = $"tcp://127.0.0.1:{dataPort}";
        _commandEndpoint = $"tcp://127.0.0.1:{commandPort}";
        _ct = ct;
    }

    public Task ConnectAsync()
    {
        _pub = new PublisherSocket();
        _pub.Bind(_dataEndpoint);

        _dealer = new DealerSocket();
        _dealer.Connect(_commandEndpoint);
        _dealer.ReceiveReady += OnDealerReceive;

        _poller = new NetMQPoller { _dealer };
        _poller.RunAsync();

        LogMsg($"PUB_BOUND|{_dataEndpoint}");
        LogMsg($"DEALER_CONNECTED|{_commandEndpoint}");

        return Task.CompletedTask;
    }

    public async Task HandshakeAsync(IReadOnlyList<string> symbols, IReadOnlyList<string> periods, int barsLoaded = 0)
    {
        var hello = Serialize("hello", new Dictionary<string, object>
        {
            ["v"] = 1,
            ["symbols"] = symbols,
            ["periods"] = periods,
            ["barsLoaded"] = barsLoaded
        });
        Dealer.SendFrame(hello);
        LogMsg($"HELLO_SENT|symbols={string.Join(",", symbols)}|periods={string.Join(",", periods)}");

        for (int retry = 0; retry < 50 && !_connected; retry++)
        {
            if (retry > 0 && retry % 10 == 0)
            {
                Dealer.SendFrame(hello);
                LogMsg($"HELLO_RESEND|retry={retry}");
            }
            await Task.Delay(100, _ct);
        }

        if (!_connected)
            throw new TimeoutException("Handshake failed: no hello_ack received within timeout");
    }

    public async Task<List<JsonObject>> SendBarAndWaitAsync(
        string symbol, string period, DateTime openTime,
        double open, double high, double low, double close, double volume,
        DateTime simTime, double balance, double equity, int seq)
    {
        var bar = Serialize("bar", new Dictionary<string, object>
        {
            ["v"] = 1, ["seq"] = seq, ["symbol"] = symbol, ["period"] = period,
            ["openTime"] = openTime.ToString("o"), ["open"] = open, ["high"] = high,
            ["low"] = low, ["close"] = close, ["volume"] = volume,
            ["simTime"] = simTime.ToString("o"),
            ["account"] = new Dictionary<string, double> { ["balance"] = balance, ["equity"] = equity }
        });
        Dealer.SendFrame(bar);
        BarsSent++;
        LogMsg($"BAR_SENT|seq={seq}|{symbol}|{period}|close={close:F5}");

        var commands = new List<JsonObject>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_inbox.TryTake(out var msg, 100, _ct))
            {
                var type = msg["type"]?.GetValue<string>();
                if (type == "bar_done" && msg["seq"]?.GetValue<int>() == seq)
                {
                    if (msg["commands"] is JsonArray arr)
                    {
                        foreach (var cmd in arr)
                        {
                            if (cmd is not null)
                                commands.Add(cmd.AsObject());
                        }
                    }
                    LogMsg($"BAR_DONE|seq={seq}|commands={commands.Count}");
                    return commands;
                }
                else if (type == "shutdown")
                {
                    LogMsg($"SHUTDOWN_RECV|seq={seq}");
                    return commands;
                }
            }
        }

        throw new TimeoutException($"bar_done not received for seq={seq} within 30s");
    }

    public void SendBarResult(int seq, IReadOnlyList<ExecResult> execs, double balance, double equity)
    {
        var execArray = new JsonArray();
        foreach (var e in execs)
        {
            execArray.Add(new JsonObject
            {
                ["clientOrderId"] = e.ClientOrderId,
                ["kind"] = e.Kind,
                ["positionId"] = e.PositionId,
                ["state"] = e.State,
                ["fillPrice"] = e.FillPrice,
                ["filledLots"] = e.FilledLots,
                ["reason"] = e.Reason,
                ["simTime"] = e.SimTime.ToString("o"),
                ["grossProfit"] = e.GrossProfit,
                ["netProfit"] = e.NetProfit
            });
        }

        var result = Serialize("bar_result", new Dictionary<string, object>
        {
            ["v"] = 1, ["seq"] = seq,
            ["execs"] = execArray,
            ["account"] = new Dictionary<string, double> { ["balance"] = balance, ["equity"] = equity }
        });
        Dealer.SendFrame(result);
        OrdersExecuted += execs.Count(e => e.State == "Filled" && e.Kind == "entry_fill");
        ExecsSent += execs.Count;
        LogMsg($"BAR_RESULT|seq={seq}|execs={execs.Count}|filled={execs.Count(e => e.State == "Filled")}");
    }

    public void SendAsyncExec(ExecResult exec)
    {
        var msg = Serialize("exec", new Dictionary<string, object>
        {
            ["v"] = 1,
            ["clientOrderId"] = exec.ClientOrderId,
            ["kind"] = exec.Kind,
            ["positionId"] = exec.PositionId,
            ["state"] = exec.State,
            ["fillPrice"] = exec.FillPrice,
            ["filledLots"] = exec.FilledLots,
            ["reason"] = exec.Reason!,
            ["simTime"] = exec.SimTime.ToString("o"),
            ["grossProfit"] = exec.GrossProfit,
            ["netProfit"] = exec.NetProfit
        });
        Dealer.SendFrame(msg);
        ExecsSent++;
        LogMsg($"EXEC_SENT|{exec.ClientOrderId}|{exec.State}|kind={exec.Kind}");
    }

    public string SendStatsAndGetJson()
    {
        var stats = Serialize("stats", new Dictionary<string, object>
        {
            ["v"] = 1,
            ["barsSent"] = BarsSent,
            ["cmdsReceived"] = CommandsReceived,
            ["ordersExecuted"] = OrdersExecuted,
            ["execsSent"] = ExecsSent
        });
        Dealer.SendFrame(stats);
        LogMsg($"STATS_SENT|bars={BarsSent}|cmds={CommandsReceived}|orders={OrdersExecuted}|execs={ExecsSent}");
        return stats;
    }

    public async Task StopAsync()
    {
        _inbox.CompleteAdding();
        if (_dealer is not null) _dealer.Options.Linger = TimeSpan.FromSeconds(2);
        _poller?.Stop();
        _poller?.Dispose();
        _dealer?.Dispose();
        _pub?.Dispose();
        try { NetMQConfig.Cleanup(true); } catch { NetMQConfig.Cleanup(false); }
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void OnDealerReceive(object? sender, NetMQSocketEventArgs e)
    {
        while (e.Socket.TryReceiveFrameString(out var json))
        {
            if (json is null) continue;
            try
            {
                var doc = JsonNode.Parse(json)?.AsObject();
                if (doc is null) continue;
                var type = doc["type"]?.GetValue<string>();

                if (type == "hello_ack")
                {
                    _connected = true;
                    LogMsg("HELLO_ACK|received");
                    continue;
                }

                if (type == "bar_done" || type == "submit_order" || type == "close_position")
                    CommandsReceived++;

                _inbox.Add(doc, _ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private DealerSocket Dealer => _dealer ?? throw new ObjectDisposedException(nameof(FakeCBot));

    private static string Serialize(string type, object payload)
    {
        var dict = new Dictionary<string, object> { ["type"] = type };
        if (payload is Dictionary<string, object> d)
        {
            foreach (var kv in d) dict[kv.Key] = kv.Value;
        }
        return JsonSerializer.Serialize(dict, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private void LogMsg(string msg) => _log.Enqueue($"[CBOT] {msg}");

    public sealed record ExecResult(
        string ClientOrderId,
        string Kind,
        long PositionId,
        string State,
        double FillPrice,
        double FilledLots,
        string? Reason,
        DateTime SimTime,
        double GrossProfit,
        double NetProfit);
}
