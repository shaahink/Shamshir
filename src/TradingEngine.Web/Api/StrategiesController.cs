using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Web.Api;

/// <summary>
/// Strategies are canonical in the database (<see cref="IStrategyConfigStore"/>), seeded from
/// <c>config/strategies/*.json</c> at startup. This controller reads/writes that store directly — it no
/// longer depends on the in-memory <see cref="StrategyRegistry"/> cache (which is only populated lazily
/// during a run and left the Strategies page + New-Backtest picker empty). Per-strategy performance
/// stats are aggregated from the Trades table so the cards mean something across runs.
/// </summary>
[ApiController]
[Route("api/strategies")]
public class StrategiesController : ControllerBase
{
    private readonly IStrategyConfigStore _store;
    private readonly TradingDbContext _db;
    private readonly IStrategyBank _bank;
    private readonly ILogger<StrategiesController> _logger;

    public StrategiesController(
        IStrategyConfigStore store,
        TradingDbContext db,
        IStrategyBank bank,
        ILogger<StrategiesController> logger)
    {
        _store = store;
        _db = db;
        _bank = bank;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var configs = await _store.GetAllAsync(ct);
        var stats = await ComputeStatsAsync(ct);

        var strategies = configs.Select(c => new
        {
            id = c.Id,
            displayName = c.DisplayName,
            isEnabled = c.Enabled,
            timeframe = c.Timeframe,
            symbols = c.Symbols,
            riskProfileId = c.RiskProfileId,
            stats = stats.GetValueOrDefault(c.Id) ?? EmptyStats(),
        }).ToList();

        return Ok(new { strategies });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var c = (await _store.GetAllAsync(ct)).FirstOrDefault(s => s.Id == id);
        if (c is null) return NotFound(new { error = $"Strategy {id} not found" });

        return Ok(new
        {
            id = c.Id,
            displayName = c.DisplayName,
            isEnabled = c.Enabled,
            enabled = c.Enabled,
            timeframe = c.Timeframe,
            symbols = c.Symbols,
            riskProfileId = c.RiskProfileId,
            parametersJson = RawText(c.Parameters),
            positionManagementJson = Serialize(c.PositionManagement),
            orderEntryJson = Serialize(c.OrderEntry),
            regimeFilterJson = Serialize(c.RegimeFilter),
            reentryJson = Serialize(c.Reentry),
        });
    }

    [HttpPut("{id}/enable")]
    public async Task<IActionResult> Enable(string id, CancellationToken ct)
    {
        await SetEnabledAsync(id, true, ct);
        _bank.Enable(id);
        _logger.LogInformation("Strategy {StrategyId} enabled", id);
        return Ok(new { id, enabled = true });
    }

    [HttpPut("{id}/disable")]
    public async Task<IActionResult> Disable(string id, CancellationToken ct)
    {
        await SetEnabledAsync(id, false, ct);
        _bank.Disable(id);
        _logger.LogInformation("Strategy {StrategyId} disabled", id);
        return Ok(new { id, enabled = false });
    }

    [HttpPut("{id}/config")]
    public async Task<IActionResult> UpdateConfig(string id, CancellationToken ct)
    {
        var existing = (await _store.GetAllAsync(ct)).FirstOrDefault(s => s.Id == id);
        if (existing is null) return NotFound(new { error = $"Strategy {id} not found" });

        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(ct);

        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException ex) { return BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }

        // Patch the stored entry from whatever fields the body carries; unknown/missing fields keep
        // their current values. `parameters` is stored as a raw JSON element.
        var updated = existing with
        {
            DisplayName = GetString(root, "displayName") ?? existing.DisplayName,
            Timeframe = GetString(root, "timeframe") ?? existing.Timeframe,
            RiskProfileId = GetString(root, "riskProfileId") ?? existing.RiskProfileId,
            Symbols = root.TryGetProperty("symbols", out var sy) && sy.ValueKind == JsonValueKind.Array
                ? sy.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : existing.Symbols,
            Parameters = root.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object
                ? p.Clone()
                : existing.Parameters,
        };
        updated = updated with
        {
            Enabled = root.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? en.GetBoolean() : existing.Enabled,
            RegimeFilter = Deserialize<RegimeFilterOptions>(root, "regimeFilter") ?? existing.RegimeFilter,
            OrderEntry = Deserialize<OrderEntryOptions>(root, "orderEntry") ?? existing.OrderEntry,
            PositionManagement = Deserialize<PositionManagementOptions>(root, "positionManagement") ?? existing.PositionManagement,
            Reentry = Deserialize<ReentryOptions>(root, "reentry") ?? existing.Reentry,
        };

        await _store.UpsertAsync(updated, ct);
        _logger.LogInformation("Strategy {StrategyId} config updated", id);
        return Ok(new { id, saved = true });
    }

    private async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        var existing = (await _store.GetAllAsync(ct)).FirstOrDefault(s => s.Id == id);
        if (existing is null) return;
        await _store.UpsertAsync(existing with { Enabled = enabled }, ct);
    }

    private async Task<Dictionary<string, object>> ComputeStatsAsync(CancellationToken ct)
    {
        var rows = await _db.Trades
            .GroupBy(t => t.StrategyId)
            .Select(g => new
            {
                StrategyId = g.Key,
                TotalTrades = g.Count(),
                WinningTrades = g.Count(t => t.NetPnLAmount > 0),
                TotalPnL = g.Sum(t => t.NetPnLAmount),
                GrossWin = g.Where(t => t.GrossPnLAmount > 0).Sum(t => t.GrossPnLAmount),
                GrossLoss = g.Where(t => t.GrossPnLAmount < 0).Sum(t => t.GrossPnLAmount),
            })
            .ToListAsync(ct);

        var map = new Dictionary<string, object>();
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.StrategyId)) continue;
            var loss = Math.Abs(r.GrossLoss);
            map[r.StrategyId] = new
            {
                totalTrades = r.TotalTrades,
                winningTrades = r.WinningTrades,
                totalPnL = r.TotalPnL,
                profitFactor = loss > 0 ? r.GrossWin / loss : (r.GrossWin > 0 ? 999 : 0),
                winStreak = 0,
                lossStreak = 0,
                lastRegime = 0,
                winRate = r.TotalTrades > 0 ? (double)r.WinningTrades / r.TotalTrades : 0,
            };
        }
        return map;
    }

    private static object EmptyStats() => new
    {
        totalTrades = 0, winningTrades = 0, totalPnL = 0m, profitFactor = 0.0,
        winStreak = 0, lossStreak = 0, lastRegime = 0, winRate = 0.0,
    };

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string RawText(JsonElement e) =>
        e.ValueKind == JsonValueKind.Undefined ? "{}" : e.GetRawText();

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value);

    private static T? Deserialize<T>(JsonElement root, string name) where T : class =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<T>(e.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            })
            : null;
}
