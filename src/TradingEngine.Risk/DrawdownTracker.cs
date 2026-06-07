namespace TradingEngine.Risk;

public sealed class DrawdownTracker
{
    public decimal InitialAccountBalance { get; private set; }
    public decimal PeakEquity { get; private set; }
    public decimal DailyStartEquity { get; private set; }
    public decimal CurrentDailyDrawdown { get; private set; }
    public decimal CurrentMaxDrawdown { get; private set; }
    public string DrawdownType { get; private set; } = "Fixed";

    private bool _initialized;

    public void Initialize(decimal initialBalance, string drawdownType = "Fixed")
    {
        if (_initialized) return;
        InitialAccountBalance = initialBalance;
        PeakEquity = initialBalance;
        DailyStartEquity = initialBalance;
        DrawdownType = drawdownType;
        _initialized = true;
    }

    public void InitializeIfNeeded(decimal initialBalance, string drawdownType = "Fixed")
    {
        if (_initialized) return;
        Initialize(initialBalance, drawdownType);
    }

    public DailyDdBase DailyDdBaseMode { get; set; } = DailyDdBase.InitialBalance;

    public void OnEquityUpdate(decimal equity)
    {
        if (!_initialized) return;

        if (equity > PeakEquity)
            PeakEquity = equity;

        CurrentDailyDrawdown = DailyDdBaseMode switch
        {
            DailyDdBase.DailyStart => DailyStartEquity > 0
                ? Math.Max(0m, (DailyStartEquity - equity) / DailyStartEquity)
                : 0m,
            _ => InitialAccountBalance > 0
                ? Math.Max(0m, (InitialAccountBalance - equity) / InitialAccountBalance)
                : 0m,
        };

        var equityBase = DrawdownType == "Trailing" ? PeakEquity : InitialAccountBalance;
        CurrentMaxDrawdown = equityBase > 0
            ? Math.Max(0m, (equityBase - equity) / equityBase)
            : 0m;
    }

    public void OnDailyReset(decimal currentEquity)
    {
        DailyStartEquity = currentEquity;
    }

    public decimal GetMaxDrawdownFloor(decimal maxTotalLossPercent) =>
        DrawdownType == "Trailing"
            ? PeakEquity * (1m - (decimal)maxTotalLossPercent)
            : InitialAccountBalance * (1m - (decimal)maxTotalLossPercent);

    public decimal GetDailyLossLimit(decimal maxDailyLossPercent) =>
        InitialAccountBalance * (1m - (decimal)maxDailyLossPercent);
}
