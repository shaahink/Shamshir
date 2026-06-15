namespace TradingEngine.Host;

/// <summary>
/// Thin hosted shell. All engine run logic lives in <see cref="EngineRunner"/> (no hosting
/// dependency, directly testable); this type exists only to plug that logic into the generic-host
/// lifecycle as a <c>BackgroundService</c>.
/// </summary>
public sealed class EngineWorker(
    EngineWorkerDependencies deps,
    EngineRunContext runContext,
    ILogger<EngineWorker> logger) : BackgroundService
{
    private readonly EngineRunner _runner = new(deps, runContext, logger);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _runner.RunAsync(stoppingToken);
}
