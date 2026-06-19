using System.Text.Json;
using System.Text.Json.Serialization;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/risk-profiles")]
public sealed class RiskProfilesController : ControllerBase
{
    private readonly IRiskProfileStore _store;
    private readonly ILogger<RiskProfilesController> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public RiskProfilesController(IRiskProfileStore store, ILogger<RiskProfilesController> logger)
    {
        _store = store;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var profiles = await _store.GetAllAsync(ct);
        var result = profiles.Select(p => new
        {
            id = p.Id,
            displayName = p.DisplayName,
            riskPerTradePercent = p.RiskPerTradePercent,
            maxDailyDrawdownPercent = p.MaxDailyDrawdownPercent,
            maxTotalDrawdownPercent = p.MaxTotalDrawdownPercent,
            maxSlPips = p.MaxSlPips,
            maxExposurePercent = p.MaxExposurePercent,
            maxExposurePerCurrencyPercent = p.MaxExposurePerCurrencyPercent,
            drawdownScaleThreshold = p.DrawdownScaleThreshold,
            drawdownScaleFloor = p.DrawdownScaleFloor,
            maxConcurrentPositions = p.MaxConcurrentPositions,
            allowHedging = p.AllowHedging,
            propFirmRuleSetId = p.PropFirmRuleSetId,
            lotSizingMethod = p.LotSizingMethod.ToString(),
            fixedLots = p.FixedLots,
            fixedDollarRisk = p.FixedDollarRisk,
            kellyFraction = p.KellyFraction,
            antiMartingaleMultiplier = p.AntiMartingaleMultiplier,
            antiMartingaleMaxSteps = p.AntiMartingaleMaxSteps,
            sizeModifiers = p.SizeModifiers,
        }).ToList();
        return Ok(new { profiles = result });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var p = await _store.GetByIdAsync(id, ct);
        if (p is null) return NotFound(new { error = $"Risk profile {id} not found" });
        return Ok(p);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(ct);
        RiskProfile profile;
        try { profile = JsonSerializer.Deserialize<RiskProfile>(json, _jsonOpts)!; }
        catch (JsonException ex) { return BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }
        if (profile is null || profile.Id != id)
            return BadRequest(new { error = "Id in body must match route id" });
        await _store.UpsertAsync(profile, ct);
        _logger.LogInformation("Risk profile {ProfileId} updated", id);
        return Ok(new { id, saved = true });
    }

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(ct);
        RiskProfile profile;
        try { profile = JsonSerializer.Deserialize<RiskProfile>(json, _jsonOpts)!; }
        catch (JsonException ex) { return BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }
        if (string.IsNullOrWhiteSpace(profile.Id))
            return BadRequest(new { error = "id is required" });
        var existing = await _store.GetByIdAsync(profile.Id, ct);
        if (existing is not null)
            return Conflict(new { error = $"Risk profile {profile.Id} already exists" });
        await _store.UpsertAsync(profile, ct);
        _logger.LogInformation("Risk profile {ProfileId} created", profile.Id);
        return Ok(new { id = profile.Id, saved = true });
    }

    [HttpPost("{id}/duplicate")]
    public async Task<IActionResult> Duplicate(string id, CancellationToken ct)
    {
        var source = await _store.GetByIdAsync(id, ct);
        if (source is null) return NotFound(new { error = $"Risk profile {id} not found" });

        var copy = source with
        {
            Id = $"{id}-copy",
            DisplayName = $"{source.DisplayName} (Copy)",
        };
        await _store.UpsertAsync(copy, ct);
        _logger.LogInformation("Risk profile {SourceId} duplicated to {NewId}", id, copy.Id);
        return Ok(new { id = copy.Id, saved = true });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await _store.DeleteAsync(id, ct);
        _logger.LogInformation("Risk profile {ProfileId} deleted", id);
        return Ok(new { deleted = true });
    }
}
