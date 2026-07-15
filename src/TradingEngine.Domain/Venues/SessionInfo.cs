namespace TradingEngine.Domain;

public sealed record SessionInfo(
    string[] Symbols,
    string[] Periods,
    decimal Balance,
    decimal Equity,
    string Mode);
