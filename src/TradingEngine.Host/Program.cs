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
        if (args.Length >= 1 && args[0] == "lint-config")
        {
            // F10 (P1.1): resolve the repo root via the single source of truth (DbPathResolver), not the
            // fragile fixed five-levels-up heuristic — so lint-config finds the SAME config/ tree the Web
            // app and the other Host verbs use regardless of launch depth.
            var lintRoot = DbPathResolver.FindRepoRoot();
            var violations = ConfigLinter.LintDirectories(
                Path.Combine(lintRoot, "config", "strategies"),
                Path.Combine(lintRoot, "config", "risk-profiles"));
            if (violations.Count == 0)
            {
                Console.WriteLine("Config lint: OK (no raw-pip fields without their normalized companion).");
                Environment.Exit(0);
            }
            Console.WriteLine($"Config lint FAILED ({violations.Count} violation(s)):");
            foreach (var v in violations) Console.WriteLine($"  - {v}");
            Environment.Exit(1);
            return;
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SERILOG_FILE_PATH")))
            Environment.SetEnvironmentVariable("SERILOG_FILE_PATH", "logs/engine-.log");
        Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(
            new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables().Build()).CreateLogger();
        // P1.1 (F10): populate the per (symbol, timeframe) ReferenceScales cells into the SAME DB the Web
        // app uses. Own exit codes: 0 = cells updated, 1 = nothing updated, 2 = fatal (incl. pending
        // migrations). Dispatched after Serilog init so it logs through the shared sink.
        if (args.Length >= 1 && args[0] == "compute-reference-scales")
        {
            try
            {
                var exit = ReferenceScaleCli.RunAsync().GetAwaiter().GetResult();
                Environment.Exit(exit);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "compute-reference-scales failed");
                Environment.Exit(2);
            }
            finally { Log.CloseAndFlush(); }
            return;
        }
        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
            builder.Services.AddSerilog(Log.Logger);
            var root = DbPathResolver.FindRepoRoot();
            var mode = builder.Configuration.GetValue<EngineMode?>("Engine:Mode") ?? EngineMode.Backtest;
            // F10: one DB path, shared with the Web app + orchestrator via DbPathResolver (repo-root
            // anchored, cwd-independent) — no more Host-CLI-only root data/trading.db.
            var dbPath = DbPathResolver.ResolveTradingDbPath(builder.Configuration.GetValue<string>("Persistence:DbPath"));
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var slip = mode == EngineMode.Backtest ? builder.Configuration.GetValue<double?>("Simulation:SlippagePips") ?? 0.5 : 0.5;
            var runId = builder.Configuration["Engine:RunId"] ?? "";
            var activeIds = builder.Configuration.GetValue<string>("Engine:ActiveStrategyIds")
                ?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                ?? Array.Empty<string>();

            var options = new EngineHostOptions
            {
                RunId = runId,
                Mode = mode,
                AdapterFactory = sp => BrokerAdapterFactory.Create(mode, slip, builder.Configuration, sp),
                DbPath = dbPath,
                SolutionRoot = root,
                ActiveStrategyIds = activeIds,
                RunPlan = null,
                MinLogLevel = Microsoft.Extensions.Logging.LogLevel.Information,
            };

            builder.Services.AddEngineHost(options);

            var app = builder.Build();

            // F10 fail-loud: never run the engine against a stale schema. The Web app owns migration
            // application; the Host CLI refuses to start (exit 3) and logs the exact DB path if the
            // unified DB has any pending migration.
            try
            {
                using var guardScope = app.Services.CreateScope();
                var guardDb = guardScope.ServiceProvider.GetRequiredService<TradingDbContext>();
                var guardLog = guardScope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MigrationGuard");
                MigrationGuard.EnsureUpToDate(guardDb, dbPath, guardLog);
            }
            catch (PendingMigrationsException)
            {
                Log.CloseAndFlush();
                Environment.Exit(3);
                return;
            }

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
