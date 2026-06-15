using System.Threading.Channels;

namespace TradingEngine.Tests.Simulation.Harness;

public sealed class FakeVenue : IBrokerAdapter
{
    private readonly Channel<Tick> _tickChannel =
        Channel.CreateUnbounded<Tick>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Bar> _barChannel =
        Channel.CreateUnbounded<Bar>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<AccountUpdate> _accountChannel =
        Channel.CreateUnbounded<AccountUpdate>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<ExecutionEvent> _executionChannel =
        Channel.CreateUnbounded<ExecutionEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly List<OrderRequest> _submittedOrders = [];
    private readonly List<(Guid PositionId, DateTime TimestampUtc)> _closeRequests = [];

    private int _barsConsumed;
    private int _barsFed;

    public int BarsConsumed => _barsConsumed;
    public int BarsFed => _barsFed;
    public void IncrementBarsConsumed() => Interlocked.Increment(ref _barsConsumed);
    public void IncrementBarsFed() => Interlocked.Increment(ref _barsFed);
    public IReadOnlyList<OrderRequest> SubmittedOrders => _submittedOrders;
    public IReadOnlyList<(Guid PositionId, DateTime TimestampUtc)> CloseRequests => _closeRequests;

    public ChannelReader<Tick> TickStream => _tickChannel.Reader;
    public ChannelReader<Bar> BarStream => _barChannel.Reader;
    public ChannelReader<AccountUpdate> AccountStream => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _executionChannel.Reader;

    public DateTime BrokerTimeUtc { get; set; } = DateTime.UtcNow;
    public bool IsConnected { get; private set; }

    private Action? _connectedHandler;

    public async Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        var orderId = Guid.NewGuid();
        _submittedOrders.Add(request);
        IncrementBarsConsumed();

        var fill = new ExecutionEvent(
            orderId, OrderState.Filled, request.LimitPrice ?? new Price(0),
            request.Lots, null, BrokerTimeUtc);
        await _executionChannel.Writer.WriteAsync(fill, ct);
        return orderId;
    }

    public async Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct)
        => await Task.CompletedTask;

    public async Task CancelOrderAsync(Guid orderId, CancellationToken ct)
        => await Task.CompletedTask;

    public async Task ClosePositionAsync(Guid positionId, CancellationToken ct)
    {
        _closeRequests.Add((positionId, BrokerTimeUtc));
        var close = new ExecutionEvent(
            positionId, OrderState.Filled, new Price(0), 0, null, BrokerTimeUtc);
        await _executionChannel.Writer.WriteAsync(close, ct);
    }

    public async Task ClosePartialPositionAsync(Guid positionId, decimal lots, CancellationToken ct)
    {
        _closeRequests.Add((positionId, BrokerTimeUtc));
        var close = new ExecutionEvent(
            positionId, OrderState.Filled, new Price(0), lots, null, BrokerTimeUtc);
        await _executionChannel.Writer.WriteAsync(close, ct);
    }

    public async Task<AccountState> GetAccountStateAsync(CancellationToken ct)
        => await Task.FromResult(new AccountState(0, 0, []));

    public async Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        await Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        IsConnected = false;
        await Task.CompletedTask;
    }

    public async Task CompleteBarAsync(long seq, CancellationToken ct)
        => await Task.CompletedTask;

    public void RegisterConnectedHandler(Action handler) => _connectedHandler = handler;
    public void OnTickObserved(Tick tick) { }
    public void OnBarObserved(Bar bar) { }
    public Task CompleteBarAsync(CancellationToken ct) => Task.CompletedTask;

    public void PostBar(Bar bar) => _barChannel.Writer.TryWrite(bar);
    public void PostAccount(AccountUpdate update) => _accountChannel.Writer.TryWrite(update);

    public IReadOnlyList<ExecutionEvent> DrainExecutions()
    {
        var list = new List<ExecutionEvent>();
        while (_executionChannel.Reader.TryRead(out var evt))
            list.Add(evt);
        return list;
    }
}
