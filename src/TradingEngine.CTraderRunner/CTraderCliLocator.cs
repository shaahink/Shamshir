namespace TradingEngine.CTraderRunner;

public static class CTraderCliLocator
{
    public static string Locate(IConfiguration config)
    {
        var configured = config["CTrader:CliPath"];
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var spotwarePath = Path.Combine(localAppData, "Spotware", "cTrader");
        if (!Directory.Exists(spotwarePath))
            throw new FileNotFoundException("cTrader installation not found. Set CTrader:CliPath in config.");

        var found = Directory.EnumerateFiles(spotwarePath, "ctrader-cli.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return found ?? throw new FileNotFoundException(
            "ctrader-cli.exe not found under AppData\\Spotware\\cTrader. Set CTrader:CliPath in config.");
    }
}
