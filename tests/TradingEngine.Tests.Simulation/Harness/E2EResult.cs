namespace TradingEngine.Tests.Simulation.Harness;

public sealed record E2EResult(
    string RunId,
    int Trades,
    int BarEvals,
    int Signals,
    int Orders,
    int Executions,
    int CliExitCode,
    string CliStderr,
    string? ReportJsonPath,
    IReadOnlyList<E2ETradeRow>? TradesList,
    TransportStatusRecord? FinalTransportStatus
);

public sealed record E2ETradeRow(
    string Symbol,
    string Direction,
    decimal Lots,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal GrossPnL,
    decimal Commission,
    decimal Swap,
    decimal NetPnL,
    double Pips,
    double RMultiple,
    string ExitReason,
    string StrategyId
);

public sealed record TransportStatusRecord(
    string Phase,
    DateTime? ConnectedAtUtc,
    DateTime? LastMessageAtUtc,
    int BarsReceived,
    int CommandsSent,
    int ExecutionsReceived,
    string? LastError
);
