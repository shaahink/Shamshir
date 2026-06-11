using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class BacktestReplayAdapter : IBrokerAdapter, IAsyncDisposable
{
    private readonly IBarRepository _barRepo;
    private readonly Symbol _symbol;
    private readonly Timeframe _timeframe;
    private readonly DateTime _from;
    private readonly DateTime _to;
    private readonly decimal _initialBalance;
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
        ILogger<BacktestReplayAdapter> logger)
    {
        _barRepo = barRepo;
        _symbol = symbol;
        _timeframe = timeframe;
        _from = from;
        _to = to;
        _initialBalance = initialBalance;
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
                await _accountChannel.Writer.WriteAsync(
                    new AccountUpdate(_initialBalance, _initialBalance, 0, bar.OpenTimeUtc), ct);
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
        _logger.LogDebug("BacktestReplay: instant fill {Id} at {Price:F5}", orderId, fillPrice.Value);
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
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _feedCts?.Cancel();
        _barChannel.Writer.TryComplete();
        _tickChannel.Writer.TryComplete();
        _accountChannel.Writer.TryComplete();
        _executionChannel.Writer.TryComplete();
        try { await _feedTask; } catch (OperationCanceledException) { }
        _feedCts?.Dispose();
    }
}
