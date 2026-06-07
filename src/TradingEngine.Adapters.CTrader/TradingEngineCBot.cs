using System;
using System.Threading;
using cAlgo.API;

namespace TradingEngine.Adapters.CTrader;

[Robot(AccessRights = AccessRights.None)]
public class TradingEngineCBot : Robot
{
    [Parameter("Pipe Name", DefaultValue = "trading-engine")]
    public string PipeName { get; set; } = "trading-engine";

    [Parameter("Transport", DefaultValue = "pipe")]
    public string Transport { get; set; } = "pipe";

    private readonly Queue<Guid> _pendingClientOrderIds = new();
    private PipeClient? _pipe;
    private TickPublisher? _tickPublisher;
    private BarPublisher? _barPublisher;
    private AccountUpdatePublisher? _accountPublisher;
    private ExecutionEventPublisher? _executionPublisher;
    private OrderCommandHandler? _commandHandler;
    private Thread? _readThread;
    private volatile bool _running;

    protected override void OnStart()
    {
        _pipe = new PipeClient(PipeName);
        _pipe.OnMessageReceived += OnPipeMessage;
        _pipe.OnDisconnected += OnPipeDisconnected;
        _pipe.OnReconnected += OnReconnected;

        _tickPublisher = new TickPublisher(_pipe);
        _barPublisher = new BarPublisher(_pipe);
        _accountPublisher = new AccountUpdatePublisher(_pipe);
        _executionPublisher = new ExecutionEventPublisher(_pipe);
        _commandHandler = new OrderCommandHandler(_pipe, this, _executionPublisher, _accountPublisher, _pendingClientOrderIds);

        Positions.Opened += OnPositionOpened;
        Positions.Closed += OnPositionClosed;

        Print("Shamshir engine cBot starting. Connecting to pipe...");

        if (_pipe.Connect(5000))
        {
            StartReadLoop();
            SendInitialState();
            Print("Connected to Shamshir engine.");
        }
        else
        {
            Print("Failed to connect to Shamshir engine. Retrying...");
            _pipe.RetryConnect();
        }
    }

    private void StartReadLoop()
    {
        _running = true;
        _readThread = new Thread(() =>
        {
            while (_running)
            {
                if (_pipe is null) break;
                if (!_pipe.ReadMessage()) break;
            }
        })
        { IsBackground = true };
        _readThread.Start();
    }

    private void SendInitialState()
    {
        _accountPublisher?.Publish(Account.Balance, Account.Equity, Account.Equity - Account.Balance, Server.TimeInUtc);

        foreach (var pos in Positions)
        {
            _executionPublisher?.Publish(
                Guid.Parse(pos.Id.ToString()),
                "Filled",
                pos.EntryPrice,
                pos.VolumeInUnits / 100000.0,
                null,
                pos.EntryTime);
        }
    }

    protected override void OnTick()
    {
        if (!_running) return;

        _tickPublisher?.Publish(SymbolName, Symbol.Bid, Symbol.Ask, Server.TimeInUtc);

        _accountPublisher?.Publish(
            Account.Balance,
            Account.Equity,
            Account.Equity - Account.Balance,
            Server.TimeInUtc);
    }

    protected override void OnBar()
    {
        if (!_running) return;

        var bars = MarketData.GetBars(TimeFrame, SymbolName);
        if (bars is null || bars.Count == 0) return;

        var last = bars.Last(1);
        _barPublisher?.Publish(
            SymbolName,
            TimeFrame.ShortName,
            last.OpenTime,
            last.Open,
            last.High,
            last.Low,
            last.Close,
            last.TickVolume);
    }

    private void OnPositionOpened(PositionOpenedEventArgs args)
    {
        if (!_running) return;
        var pos = args.Position;
        var clientOrderId = _pendingClientOrderIds.Count > 0 ? _pendingClientOrderIds.Dequeue() : Guid.NewGuid();
        _executionPublisher?.Publish(
            clientOrderId, "Filled",
            pos.EntryPrice, pos.VolumeInUnits / 100000.0,
            null, pos.EntryTime);
        _accountPublisher?.Publish(Account.Balance, Account.Equity,
            Account.Equity - Account.Balance, DateTime.UtcNow);
    }

    private void OnPositionClosed(PositionClosedEventArgs args)
    {
        if (!_running) return;
        var pos = args.Position;
        _executionPublisher?.Publish(
            Guid.Parse(pos.Id.ToString()), "Filled",
            pos.EntryPrice, pos.VolumeInUnits / 100000.0,
            null, Server.TimeInUtc);
        _accountPublisher?.Publish(Account.Balance, Account.Equity,
            Account.Equity - Account.Balance, DateTime.UtcNow);
    }

    private void OnReconnected()
    {
        Print("Reconnected to Shamshir engine.");
        StartReadLoop();
        SendInitialState();
    }

    protected override void OnStop()
    {
        _running = false;
        _pipe?.Disconnect();
    }

    private void OnPipeMessage(PipeMessage message)
    {
        _commandHandler?.Handle(message);
    }

    private void OnPipeDisconnected()
    {
        Print("Disconnected from Shamshir engine.");
        _running = false;
    }
}
