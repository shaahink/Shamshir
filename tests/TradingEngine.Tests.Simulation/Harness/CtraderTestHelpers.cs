using Microsoft.Extensions.Configuration;

namespace TradingEngine.Tests.Simulation.Harness;

public static class CtraderTestHelpers
{
    public static string ResolveCredential(string key, string envKey)
    {
        var solutionRoot = SolutionRoot;
        var devSettingsPath = Path.Combine(solutionRoot, "src", "TradingEngine.Web", "appsettings.Development.json");
        if (File.Exists(devSettingsPath))
        {
            var devConfig = new ConfigurationBuilder().AddJsonFile(devSettingsPath).Build();
            var value = devConfig[$"CTrader:{key}"];
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return Environment.GetEnvironmentVariable(envKey) ?? "";
    }

    public static string SolutionRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static string ResolveAlgo()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Debug", "net6.0", "Shamshir.algo")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Release", "net6.0", "Shamshir.algo")),
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Shamshir.algo not found");
    }
}
