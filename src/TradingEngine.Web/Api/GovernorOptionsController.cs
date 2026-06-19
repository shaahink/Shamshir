using System.Text.Json;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/governor-options")]
public sealed class GovernorOptionsController : ControllerBase
{
    private readonly IGovernorOptionsStore _store;
    private readonly ILogger<GovernorOptionsController> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public GovernorOptionsController(IGovernorOptionsStore store, ILogger<GovernorOptionsController> logger)
    {
        _store = store;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var options = await _store.GetAsync(ct);
        return Ok(options);
    }

    [HttpPut]
    public async Task<IActionResult> Update(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(ct);
        GovernorOptions options;
        try { options = JsonSerializer.Deserialize<GovernorOptions>(json, _jsonOpts)!; }
        catch (JsonException ex) { return BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }
        if (options is null) return BadRequest(new { error = "Invalid body" });
        await _store.UpsertAsync(options, ct);
        _logger.LogInformation("Governor options updated");
        return Ok(new { saved = true });
    }

    [HttpPost("duplicate")]
    public async Task<IActionResult> Duplicate(CancellationToken ct)
    {
        var source = await _store.GetAsync(ct);
        await _store.UpsertAsync(source, ct);
        _logger.LogInformation("Governor options duplicated (no-op for single-row)");
        return Ok(new { saved = true });
    }
}
