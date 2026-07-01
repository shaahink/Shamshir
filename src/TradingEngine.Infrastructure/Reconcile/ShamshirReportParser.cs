using System.Text.Json;

namespace TradingEngine.Infrastructure.Reconcile;

/// <summary>
/// iter-marketdata-tape P0 — parses the cBot's own venue ledger (<c>shamshir-report.json</c>, written by
/// ShamshirTradeLogger and harvested by CtraderReportHarvester) into a <see cref="ReconcileLedger"/>. This is
/// the ORACLE: its per-trade net/gross/commission/swap are cTrader's own values. We reconcile the engine DB
/// against this. Schema is the one ShamshirTradeLogger.BuildReport writes (main / tradeStatistics / equity /
/// history.items); parsing is defensive so a partial/checkpoint report still yields what it can.
/// </summary>
public static class ShamshirReportParser
{
    public static ReconcileLedger Parse(string reportJson)
    {
        using var doc = JsonDocument.Parse(reportJson);
        var root = doc.RootElement;

        var trades = new List<ReconcileTrade>();
        decimal gross = 0, commission = 0, swap = 0, netFromItems = 0;
        int total = 0, winning = 0;

        if (root.TryGetProperty("history", out var history)
            && history.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
            {
                var net = GetDecimal(it, "net");
                gross += GetDecimal(it, "gross");
                commission += GetDecimal(it, "commissions");
                swap += GetDecimal(it, "swaps");
                netFromItems += net;
                total++;
                if (net > 0) winning++;

                trades.Add(new ReconcileTrade(
                    Epoch(GetLong(it, "entryTime")),
                    Epoch(GetLong(it, "closeTime")),
                    GetString(it, "direction"),
                    GetDecimal(it, "quantity"),
                    GetDecimal(it, "entryPrice"),
                    GetDecimal(it, "closePrice"),
                    net,
                    ExitReason: null));
            }
        }

        var maxDdPct = 0.0;
        if (root.TryGetProperty("equity", out var equity)
            && equity.TryGetProperty("maxEquityDrawdownPercent", out var dd)
            && dd.ValueKind is JsonValueKind.Number)
        {
            maxDdPct = dd.GetDouble();
        }

        // Prefer the venue's own tradeStatistics.netProfit.all when present (authoritative), else the sum.
        var netProfit = StatAll(root, "netProfit") ?? (double)netFromItems;
        var winRate = total > 0 ? winning * 100.0 / total : 0.0;

        return new ReconcileLedger(
            "ctrader", (decimal)netProfit, gross, commission, swap, maxDdPct, total, winning, winRate, trades);
    }

    private static double? StatAll(JsonElement root, string field)
    {
        if (root.TryGetProperty("tradeStatistics", out var stats)
            && stats.TryGetProperty(field, out var f)
            && f.TryGetProperty("all", out var all)
            && all.ValueKind is JsonValueKind.Number)
        {
            return all.GetDouble();
        }
        return null;
    }

    private static decimal GetDecimal(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetInt64() : 0L;

    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTime Epoch(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
