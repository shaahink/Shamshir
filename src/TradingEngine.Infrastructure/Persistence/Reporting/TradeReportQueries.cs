namespace TradingEngine.Infrastructure.Persistence.Reporting;

public sealed class TradeReportQueries(IDbConnection db)
{
    public async Task<PerformanceSummary> GetSummaryAsync(
        DateTime from, DateTime to, string? strategyId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                COUNT(*) AS TotalTrades,
                SUM(CASE WHEN NetPnLAmount > 0 THEN 1 ELSE 0 END) AS Wins,
                SUM(NetPnLAmount) AS TotalNetPnL,
                MIN(NetPnLAmount) AS MaxSingleLoss,
                MAX(MaxAdverseExcursion) AS WorstMAE,
                AVG(CAST(DurationSeconds AS REAL) / 3600.0) AS AvgHoldHours
            FROM TradeResults
            WHERE ClosedAtUtc BETWEEN @from AND @to
              AND (@strategyId IS NULL OR StrategyId = @strategyId)
            """;
        return await db.QuerySingleAsync<PerformanceSummary>(
            sql, new { from, to, strategyId }, commandTimeout: 30);
    }
}
