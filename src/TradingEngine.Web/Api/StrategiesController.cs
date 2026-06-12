namespace TradingEngine.Web.Api;

using TradingEngine.Host;

[ApiController]
[Route("api/strategies")]
public class StrategiesController : ControllerBase
{
    private readonly IStrategyBank _bank;
    private readonly StrategyRegistry _registry;
    private readonly string _configDir;

    public StrategiesController(IStrategyBank bank, StrategyRegistry registry)
    {
        _bank = bank;
        _registry = registry;
        _configDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "strategies");
        Directory.CreateDirectory(_configDir);
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        var snapshot = _bank.GetSnapshot();
        return Ok(snapshot);
    }

    [HttpPut("{id}/enable")]
    public IActionResult Enable(string id)
    {
        _bank.Enable(id);
        return Ok(new { id, enabled = true });
    }

    [HttpPut("{id}/disable")]
    public IActionResult Disable(string id)
    {
        _bank.Disable(id);
        return Ok(new { id, enabled = false });
    }

    [HttpPut("{id}/config")]
    public async Task<IActionResult> UpdateConfig(string id)
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();
        var path = Path.Combine(_configDir, $"{id}.json");
        await System.IO.File.WriteAllTextAsync(path, json);
        return Ok(new { id, saved = true });
    }
}
