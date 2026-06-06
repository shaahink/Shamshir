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

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SubmitOrderAsync(OrderRequest request, CancellationToken ct) => Task.CompletedTask;
    public Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct) => Task.CompletedTask;
    public Task CancelOrderAsync(Guid orderId, CancellationToken ct) => Task.CompletedTask;
    public Task ClosePositionAsync(Guid positionId, CancellationToken ct) => Task.CompletedTask;

    public void OnTickReceived(Tick tick)
    {
    }
}
