namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/governor")]
public sealed class GovernorController : ControllerBase
{
    private readonly ITradingGovernor _governor;

    public GovernorController(ITradingGovernor governor)
    {
        _governor = governor;
    }

    [HttpGet("state")]
    public IActionResult GetState()
    {
        var snapshot = _governor.GetSnapshot();
        return Ok(new
        {
            state = snapshot.State.ToString(),
            sizeMultiplier = snapshot.SizeMultiplier,
            consecutiveLosses = snapshot.ConsecutiveLosses,
            dayRealizedPnLPercent = snapshot.DayRealizedPnLPercent,
            distanceToDailyLimitFraction = snapshot.DistanceToDailyLimitFraction,
            reason = snapshot.Reason,
        });
    }
}
