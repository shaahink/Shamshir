namespace TradingEngine.Services;

public sealed record EngineRunContext(string RunId)
{
    public bool DiagnosticsEnabled { get; init; }

    /// <summary>
    /// P0.1 (F1): the run's configured account balance. In backtest this is the sizing authority — the
    /// venue-reported hello balance must never override it (the audited ¼-sizing parity bug). 0 = not set
    /// (older callers), in which case EngineRunner falls back to the RiskManager / venue balance.
    /// </summary>
    public decimal InitialBalance { get; init; }
}
