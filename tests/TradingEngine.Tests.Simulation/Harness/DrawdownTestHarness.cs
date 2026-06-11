namespace TradingEngine.Tests.Simulation.Harness;

public sealed class DrawdownTestHarness
{
    private readonly RiskManager _riskManager;
    private readonly DrawdownTracker _tracker;
    private decimal _currentBalance;

    private static readonly PropFirmRuleSet FtmoRules = new(
        "ftmo-standard", "FTMO Standard", "Fixed", 0.05, 0.10, 0.10, 4,
        "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
        false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false);

    private static readonly RiskProfile Profile = new(
        "standard", "Standard", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 5, false, "ftmo-standard");

    public DrawdownTestHarness(decimal initialBalance = 100_000)
    {
        _currentBalance = initialBalance;

        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));

        _tracker = new DrawdownTracker();
        _tracker.Initialize(initialBalance);

        _riskManager = new RiskManager(_tracker, registry, (_, _) => 1m,
            new NewsFilter(), new SessionFilter(), new StubClock(DateTime.UtcNow),
            Substitute.For<ICurrencyExposureTracker>());
        _riskManager.SetActiveRuleSet(FtmoRules);
    }

    public decimal DailyDdFraction => _riskManager.CurrentState.DailyDrawdownUsed;
    public decimal MaxDdFraction => _riskManager.CurrentState.MaxDrawdownUsed;
    public bool IsInProtectionMode => _riskManager.CurrentState.InProtectionMode;
    public int TradesAfterDailyLimitBreached { get; private set; }
    public int TradesAfterMaxDdBreached { get; private set; }

    public void InjectLoss(decimal amountUsd, string strategyId = "default")
    {
        _currentBalance -= amountUsd;

        var beforeState = _riskManager.CurrentState;
        _riskManager.UpdateEquityLevels(_currentBalance);

        var afterState = _riskManager.CurrentState;

        if (!beforeState.InProtectionMode && afterState.InProtectionMode)
            return;

        if (afterState.MaxDrawdownUsed >= (decimal)FtmoRules.MaxTotalLossPercent)
        {
            _riskManager.EnterProtectionMode("Max DD breach", ProtectionCause.MaxDrawdown);
            TradesAfterMaxDdBreached++;
        }
        else if (afterState.DailyDrawdownUsed >= (decimal)FtmoRules.MaxDailyLossPercent)
        {
            _riskManager.EnterProtectionMode("Daily DD breach", ProtectionCause.DailyDrawdown);
            TradesAfterDailyLimitBreached++;
        }
    }

    public void InjectWin(decimal amountUsd)
    {
        _currentBalance += amountUsd;
        _riskManager.UpdateEquityLevels(_currentBalance);
    }

    public void SimulateDailyReset()
    {
        _riskManager.OnDailyReset(_currentBalance);
    }
}
