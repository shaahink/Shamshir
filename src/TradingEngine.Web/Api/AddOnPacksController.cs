using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Services.AddOns;

namespace TradingEngine.Web.Api;

/// <summary>
/// iter-38 (Stream PK / U1). CRUD over reusable add-on packs + the auto-tune PREVIEW the New-Backtest and pack
/// editors call to show the numbers a given (timeframe, symbol, volatility) would produce. Skeleton — the store
/// must be DI-registered (PK1) and seeded with the 3 starter packs; the run wiring (PK3) lives in the orchestrator.
/// </summary>
[ApiController]
[Route("api/addons")]
public sealed class AddOnPacksController(IAddOnPackStore store) : ControllerBase
{
    [HttpGet("packs")]
    public async Task<IActionResult> GetAll(CancellationToken ct) => Ok(await store.GetAllAsync(ct));

    [HttpGet("packs/{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var pack = await store.GetByIdAsync(id, ct);
        return pack is null ? NotFound(new { error = $"Pack {id} not found" }) : Ok(pack);
    }

    [HttpPut("packs/{id}")]
    public async Task<IActionResult> Upsert(string id, [FromBody] AddOnPack body, CancellationToken ct)
    {
        // TODO(iter-38 U1): validate the add-on bundle (e.g. enabled add-ons have a method; fractions in (0,1]).
        await store.UpsertAsync(body with { Id = id }, ct);
        return Ok(new { id });
    }

    [HttpDelete("packs/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await store.DeleteAsync(id, ct);
        return Ok(new { deleted = true });
    }

    /// <summary>
    /// iter-38 (A2 / U1/U3). Preview the auto-tuned add-on numbers for a (timeframe, volatility) context, so the
    /// UI can show "Enable Trailing on USDJPY/M15 → atrMult 2.2" before a run. <paramref name="atrPips"/> /
    /// <paramref name="spreadPips"/> / <paramref name="referenceAtrPips"/> are optional; defaults give a neutral
    /// preview. (A live run resolves these from the entry bar instead — Stream A3.)
    /// </summary>
    [HttpGet("preview")]
    public IActionResult Preview(
        [FromQuery] string tf,
        [FromQuery] double atrPips = 10,
        [FromQuery] double spreadPips = 1,
        [FromQuery] double referenceAtrPips = 0)
    {
        if (!Enum.TryParse<Timeframe>(tf, ignoreCase: true, out var timeframe))
            return BadRequest(new { error = $"Unknown timeframe '{tf}'." });

        var values = AddOnAutoTuner.Tune(timeframe, new VolatilityContext(atrPips, spreadPips, referenceAtrPips));
        return Ok(values);
    }
}
