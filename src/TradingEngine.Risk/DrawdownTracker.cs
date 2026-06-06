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
        if (_initialized)
            return;

        InitialAccountBalance = initialBalance;
        PeakEquity = initialBalance;
        DailyStartEquity = initialBalance;
        DrawdownType = drawdownType;
        _initialized = true;
    }

    public void OnEquityUpdate(decimal equity)
    {
        if (!_initialized)
            return;

        if (equity > PeakEquity)
            PeakEquity = equity;

        var dailyDd = DailyStartEquity > 0
            ? (DailyStartEquity - equity) / DailyStartEquity
            : 0m;
        CurrentDailyDrawdown = Math.Max(0, dailyDd);

        var equityBase = DrawdownType == "Trailing" ? PeakEquity : InitialAccountBalance;
        var maxDd = equityBase > 0
            ? (equityBase - equity) / equityBase
            : 0m;
        CurrentMaxDrawdown = Math.Max(0, maxDd);
    }

    public void OnDailyReset(decimal currentEquity)
    {
        DailyStartEquity = currentEquity;
        CurrentDailyDrawdown = 0;
    }

    public decimal GetMaxDrawdownFloor(decimal maxTotalLossPercent) =>
        DrawdownType == "Trailing"
            ? PeakEquity * (1 - (decimal)maxTotalLossPercent)
            : InitialAccountBalance * (1 - (decimal)maxTotalLossPercent);

    public decimal GetDailyLossLimit(decimal maxDailyLossPercent) =>
        DailyStartEquity * (1 - (decimal)maxDailyLossPercent);
}
