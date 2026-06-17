using Serilog;

namespace TradingEngine.Host;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "experiment")
        {
            ExperimentCli.Run(args.AsSpan(1), services =>
                services.AddSingleton<IExperimentHostFactory, ExperimentHostFactoryAdapter>());
            return;
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SERILOG_FILE_PATH")))
            Environment.SetEnvironmentVariable("SERILOG_FILE_PATH", "logs/engine-.log");
        Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(
            new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables().Build()).CreateLogger();
        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
            builder.Services.AddSerilog(Log.Logger);
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var mode = builder.Configuration.GetValue<EngineMode?>("Engine:Mode") ?? EngineMode.Backtest;
            var dbPath = builder.Configuration.GetValue<string>("Persistence:DbPath") ?? Path.Combine(root, "data", "trading.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var slip = mode == EngineMode.Backtest ? builder.Configuration.GetValue<double?>("Simulation:SlippagePips") ?? 0.5 : 0.5;

            builder.Services.AddMarketData(mode, root, slip, builder.Configuration);
            builder.Services.AddRisk(root);
            builder.Services.AddPersistence(dbPath, root);
            builder.Services.AddStrategies();
            builder.Services.AddEventInfrastructure(mode);
            builder.Services.AddEngineWorker(mode);
            builder.Services.AddHostedService<DailyResetService>();

            var app = builder.Build();
            app.Services.GetRequiredService<StrategyConfigSeeder>().SeedAsync().GetAwaiter().GetResult();

            var store = app.Services.GetRequiredService<IStrategyConfigStore>();
            var strategyConfigs = store.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult();
            var loadedConfig = app.Services.GetRequiredService<LoadedConfig>();
            loadedConfig.StrategyConfigs = strategyConfigs;

            app.WireEventHandlers();
            app.WireRiskRules(loadedConfig);
            app.Run();
        }
        catch (Exception ex) { Log.Fatal(ex, "Engine terminated unexpectedly"); }
        finally { Log.CloseAndFlush(); }
    }
}
