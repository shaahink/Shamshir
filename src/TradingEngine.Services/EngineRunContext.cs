namespace TradingEngine.Services;

public sealed record EngineRunContext(string RunId)
{
    public bool DiagnosticsEnabled { get; init; }
}
