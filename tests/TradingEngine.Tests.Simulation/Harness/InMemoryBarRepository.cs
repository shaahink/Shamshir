namespace TradingEngine.Tests.Simulation.Harness;

public sealed class InMemoryBarRepository : IBarRepository
{
    private readonly IReadOnlyList<Bar> _bars;

    public InMemoryBarRepository(IReadOnlyList<Bar> bars) => _bars = bars;

    public Task BulkInsertAsync(IReadOnlyList<Bar> bars, CancellationToken ct)
        => Task.CompletedTask;

    public Task<IReadOnlyList<Bar>> GetAsync(Symbol symbol, Timeframe tf, DateTime from, DateTime to, CancellationToken ct)
        => Task.FromResult(_bars);
}
