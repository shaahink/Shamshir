using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingEngine.Services.Helpers;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class BacktestReplayAdapter : IBrokerAdapter, IAsyncDisposable
{
    private readonly IBarRepository _barRepo;
    private readonly Symbol _symbol;
    private readonly Timeframe _timeframe;
    private readonly DateTime _from;
    private readonly DateTime _to;
    private readonly decimal _initialBalance;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Func<string, string, decimal> _crossRateProvider;
    private readonly ILogger<BacktestReplayAdapter> _logger;

    private readonly Channel<Tick> _tickChannel =
        Channel.CreateUnbounded<Tick>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Channel<Bar> _barChannel =
        Channel.CreateUnbounded<Bar>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Channel<AccountUpdate> _accountChannel =
        Channel.CreateBounded<AccountUpdate>(new BoundedChannelOptions(500)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<ExecutionEvent> _executionChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1_000)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

    private readonly Dictionary<Guid, (TradeDirection Direction, decimal EntryPrice, decimal Lots)> _openTrades = new();
    private decimal _balance;
    private decimal _lastClose;
    private Task _feedTask = Task.CompletedTask;
    private CancellationTokenSource? _feedCts;

    public bool IsConnected { get; private set; }
    public ChannelReader<Tick> TickStream => _tickChannel.Reader;
    public ChannelReader<Bar> BarStream => _barChannel.Reader;
    public ChannelReader<AccountUpdate> AccountStream => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _executionChannel.Reader;
    public int BarCount { get; private set; }
    public DateTime BrokerTimeUtc { get; private set; }

    public BacktestReplayAdapter(
        IBarRepository barRepo,
        Symbol symbol,
        Timeframe timeframe,
        DateTime from,
        DateTime to,
        decimal initialBalance,
        ISymbolInfoRegistry symbolRegistry,
        Func<string, string, decimal> crossRateProvider,
        ILogger<BacktestReplayAdapter> logger)
    {
        _barRepo = barRepo;
        _symbol = symbol;
        _timeframe = timeframe;
        _from = from;
        _to = to;
        _initialBalance = initialBalance;
        _balance = initialBalance;
        _symbolRegistry = symbolRegistry;
        _crossRateProvider = crossRateProvider;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        _feedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await _accountChannel.Writer.WriteAsync(
            new AccountUpdate(_initialBalance, _initialBalance, 0, _from), ct);

        _feedTask = FeedBarsAsync(_feedCts.Token);
    }

    private async Task FeedBarsAsync(CancellationToken ct)
    {
        try
        {
            var bars = await _barRepo.GetAsync(_symbol, _timeframe, _from, _to, ct);
            BarCount = bars.Count;
            _logger.LogInformation("BacktestReplay: loaded {Count} bars for {Symbol} {Tf}",
                bars.Count, _symbol, _timeframe);

            if (bars.Count == 0)
            {
                _logger.LogWarning("BacktestReplay: no bars found for {Symbol} {Tf} in [{From}–{To}]. Check seed-bars.ps1.",
                    _symbol, _timeframe, _from, _to);
            }

            foreach (var bar in bars)
            {
                ct.ThrowIfCancellationRequested();
                BrokerTimeUtc = bar.OpenTimeUtc;
                _lastClose = bar.Close;

                await _barChannel.Writer.WriteAsync(bar, ct);
                await _tickChannel.Writer.WriteAsync(
                    new Tick(bar.Symbol, bar.Close, bar.Close + 0.0001m, bar.OpenTimeUtc), ct);

                var floatingPnL = ComputeFloatingPnL(bar.Close);
                var equity = _balance + floatingPnL;
                await _accountChannel.Writer.WriteAsync(
                    new AccountUpdate(_balance, equity, floatingPnL, bar.OpenTimeUtc), ct);
            }

            _logger.LogInformation("BacktestReplay: feed complete, {Count} bars sent", bars.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("BacktestReplay: feed cancelled");
        }
        finally
        {
            _barChannel.Writer.TryComplete();
            _tickChannel.Writer.TryComplete();
            _accountChannel.Writer.TryComplete();
        }
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        IsConnected = false;
        _feedCts?.Cancel();
        _barChannel.Writer.TryComplete();
        _tickChannel.Writer.TryComplete();
        _accountChannel.Writer.TryComplete();
        _executionChannel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task<AccountState> GetAccountStateAsync(CancellationToken ct)
        => Task.FromResult(new AccountState(_initialBalance, _initialBalance, []));

    public Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        var orderId = Guid.NewGuid();
        var fillPrice = new Price(_lastClose > 0 ? _lastClose : 1m);
        _executionChannel.Writer.TryWrite(
            new ExecutionEvent(orderId, OrderState.Filled, fillPrice, request.Lots, null, BrokerTimeUtc));
        _openTrades[orderId] = (request.Direction, fillPrice.Value, request.Lots);
        _logger.LogDebug("BacktestReplay: instant fill {Id} at {Price:F5} dir={Dir} lots={Lots}",
            orderId, fillPrice.Value, request.Direction, request.Lots);
        return Task.FromResult(orderId);
    }

    public Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct)
        => Task.CompletedTask;

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct)
        => Task.CompletedTask;

    public void SyncToBar(decimal close, DateTime openTimeUtc)
    {
        _lastClose = close;
        BrokerTimeUtc = openTimeUtc;
    }

    public Task ClosePositionAsync(Guid positionId, CancellationToken ct)
    {
        var fillPrice = new Price(_lastClose > 0 ? _lastClose : 1m);
        _executionChannel.Writer.TryWrite(
            new ExecutionEvent(positionId, OrderState.Filled, fillPrice, 0, null, BrokerTimeUtc));

        if (_openTrades.TryGetValue(positionId, out var trade))
        {
            var symbolInfo = _symbolRegistry.Get(_symbol);
            decimal pnl = 0;
            try
            {
                var priceDiff = trade.Direction == TradeDirection.Long
                    ? fillPrice.Value - trade.EntryPrice
                    : trade.EntryPrice - fillPrice.Value;
                var pipValue = PipCalculator.PipValuePerLot(symbolInfo, fillPrice.Value, _crossRateProvider);
                pnl = (priceDiff / symbolInfo.PipSize) * pipValue * trade.Lots;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute PnL for close {PositionId}", positionId);
            }
            _balance += pnl;
            _openTrades.Remove(positionId);
            _logger.LogDebug("BacktestReplay: close {PositionId} at {Price:F5} PnL={PnL:F2} balance={Balance:F2}",
                positionId, fillPrice.Value, pnl, _balance);
        }

        return Task.CompletedTask;
    }

    public Task ClosePartialPositionAsync(Guid positionId, decimal lots, CancellationToken ct)
    {
        var fillPrice = new Price(_lastClose > 0 ? _lastClose : 1m);
        _executionChannel.Writer.TryWrite(
            new ExecutionEvent(positionId, OrderState.Filled, fillPrice, lots, null, BrokerTimeUtc));

        if (_openTrades.TryGetValue(positionId, out var trade))
        {
            var symbolInfo = _symbolRegistry.Get(_symbol);
            decimal pnl = 0;
            try
            {
                var priceDiff = trade.Direction == TradeDirection.Long
                    ? fillPrice.Value - trade.EntryPrice
                    : trade.EntryPrice - fillPrice.Value;
                var pipValue = PipCalculator.PipValuePerLot(symbolInfo, fillPrice.Value, _crossRateProvider);
                pnl = (priceDiff / symbolInfo.PipSize) * pipValue * lots;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute partial PnL for {PositionId}", positionId);
            }
            _balance += pnl;
            var remaining = trade.Lots - lots;
            if (remaining <= 0)
                _openTrades.Remove(positionId);
            else
                _openTrades[positionId] = (trade.Direction, trade.EntryPrice, remaining);
            _logger.LogDebug("BacktestReplay: partial close {PositionId} lots={Lots} remaining={Remaining} PnL={PnL:F2}",
                positionId, lots, remaining, pnl);
        }

        return Task.CompletedTask;
    }

    private decimal ComputeFloatingPnL(decimal close)
    {
        if (_openTrades.Count == 0) return 0m;

        try
        {
            var symbolInfo = _symbolRegistry.Get(_symbol);
            var pipValue = PipCalculator.PipValuePerLot(symbolInfo, close, _crossRateProvider);
            var total = 0m;
            foreach (var (_, (dir, entry, lots)) in _openTrades)
            {
                var diff = dir == TradeDirection.Long ? close - entry : entry - close;
                total += (diff / symbolInfo.PipSize) * pipValue * lots;
            }
            return total;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute floating PnL");
            return 0m;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _feedCts?.Cancel();
        _barChannel.Writer.TryComplete();
        _tickChannel.Writer.TryComplete();
        _accountChannel.Writer.TryComplete();
        _executionChannel.Writer.TryComplete();
        _openTrades.Clear();
        try { await _feedTask; } catch (OperationCanceledException) { }
        _feedCts?.Dispose();
    }
}
