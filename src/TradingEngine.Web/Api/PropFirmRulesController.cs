using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/prop-firm-rules")]
public sealed class PropFirmRulesController : ControllerBase
{
    private readonly IPropFirmRuleSetStore _store;
    private readonly ILogger<PropFirmRulesController> _logger;

    public PropFirmRulesController(IPropFirmRuleSetStore store, ILogger<PropFirmRulesController> logger)
    {
        _store = store;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var rules = await _store.GetAllAsync(ct);
        return Ok(new { rules });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var r = await _store.GetByIdAsync(id, ct);
        if (r is null) return NotFound(new { error = $"Prop-firm rule set {id} not found" });
        return Ok(r);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] PropFirmRuleSet ruleSet, CancellationToken ct)
    {
        if (ruleSet is null || ruleSet.Id != id)
            return BadRequest(new { error = "Id in body must match route id" });
        await _store.UpsertAsync(ruleSet, ct);
        _logger.LogInformation("Prop-firm rule set {Id} updated", id);
        return Ok(new { id, saved = true });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PropFirmRuleSet ruleSet, CancellationToken ct)
    {
        if (ruleSet is null || string.IsNullOrWhiteSpace(ruleSet.Id))
            return BadRequest(new { error = "id is required" });
        var existing = await _store.GetByIdAsync(ruleSet.Id, ct);
        if (existing is not null)
            return Conflict(new { error = $"Prop-firm rule set {ruleSet.Id} already exists" });
        await _store.UpsertAsync(ruleSet, ct);
        _logger.LogInformation("Prop-firm rule set {Id} created", ruleSet.Id);
        return Ok(new { id = ruleSet.Id, saved = true });
    }

    [HttpPost("{id}/duplicate")]
    public async Task<IActionResult> Duplicate(string id, CancellationToken ct)
    {
        var source = await _store.GetByIdAsync(id, ct);
        if (source is null) return NotFound(new { error = $"Prop-firm rule set {id} not found" });

        var copy = source with
        {
            Id = $"{id}-copy",
            DisplayName = $"{source.DisplayName} (Copy)",
        };
        await _store.UpsertAsync(copy, ct);
        _logger.LogInformation("Prop-firm rule set {SourceId} duplicated to {NewId}", id, copy.Id);
        return Ok(new { id = copy.Id, saved = true });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await _store.DeleteAsync(id, ct);
        _logger.LogInformation("Prop-firm rule set {Id} deleted", id);
        return Ok(new { deleted = true });
    }
}
