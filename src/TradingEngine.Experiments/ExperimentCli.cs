using Microsoft.EntityFrameworkCore;
using TradingEngine.Risk.Compliance;

namespace TradingEngine.Experiments;

public static class ExperimentCli
{
    public static void Run(ReadOnlySpan<string> args, Action<IServiceCollection>? registerHostServices = null)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: experiment <run|report|list> [args]");
            return;
        }

        var verb = args[0];
        var rest = args.Length > 1 ? args[1..] : [];

        switch (verb)
        {
            case "run":
                RunExperiment(rest, registerHostServices);
                break;
            case "report":
                ShowReport(rest);
                break;
            case "list":
                ListExperiments();
                break;
            default:
                Console.WriteLine($"Unknown experiment verb: {verb}");
                break;
        }
    }

    private static void RunExperiment(ReadOnlySpan<string> args, Action<IServiceCollection>? registerHostServices = null)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: experiment run <spec.json>");
            return;
        }

        var specPath = args[0].ToString();
        if (!File.Exists(specPath))
        {
            Console.WriteLine($"Spec file not found: {specPath}");
            Environment.Exit(1);
            return;
        }

        var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var json = File.ReadAllText(specPath);
        var spec = JsonSerializer.Deserialize<ExperimentSpec>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        if (spec is null)
        {
            Console.WriteLine("Failed to parse spec file.");
            Environment.Exit(2);
            return;
        }

        var dbPath = Path.Combine(Path.GetTempPath(), $"exp_cli_{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TradingDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<IBarRepository, SqliteBarRepository>();
        services.AddSingleton<IExperimentRepository, SqliteExperimentRepository>();
        services.AddSingleton<IBacktestRunRepository, SqliteBacktestRunRepository>();
        services.AddSingleton<ITradeRepository, SqliteTradeRepository>();
        services.AddSingleton<IEquityRepository, SqliteEquityRepository>();
        services.AddSingleton<IPassProbabilityEstimator, PassProbabilityEstimator>();
        services.AddSingleton<ISymbolInfoRegistry>(_ =>
        {
            var catalog = new SymbolCatalog(solRoot);
            var reg = new SymbolInfoRegistry();
            foreach (var si in catalog.GetAll()) reg.Register(si);
            return reg;
        });
        registerHostServices?.Invoke(services);
        services.AddSingleton<ExperimentRunner>(sp => new ExperimentRunner(
            sp.GetRequiredService<IBarRepository>(),
            sp.GetRequiredService<IPassProbabilityEstimator>(),
            sp.GetRequiredService<IExperimentRepository>(),
            sp.GetRequiredService<IBacktestRunRepository>(),
            sp.GetRequiredService<ITradeRepository>(),
            sp.GetRequiredService<IEquityRepository>(),
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<IExperimentHostFactory>(),
            sp.GetRequiredService<ILogger<ExperimentRunner>>()
        ));

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.Database.EnsureCreated();

        Console.WriteLine($"Running experiment: {spec.Name}");
        Console.WriteLine($"  Variants: {spec.Variants.Length}, MaxRuns: {spec.MaxRuns}");
        Console.WriteLine();

        var runner = sp.GetRequiredService<ExperimentRunner>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        var result = runner.RunAsync(spec, cts.Token).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine(result.Success
            ? $"Completed: {result.Name} ({result.VariantScores.Count} variants scored)"
            : $"Failed: {result.ErrorMessage}");

        Environment.Exit(result.Success ? 0 : 3);
    }

    private static void ShowReport(ReadOnlySpan<string> args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: experiment report <id>");
            return;
        }

        var idText = args[0].ToString();
        if (!Guid.TryParse(idText, out var id))
        {
            Console.WriteLine($"Invalid experiment ID: {idText}");
            return;
        }

        var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dir = Path.Combine(solRoot, "docs", "experiments");
        foreach (var subdir in Directory.GetDirectories(dir))
        {
            var reportPath = Path.Combine(subdir, "REPORT.md");
            if (File.Exists(reportPath))
            {
                Console.WriteLine(File.ReadAllText(reportPath));
                return;
            }
        }

        Console.WriteLine($"Report not found for experiment {idText}");
    }

    private static void ListExperiments()
    {
        var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dir = Path.Combine(solRoot, "docs", "experiments");
        if (!Directory.Exists(dir))
        {
            Console.WriteLine("No experiments found.");
            return;
        }

        Console.WriteLine("Experiments:");
        foreach (var subdir in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(subdir);
            var reportPath = Path.Combine(subdir, "REPORT.md");
            var hasReport = File.Exists(reportPath);
            Console.WriteLine($"  {name} {(hasReport ? "[report]" : "[no report]")}");
        }
    }
}
