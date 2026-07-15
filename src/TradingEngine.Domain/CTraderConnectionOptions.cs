namespace TradingEngine.Domain;

/// <summary>
/// Strongly-typed binding for the CTrader config section. One place for CtId, PwdFile, and Account —
/// every consumer that was reading raw <c>_config["CTrader:Account"]</c> now injects
/// <c>IOptions&lt;CTraderConnectionOptions&gt;</c> instead.
/// </summary>
public sealed class CTraderConnectionOptions
{
    public const string Section = "CTrader";

    public string CtId { get; init; } = string.Empty;
    public string PwdFile { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;

    public bool IsValid => !string.IsNullOrWhiteSpace(CtId)
                        && !string.IsNullOrWhiteSpace(PwdFile)
                        && !string.IsNullOrWhiteSpace(Account);
}
