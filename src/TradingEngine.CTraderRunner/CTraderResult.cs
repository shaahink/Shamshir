namespace TradingEngine.CTraderRunner;

public sealed record CTraderResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool IsKnownPostBacktestCrash);
