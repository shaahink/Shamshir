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
        // iter-38 W-C4: honour the from/to date window (was previously accepted but always ignored).
        var rows = trades.Where(t =>
            (from is null || t.ClosedAtUtc >= from.Value) &&
            (to is null || t.ClosedAtUtc <= to.Value));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Symbol,Direction,Lots,EntryPrice,ExitPrice,GrossPnL,Commission,Swap,NetPnL,Pips,RMultiple,ExitReason,OpenedAtUtc,ClosedAtUtc,StrategyId,DurationSeconds");

        foreach (var t in rows)
        {
            var dur = (t.ClosedAtUtc - t.OpenedAtUtc).TotalSeconds;
            // iter-38 W-B7: RFC-4180 quoting + CSV-injection guard on free-text fields.
            sb.AppendLine(
                $"{Csv(t.Symbol)},{Csv(t.Direction)},{t.Lots},{t.EntryPrice},{t.ExitPrice},{t.GrossPnLAmount},{t.CommissionAmount},{t.SwapAmount},{t.NetPnLAmount},{t.PnLPips},{t.RMultiple},{Csv(t.ExitReason)},{t.OpenedAtUtc:O},{t.ClosedAtUtc:O},{Csv(t.StrategyId)},{dur:F0}");
        }

        return Content(sb.ToString(), "text/csv", System.Text.Encoding.UTF8);
    }

    // RFC-4180 field quoting + CSV-injection guard (prefix a leading =,+,-,@ with a single quote so
    // spreadsheets don't evaluate it as a formula).
    private static string Csv(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@'))
            s = "'" + s;
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
