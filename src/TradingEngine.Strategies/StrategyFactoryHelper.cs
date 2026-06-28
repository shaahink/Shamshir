using System.Text.Json;

namespace TradingEngine.Strategies;

public static class StrategyFactoryHelper
{
    public static T DeserializeParams<T>(JsonElement element) where T : new()
    {
        if (element.ValueKind == JsonValueKind.Undefined) return new T();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<T>(element.GetRawText(), opts) ?? new T();
    }
}
