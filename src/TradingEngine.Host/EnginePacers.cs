namespace TradingEngine.Host;

public sealed class BarSteppedPacer : IEnginePacer
{
    public async Task PaceAsync(EngineRunner runner, CancellationToken ct)
    {
        await runner.RunBacktestLoopAsync(ct);
    }
}

public sealed class AsyncStreamPacer : IEnginePacer
{
    public async Task PaceAsync(EngineRunner runner, CancellationToken ct)
    {
        await Task.WhenAll(
            runner.ProcessTicksAsync(ct),
            runner.ProcessBarsAsync(ct),
            runner.ProcessAccountUpdatesAsync(ct),
            runner.ProcessExecutionEventsAsync(ct),
            runner.ConsumeExecutionsAsync(ct),
            runner.ProcessAccountQueueAsync(ct));
    }
}
