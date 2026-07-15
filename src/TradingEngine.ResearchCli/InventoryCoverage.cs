using System.Globalization;
using System.Text.Json;

namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.1 — the pure coverage check behind <c>research data ensure</c>. Given the market-data inventory
/// (<c>GET /api/data-manager/inventory</c> — rows of symbol/timeframe/barCount/firstBar/lastBar) and the
/// requested symbols × timeframes × [from,to] window, it decides which cells are MISSING (absent, empty,
/// or not covering the whole requested range). The impure part (fetch inventory, POST download) is a thin
/// shell around this; the decision is unit-tested credential-free so a playbook's <c>ensure-data</c> step
/// (P3.2) never claims coverage it can't prove — the F11 data-famine class of bug starts here.
/// </summary>
public sealed record InventoryRow(string Symbol, string Timeframe, long BarCount, DateTime? FirstBar, DateTime? LastBar);

public sealed record CoverageCell(string Symbol, string Timeframe, bool Present, long BarCount, bool CoversRange)
{
    /// <summary>A cell is satisfied only when it exists, has bars, AND spans the requested window.</summary>
    public bool Satisfied => Present && BarCount > 0 && CoversRange;
}

public static class InventoryCoverage
{
    public static IReadOnlyList<InventoryRow> ParseInventory(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var rows = new List<InventoryRow>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            rows.Add(new InventoryRow(
                Symbol: GetString(el, "symbol") ?? "",
                Timeframe: GetString(el, "timeframe") ?? "",
                BarCount: GetLong(el, "barCount"),
                FirstBar: GetDate(el, "firstBar"),
                LastBar: GetDate(el, "lastBar")));
        }
        return rows;
    }

    /// <summary>
    /// One <see cref="CoverageCell"/> per requested (symbol, timeframe), case-insensitive on both. When
    /// <paramref name="from"/>/<paramref name="to"/> are null the range check degrades to "any bars present".
    /// </summary>
    public static IReadOnlyList<CoverageCell> Evaluate(
        IReadOnlyList<InventoryRow> inventory,
        IReadOnlyList<string> symbols,
        IReadOnlyList<string> timeframes,
        DateTime? from,
        DateTime? to)
    {
        var cells = new List<CoverageCell>();
        foreach (var symbol in symbols)
        {
            foreach (var tf in timeframes)
            {
                var match = inventory.FirstOrDefault(r =>
                    string.Equals(r.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.Timeframe, tf, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                {
                    cells.Add(new CoverageCell(symbol, tf, Present: false, BarCount: 0, CoversRange: false));
                    continue;
                }

                var coversRange =
                    (from is null || (match.FirstBar is { } fb && fb <= from.Value))
                    && (to is null || (match.LastBar is { } lb && lb >= to.Value));
                cells.Add(new CoverageCell(symbol, tf, Present: true, match.BarCount, coversRange));
            }
        }
        return cells;
    }

    /// <summary>The cells that still need downloading (not satisfied).</summary>
    public static IReadOnlyList<CoverageCell> Missing(IReadOnlyList<CoverageCell> cells) =>
        [.. cells.Where(c => !c.Satisfied)];

    private static string? GetString(JsonElement el, string name) =>
        TryGet(el, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement el, string name)
    {
        if (TryGet(el, name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            {
                return n;
            }
            if (v.ValueKind == JsonValueKind.String
                && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            {
                return s;
            }
        }
        return 0;
    }

    private static DateTime? GetDate(JsonElement el, string name)
    {
        if (TryGet(el, name, out var v)
            && v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        {
            return dt;
        }
        return null;
    }

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
