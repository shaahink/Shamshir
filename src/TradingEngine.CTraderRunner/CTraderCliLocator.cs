namespace TradingEngine.CTraderRunner;

public static class CTraderCliLocator
{
    public static string Locate(IConfiguration config)
    {
        var configured = config["CTrader:CliPath"];
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        // Env-var override — lets the harness/orchestrator pin a specific cTrader CLI binary even
        // when no IConfiguration is threaded through (BacktestCli builds an empty config). The
        // "prefer root" heuristic below is fragile (different installed binaries behave differently
        // for handshake vs report-saving), so an explicit override is the reliable escape hatch.
        var envPath = Environment.GetEnvironmentVariable("CTRADER_CLI_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var spotwarePath = Path.Combine(localAppData, "Spotware", "cTrader");
        if (!Directory.Exists(spotwarePath))
            throw new FileNotFoundException("cTrader installation not found. Set CTrader:CliPath in config.");

        var candidates = Directory.EnumerateFiles(spotwarePath, "ctrader-cli.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        // Prefer the root binary. The x64 subdirectory copy often fails with .NET runtime errors.
        var rootPath = spotwarePath;
        var rootBinary = candidates.FirstOrDefault(c =>
            string.Equals(Path.GetDirectoryName(c), rootPath, StringComparison.OrdinalIgnoreCase));
        if (rootBinary is not null)
            return rootBinary;

        return candidates.FirstOrDefault()
            ?? throw new FileNotFoundException(
                "ctrader-cli.exe not found under AppData\\Spotware\\cTrader. Set CTrader:CliPath in config.");
    }
}
