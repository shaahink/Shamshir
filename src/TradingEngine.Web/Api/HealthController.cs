namespace TradingEngine.Web.Api;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult GetHealth()
    {
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            version = "2.0.0",
        });
    }
}
