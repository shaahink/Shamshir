using Microsoft.AspNetCore.Mvc;
using TradingEngine.CTraderRunner;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/research/block-bootstrap")]
public sealed class BlockBootstrapController : ControllerBase
{
    private readonly IMarketDataStore? _marketDataStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEngineClock _clock;
    private readonly ILogger<BlockBootstrapController> _logger;

    public BlockBootstrapController(
        IMarketDataStore? marketDataStore,
        IServiceScopeFactory scopeFactory,
        IEngineClock clock,
        ILogger<BlockBootstrapController> logger)
    {
        _marketDataStore = marketDataStore;
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Generate([FromBody] BlockBootstrapRequest req, CancellationToken ct)
    {
        if (_marketDataStore is null)
            return Problem("Market data store not available.");

        if (string.IsNullOrWhiteSpace(req.Symbol))
            return BadRequest(new { error = "Symbol is required." });

        if (string.IsNullOrWhiteSpace(req.Timeframe))
            return BadRequest(new { error = "Timeframe is required." });

        if (!Enum.TryParse<Timeframe>(req.Timeframe, ignoreCase: true, out var tf))
            return BadRequest(new { error = $"Unknown timeframe: {req.Timeframe}" });

        var symbol = Symbol.Parse(req.Symbol);
        var from = req.From ?? _clock.UtcNow.AddYears(-1);
        var to = req.To ?? _clock.UtcNow;
        var tapeCount = Math.Clamp(req.N, 1, 200);
        var blockSize = ParseBlockSize(req.BlockSize ?? "week");
        var seed = req.Seed ?? Environment.TickCount;

        var bootstrapper = new BlockBootstrapper(_marketDataStore);
        var tapes = await bootstrapper.GenerateAsync(symbol, tf, from, to, blockSize, tapeCount, seed, ct);

        if (tapes.Count == 0)
            return Ok(new { error = "No bars available for the requested range.", runIds = Array.Empty<string>() });

        var runIds = new List<string>(tapeCount);
        var syntheticStart = tapes[0].Count > 0 ? tapes[0][0].OpenTimeUtc : _clock.UtcNow;
        var syntheticEnd = tapes[0].Count > 0 ? tapes[0][^1].OpenTimeUtc : _clock.UtcNow;

        for (var i = 0; i < tapes.Count; i++)
        {
            var source = $"bootstrap-{Guid.NewGuid():N}";
            await _marketDataStore.WriteBarsAsync(source, tapes[i], ct);
            _logger.LogInformation("Block bootstrap tape {Index}/{Total} written: {Bars} bars source={Source}",
                i + 1, tapes.Count, tapes[i].Count, source);

            using var scope = _scopeFactory.CreateScope();
            var command = scope.ServiceProvider.GetRequiredService<IBacktestCommandService>();

            var config = new BacktestConfig
            {
                Symbol = req.Symbol,
                Period = req.Timeframe,
                Start = syntheticStart,
                End = syntheticEnd,
                Balance = req.Balance ?? 100_000,
                CommissionPerMillion = req.CommissionPerMillion ?? 30,
                SpreadPips = req.SpreadPips ?? 1,
                Symbols = [req.Symbol],
                Periods = [req.Timeframe],
                CustomParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Venue"] = "tape",
                    ["DataSource"] = source,
                    ["BlockBootstrapTape"] = i.ToString(),
                    ["BlockBootstrapSource"] = source,
                },
            };

            if (req.StrategyIds is { Length: > 0 })
                config.CustomParams["StrategyIds"] = string.Join(",", req.StrategyIds);

            try
            {
                var runId = await command.StartAsync(config, ct);
                if (!string.IsNullOrWhiteSpace(runId))
                {
                    runIds.Add(runId);
                    _logger.LogInformation("Block bootstrap run started: {RunId} tape {Index}",
                        runId, i);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Block bootstrap failed to start run for tape {Index}", i);
            }
        }

        return Ok(new
        {
            tapeCount = tapes.Count,
            runIds,
            syntheticStart,
            syntheticEnd,
            seed,
        });
    }

    private static TimeSpan ParseBlockSize(string blockSize) => blockSize.ToLowerInvariant() switch
    {
        "day" => TimeSpan.FromDays(1),
        "week" => TimeSpan.FromDays(7),
        "month" => TimeSpan.FromDays(30),
        "quarter" => TimeSpan.FromDays(90),
        _ => TimeSpan.FromDays(7),
    };
}

public sealed record BlockBootstrapRequest
{
    public string? Symbol { get; init; }
    public string? Timeframe { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? BlockSize { get; init; }
    public int N { get; init; } = 100;
    public int? Seed { get; init; }
    public decimal? Balance { get; init; }
    public double? CommissionPerMillion { get; init; }
    public double? SpreadPips { get; init; }
    public string[]? StrategyIds { get; init; }
}
