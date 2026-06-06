namespace TradingEngine.Web.Api;

[ApiController]
public sealed class RiskSseController : ControllerBase
{
    private static readonly Channel<RiskState> _riskChannel =
        Channel.CreateBounded<RiskState>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false
        });

    public static void PushRiskState(RiskState state)
    {
        _riskChannel.Writer.TryWrite(state);
    }

    [HttpGet("/sse/risk")]
    public async Task StreamRisk(CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        await foreach (var state in _riskChannel.Reader.ReadAllAsync(ct))
        {
            var json = JsonSerializer.Serialize(state);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}
