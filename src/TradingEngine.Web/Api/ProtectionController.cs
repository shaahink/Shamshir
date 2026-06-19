using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/protection")]
public sealed class ProtectionController : ControllerBase
{
    private readonly IProtectionQueryService _protection;

    public ProtectionController(IProtectionQueryService protection)
    {
        _protection = protection;
    }

    [HttpGet("days")]
    public async Task<IActionResult> GetDays([FromQuery] string? runId, CancellationToken ct)
    {
        var days = await _protection.GetDaysAsync(runId, ct);
        return Ok(days);
    }

    [HttpGet("days/{date:datetime}")]
    public async Task<IActionResult> GetDayDetails(DateTime date, [FromQuery] string? runId, CancellationToken ct)
    {
        var entries = await _protection.GetDayDetailsAsync(date, runId, ct);
        if (entries.Count == 0)
            return NotFound(new { error = $"No ledger entries for {date:yyyy-MM-dd}" });
        return Ok(entries);
    }
}
