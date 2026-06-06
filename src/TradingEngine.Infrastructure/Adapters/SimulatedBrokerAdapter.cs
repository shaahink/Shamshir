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

    private readonly Dictionary<Guid, PendingOrder> _pendingOrders = new();
    private readonly Dictionary<Guid, SimPosition> _openPositions = new();
    private readonly double _slippagePips = 0.5;

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
                var fillPrice = order.Direction == TradeDirection.Long
                    ? tick.Ask + (decimal)_slippagePips * 0.0001m
                    : tick.Bid - (decimal)_slippagePips * 0.0001m;

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
