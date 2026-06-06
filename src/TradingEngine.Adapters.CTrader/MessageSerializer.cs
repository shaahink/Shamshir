using System;
using System.Text;
using Newtonsoft.Json;

namespace TradingEngine.Adapters.CTrader;

public static class MessageSerializer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Include,
    };

    public static string Serialize(object obj)
    {
        return JsonConvert.SerializeObject(obj, Settings);
    }

    public static T Deserialize<T>(string json) where T : class
    {
        return JsonConvert.DeserializeObject<T>(json, Settings) ?? throw new InvalidOperationException("Deserialization returned null");
    }
}
