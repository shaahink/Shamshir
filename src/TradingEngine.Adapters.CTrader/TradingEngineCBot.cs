using System;
using System.Threading;
using cAlgo.API;

namespace TradingEngine.Adapters.CTrader;

[Robot(AccessRights = AccessRights.FullAccess)]
public class TradingEngineCBot : Robot
{
    [Parameter("Pipe Name", DefaultValue = "trading-engine")]
    public string PipeName { get; set; } = "trading-engine";

    private readonly Queue<Guid> _pendingClientOrderIds = new();
    private PipeClient? _pipe;
    private TickPublisher? _tickPublisher;
    private BarPublisher? _barPublisher;
    private AccountUpdatePublisher? _accountPublisher;
    private ExecutionEventPublisher? _executionPublisher;
    private OrderCommandHandler? _commandHandler;
    private Thread? _readThread;
    private volatile bool _running;

    private static double VolumeToLots(double volumeInUnits, cAlgo.API.Internals.Symbol? symbol)
    {
        var lotSize = symbol?.LotSize ?? 100_000.0;
        return lotSize > 0 ? volumeInUnits / lotSize : volumeInUnits / 100_000.0;
    }

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
            Print($"PIPE_DIAG|CONNECTED|pipe={PipeName}|pid={System.Diagnostics.Process.GetCurrentProcess().Id}");
            StartReadLoop();
            SendInitialState();
            Print("Connected to Shamshir engine.");
        }
        else
        {
            Print($"PIPE_DIAG|FAILED|pipe={PipeName}|error={_pipe.LastConnectError ?? "timeout"}|pid={System.Diagnostics.Process.GetCurrentProcess().Id}");
            Print("Failed to connect to Shamshir engine. Retrying...");
            _pipe.RetryConnect();
        }

        var bars = MarketData.GetBars(TimeFrame, SymbolName);
        bars.BarClosed += OnBarClosed;
        bars.Tick += OnBarsTick;

        Print($"CBOT|START|symbol={SymbolName}|tf={TimeFrame.ShortName}");
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
            var sym = Symbols.GetSymbol(pos.SymbolName);
            _executionPublisher?.Publish(
                Guid.NewGuid(),
                "Filled",
                pos.EntryPrice,
                VolumeToLots(pos.VolumeInUnits, sym),
                null,
                pos.EntryTime);
        }
    }

    private void OnBarsTick(BarsTickEventArgs args)
    {
        if (!_running) return;
        _tickPublisher?.Publish(SymbolName, Symbol.Bid, Symbol.Ask, Server.TimeInUtc);
        _accountPublisher?.Publish(Account.Balance, Account.Equity,
            Account.Equity - Account.Balance, Server.TimeInUtc);
    }

    private void OnBarClosed(BarClosedEventArgs args)
    {
        if (!_running) return;
        var bars = args.Bars;
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
        var sym = Symbols.GetSymbol(pos.SymbolName);
        _executionPublisher?.Publish(
            clientOrderId, "Filled",
            pos.EntryPrice, VolumeToLots(pos.VolumeInUnits, sym),
            null, pos.EntryTime);
        _accountPublisher?.Publish(Account.Balance, Account.Equity,
            Account.Equity - Account.Balance, DateTime.UtcNow);
    }

    private void OnPositionClosed(PositionClosedEventArgs args)
    {
        if (!_running) return;
        var pos = args.Position;
        var clientOrderId = _pendingClientOrderIds.Count > 0 ? _pendingClientOrderIds.Dequeue() : Guid.NewGuid();
        var sym = Symbols.GetSymbol(pos.SymbolName);
        _executionPublisher?.Publish(
            clientOrderId, "Filled",
            pos.EntryPrice, VolumeToLots(pos.VolumeInUnits, sym),
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
        Print("CBOT|STOP");
    }

    private void OnPipeMessage(PipeMessage message)
    {
        _commandHandler?.Handle(message);
    }

    private void OnPipeDisconnected()
    {
        Print($"PIPE_DIAG|DISCONNECTED|pipe={PipeName}");
        _running = false;
    }
}
