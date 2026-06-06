namespace TradingEngine.Web.Api;

[ApiController]
public sealed class RiskSseController : ControllerBase
{
    [HttpGet("/sse/risk")]
    public async Task StreamRisk(CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var state = new RiskState(true, false, null, 0, 0, 0.05m, 0.10m, null);
        var json = JsonSerializer.Serialize(state);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
