using TradingEngine.Domain;
using TradingEngine.Host;
using TradingEngine.Web.Dtos.Strategies;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/strategies")]
public class StrategiesController : ControllerBase
{
    private readonly IStrategyBank _bank;
    private readonly StrategyRegistry _registry;
    private readonly string _configDir;
    private readonly ILogger<StrategiesController> _logger;

    public StrategiesController(IStrategyBank bank, StrategyRegistry registry, ILogger<StrategiesController> logger)
    {
        _bank = bank;
        _registry = registry;
        _logger = logger;
        _configDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "strategies");
        Directory.CreateDirectory(_configDir);
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        var snapshot = _bank.GetSnapshot();
        return Ok(snapshot);
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var snapshot = _bank.GetSnapshot();
        var config = snapshot.Strategies.FirstOrDefault(s => s.Id == id);
        if (config is null) return NotFound(new { error = $"Strategy {id} not found" });

        return Ok(new StrategyDetailResponse
        {
            Id = config.Id,
            DisplayName = config.DisplayName,
            Enabled = config.IsEnabled,
            Timeframe = "",
            Symbols = [],
        });
    }

    [HttpPut("{id}/enable")]
    public IActionResult Enable(string id)
    {
        _bank.Enable(id);
        _logger.LogInformation("Strategy {StrategyId} enabled", id);
        return Ok(new { id, enabled = true });
    }

    [HttpPut("{id}/disable")]
    public IActionResult Disable(string id)
    {
        _bank.Disable(id);
        _logger.LogInformation("Strategy {StrategyId} disabled", id);
        return Ok(new { id, enabled = false });
    }

    [HttpPut("{id}/config")]
    public async Task<IActionResult> UpdateConfig(string id)
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();
        var path = Path.Combine(_configDir, $"{id}.json");
        await System.IO.File.WriteAllTextAsync(path, json);
        _logger.LogInformation("Strategy {StrategyId} config updated", id);
        return Ok(new { id, saved = true });
    }
}
