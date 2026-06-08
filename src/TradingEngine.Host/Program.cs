using System.Text.Json;
using Serilog;
using TradingEngine.Infrastructure.Events;

namespace TradingEngine.Host;

public static class Program
{
    public static void Main(string[] args)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SERILOG_FILE_PATH")))
            Environment.SetEnvironmentVariable("SERILOG_FILE_PATH", "logs/engine-.log");

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
                .AddEnvironmentVariables()
                .Build())
            .CreateLogger();

        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
            builder.Services.AddSerilog(Log.Logger);

            var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var configLoader = new ConfigLoader(solutionRoot);
            var loadedConfig = configLoader.Load();
            builder.Services.AddSingleton(loadedConfig);

            var mode = builder.Configuration.GetValue<EngineMode?>("Engine:Mode") ?? EngineMode.Backtest;
            Log.Information("Engine mode: {Mode}", mode);

            var slipPips = mode == EngineMode.Backtest
                ? builder.Configuration.GetValue<double?>("Simulation:SlippagePips") ?? 0.5
                : 0.5;

            if (mode == EngineMode.Live || mode == EngineMode.Paper)
            {
                var dataPort = int.TryParse(builder.Configuration["Engine:Broker:NetMQ:DataPort"], out var dp) ? dp : 15555;
                var commandPort = int.TryParse(builder.Configuration["Engine:Broker:NetMQ:CommandPort"], out var cp) ? cp : 15556;
                builder.Services.AddSingleton<IBrokerAdapter>(sp => new NetMQBrokerAdapter(
                    $"tcp://127.0.0.1:{dataPort}",
                    $"tcp://*:{commandPort}",
                    sp.GetRequiredService<ILogger<NetMQBrokerAdapter>>()));
            }
            else
            {
                builder.Services.AddSingleton<IBrokerAdapter>(sp =>
                    new SimulatedBrokerAdapter(
                        sp.GetRequiredService<ISymbolInfoRegistry>(),
                        sp.GetRequiredService<Func<string, string, decimal>>(),
                        slippagePips: slipPips));
            }

            if (mode == EngineMode.Backtest)
            {
                var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "data"));
                builder.Services.AddSingleton<IMarketDataProvider>(_ => new HistoricalDataProvider(dataDir));
            }
            else
            {
                builder.Services.AddSingleton<IMarketDataProvider, LiveMarketDataProvider>();
            }

            builder.Services.AddSingleton<DrawdownTracker>();

            builder.Services.AddSingleton<ISymbolInfoRegistry>(sp =>
            {
                var reg = new SymbolInfoRegistry();
                var symbolsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "symbols", "defaults.json"));
                if (File.Exists(symbolsPath))
                {
                    var json = File.ReadAllText(symbolsPath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var entries = JsonSerializer.Deserialize<List<SymbolJsonEntry>>(json, options);
                    if (entries is not null)
                    {
                        foreach (var e in entries)
                        {
                            reg.Register(new SymbolInfo(
                                Symbol.Parse(e.Symbol),
                                Enum.Parse<SymbolCategory>(e.Category),
                                e.BaseCurrency, e.QuoteCurrency,
                                (decimal)e.PipSize, (decimal)e.TickSize, (decimal)e.ContractSize,
                                (decimal)e.MinLots, (decimal)e.MaxLots, (decimal)e.LotStep,
                                (decimal)e.MarginRate, (decimal)e.TypicalSpread));
                        }
                    }
                }
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
            builder.Services.AddSingleton<IRiskProfileResolver>(sp => new RiskProfileResolver(loadedConfig.RiskProfiles));

            builder.Services.AddSingleton<IEngineClock, BrokerClock>();

            var dbPath = builder.Configuration.GetValue<string>("Persistence:DbPath")
                ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "trading.db"));
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            using (var ctx = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite($"Data Source={dbPath}").Options))
                ctx.Database.EnsureCreated();

            builder.Services.AddDbContext<TradingDbContext>(opt => opt.UseSqlite($"Data Source={dbPath}"));
            builder.Services.AddScoped<ITradeRepository, SqliteTradeRepository>();
            builder.Services.AddScoped<IEquityRepository, SqliteEquityRepository>();
            builder.Services.AddSingleton<PersistenceService>();

            builder.Services.AddSingleton<IPositionManager, PositionManager>();
            builder.Services.AddSingleton<IEventBus, TypedEventBus>();
            builder.Services.AddSingleton<EquityPersistenceHandler>();
            builder.Services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
            builder.Services.AddSingleton<OrderDispatcher>();
            builder.Services.AddSingleton<PositionTracker>();

            var registry = new StrategyRegistry();
            var activeStrategyIds = builder.Configuration.GetValue<string>("Engine:ActiveStrategyIds")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                ?? ["trend-breakout", "ema-alignment", "mean-reversion", "session-breakout"];

            builder.Services.AddSingleton(registry);
            builder.Services.AddSingleton<IReadOnlyList<string>>(activeStrategyIds);
            builder.Services.AddSingleton<IEnumerable<IStrategy>>(sp =>
            {
                var reg = sp.GetRequiredService<StrategyRegistry>();
                var ids = sp.GetRequiredService<IReadOnlyList<string>>();
                var cfg = sp.GetRequiredService<LoadedConfig>();
                return reg.CreateStrategies(ids, cfg, sp);
            });
            builder.Services.AddSingleton<IStrategy>(sp => sp.GetRequiredService<IEnumerable<IStrategy>>().First());

            if (mode == EngineMode.Backtest)
            {
                builder.Services.AddSingleton<DataFeedService>(sp =>
                {
                    var symbols = loadedConfig.StrategyConfigs
                        .SelectMany(c => c.Symbols).Distinct().Select(Symbol.Parse).ToList();
                    return new DataFeedService(
                        sp.GetRequiredService<IMarketDataProvider>(),
                        sp.GetRequiredService<IBrokerAdapter>(),
                        sp.GetRequiredService<ILogger<DataFeedService>>())
                    { Symbols = symbols.Count > 0 ? symbols : [Symbol.Parse("EURUSD")] };
                });
                builder.Services.AddHostedService<DataFeedService>(sp => sp.GetRequiredService<DataFeedService>());
            }
            builder.Services.AddSingleton<EngineWorker>();
            builder.Services.AddHostedService<EngineWorker>(sp => sp.GetRequiredService<EngineWorker>());
            builder.Services.AddHostedService<DailyResetService>();

            var app = builder.Build();

            var eventBus = app.Services.GetRequiredService<IEventBus>();
            var equityHandler = app.Services.GetRequiredService<EquityPersistenceHandler>();
            eventBus.Subscribe<EquityUpdated>(equityHandler);

            var rm = app.Services.GetRequiredService<RiskManager>();
            var activeRiskProfileId = loadedConfig.StrategyConfigs
                .Select(c => c.RiskProfileId).FirstOrDefault() ?? "standard";
            var activeProfile = loadedConfig.RiskProfiles.FirstOrDefault(r => r.Id == activeRiskProfileId);
            var activeRuleSetId = activeProfile?.PropFirmRuleSetId ?? "ftmo-standard";
            var ruleSet = loadedConfig.PropFirms.FirstOrDefault(r => r.Id == activeRuleSetId);
            if (ruleSet is not null)
            {
                rm.SetActiveRuleSet(ruleSet);
                Log.Information("Active prop firm rule set: {Id} (max daily DD {DailyPct}%, max total DD {TotalPct}%)",
                    ruleSet.Id, ruleSet.MaxDailyLossPercent * 100, ruleSet.MaxTotalLossPercent * 100);
            }
            else
            {
                Log.Warning("No PropFirmRuleSet found for id={Id} — risk gates disabled", activeRuleSetId);
            }

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

    private sealed record SymbolJsonEntry
    {
        public string Symbol { get; init; } = "";
        public string Category { get; init; } = "";
        public string BaseCurrency { get; init; } = "";
        public string QuoteCurrency { get; init; } = "";
        public double PipSize { get; init; }
        public double TickSize { get; init; }
        public double ContractSize { get; init; }
        public double MinLots { get; init; }
        public double MaxLots { get; init; }
        public double LotStep { get; init; }
        public double MarginRate { get; init; }
        public double TypicalSpread { get; init; }
    }
}
