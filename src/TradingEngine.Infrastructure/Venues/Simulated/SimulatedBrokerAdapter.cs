using System.Threading.Channels;

namespace TradingEngine.Infrastructure.Venues.Simulated;

public sealed class SimulatedBrokerAdapter : IBrokerAdapter
{
    private readonly Channel<Tick> _tickChannel =
        Channel.CreateBounded<Tick>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<Bar> _barChannel =
        Channel.CreateBounded<Bar>(new BoundedChannelOptions(2_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<AccountUpdate> _accountChannel =
        Channel.CreateBounded<AccountUpdate>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<ExecutionEvent> _executionChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

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
    private decimal _currentBalance;
    private readonly Dictionary<Guid, PendingOrder> _pendingOrders = new();
    private readonly Dictionary<Guid, SimPosition> _openPositions = new();
    private decimal _lastBid;
    private decimal _lastAsk;

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
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("GBPUSD"), SymbolCategory.Forex, "GBP", "USD", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.00012m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("USDJPY"), SymbolCategory.Forex, "USD", "JPY", 0.01m, 0.001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.010m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("USDCHF"), SymbolCategory.Forex, "USD", "CHF", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.00011m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("AUDUSD"), SymbolCategory.Forex, "AUD", "USD", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.00011m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("USDCAD"), SymbolCategory.Forex, "USD", "CAD", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.00013m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("NZDUSD"), SymbolCategory.Forex, "NZD", "USD", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.00014m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("EURGBP"), SymbolCategory.Forex, "EUR", "GBP", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.00013m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("EURJPY"), SymbolCategory.Forex, "EUR", "JPY", 0.01m, 0.001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.012m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("GBPJPY"), SymbolCategory.Forex, "GBP", "JPY", 0.01m, 0.001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.018m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("XAUUSD"), SymbolCategory.Metal, "XAU", "USD", 0.01m, 0.001m, 100, 0.01m, 100m, 0.01m, 0.03333m, 0.30m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("XAGUSD"), SymbolCategory.Metal, "XAG", "USD", 0.001m, 0.0001m, 5_000, 0.01m, 100m, 0.01m, 0.03333m, 0.030m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("BTCUSD"), SymbolCategory.Crypto, "BTC", "USD", 1.0m, 0.1m, 1, 0.001m, 100m, 0.001m, 0.5m, 50.0m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("ETHUSD"), SymbolCategory.Crypto, "ETH", "USD", 0.01m, 0.001m, 1, 0.001m, 100m, 0.001m, 0.5m, 2.0m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("US30"), SymbolCategory.Index, "US30", "USD", 1.0m, 0.1m, 1, 0.1m, 100m, 0.1m, 0.03333m, 3.0m));
        _symbolRegistry.Register(new SymbolInfo(Symbol.Parse("NAS100"), SymbolCategory.Index, "NAS100", "USD", 0.25m, 0.01m, 1, 0.1m, 100m, 0.1m, 0.03333m, 1.0m));
    }

    public Task<AccountState> GetAccountStateAsync(CancellationToken ct)
    {
        var positions = new List<OpenPositionInfo>();
        lock (_openPositions)
        {
            foreach (var (_, pos) in _openPositions)
            {
                positions.Add(new OpenPositionInfo(
                    pos.OrderId, pos.Symbol, pos.Direction, pos.Lots,
                    pos.EntryPrice, pos.StopLoss, pos.TakeProfit));
            }
        }
        return Task.FromResult(new AccountState(_currentBalance, _currentBalance, positions));
    }

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        var orderId = Guid.NewGuid();
        lock (_pendingOrders)
        {
            _pendingOrders[orderId] = new PendingOrder
            {
                Symbol = request.Symbol,
                Direction = request.Direction,
                Lots = request.Lots,
                StopLoss = request.Intent.StopLoss,
                TakeProfit = request.Intent.TakeProfit,
                StrategyId = request.Intent.StrategyId,
            };
        }
        return Task.FromResult(orderId);
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

    public Task ClosePartialPositionAsync(Guid positionId, decimal lots, CancellationToken ct)
    {
        lock (_openPositions)
        {
            if (_openPositions.TryGetValue(positionId, out var pos))
            {
                var remaining = pos.Lots - lots;
                if (remaining <= 0)
                    _openPositions.Remove(positionId);
                else
                    pos.Lots = remaining;

                _executionChannel.Writer.TryWrite(new ExecutionEvent(
                    positionId, OrderState.Filled,
                    new Price(pos.Direction == TradeDirection.Long ? _lastBid : _lastAsk),
                    lots, null, BrokerTimeUtc));
            }
        }
        return Task.CompletedTask;
    }

    public void OnTickReceived(Tick tick)
    {
        BrokerTimeUtc = tick.TimestampUtc;
        _lastBid = tick.Bid;
        _lastAsk = tick.Ask;

        lock (_pendingOrders)
        {
            foreach (var (id, order) in _pendingOrders.ToList())
            {
                var symbolInfo = ResolveSymbolInfo(order.Symbol);
                if (symbolInfo is null) continue;

                var pipSize = symbolInfo.PipSize;
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
                    SymbolInfo = symbolInfo,
                };

                _openPositions[id] = pos;
                _pendingOrders.Remove(id);

                _executionChannel.Writer.TryWrite(new ExecutionEvent(
                    id, OrderState.Filled, new Price(fillPrice),
                    order.Lots, null, tick.TimestampUtc));

                _accountChannel.Writer.TryWrite(new AccountUpdate(
                    _currentBalance, 0m, _currentBalance, tick.TimestampUtc));
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

                    var exitPrice = slHit ? pos.StopLoss.Value : pos.TakeProfit!.Value.Value;
                    var rawPnl = pos.Direction == TradeDirection.Long
                        ? (exitPrice - pos.EntryPrice.Value) * pos.Lots * pos.SymbolInfo.ContractSize
                        : (pos.EntryPrice.Value - exitPrice) * pos.Lots * pos.SymbolInfo.ContractSize;

                    var pnlUsd = pos.SymbolInfo.QuoteCurrency == "USD"
                        ? rawPnl
                        : rawPnl * _crossRateProvider(pos.SymbolInfo.QuoteCurrency, "USD");

                    _currentBalance += pnlUsd;

                    _executionChannel.Writer.TryWrite(new ExecutionEvent(
                        id, OrderState.Filled,
                        slHit ? pos.StopLoss : pos.TakeProfit!,
                        pos.Lots, null, tick.TimestampUtc));

                    _accountChannel.Writer.TryWrite(new AccountUpdate(
                        _currentBalance, 0m, _currentBalance, tick.TimestampUtc));
                }
            }
        }
    }

    private SymbolInfo? ResolveSymbolInfo(Symbol symbol)
    {
        try { return _symbolRegistry.Get(symbol); }
        catch { return null; }
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
        public SymbolInfo SymbolInfo { get; set; } = null!;
    }
}
