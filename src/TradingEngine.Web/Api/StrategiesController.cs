namespace TradingEngine.Web.Api;

using TradingEngine.Web.Services;

[ApiController]
[Route("api/strategies")]
public class StrategiesController : ControllerBase
{
    private readonly IBacktestCommandService _cmd;
    private readonly IBacktestQueryService _query;

    public StrategiesController(IBacktestCommandService cmd, IBacktestQueryService query)
    {
        _cmd = cmd;
        _query = query;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        // Return available strategies from config
        var configDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "strategies");
        if (!Directory.Exists(configDir))
            return Ok(Array.Empty<object>());

        var strategies = new List<object>();
        foreach (var file in Directory.GetFiles(configDir, "*.json"))
        {
            var json = System.IO.File.ReadAllText(file);
            strategies.Add(new { file = Path.GetFileNameWithoutExtension(file), config = json });
        }
        return Ok(strategies);
    }

    [HttpPut("{id}/enable")]
    public IActionResult Enable(string id)
    {
        return Ok(new { id, enabled = true });
    }

    [HttpPut("{id}/disable")]
    public IActionResult Disable(string id)
    {
        return Ok(new { id, enabled = false });
    }

    [HttpPut("{id}/config")]
    public async Task<IActionResult> UpdateConfig(string id)
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();
        var configDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "strategies");
        var path = Path.Combine(configDir, $"{id}.json");
        await System.IO.File.WriteAllTextAsync(path, json);
        return Ok(new { id, saved = true });
    }
}
