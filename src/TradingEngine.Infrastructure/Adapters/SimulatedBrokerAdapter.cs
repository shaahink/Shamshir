using System.Threading.Channels;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class SimulatedBrokerAdapter : IBrokerAdapter
{
    private readonly Channel<Tick> _tickChannel = Channel.CreateUnbounded<Tick>();
    private readonly Channel<Bar> _barChannel = Channel.CreateUnbounded<Bar>();
    private readonly Channel<AccountUpdate> _accountChannel = Channel.CreateUnbounded<AccountUpdate>();
    private readonly Channel<ExecutionEvent> _executionChannel = Channel.CreateUnbounded<ExecutionEvent>();

    public ChannelReader<Tick> TickStream => _tickChannel.Reader;
    public ChannelReader<Bar> BarStream => _barChannel.Reader;
    public ChannelReader<AccountUpdate> AccountStream => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _executionChannel.Reader;

    public ChannelWriter<Tick> TickWriter => _tickChannel.Writer;
    public ChannelWriter<Bar> BarWriter => _barChannel.Writer;
    public ChannelWriter<AccountUpdate> AccountWriter => _accountChannel.Writer;
    public ChannelWriter<ExecutionEvent> ExecutionWriter => _executionChannel.Writer;

    public DateTime BrokerTimeUtc { get; private set; } = DateTime.UtcNow;
    public bool IsConnected => true;

    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Func<string, string, decimal> _crossRateProvider;
    private readonly double _slippagePips;
    #pragma warning disable IDE0044
    private decimal _currentBalance;
    #pragma warning restore IDE0044
    private readonly Dictionary<Guid, PendingOrder> _pendingOrders = new();
    private readonly Dictionary<Guid, SimPosition> _openPositions = new();

    public SimulatedBrokerAdapter()
    {
        _symbolRegistry = new SymbolInfoRegistry();
        _crossRateProvider = (_, _) => 1;
        _slippagePips = 0.5;
        _currentBalance = 100_000;
        InitDefaults();
    }

    public SimulatedBrokerAdapter(
        ISymbolInfoRegistry symbolRegistry,
        Func<string, string, decimal> crossRateProvider,
        double slippagePips = 0.5,
        decimal initialBalance = 100_000)
    {
        _symbolRegistry = symbolRegistry;
        _crossRateProvider = crossRateProvider;
        _slippagePips = slippagePips;
        _currentBalance = initialBalance;
    }

    private void InitDefaults()
    {
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
    }

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        lock (_pendingOrders)
        {
            _pendingOrders[Guid.NewGuid()] = new PendingOrder
            {
                Symbol = request.Symbol,
                Direction = request.Direction,
                Lots = request.Lots,
                StopLoss = request.Intent.StopLoss,
                TakeProfit = request.Intent.TakeProfit,
                StrategyId = request.Intent.StrategyId,
            };
        }
        return Task.CompletedTask;
    }

    public Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct)
    {
        lock (_openPositions)
        {
            if (_openPositions.TryGetValue(orderId, out var pos))
            {
                pos.StopLoss = newStopLoss;
                pos.TakeProfit = newTakeProfit;
            }
        }
        return Task.CompletedTask;
    }

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct) => Task.CompletedTask;

    public Task ClosePositionAsync(Guid positionId, CancellationToken ct)
    {
        lock (_openPositions)
        {
            _openPositions.Remove(positionId);
        }
        return Task.CompletedTask;
    }

    public void OnTickReceived(Tick tick)
    {
        BrokerTimeUtc = tick.TimestampUtc;

        lock (_pendingOrders)
        {
            foreach (var (id, order) in _pendingOrders.ToList())
            {
                var pipSize = ResolvePipSize(order.Symbol);
                var fillPrice = order.Direction == TradeDirection.Long
                    ? tick.Ask + (decimal)_slippagePips * pipSize
                    : tick.Bid - (decimal)_slippagePips * pipSize;

                var pos = new SimPosition
                {
                    OrderId = id,
                    Symbol = order.Symbol,
                    Direction = order.Direction,
                    Lots = order.Lots,
                    EntryPrice = new Price(fillPrice),
                    StopLoss = order.StopLoss,
                    TakeProfit = order.TakeProfit,
                    StrategyId = order.StrategyId,
                };

                _openPositions[id] = pos;
                _pendingOrders.Remove(id);

                _executionChannel.Writer.TryWrite(new ExecutionEvent(
                    id, OrderState.Filled, new Price(fillPrice),
                    order.Lots, null, tick.TimestampUtc));
            }
        }

        lock (_openPositions)
        {
            foreach (var (id, pos) in _openPositions.ToList())
            {
                var slHit = pos.Direction == TradeDirection.Long
                    ? tick.Bid <= pos.StopLoss.Value
                    : tick.Ask >= pos.StopLoss.Value;

                var tpHit = pos.TakeProfit.HasValue && (
                    pos.Direction == TradeDirection.Long
                        ? tick.Bid >= pos.TakeProfit.Value.Value
                        : tick.Ask <= pos.TakeProfit.Value.Value);

                if (slHit || tpHit)
                {
                    _openPositions.Remove(id);
                    _executionChannel.Writer.TryWrite(new ExecutionEvent(
                        id, OrderState.Filled,
                        slHit ? pos.StopLoss : pos.TakeProfit!,
                        pos.Lots, null, tick.TimestampUtc));
                }
            }
        }
    }

    private decimal ResolvePipSize(Symbol symbol)
    {
        try { return _symbolRegistry.Get(symbol).PipSize; }
        catch { return 0.0001m; }
    }

    private sealed class PendingOrder
    {
        public Symbol Symbol { get; set; }
        public TradeDirection Direction { get; set; }
        public decimal Lots { get; set; }
        public Price StopLoss { get; set; }
        public Price? TakeProfit { get; set; }
        public string StrategyId { get; set; } = "";
    }

    private sealed class SimPosition
    {
        public Guid OrderId { get; set; }
        public Symbol Symbol { get; set; }
        public TradeDirection Direction { get; set; }
        public decimal Lots { get; set; }
        public Price EntryPrice { get; set; }
        public Price StopLoss { get; set; }
        public Price? TakeProfit { get; set; }
        public string StrategyId { get; set; } = "";
    }
}
