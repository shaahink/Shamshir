using Serilog;

namespace TradingEngine.Host;

public static class Program
{
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File("logs/engine-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

            builder.Services.AddSerilog(Log.Logger);

            var configLoader = new ConfigLoader();
            var loadedConfig = configLoader.Load();
            builder.Services.AddSingleton(loadedConfig);

            var mode = EngineMode.Backtest;

            if (mode == EngineMode.Live || mode == EngineMode.Paper)
                builder.Services.AddSingleton<IBrokerAdapter, NamedPipeBrokerAdapter>();
            else
                builder.Services.AddSingleton<IBrokerAdapter, SimulatedBrokerAdapter>();

            if (mode == EngineMode.Backtest)
            {
                builder.Services.AddSingleton<IMarketDataProvider>(_ =>
                    new HistoricalDataProvider(Path.Combine(AppContext.BaseDirectory, "config")));
            }
            else
            {
                builder.Services.AddSingleton<IMarketDataProvider, LiveMarketDataProvider>();
            }

            var tracker = new DrawdownTracker();
            tracker.Initialize(100_000);
            builder.Services.AddSingleton(tracker);
            builder.Services.AddSingleton<RiskManager>();
            builder.Services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());

            builder.Services.AddSingleton<IEngineClock>(new StubClock(DateTime.UtcNow));
            builder.Services.AddSingleton<IPositionManager>(_ =>
                throw new NotSupportedException("PositionManager not yet implemented"));
            builder.Services.AddSingleton<IEventBus>(_ =>
                throw new NotSupportedException("EventBus not yet implemented"));
            builder.Services.AddSingleton<IIndicatorService>(_ =>
                throw new NotSupportedException("IndicatorService requires Skender in Infrastructure"));

            var registry = new StrategyRegistry();
            var strategies = registry.CreateStrategies(
                ["trend-breakout"], loadedConfig, builder.Services.BuildServiceProvider());
            foreach (var strategy in strategies)
                builder.Services.AddSingleton(strategy);

            builder.Services.AddSingleton<IStrategy>(sp => sp.GetServices<IStrategy>().First());
            builder.Services.AddSingleton<DataFeedService>();
            builder.Services.AddHostedService<DataFeedService>();
            builder.Services.AddHostedService<EngineWorker>();
            builder.Services.AddHostedService<DailyResetService>();

            var app = builder.Build();
            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Engine terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
