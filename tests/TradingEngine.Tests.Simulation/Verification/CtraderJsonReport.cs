using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingEngine.Tests.Simulation.Verification;

public sealed class CtraderJsonReport
{
    [JsonPropertyName("serial")]
    public int Serial { get; set; }

    [JsonPropertyName("positionId")]
    public int PositionId { get; set; }

    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("entryPrice")]
    public decimal? EntryPrice { get; set; }

    [JsonPropertyName("closePrice")]
    public decimal? ClosePrice { get; set; }

    [JsonPropertyName("grossProfit")]
    public decimal? GrossProfit { get; set; }

    [JsonPropertyName("pips")]
    public double? Pips { get; set; }

    [JsonPropertyName("balance")]
    public decimal? Balance { get; set; }

    [JsonPropertyName("equity")]
    public decimal? Equity { get; set; }

    public bool IsCreate => Event == "Create Position";
    public bool IsClosed => Event is "Stop Loss Hit" or "Take Profit Hit" or "Closed" or "Position Closed";

    public static ParsedCtraderReport Parse(string path)
    {
        var result = new ParsedCtraderReport();
        if (!File.Exists(path)) return result;
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return result;

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            if (json.TrimStart().StartsWith("["))
            {
                result.Events = JsonSerializer.Deserialize<List<CtraderJsonReport>>(json, opts)
                    ?? new List<CtraderJsonReport>();
            }
            else
            {
                result.Summary = JsonSerializer.Deserialize<CtraderSummaryReport>(json, opts);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CtraderDiff] Failed to parse {path}: {ex.GetType().Name} - {ex.Message}");
        }
        return result;
    }
}

public sealed class ParsedCtraderReport
{
    public CtraderSummaryReport? Summary { get; set; }
    public List<CtraderJsonReport> Events { get; set; } = new();
    public bool HasSummary => Summary is not null;
    public bool HasEvents => Events.Count > 0;
    public bool IsEmpty => !HasSummary && !HasEvents;
}
