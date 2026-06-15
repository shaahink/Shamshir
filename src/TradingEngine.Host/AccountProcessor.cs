using System.Globalization;

namespace TradingEngine.Host;

public sealed class AccountProcessor
{
    private readonly IRiskManager _riskManager;
    private readonly PositionTracker _positionTracker;
    private readonly SizingPolicyOptions _sizingPolicy;
    private readonly IEventBus _eventBus;
    private readonly IEngineClock _clock;
    private readonly EngineMode _engineMode;
    private readonly CrossRateStore _crossRateStore;
    private readonly IEquitySink? _equitySink;
    private readonly Action<EquitySnapshot> _setEquity;
    private readonly IDecisionJournal? _decisionJournal;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    private int _lastResetIsoWeek = -1;
    private int _lastResetMonth = -1;
    private int _lastResetDayOfYear = -1;

    public AccountProcessor(
        IRiskManager riskManager,
        PositionTracker positionTracker,
        SizingPolicyOptions sizingPolicy,
        IEventBus eventBus,
        IEngineClock clock,
        EngineMode engineMode,
        CrossRateStore crossRateStore,
        IEquitySink? equitySink,
        Action<EquitySnapshot> setEquity,
        Microsoft.Extensions.Logging.ILogger logger,
        IDecisionJournal? decisionJournal = null)
    {
        _riskManager = riskManager;
        _positionTracker = positionTracker;
        _sizingPolicy = sizingPolicy;
        _eventBus = eventBus;
        _clock = clock;
        _engineMode = engineMode;
        _crossRateStore = crossRateStore;
        _equitySink = equitySink;
        _setEquity = setEquity;
        _decisionJournal = decisionJournal;
        _logger = logger;
    }

    public async Task HandleAsync(AccountUpdate update)
    {
        if (update.Balance > 0)
            _riskManager.InitializeDrawdownIfNeeded(update.Balance);

        _riskManager.UpdateEquityLevels(update.Equity);

        var constraints = _riskManager.Constraints;
        if (constraints is not null && !_riskManager.CurrentState.InProtectionMode)
        {
            var flattenFraction = (decimal)_sizingPolicy.FlattenAtFraction;

            if (_riskManager.CurrentState.DailyDrawdownUsed >= constraints.MaxDailyLoss * flattenFraction)
            {
                _riskManager.EnterProtectionMode(
                    $"Daily DD at {_riskManager.CurrentState.DailyDrawdownUsed:P1} >= {constraints.MaxDailyLoss * flattenFraction:P1} hard limit",
                    ProtectionCause.DailyDrawdown);
                _logger.LogCritical("BREACH_WATCHDOG: Entered protection mode — daily DD");
                _decisionJournal?.Record(new DecisionRecord(
                    string.Empty, update.TimestampUtc, 0, null, null, null,
                    "BreachDetected", "DailyDD",
                    null,
                    $"Daily DD at {_riskManager.CurrentState.DailyDrawdownUsed:P1} >= {constraints.MaxDailyLoss * flattenFraction:P1}",
                    "{}"));
                await _positionTracker.RequestForceCloseAllAsync("DailyDD");
            }
            else if (_riskManager.CurrentState.MaxDrawdownUsed >= constraints.MaxTotalLoss * flattenFraction)
            {
                _riskManager.EnterProtectionMode(
                    $"Max DD at {_riskManager.CurrentState.MaxDrawdownUsed:P1} >= {constraints.MaxTotalLoss * flattenFraction:P1} hard limit",
                    ProtectionCause.MaxDrawdown);
                _logger.LogCritical("BREACH_WATCHDOG: Entered protection mode — max DD");
                _decisionJournal?.Record(new DecisionRecord(
                    string.Empty, update.TimestampUtc, 0, null, null, null,
                    "BreachDetected", "MaxDD",
                    null,
                    $"Max DD at {_riskManager.CurrentState.MaxDrawdownUsed:P1} >= {constraints.MaxTotalLoss * flattenFraction:P1}",
                    "{}"));
                await _positionTracker.RequestForceCloseAllAsync("MaxDD");
            }
        }

        var now = update.TimestampUtc;
        var isoWeek = ISOWeek.GetWeekOfYear(now);
        var month = now.Month;
        var dailyKey = now.Year * 1000 + now.DayOfYear;

        if (dailyKey != _lastResetDayOfYear)
        {
            _lastResetDayOfYear = dailyKey;
            _riskManager.OnDailyReset(update.Equity);
            _ = _eventBus.PublishAsync(new DayRolled(now), CancellationToken.None);
        }
        if (isoWeek != _lastResetIsoWeek)
        {
            _lastResetIsoWeek = isoWeek;
            _riskManager.OnWeeklyReset(update.Equity);
            _ = _eventBus.PublishAsync(new WeekRolled(now), CancellationToken.None);
            _ = _eventBus.PublishAsync(new DayRolled(now), CancellationToken.None);
            _ = _eventBus.PublishAsync(new WeeklyEquitySnapshotTaken(
                new EquitySnapshot(update.TimestampUtc, update.Balance, update.FloatingPnL, update.Equity,
                    _riskManager.Drawdown.PeakEquity, _riskManager.Drawdown.DailyStartEquity,
                    _riskManager.CurrentState.WeeklyDrawdownUsed, _riskManager.CurrentState.MaxDrawdownUsed, _engineMode),
                _riskManager.CurrentState, _clock.UtcNow), CancellationToken.None);
        }
        if (month != _lastResetMonth)
        {
            _lastResetMonth = month;
            _riskManager.OnMonthlyReset(update.Equity);
            _ = _eventBus.PublishAsync(new MonthlyEquitySnapshotTaken(
                new EquitySnapshot(update.TimestampUtc, update.Balance, update.FloatingPnL, update.Equity,
                    _riskManager.Drawdown.PeakEquity, _riskManager.Drawdown.DailyStartEquity,
                    _riskManager.CurrentState.MonthlyDrawdownUsed, _riskManager.CurrentState.MaxDrawdownUsed, _engineMode),
                _riskManager.CurrentState, _clock.UtcNow), CancellationToken.None);
        }

        var riskState = _riskManager.CurrentState;
        var equity = new EquitySnapshot(
            update.TimestampUtc, update.Balance, update.FloatingPnL, update.Equity,
            _riskManager.Drawdown.PeakEquity, _riskManager.Drawdown.DailyStartEquity,
            riskState.DailyDrawdownUsed, riskState.MaxDrawdownUsed, _engineMode);
        _setEquity(equity);

        if (_equitySink is not null)
        {
            _equitySink.Observe(new AccountSnapshot(
                update.TimestampUtc, update.Balance, update.Equity, update.FloatingPnL,
                _riskManager.Drawdown.PeakEquity, _riskManager.Drawdown.DailyStartEquity,
                riskState.DailyDrawdownUsed, riskState.MaxDrawdownUsed,
                _positionTracker.OpenPositions.Count));
        }
        _ = _eventBus.PublishAsync(new EquityUpdated(equity, riskState, _clock.UtcNow), CancellationToken.None);
        _logger.LogInformation("ACCOUNT|balance={Balance:F2}|equity={Equity:F2}|dd={DD:P1}",
            update.Balance, update.Equity, riskState.DailyDrawdownUsed);
    }
}
