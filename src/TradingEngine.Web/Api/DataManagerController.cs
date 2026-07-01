using Microsoft.AspNetCore.Mvc;
using TradingEngine.Domain;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/data-manager")]
public sealed class DataManagerController : ControllerBase
{
    private readonly IMarketDataStore? _marketDataStore;

    public DataManagerController(IMarketDataStore? marketDataStore = null)
    {
        _marketDataStore = marketDataStore;
    }

    [HttpGet("inventory")]
    public async Task<IActionResult> GetInventory(CancellationToken ct)
    {
        if (_marketDataStore is null)
            return Ok(Array.Empty<object>());

        var inventory = await _marketDataStore.GetInventoryAsync(ct);
        return Ok(inventory.Select(i => new
        {
            symbol = i.Symbol,
            timeframe = i.Timeframe.ToString(),
            source = i.Source,
            firstBar = i.FirstOpenUtc,
            lastBar = i.LastOpenUtc,
            barCount = i.BarCount,
        }));
    }
}
