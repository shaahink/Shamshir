namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/export")]
public sealed class ExportController : ControllerBase
{
    [HttpGet("trades.csv")]
    public IActionResult ExportCsv(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var csv = "Symbol,Direction,Lots,EntryPrice,ExitPrice,NetPnL,ExitReason,Date\n";
        return Content(csv, "text/csv", System.Text.Encoding.UTF8);
    }
}
