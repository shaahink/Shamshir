using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class NamedPipeBrokerAdapter : IBrokerAdapter, IAsyncDisposable
{
    private readonly string _pipeName;
    private NamedPipeServerStream? _pipeServer;
    private readonly CancellationTokenSource _cts = new();

    private readonly Channel<Tick> _tickChannel = Channel.CreateUnbounded<Tick>();
    private readonly Channel<Bar> _barChannel = Channel.CreateUnbounded<Bar>();
    private readonly Channel<AccountUpdate> _accountChannel = Channel.CreateUnbounded<AccountUpdate>();
    private readonly Channel<ExecutionEvent> _executionChannel = Channel.CreateUnbounded<ExecutionEvent>();

    public ChannelReader<Tick> TickStream => _tickChannel.Reader;
    public ChannelReader<Bar> BarStream => _barChannel.Reader;
    public ChannelReader<AccountUpdate> AccountStream => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _executionChannel.Reader;

    public DateTime BrokerTimeUtc { get; private set; } = DateTime.UtcNow;
    public bool IsConnected => _pipeServer?.IsConnected ?? false;

    public NamedPipeBrokerAdapter(string pipeName = "trading-engine")
    {
        _pipeName = pipeName;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _pipeServer = new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        await _pipeServer.WaitForConnectionAsync(ct);
        _ = ReadLoopAsync(_cts.Token);
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        _cts.Cancel();
        _pipeServer?.Disconnect();
        _pipeServer?.Dispose();
        _pipeServer = null;
        return Task.CompletedTask;
    }

    public Task SubmitOrderAsync(OrderRequest request, CancellationToken ct)
        => SendCommandAsync("SubmitOrder", request, ct);

    public Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct)
        => SendCommandAsync("ModifyOrder", new { OrderId = orderId, NewStopLoss = newStopLoss.Value, NewTakeProfit = newTakeProfit?.Value }, ct);

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct)
        => SendCommandAsync("CancelOrder", new { OrderId = orderId }, ct);

    public Task ClosePositionAsync(Guid positionId, CancellationToken ct)
        => SendCommandAsync("ClosePosition", new { PositionId = positionId }, ct);

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested && _pipeServer!.IsConnected)
            {
                var bytesRead = await _pipeServer.ReadAsync(buffer, 0, 4, ct);
                if (bytesRead < 4) break;

                var length = BitConverter.ToInt32(buffer, 0);
                var messageBytes = new byte[length];
                var totalRead = 0;
                while (totalRead < length)
                {
                    bytesRead = await _pipeServer.ReadAsync(messageBytes, totalRead, length - totalRead, ct);
                    if (bytesRead == 0) break;
                    totalRead += bytesRead;
                }

                var json = Encoding.UTF8.GetString(messageBytes, 0, totalRead);
                ProcessMessage(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private void ProcessMessage(string json) { }

    private async Task SendCommandAsync(string type, object payload, CancellationToken ct)
    {
        if (_pipeServer is null || !_pipeServer.IsConnected) return;
        var json = System.Text.Json.JsonSerializer.Serialize(new { Type = type, Payload = payload });
        var bytes = Encoding.UTF8.GetBytes(json);
        var lengthBytes = BitConverter.GetBytes(bytes.Length);
        await _pipeServer.WriteAsync(lengthBytes, 0, 4, ct);
        await _pipeServer.WriteAsync(bytes, 0, bytes.Length, ct);
        await _pipeServer.FlushAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        if (_pipeServer is not null)
        {
            await _pipeServer.DisposeAsync();
        }
    }
}
