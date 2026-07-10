namespace TradingEngine.Domain;

public sealed record ExposureGroup(string Id, string Label, IReadOnlySet<string> Symbols, decimal MaxExposure)
{
    public bool Contains(string symbol) => Symbols.Contains(symbol);
}

public sealed record ExposureGroupConfig(IReadOnlyList<ExposureGroup> Groups);
