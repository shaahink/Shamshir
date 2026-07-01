using System.Text.Json;
using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.MarketData;

/// <summary>
/// The normalized, portable interchange format (iter-marketdata-tape P2 / D5): NDJSON — one self-describing
/// bar per line — shared by the cTrader recorder cBot AND any future data source. camelCase keys, ISO-8601
/// UTC times. Prices are doubles (cTrader's native representation). Keeping this one format means the
/// ingester is source-agnostic and files are inspectable/portable.
/// </summary>
public static class MarketDataShardIo
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string ToLine(Bar bar) => JsonSerializer.Serialize(
        new ShardBar(
            bar.Symbol.ToString(), bar.Timeframe.ToString(), bar.OpenTimeUtc,
            (double)bar.Open, (double)bar.High, (double)bar.Low, (double)bar.Close, bar.Volume),
        Json);

    public static bool TryParse(string line, out Bar bar)
    {
        bar = default!;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            var s = JsonSerializer.Deserialize<ShardBar>(line, Json);
            if (s is null || string.IsNullOrEmpty(s.Symbol) || string.IsNullOrEmpty(s.Timeframe)) return false;
            // ignoreCase: cTrader emits lowercase short-names ("h1"), vendors vary.
            if (!Enum.TryParse<Timeframe>(s.Timeframe, ignoreCase: true, out var tf)) return false;
            bar = new Bar(
                Symbol.Parse(s.Symbol), tf, DateTime.SpecifyKind(s.OpenTimeUtc, DateTimeKind.Utc),
                (decimal)s.Open, (decimal)s.High, (decimal)s.Low, (decimal)s.Close, s.Volume);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public sealed record ShardBar(
        string Symbol,
        string Timeframe,
        DateTime OpenTimeUtc,
        double Open,
        double High,
        double Low,
        double Close,
        double Volume);
}
