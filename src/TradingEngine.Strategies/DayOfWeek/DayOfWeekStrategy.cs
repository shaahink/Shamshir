using Microsoft.Extensions.DependencyInjection;

namespace TradingEngine.Strategies.DayOfWeek;

/// <summary>
/// V4 session family — a weekday directional-bias strategy and the lowest-frequency member. At the exact
/// entry hour (00:00 UTC) on each pre-registered weekday it opens a single position in the fixed direction
/// and holds it to the end-of-day flatten (23:00 UTC). Purely clock/calendar-keyed — no indicator drives
/// the entry (ATR only sizes the SL/TP). This is net-new: no day-of-week gate existed in the codebase.
/// </summary>
[StrategyId("day-of-week")]
public sealed class DayOfWeekStrategy : IStrategy
{
    private readonly DayOfWeekConfig _config;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly ILogger<DayOfWeekStrategy> _logger;
    private readonly HashSet<System.DayOfWeek> _weekdays;
    private readonly TradeDirection _direction;
    private DateOnly? _lastEntryDay;

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public Timeframe EntryTimeframe => _config.EntryTimeframe;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.EntryTimeframe];
    public int RequiredBarCount => _config.Parameters.AtrPeriod + 5;
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats { get; private set; } = new(0, 0, 0, 0);

    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod, Timeframe: _config.EntryTimeframe),
    ];

    public DayOfWeekStrategy(DayOfWeekConfig config, ISymbolInfoRegistry symbolRegistry, ILogger<DayOfWeekStrategy> logger)
    {
        _config = config;
        _symbolRegistry = symbolRegistry;
        _logger = logger;

        _weekdays = [];
        foreach (var name in config.Parameters.Weekdays)
        {
            if (Enum.TryParse<System.DayOfWeek>(name, ignoreCase: true, out var day))
                _weekdays.Add(day);
        }

        _direction = Enum.TryParse<TradeDirection>(config.Parameters.Direction, ignoreCase: true, out var dir)
            ? dir
            : TradeDirection.Long;
    }

    public TradeIntent? Evaluate(MarketContext context)
    {
        try
        {
            var bars = context.Bars.GetValueOrDefault(_config.EntryTimeframe);
            if (bars is null || bars.Count < RequiredBarCount)
            {
                return null;
            }

            var p = _config.Parameters;
            var now = TimeOnly.FromDateTime(context.EngineTimeUtc);

            // Fire only on the exact entry-hour bar of a listed weekday — one entry per day.
            if (now != p.EntryHourUtc)
            {
                return null;
            }
            if (!_weekdays.Contains(context.EngineTimeUtc.DayOfWeek))
            {
                return null;
            }

            var today = DateOnly.FromDateTime(context.EngineTimeUtc);
            if (_lastEntryDay == today)
            {
                return null;
            }

            var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");
            if (atr <= 0)
            {
                return null;
            }

            var price = context.LatestTick.Mid;
            var resolver = new SlTpResolver();
            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var (sl, tp) = resolver.Resolve(new Price(price), _direction, atr, symbolInfo, _config.PositionManagement);

            _lastEntryDay = today;

            return new TradeIntent(context.Symbol, _direction, OrderType.Market, null, sl, tp,
                Id, _config.RiskProfileId,
                $"Day-of-week entry: {context.EngineTimeUtc.DayOfWeek} {_direction}",
                context.EngineTimeUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DayOfWeekStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
            return null;
        }
    }

    public void OnTradeResult(TradeResult result)
    {
        var w = Stats.ConsecutiveWins;
        var l = Stats.ConsecutiveLosses;
        if (result.NetPnL.Amount > 0)
        {
            w++;
            l = 0;
        }
        else
        {
            l++;
            w = 0;
        }
        Stats = new StrategyStats(w, l, Stats.WinRateLast20, Stats.AvgRLast20);
    }

    public void Reset()
    {
        _lastEntryDay = null;
        Stats = new StrategyStats(0, 0, 0, 0);
    }

    public static DayOfWeekStrategy Create(StrategyConfigEntry entry, IServiceProvider sp)
    {
        var config = new DayOfWeekConfig(
            entry.Id, entry.DisplayName, entry.Enabled,
            entry.RiskProfileId,
            StrategyFactoryHelper.DeserializeParams<DayOfWeekParameters>(entry.Parameters))
        {
            RegimeFilter = entry.RegimeFilter ?? new(),
            OrderEntry = entry.OrderEntry ?? new(),
            PositionManagement = entry.PositionManagement ?? new(),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.M15,
            Symbol = entry.Symbol,
        };
        return new DayOfWeekStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<DayOfWeekStrategy>>());
    }
}
