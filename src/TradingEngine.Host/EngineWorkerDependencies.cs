namespace TradingEngine.Host;

public sealed record EngineWorkerDependencies
{
    public required MarketServices Market { get; init; }
    public required RiskServices Risk { get; init; }
    public required StrategyServices Strategies { get; init; }
    public required PersistenceServices Persistence { get; init; }
}

public sealed record MarketServices
{
    public required IBrokerAdapter Broker { get; init; }
    public required IIndicatorService Indicators { get; init; }
    public required ISymbolInfoRegistry SymbolRegistry { get; init; }
    public required CrossRateStore CrossRateStore { get; init; }
    public required IEngineClock Clock { get; init; }
    public required EngineMode EngineMode { get; init; }
    public DataFeedService? DataFeed { get; init; }
}

public sealed record RiskServices
{
    public required IRiskManager RiskManager { get; init; }
    public required DrawdownTracker DrawdownTracker { get; init; }
    public required IRiskProfileResolver RiskProfileResolver { get; init; }
    public required Func<string, string, decimal> CrossRateProvider { get; init; }
    public ITradingGovernor? Governor { get; init; }
    public SizingPolicyOptions SizingPolicy { get; init; } = new();
}

public sealed record StrategyServices
{
    public required IEnumerable<IStrategy> Strategies { get; init; }
    public required IStrategyBank StrategyBank { get; init; }
    public required IRegimeDetector RegimeDetector { get; init; }
    public required OrderDispatcher OrderDispatcher { get; init; }
    public required PositionTracker PositionTracker { get; init; }
    public ISignalGate? SignalGate { get; init; }
}

public sealed record PersistenceServices
{
    public required IEventBus EventBus { get; init; }
    public required PersistenceService Persistence { get; init; }
    public IEquitySink? EquitySink { get; init; }
    public IProgress<BacktestProgressEvent>? Progress { get; init; }
    public IPipelineJournal? Journal { get; init; }
}
