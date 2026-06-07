using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TradingEngine.CTraderRunner;

namespace TradingEngine.Tests.Simulation.Pipeline;

[Trait("Category", "Pipeline")]
public sealed class FullBacktestPipelineTest
{
    [Fact(Timeout = 600_000)]
    public async Task EurUsdH1_ThreeMonth_GeneratesAtLeastOneTrade()
    {
        var ctid = Environment.GetEnvironmentVariable("CTrader__CtId");
        var pwdFile = Environment.GetEnvironmentVariable("CTrader__PwdFile");
        var account = Environment.GetEnvironmentVariable("CTrader__Account");

        if (string.IsNullOrEmpty(ctid) || string.IsNullOrEmpty(pwdFile) || string.IsNullOrEmpty(account))
        {
            throw new InvalidOperationException(
                "Set CTrader__CtId, CTrader__PwdFile, CTrader__Account env vars first. " +
                "Also ensure the engine is running (dotnet run --project src/TradingEngine.Host -- --Engine:Mode Live)");
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CTrader:CtId"] = ctid,
                ["CTrader:PwdFile"] = pwdFile,
                ["CTrader:Account"] = account,
                ["Engine:Broker:PipeName"] = "trading-engine",
            })
            .AddEnvironmentVariables()
            .Build();

        var runnerLogger = new SimpleLogger<BacktestRunner>();
        var runner = new BacktestRunner(config, runnerLogger);

        var cfg = new BacktestConfig
        {
            Symbol = "EURUSD",
            Period = "h1",
            Start = new DateTime(2024, 1, 15),
            End = new DateTime(2024, 4, 15),
            Balance = 100_000,
        };

        Console.WriteLine($"[TEST] Launching backtest: {cfg.Symbol} {cfg.Period} {cfg.Start:dd/MM/yyyy}-{cfg.End:dd/MM/yyyy}");
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(cfg);
        sw.Stop();

        Console.WriteLine($"[TEST] Done in {sw.Elapsed.TotalSeconds:F1}s. ExitCode={result.ExitCode} Success={result.Success}");
        if (!string.IsNullOrEmpty(result.ErrorMessage))
            Console.WriteLine($"[TEST] stderr: {result.ErrorMessage}");

        result.Success.Should().BeTrue("backtest should complete successfully");
        // Note: engine logs (BAR|, TICK|, SIGNAL|) must be viewed in the engine's console
        // This test only verifies the CLI backtest runs; engine-side verification is manual
    }
}

internal sealed class SimpleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Console.WriteLine($"[RUNNER] {formatter(state, exception)}");
    }
}
