using Microsoft.AspNetCore.Mvc;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/phase-tracker")]
public class PhaseTrackerController(PassProbabilityService passProbabilityService) : ControllerBase
{
    [HttpPost("evaluate")]
    public async Task<IActionResult> Evaluate([FromBody] PhaseEstimateRequest req, CancellationToken ct)
    {
        if (req.InitialBalance <= 0 || req.DaysRemaining <= 0)
            return BadRequest("Balance and daysRemaining must be positive.");

        if (req.ProfitTargetPercent <= 0 || req.MaxDailyLossPercent <= 0 || req.MaxTotalLossPercent <= 0)
            return BadRequest("Target and loss percents must be positive.");

        if (!string.IsNullOrEmpty(req.RunId))
        {
            var result = await passProbabilityService.ComputeAsync(req.RunId, (int)req.DaysRemaining, ct);
            return Ok(new
            {
                passProbability = result.ProbabilityOfPass * 100,
                result.ExpectedDaysToTarget,
                dailyBreachRate = result.ProbabilityOfDailyBreach,
                maxBreachRate = result.ProbabilityOfMaxBreach,
                projectedEquity = (double)result.ProjectedFinalEquity,
                recommendation = result.Recommendation,
            });
        }

        return Ok(new
        {
            passProbability = 0,
            expectedDaysToTarget = (int?)null,
            dailyBreachRate = 0,
            maxBreachRate = 0,
            projectedEquity = req.InitialBalance,
            recommendation = "Supply a RunId with trade history for a data-driven P(pass) estimate.",
        });
    }
}

public sealed record PhaseEstimateRequest
{
    public double InitialBalance { get; init; }
    public double DaysRemaining { get; init; }
    public double ProfitTargetPercent { get; init; }
    public double MaxDailyLossPercent { get; init; }
    public double MaxTotalLossPercent { get; init; }
    public string? RunId { get; init; }
}
