using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingEngine.Tests.Simulation.Verification;

public sealed class CtraderSummaryReport
{
    [JsonPropertyName("main")]
    public CtraderMainSection? Main { get; set; }

    [JsonPropertyName("tradeStatistics")]
    public CtraderTradeStatsSection? TradeStatistics { get; set; }

    [JsonPropertyName("equity")]
    public CtraderEquitySection? Equity { get; set; }

    [JsonPropertyName("history")]
    public CtraderHistorySection? History { get; set; }
}

public sealed class CtraderHistorySection
{
    [JsonPropertyName("items")]
    public List<CtraderHistoryItem> Items { get; set; } = new();
}

public sealed class CtraderHistoryItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    // Present in the cBot-written ledger (ShamshirTradeLogger); absent in cTrader's native report.
    // Equals the engine clientOrderId == DB TradeResult.PositionId — the per-trade join key.
    [JsonPropertyName("clientOrderId")]
    public string? ClientOrderId { get; set; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("net")]
    public decimal Net { get; set; }

    [JsonPropertyName("gross")]
    public decimal Gross { get; set; }

    [JsonPropertyName("commissions")]
    public decimal Commissions { get; set; }

    [JsonPropertyName("swaps")]
    public decimal Swaps { get; set; }

    [JsonPropertyName("entryPrice")]
    public decimal EntryPrice { get; set; }

    [JsonPropertyName("closePrice")]
    public decimal ClosePrice { get; set; }

    [JsonPropertyName("pips")]
    public double Pips { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("entryTime")]
    public long EntryTime { get; set; }

    [JsonPropertyName("closeTime")]
    public long CloseTime { get; set; }
}

public sealed class CtraderMainSection
{
    [JsonPropertyName("netProfit")]
    public decimal NetProfit { get; set; }

    [JsonPropertyName("startingCapital")]
    public decimal StartingCapital { get; set; }

    [JsonPropertyName("endingEquity")]
    public decimal EndingEquity { get; set; }

    [JsonPropertyName("endingBalance")]
    public decimal EndingBalance { get; set; }

    [JsonPropertyName("roi")]
    public double Roi { get; set; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("period")]
    public string? Period { get; set; }
}

public sealed class CtraderTradeStatsSection
{
    [JsonPropertyName("netProfit")]
    public CtraderDirectionalValue? NetProfit { get; set; }

    [JsonPropertyName("profitFactor")]
    public CtraderDirectionalValue? ProfitFactor { get; set; }

    [JsonPropertyName("commissions")]
    public CtraderDirectionalValue? Commissions { get; set; }

    [JsonPropertyName("swaps")]
    public CtraderDirectionalValue? Swaps { get; set; }

    [JsonPropertyName("totalTrades")]
    public CtraderDirectionalValue? TotalTrades { get; set; }

    [JsonPropertyName("winningTrades")]
    public CtraderDirectionalValue? WinningTrades { get; set; }

    [JsonPropertyName("losingTrades")]
    public CtraderDirectionalValue? LosingTrades { get; set; }

    [JsonPropertyName("largestWinningTrade")]
    public CtraderDirectionalValue? LargestWinningTrade { get; set; }

    [JsonPropertyName("largestLosingTrade")]
    public CtraderDirectionalValue? LargestLosingTrade { get; set; }

    [JsonPropertyName("averageTrade")]
    public CtraderDirectionalValue? AverageTrade { get; set; }

    [JsonPropertyName("maxConsecutiveWinningTrades")]
    public CtraderDirectionalValue? MaxConsecutiveWinningTrades { get; set; }

    [JsonPropertyName("maxConsecutiveLosingTrades")]
    public CtraderDirectionalValue? MaxConsecutiveLosingTrades { get; set; }
}

public sealed class CtraderDirectionalValue
{
    [JsonPropertyName("all")]
    public decimal All { get; set; }

    [JsonPropertyName("long")]
    public decimal Long { get; set; }

    [JsonPropertyName("short")]
    public decimal Short { get; set; }
}

public sealed class CtraderEquitySection
{
    [JsonPropertyName("maxBalanceDrawdownPercent")]
    public decimal? MaxBalanceDrawdownPercent { get; set; }

    [JsonPropertyName("maxEquityDrawdownPercent")]
    public decimal? MaxEquityDrawdownPercent { get; set; }

    [JsonPropertyName("maxBalanceDrawdownAbsolute")]
    public decimal? MaxBalanceDrawdownAbsolute { get; set; }

    [JsonPropertyName("maxEquityDrawdownAbsolute")]
    public decimal? MaxEquityDrawdownAbsolute { get; set; }
}
