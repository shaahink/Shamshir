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
    public IProgress<BacktestProgressEvent>? Progress { get; init; }
    public LogLevel MinLogLevel { get; init; } = LogLevel.Information;
    public LoadedConfig? PreloadedConfig { get; init; }
}
