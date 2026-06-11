namespace TradingEngine.Risk;

public sealed class DrawdownTracker
{
    public decimal InitialAccountBalance { get; private set; }
    public decimal PeakEquity { get; private set; }
    public decimal DailyStartEquity { get; private set; }
    public decimal CurrentDailyDrawdown { get; private set; }
    public decimal CurrentMaxDrawdown { get; private set; }
    public decimal WeeklyStartEquity { get; private set; }
    public decimal MonthlyStartEquity { get; private set; }
    public decimal CurrentWeeklyDrawdown { get; private set; }
    public decimal CurrentMonthlyDrawdown { get; private set; }
    public decimal DrawdownVelocity { get; private set; }
    public bool IsAccelerating => DrawdownVelocity > 0.001m;
    public string DrawdownType { get; private set; } = "Fixed";

    private readonly Queue<decimal> _velocityWindow = new();

    private bool _initialized;

    public void Initialize(decimal initialBalance, string drawdownType = "Fixed")
    {
        if (_initialized) return;
        InitialAccountBalance = initialBalance;
        PeakEquity = initialBalance;
        DailyStartEquity = initialBalance;
        WeeklyStartEquity = initialBalance;
        MonthlyStartEquity = initialBalance;
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

        CurrentWeeklyDrawdown = WeeklyStartEquity > 0
            ? Math.Max(0m, (WeeklyStartEquity - equity) / WeeklyStartEquity)
            : 0m;

        CurrentMonthlyDrawdown = MonthlyStartEquity > 0
            ? Math.Max(0m, (MonthlyStartEquity - equity) / MonthlyStartEquity)
            : 0m;

        var equityBase = DrawdownType == "Trailing" ? PeakEquity : InitialAccountBalance;
        CurrentMaxDrawdown = equityBase > 0
            ? Math.Max(0m, (equityBase - equity) / equityBase)
            : 0m;
    }

    public void OnDailyReset(decimal currentEquity)
    {
        DailyStartEquity = currentEquity;
        _velocityWindow.Enqueue(CurrentMaxDrawdown);
        while (_velocityWindow.Count > 5)
            _velocityWindow.Dequeue();

        if (_velocityWindow.Count >= 2)
        {
            var values = _velocityWindow.ToArray();
            double sum = 0;
            for (int i = 1; i < values.Length; i++)
                sum += (double)(values[i] - values[i - 1]);
            DrawdownVelocity = (decimal)(sum / (values.Length - 1));
        }
    }

    public void OnWeeklyReset(decimal currentEquity)
    {
        WeeklyStartEquity = currentEquity;
    }

    public void OnMonthlyReset(decimal currentEquity)
    {
        MonthlyStartEquity = currentEquity;
    }

    public decimal GetMaxDrawdownFloor(decimal maxTotalLossPercent) =>
        DrawdownType == "Trailing"
            ? PeakEquity * (1m - (decimal)maxTotalLossPercent)
            : InitialAccountBalance * (1m - (decimal)maxTotalLossPercent);

    public decimal GetDailyLossLimit(decimal maxDailyLossPercent) =>
        InitialAccountBalance * (1m - (decimal)maxDailyLossPercent);
}
