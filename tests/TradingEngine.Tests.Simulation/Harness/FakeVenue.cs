using System.Threading.Channels;
using TradingEngine.Services.Helpers;

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

    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Func<string, string, decimal> _crossRate;

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
    public decimal CurrentMarketPrice { get; set; }

    private Action? _connectedHandler;

    private readonly Dictionary<Guid, OrderEntry> _orderEntries = [];
    private readonly Dictionary<Guid, Price> _exitPrices = [];

    private sealed record OrderEntry(Price EntryPrice, decimal Lots, TradeDirection Direction, Symbol Symbol);

    public FakeVenue(ISymbolInfoRegistry symbolRegistry, Func<string, string, decimal> crossRate)
    {
        _symbolRegistry = symbolRegistry;
        _crossRate = crossRate;
    }

    public async Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        var orderId = Guid.NewGuid();
        _submittedOrders.Add(request);
        IncrementBarsConsumed();

        var fillPrice = request.LimitPrice ?? new Price(CurrentMarketPrice);
        _orderEntries[orderId] = new OrderEntry(fillPrice, request.Lots, request.Intent.Direction, request.Intent.Symbol);

        var fill = new ExecutionEvent(
            orderId, OrderState.Filled, fillPrice,
            request.Lots, null, BrokerTimeUtc);
        await _executionChannel.Writer.WriteAsync(fill, ct);
        return orderId;
    }

    public async Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct)
        => await Task.CompletedTask;

    public async Task CancelOrderAsync(Guid orderId, CancellationToken ct)
        => await Task.CompletedTask;

    public void SetExitPrice(Guid positionId, Price exitPrice)
    {
        _exitPrices[positionId] = exitPrice;
    }

    public async Task ClosePositionAsync(Guid positionId, CancellationToken ct)
    {
        _closeRequests.Add((positionId, BrokerTimeUtc));
        await WriteCloseFill(positionId, ct);
    }

    public async Task ClosePartialPositionAsync(Guid positionId, decimal lots, CancellationToken ct)
    {
        _closeRequests.Add((positionId, BrokerTimeUtc));
        await WriteCloseFill(positionId, ct, lots);
    }

    private async Task WriteCloseFill(Guid positionId, CancellationToken ct, decimal? partialLots = null)
    {
        var exitPrice = _exitPrices.TryGetValue(positionId, out var ep) ? ep : new Price(CurrentMarketPrice);
        _exitPrices.Remove(positionId);

        decimal? grossProfit = null;
        decimal? netProfit = null;
        var fillLots = 0m;

        if (_orderEntries.TryGetValue(positionId, out var entry))
        {
            fillLots = partialLots ?? entry.Lots;
            var symbolInfo = _symbolRegistry.Get(entry.Symbol);
            var gross = PipCalculator.GrossPnL(entry.Direction, entry.EntryPrice, exitPrice, fillLots, symbolInfo, _crossRate);
            grossProfit = gross.Amount;
            netProfit = gross.Amount;
            _orderEntries.Remove(positionId);
        }

        var close = new ExecutionEvent(
            positionId, OrderState.Filled, exitPrice, fillLots, null, BrokerTimeUtc)
        {
            GrossProfit = grossProfit,
            NetProfit = netProfit,
            Commission = 0m,
            Swap = 0m,
        };
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
