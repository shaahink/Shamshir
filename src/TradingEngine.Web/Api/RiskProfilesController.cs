using TradingEngine.Infrastructure.Configuration;

namespace TradingEngine.Web.Api;

/// <summary>
/// Risk / money-management profiles offered to the New-Backtest page. They are the same profiles the
/// engine evaluates against (loaded via <see cref="ConfigLoader"/>), so what the user picks is exactly
/// what risk/lot-sizing the run enforces. A run's chosen profile is applied to every strategy by
/// <c>BacktestOrchestrator.BuildLoadedConfigFromDbAsync</c>.
/// </summary>
[ApiController]
[Route("api/risk-profiles")]
public sealed class RiskProfilesController : ControllerBase
{
    private static string SolutionRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [HttpGet]
    public IActionResult GetAll()
    {
        var loaded = new ConfigLoader(SolutionRoot).LoadBase();
        var profiles = loaded.RiskProfiles.Select(p => new
        {
            id = p.Id,
            displayName = p.DisplayName,
            riskPerTradePercent = p.RiskPerTradePercent,
            maxDailyDrawdownPercent = p.MaxDailyDrawdownPercent,
            maxTotalDrawdownPercent = p.MaxTotalDrawdownPercent,
            maxConcurrentPositions = p.MaxConcurrentPositions,
            lotSizingMethod = p.LotSizingMethod.ToString(),
            propFirmRuleSetId = p.PropFirmRuleSetId,
        }).ToList();
        return Ok(new { profiles });
    }
}
