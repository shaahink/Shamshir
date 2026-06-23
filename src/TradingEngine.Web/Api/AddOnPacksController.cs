using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Services.AddOns;

namespace TradingEngine.Web.Api;

/// <summary>
/// iter-38 (Stream PK / U1). CRUD over reusable add-on packs + the auto-tune PREVIEW the New-Backtest and pack
/// editors call to show the numbers a given (timeframe, symbol, volatility) would produce. The store is
/// DI-registered and seeded with the 3 starter packs (PK1); the run wiring (PK3) lives in the orchestrator.
/// Upsert validates the bundle (iter-38 U1) before persisting.
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
        // iter-38 U1: validate the add-on bundle before persisting — an enabled add-on must carry sane numbers
        // (a real trailing method; fractions in (0,1]; non-negative triggers/offsets). Invalid bundles are
        // rejected with 400 + the specific reasons rather than silently stored and surfacing as bad runs later.
        var errors = ValidatePack(id, body);
        if (errors.Count > 0)
            return BadRequest(new { errors });

        await store.UpsertAsync(body with { Id = id }, ct);
        return Ok(new { id });
    }

    private static readonly HashSet<string> TrailingMethods =
        new(StringComparer.OrdinalIgnoreCase) { "StepPips", "AtrMultiple", "Structure", "SteppedR", "BreakevenThenTrail" };

    /// <summary>Validate an add-on pack: each ENABLED add-on must carry usable numbers. Disabled add-ons are
    /// not checked (their stored values are inert). Returns an empty list when the pack is well-formed.</summary>
    internal static IReadOnlyList<string> ValidatePack(string id, AddOnPack? pack)
    {
        var errors = new List<string>();
        if (pack is null) { errors.Add("Pack body is required."); return errors; }
        if (string.IsNullOrWhiteSpace(id)) errors.Add("Pack id is required.");
        if (string.IsNullOrWhiteSpace(pack.Name)) errors.Add("Pack name is required.");

        var o = pack.AddOns;
        if (o is null) { errors.Add("Pack add-ons are required."); return errors; }

        if (o.Trailing.Enabled && !TrailingMethods.Contains(o.Trailing.Method))
        {
            errors.Add($"Trailing is enabled but has no valid method (got '{o.Trailing.Method}'). " +
                       $"Expected one of: {string.Join(", ", TrailingMethods)}.");
        }

        if (o.Breakeven.Enabled && o.Breakeven.TriggerRMultiple < 0)
            errors.Add("Breakeven trigger R-multiple must be >= 0.");

        if (o.PartialTp is { Enabled: true } p)
        {
            if (p.CloseFraction is <= 0 or > 1)
                errors.Add("PartialTp close fraction must be in (0, 1].");
            if (p.TriggerRMultiple < 0)
                errors.Add("PartialTp trigger R-multiple must be >= 0.");
        }

        if (o.Ride is { Enabled: true } r)
        {
            if (r.AdxFloor < 0)
                errors.Add("Ride ADX floor must be >= 0.");
            if (r.RelaxedAtrMultiple <= 0)
                errors.Add("Ride relaxed ATR multiple must be > 0.");
        }

        if (o.DynamicSlTp is { Enabled: true } d)
        {
            if (d.AtrMultipleSl <= 0)
                errors.Add("DynamicSlTp ATR multiple (SL) must be > 0.");
            if (d.RrMultipleTp <= 0)
                errors.Add("DynamicSlTp RR multiple (TP) must be > 0.");
        }

        return errors;
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
