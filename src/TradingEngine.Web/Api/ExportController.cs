namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/export")]
public sealed class ExportController(IRunQueryService query) : ControllerBase
{
    [HttpGet("trades.csv")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] string runId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return BadRequest(new { error = "runId is required" });

        var trades = await query.GetRunTradesAsync(runId, HttpContext.RequestAborted);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Symbol,Direction,Lots,EntryPrice,ExitPrice,GrossPnL,Commission,Swap,NetPnL,Pips,RMultiple,ExitReason,OpenedAtUtc,ClosedAtUtc,StrategyId,DurationSeconds");

        foreach (var t in trades)
        {
            var dur = (t.ClosedAtUtc - t.OpenedAtUtc).TotalSeconds;
            sb.AppendLine($"{t.Symbol},{t.Direction},{t.Lots},{t.EntryPrice},{t.ExitPrice},{t.GrossPnLAmount},{t.CommissionAmount},{t.SwapAmount},{t.NetPnLAmount},{t.PnLPips},{t.RMultiple},{t.ExitReason},{t.OpenedAtUtc:O},{t.ClosedAtUtc:O},{t.StrategyId},{dur:F0}");
        }

        return Content(sb.ToString(), "text/csv", System.Text.Encoding.UTF8);
    }
}
