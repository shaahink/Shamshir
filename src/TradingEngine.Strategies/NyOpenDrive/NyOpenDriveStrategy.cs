using Microsoft.Extensions.DependencyInjection;

namespace TradingEngine.Strategies.NyOpenDrive;

/// <summary>
/// V4 session family — momentum continuation after the New York open. NOT an opening-range breakout: at
/// the first entry-TF bar inside the 13:30–15:00 UTC signal window each day, it reads the sign of that
/// bar's close-minus-open (the opening drive) and enters with it (long on an up-drive, short on a
/// down-drive) — once per day. Flattened at 20:00 UTC by the loop-level time-flatten. The <c>fade</c> mode
/// inverts the direction; the pre-registered census runs <c>drive</c>.
/// </summary>
[StrategyId("ny-open-drive")]
public sealed class NyOpenDriveStrategy : IStrategy
{
    private readonly NyOpenDriveConfig _config;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly ILogger<NyOpenDriveStrategy> _logger;
    private readonly bool _fade;
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

    public NyOpenDriveStrategy(NyOpenDriveConfig config, ISymbolInfoRegistry symbolRegistry, ILogger<NyOpenDriveStrategy> logger)
    {
        _config = config;
        _symbolRegistry = symbolRegistry;
        _logger = logger;
        _fade = string.Equals(config.Parameters.Mode, "fade", StringComparison.OrdinalIgnoreCase);
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
            if (now < p.SignalStartUtc || now >= p.SignalEndUtc)
            {
                return null;
            }

            // One entry per day: the drive is taken from the FIRST in-window bar with a valid ATR.
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

            var driveBar = bars[^1];
            var driveUp = driveBar.Close >= driveBar.Open;   // sign of the opening drive
            var baseDir = driveUp ? TradeDirection.Long : TradeDirection.Short;
            var dir = _fade ? Invert(baseDir) : baseDir;

            var price = context.LatestTick.Mid;
            var resolver = new SlTpResolver();
            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var (sl, tp) = resolver.Resolve(new Price(price), dir, atr, symbolInfo, _config.PositionManagement);

            _lastEntryDay = today;

            return new TradeIntent(context.Symbol, dir, OrderType.Market, null, sl, tp,
                Id, _config.RiskProfileId,
                $"NY open drive ({(_fade ? "fade" : "drive")}): bar={(driveUp ? "up" : "down")}",
                context.EngineTimeUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NyOpenDriveStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
            return null;
        }
    }

    private static TradeDirection Invert(TradeDirection dir) =>
        dir == TradeDirection.Long ? TradeDirection.Short : TradeDirection.Long;

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

    public static NyOpenDriveStrategy Create(StrategyConfigEntry entry, IServiceProvider sp)
    {
        var config = new NyOpenDriveConfig(
            entry.Id, entry.DisplayName, entry.Enabled,
            entry.RiskProfileId,
            StrategyFactoryHelper.DeserializeParams<NyOpenDriveParameters>(entry.Parameters))
        {
            RegimeFilter = entry.RegimeFilter ?? new(),
            OrderEntry = entry.OrderEntry ?? new(),
            PositionManagement = entry.PositionManagement ?? new(),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.M15,
            Symbol = entry.Symbol,
        };
        return new NyOpenDriveStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<NyOpenDriveStrategy>>());
    }
}
