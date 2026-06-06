using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class NamedPipeBrokerAdapter : IBrokerAdapter, IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ILogger<NamedPipeBrokerAdapter> _logger;
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
        _logger = null!;
    }

    public NamedPipeBrokerAdapter(string pipeName, ILogger<NamedPipeBrokerAdapter> logger)
    {
        _pipeName = pipeName;
        _logger = logger;
    }

    public Task<AccountState> GetAccountStateAsync(CancellationToken ct)
        => Task.FromResult(new AccountState(0, 0, []));

    public async Task ConnectAsync(CancellationToken ct)
    {
        var delays = new[] { 2000, 4000, 8000 };
        var attempt = 0;

        while (attempt <= delays.Length)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                _pipeServer?.Dispose();
                _pipeServer = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await _pipeServer.WaitForConnectionAsync(ct);
                _logger?.LogInformation("Pipe connected. PipeName={PipeName}", _pipeName);
                _ = ReadLoopAsync(_cts.Token);
                return;
            }
            catch (Exception ex) when (attempt < delays.Length)
            {
                attempt++;
                _logger?.LogWarning(ex, "Pipe connect attempt {Attempt}/{Max} failed. Retrying in {Delay}ms",
                    attempt, delays.Length, delays[attempt - 1]);
                await Task.Delay(delays[attempt - 1], ct);
            }
        }

        throw new InvalidOperationException($"Failed to connect pipe '{_pipeName}' after {delays.Length} attempts.");
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        _cts.Cancel();
        if (_pipeServer is not null)
        {
            try { _pipeServer.Disconnect(); } catch { }
            await _pipeServer.DisposeAsync();
        }
        _pipeServer = null;
    }

    public async Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        await SendCommandAsync("SubmitOrder", request, ct);
        return Guid.NewGuid();
    }

    public Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct)
        => SendCommandAsync("ModifyOrder", new { OrderId = orderId, NewStopLoss = newStopLoss.Value, NewTakeProfit = newTakeProfit?.Value }, ct);

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct)
        => SendCommandAsync("CancelOrder", new { OrderId = orderId }, ct);

    public Task ClosePositionAsync(Guid positionId, CancellationToken ct)
        => SendCommandAsync("ClosePosition", new { PositionId = positionId }, ct);

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_pipeServer is null || !_pipeServer.IsConnected)
                {
                    await TryReconnectAsync(ct);
                    continue;
                }

                var length = await ReadLengthPrefixAsync(ct);
                if (length <= 0)
                {
                    await TryReconnectAsync(ct);
                    continue;
                }

                var messageBytes = new byte[length];
                var totalRead = 0;
                while (totalRead < length)
                {
                    var bytesRead = await _pipeServer.ReadAsync(messageBytes, totalRead, length - totalRead, ct);
                    if (bytesRead == 0) break;
                    totalRead += bytesRead;
                }

                if (totalRead < length)
                {
                    await TryReconnectAsync(ct);
                    continue;
                }

                var json = Encoding.UTF8.GetString(messageBytes, 0, totalRead);
                ProcessMessage(json);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "Pipe read error — attempting reconnect");
                await TryReconnectAsync(ct);
            }
        }
    }

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        var delays = new[] { 2000, 4000, 8000 };

        CleanupPipe();

        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                _pipeServer = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await _pipeServer.WaitForConnectionAsync(ct);
                _logger?.LogInformation("Pipe reconnected on attempt {Attempt}", attempt + 1);

                _accountChannel.Writer.TryWrite(new AccountUpdate(0, 0, 0, DateTime.UtcNow));
                return;
            }
            catch (Exception ex) when (attempt < delays.Length - 1)
            {
                _logger?.LogWarning(ex, "Reconnect attempt {Attempt}/{Max} failed", attempt + 1, delays.Length);
                await Task.Delay(delays[attempt], ct);
            }
        }

        _logger?.LogCritical("All reconnect attempts failed. Pipe = {PipeName}", _pipeName);
        CleanupPipe();
    }

    private void CleanupPipe()
    {
        try { _pipeServer?.Disconnect(); } catch { }
        try { _pipeServer?.Dispose(); } catch { }
        _pipeServer = null;
    }

    private async Task<int> ReadLengthPrefixAsync(CancellationToken ct)
    {
        var buffer = new byte[4];
        var totalRead = 0;
        while (totalRead < 4)
        {
            var bytesRead = await _pipeServer!.ReadAsync(buffer, totalRead, 4 - totalRead, ct);
            if (bytesRead == 0) return -1;
            totalRead += bytesRead;
        }
        return BitConverter.ToInt32(buffer, 0);
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = GetCaseInsensitiveProperty(doc.RootElement, "Type").GetString();

            switch (type)
            {
                case "Tick":
                {
                    var tickPayload = GetCaseInsensitiveProperty(doc.RootElement, "Payload");
                    var tick = new Tick(
                        Symbol.Parse(CIProp(tickPayload, "Symbol").GetString()!),
                        CIProp(tickPayload, "Bid").GetDecimal(),
                        CIProp(tickPayload, "Ask").GetDecimal(),
                        CIProp(tickPayload, "TimestampUtc").GetDateTime());
                    _tickChannel.Writer.TryWrite(tick);
                    break;
                }

                case "Bar":
                {
                    var barPayload = GetCaseInsensitiveProperty(doc.RootElement, "Payload");
                    var bar = new Bar(
                        Symbol.Parse(CIProp(barPayload, "Symbol").GetString()!),
                        Enum.Parse<Timeframe>(CIProp(barPayload, "Timeframe").GetString()!, ignoreCase: true),
                        CIProp(barPayload, "OpenTimeUtc").GetDateTime(),
                        CIProp(barPayload, "Open").GetDecimal(),
                        CIProp(barPayload, "High").GetDecimal(),
                        CIProp(barPayload, "Low").GetDecimal(),
                        CIProp(barPayload, "Close").GetDecimal(),
                        CIProp(barPayload, "Volume").GetDouble());
                    _barChannel.Writer.TryWrite(bar);
                    break;
                }

                case "AccountUpdate":
                {
                    var acctPayload = GetCaseInsensitiveProperty(doc.RootElement, "Payload");
                    var acct = new AccountUpdate(
                        CIProp(acctPayload, "Balance").GetDecimal(),
                        CIProp(acctPayload, "Equity").GetDecimal(),
                        CIProp(acctPayload, "FloatingPnL").GetDecimal(),
                        CIProp(acctPayload, "TimestampUtc").GetDateTime());
                    _accountChannel.Writer.TryWrite(acct);
                    break;
                }

                case "ExecutionEvent":
                {
                    var execPayload = GetCaseInsensitiveProperty(doc.RootElement, "Payload");
                    var fpElem = GetCaseInsensitivePropertyOrNull(execPayload, "FillPrice");
                    var fillPrice = fpElem.HasValue && fpElem.Value.ValueKind == JsonValueKind.Number
                        ? new Price(fpElem.Value.GetDecimal()) : (Price?)null;
                    var exec = new ExecutionEvent(
                        CIProp(execPayload, "OrderId").GetGuid(),
                        Enum.Parse<OrderState>(CIProp(execPayload, "NewState").GetString()!),
                        fillPrice,
                        CIProp(execPayload, "FilledLots").GetDecimal(),
                        CIPropOrNull(execPayload, "RejectionReason"),
                        GetDateTimeUtc(execPayload));
                    _executionChannel.Writer.TryWrite(exec);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Pipe message parse error");
        }
    }

    private async Task SendCommandAsync(string type, object payload, CancellationToken ct)
    {
        if (_pipeServer is null || !_pipeServer.IsConnected) return;
        var json = JsonSerializer.Serialize(new { Type = type, Payload = payload });
        var bytes = Encoding.UTF8.GetBytes(json);
        var lengthBytes = BitConverter.GetBytes(bytes.Length);
        await _pipeServer.WriteAsync(lengthBytes, 0, 4, ct);
        await _pipeServer.WriteAsync(bytes, 0, bytes.Length, ct);
        await _pipeServer.FlushAsync(ct);
    }

    private static JsonElement GetCaseInsensitiveProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var result))
            return result;
        var camelName = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
        if (element.TryGetProperty(camelName, out result))
            return result;
        throw new KeyNotFoundException($"Property '{propertyName}' not found in JSON element.");
    }

    private static JsonElement? GetCaseInsensitivePropertyOrNull(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var result))
            return result;
        var camelName = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
        if (element.TryGetProperty(camelName, out result))
            return result;
        return null;
    }

    private static JsonElement CIProp(JsonElement e, string name) => GetCaseInsensitiveProperty(e, name);

    private static string? CIPropOrNull(JsonElement e, string name)
    {
        var prop = GetCaseInsensitivePropertyOrNull(e, name);
        return prop?.GetString();
    }

    private static DateTime GetDateTimeUtc(JsonElement e)
    {
        var tsProp = GetCaseInsensitiveProperty(e, "TimestampUtc");
        var str = tsProp.GetString();
        if (DateTime.TryParse(str, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        return tsProp.GetDateTime();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        CleanupPipe();
    }
}
