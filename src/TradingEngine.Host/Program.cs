using Serilog;
using TradingEngine.Infrastructure.Events;

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
                var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "data"));
                builder.Services.AddSingleton<IMarketDataProvider>(_ =>
                    new HistoricalDataProvider(dataDir));
            }
            else
            {
                builder.Services.AddSingleton<IMarketDataProvider, LiveMarketDataProvider>();
            }

            var tracker = new DrawdownTracker();
            tracker.Initialize(100_000);
            builder.Services.AddSingleton(tracker);

            builder.Services.AddSingleton<ISymbolInfoRegistry>(sp =>
            {
                var reg = new SymbolInfoRegistry();
                reg.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
                    0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
                reg.Register(new SymbolInfo(Symbol.Parse("GBPUSD"), SymbolCategory.Forex, "GBP", "USD",
                    0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.00012m));
                reg.Register(new SymbolInfo(Symbol.Parse("USDJPY"), SymbolCategory.Forex, "USD", "JPY",
                    0.01m, 0.001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.01m));
                return reg;
            });
            builder.Services.AddSingleton<INewsFilter>(_ => new NewsFilter());
            builder.Services.AddSingleton<SessionFilter>();
            builder.Services.AddSingleton<Func<string, string, decimal>>(_ => (from, to) =>
            {
                if (from == "JPY" && to == "USD") return 1m / 149.50m;
                if (from == "GBP" && to == "USD") return 1.2650m;
                return 1;
            });

            builder.Services.AddSingleton<RiskManager>();
            builder.Services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());

            if (mode == EngineMode.Backtest)
                builder.Services.AddSingleton<IEngineClock>(new StubClock(DateTime.UtcNow));
            else
                builder.Services.AddSingleton<IEngineClock, BrokerClock>();

            builder.Services.AddSingleton<IPositionManager, PositionManager>();
            builder.Services.AddSingleton<IEventBus, TypedEventBus>();
            builder.Services.AddSingleton<IIndicatorService, SkenderIndicatorService>();

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
