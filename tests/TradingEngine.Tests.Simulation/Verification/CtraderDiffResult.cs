namespace TradingEngine.Tests.Simulation.Verification;

public enum DiscrepancyKind
{
    Structural,
    Numeric,
}

public enum Severity
{
    Error,
    Warning,
    Info,
}

public sealed record CtraderDiscrepancy(
    string Metric,
    DiscrepancyKind Kind,
    Severity Severity,
    string Description,
    string? Expected,
    string? Actual);

public sealed class CtraderDiffResult
{
    public string RunId { get; init; } = "";
    public string? ReportJsonPath { get; init; }

    public int CtraderTradeCount { get; set; }
    public int DbTradeCount { get; set; }

    public decimal CtraderNetProfit { get; set; }
    public decimal DbNetProfit { get; set; }

    public decimal CtraderMaxDdPct { get; set; }
    public decimal DbMaxDdPct { get; set; }

    public int CtraderWinningTrades { get; set; }
    public int DbWinningTrades { get; set; }

    public decimal? CtraderCommission { get; set; }
    public decimal DbCommission { get; set; }

    public decimal? CtraderSwap { get; set; }
    public decimal DbSwap { get; set; }

    public List<CtraderDiscrepancy> Discrepancies { get; init; } = new();

    public bool HasStructuralErrors =>
        Discrepancies.Any(d => d.Kind == DiscrepancyKind.Structural && d.Severity == Severity.Error);

    public bool HasWarnings =>
        Discrepancies.Any(d => d.Severity == Severity.Warning);

    public bool IsClean =>
        Discrepancies.Count == 0 || Discrepancies.All(d => d.Kind == DiscrepancyKind.Numeric && d.Severity == Severity.Info);
}
