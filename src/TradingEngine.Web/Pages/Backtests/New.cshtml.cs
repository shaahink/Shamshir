namespace TradingEngine.Web.Pages.Backtests;

public sealed class NewBacktestModel : PageModel
{
    private readonly IConfiguration _config;
    private readonly StrategyRegistry _registry;

    public bool CredentialsConfigured { get; set; } = true;
    public bool AlgoExists { get; set; } = true;
    public string AlgoMissingMessage { get; set; } = "";
    public bool UseCTrader { get; set; } = true;
    public string PreflightMessage { get; set; } = "";
    public string[] StrategyIds { get; set; } = [];

    public NewBacktestModel(IConfiguration config, StrategyRegistry registry)
    {
        _config = config;
        _registry = registry;
    }

    public void OnGet()
    {
        CredentialsConfigured = !string.IsNullOrWhiteSpace(_config["CTrader:CtId"])
            && !string.IsNullOrWhiteSpace(_config["CTrader:PwdFile"])
            && !string.IsNullOrWhiteSpace(_config["CTrader:Account"]);

        UseCTrader = _config.GetValue<bool>("CTrader:UseForBacktest");

        var algoPath = ResolveAlgoPath();
        AlgoExists = algoPath is not null;
        if (!AlgoExists)
            AlgoMissingMessage = "src.algo not found. Build TradingEngine.Adapters.CTrader project first.";

        if (!CredentialsConfigured)
            PreflightMessage = "CTrader credentials not configured. Set CTrader:CtId, CTrader:PwdFile, CTrader:Account.";
        else if (!AlgoExists)
            PreflightMessage = AlgoMissingMessage;

        StrategyIds = _registry.GetAllIds().ToArray();
    }

    private string? ResolveAlgoPath()
    {
        var configured = _config["CTrader:AlgoPath"];
        if (!string.IsNullOrEmpty(configured) && System.IO.File.Exists(configured))
            return configured;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Release", "net6.0", "src.algo")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Debug", "net6.0", "src.algo")),
        };

        return candidates.FirstOrDefault(System.IO.File.Exists);
    }
}
