namespace TradingEngine.Services.Strategy;

public sealed class ComposedStrategy : IStrategy
{
    private readonly ISignalProvider _signal;
    private readonly IExitBehavior _exit;
    private readonly IReadOnlyList<IEntryFilter> _filters;

    public string Id { get; }
    public string DisplayName { get; }
    public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1];
    public int RequiredBarCount => _signal.RequiredBarCount;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators => _signal.RequiredIndicators;
    public IReadOnlyList<IPositionBehavior> PositionBehaviors { get; }
    public IStrategyConfig Config { get; }
    public StrategyStats Stats { get; private set; } = new(0, 0, 0, 0);

    public ComposedStrategy(
        string id,
        string displayName,
        ISignalProvider signal,
        IExitBehavior exit,
        IEnumerable<IEntryFilter>? filters = null,
        IEnumerable<IPositionBehavior>? behaviors = null,
        ReentryOptions? reentry = null)
    {
        Id = id;
        DisplayName = displayName;
        _signal = signal;
        _exit = exit;
        _filters = filters?.ToList() ?? [];
        PositionBehaviors = behaviors?.ToList() ?? [];
        Config = new ComposedStrategyConfig(id, displayName, reentry);
    }

    public TradeIntent? Evaluate(MarketContext context)
    {
        try
        {
            var h1Bars = context.Bars.GetValueOrDefault(Timeframe.H1);
            if (h1Bars is null || h1Bars.Count < RequiredBarCount)
                return null;

            foreach (var filter in _filters)
            {
                if (!filter.Allows(context))
                    return null;
            }

            var signal = _signal.Evaluate(context);
            if (signal is null)
                return null;

            var (direction, reason) = signal.Value;
            var symbolInfo = new SymbolInfo(context.Symbol, SymbolCategory.Forex, "", "", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

            var sl = _exit.ComputeStopLoss(new Price(context.LatestTick.Mid), direction, context, symbolInfo);
            var tp = _exit.ComputeTakeProfit(new Price(context.LatestTick.Mid), sl, direction, context, symbolInfo);

            return new TradeIntent(
                context.Symbol, direction, OrderType.Market, null, sl, tp,
                Id, "standard", reason, context.EngineTimeUtc);
        }
        catch
        {
            return null;
        }
    }

    public void OnTradeResult(TradeResult result)
    {
        var wins = Stats.ConsecutiveWins;
        var losses = Stats.ConsecutiveLosses;
        if (result.NetPnL.Amount > 0)
        {
            wins++;
            losses = 0;
        }
        else
        {
            losses++;
            wins = 0;
        }
        Stats = new StrategyStats(wins, losses, Stats.WinRateLast20, Stats.AvgRLast20);
    }

    public void Reset()
    {
        Stats = new StrategyStats(0, 0, 0, 0);
    }
}

internal sealed record ComposedStrategyConfig(string Id, string DisplayName, ReentryOptions? ReentryOverride = null) : IStrategyConfig
{
    public bool Enabled => true;
    public IReadOnlyList<string> Symbols => [];
    public string RiskProfileId => "standard";
    public Timeframe Timeframe => Timeframe.H1;
    public RegimeFilterOptions RegimeFilter => new();
    public OrderEntryOptions OrderEntry => new();
    public PositionManagementOptions PositionManagement => new();
    public ReentryOptions Reentry => ReentryOverride ?? new();
}
