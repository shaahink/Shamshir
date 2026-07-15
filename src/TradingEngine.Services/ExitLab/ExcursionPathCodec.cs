using System.Text.Json;

namespace TradingEngine.Services.ExitLab;

public static class ExcursionPathCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(IReadOnlyList<ExcursionPoint> path)
    {
        var dtos = new List<ExcursionPointDto>(path.Count);
        for (var i = 0; i < path.Count; i++)
        {
            var p = path[i];
            dtos.Add(new ExcursionPointDto(p.MinutesSinceEntry, Math.Round(p.HiPips, 1), Math.Round(p.LoPips, 1)));
        }
        return JsonSerializer.Serialize(dtos, Options);
    }

    public static IReadOnlyList<ExcursionPoint> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var parsed = JsonSerializer.Deserialize<List<ExcursionPointDto>>(json, Options);
        if (parsed is null) return [];
        var points = new List<ExcursionPoint>(parsed.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            var dto = parsed[i];
            points.Add(new ExcursionPoint(dto.t, dto.hi, dto.lo));
        }
        return points;
    }

    private sealed record ExcursionPointDto(int t, double hi, double lo);
}
