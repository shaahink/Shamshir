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

    /// <summary>
    /// Overrides the strategy's built-in symbols/timeframe. When provided,
    /// <see cref="IStrategyBank.GetActive"/> filters strategies by whether the
    /// run plan includes the (strategyId, symbol, timeframe) combination.
    /// When null, falls back to the stored config's symbols/timeframe.
    /// </summary>
    public RunPlan? RunPlan { get; init; }

    public IProgress<BacktestProgressEvent>? Progress { get; init; }
    public LogLevel MinLogLevel { get; init; } = LogLevel.Information;
    public LoadedConfig? PreloadedConfig { get; init; }
    public bool DiagnosticsEnabled { get; init; }

    public IRunDataCache? RunDataCache { get; init; }

    public bool SkipJournal { get; init; }
}
