using Microsoft.Extensions.Logging;

namespace TradingEngine.Domain;

public sealed record EngineHostOptions
{
    public required string RunId { get; init; }
    public required EngineMode Mode { get; init; }
    public required Func<IServiceProvider, IBrokerAdapter> AdapterFactory { get; init; }
    public required string DbPath { get; init; }
    public required string SolutionRoot { get; init; }
    public IReadOnlyList<string> SymbolNames { get; init; } = [];

    /// <summary>
    /// Strategy IDs selected for this run. Empty = all configured strategies (the default).
    /// When non-empty, only configured strategies whose ID is in this set are instantiated, so a
    /// backtest honours the strategy picker on the New-Backtest page instead of always running all.
    /// </summary>
    public IReadOnlyList<string> ActiveStrategyIds { get; init; } = [];

    public IProgress<BacktestProgressEvent>? Progress { get; init; }
    public LogLevel MinLogLevel { get; init; } = LogLevel.Information;
    public LoadedConfig? PreloadedConfig { get; init; }
}
