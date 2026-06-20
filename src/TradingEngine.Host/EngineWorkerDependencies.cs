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
    public required IRiskProfileResolver RiskProfileResolver { get; init; }
    public required Func<string, string, decimal> CrossRateProvider { get; init; }
    public ITradingGovernor? Governor { get; init; }
    public SizingPolicyOptions SizingPolicy { get; init; } = new();
    // iter-36 K4: the kernel evaluator computes the news/weekend external verdicts at sim-time, so the
    // production engine needs these wired through (they were only reachable via KernelOrderGate's own DI).
    public required TradingEngine.Risk.Filters.INewsFilter NewsFilter { get; init; }
    public required TradingEngine.Risk.Filters.SessionFilter SessionFilter { get; init; }
}

public sealed record StrategyServices
{
    public required IEnumerable<IStrategy> Strategies { get; init; }
    public required IStrategyBank StrategyBank { get; init; }
    public required IRegimeDetector RegimeDetector { get; init; }
    public required PositionTracker PositionTracker { get; init; }
    public required TradingEngine.Services.EntryPlanner EntryPlanner { get; init; }
    // iter-36 K4 gap-3: the kernel loop evaluates trailing/breakeven per bar via this manager and emits
    // ModifyStopLoss effects (the imperative TradingLoop.UpdateTrailingStopsAsync used it the same way).
    public required IPositionManager PositionManager { get; init; }
    public ISignalGate? SignalGate { get; init; }
}

public sealed record PersistenceServices
{
    public required IEventBus EventBus { get; init; }
    public required PersistenceService Persistence { get; init; }
    public EffectExecutor? EffectExecutor { get; init; }
    public IEquitySink? EquitySink { get; init; }
    public IProgress<BacktestProgressEvent>? Progress { get; init; }
    public IPipelineJournal? Journal { get; init; }
    // iter-36 K5: the single lossless StepRecord journal the kernel engine appends to.
    public IJournalWriter? StepJournal { get; init; }
    // iter-37 K-GAP-2: scope factory used to resolve the (scoped) IEquityRepository for the on-completion
    // backtest equity flush (BufferedEquitySink → EquitySnapshots).
    public Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? ScopeFactory { get; init; }
}
