namespace TradingEngine.Host;

public sealed class DailyResetService(
    IRiskManager riskManager,
    IEngineClock clock,
    ILogger<DailyResetService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var resetTime = TimeSpan.Parse("22:00:00");
        var now = clock.UtcNow;

        var nextReset = now.Date + resetTime;
        if (now > nextReset)
        {
            logger.LogInformation("Daily reset: past 22:00 UTC, firing immediately");
            riskManager.OnDailyReset(0);
            riskManager.OnEquityUpdate(new EquitySnapshot(
                now, 0, 0, 0, 0, 0, 0, 0, EngineMode.Backtest));
            nextReset = nextReset.AddDays(1);
        }

        logger.LogInformation("Daily reset service scheduled. Next reset: {NextReset:O}", nextReset);

        while (!ct.IsCancellationRequested)
        {
            var delay = nextReset - clock.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            riskManager.OnDailyReset(0);
            logger.LogInformation("Daily reset executed at {Time:O}", clock.UtcNow);

            nextReset = nextReset.AddDays(1);
        }
    }
}
