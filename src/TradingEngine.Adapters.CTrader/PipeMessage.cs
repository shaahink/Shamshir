using System.Text;
using Newtonsoft.Json;

namespace TradingEngine.Adapters.CTrader;

public class PipeMessage
{
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";

    public byte[] ToByteArray()
    {
        var json = JsonConvert.SerializeObject(this);
        var utf8 = Encoding.UTF8.GetBytes(json);
        var length = BitConverter.GetBytes(utf8.Length);
        var result = new byte[4 + utf8.Length];
        Buffer.BlockCopy(length, 0, result, 0, 4);
        Buffer.BlockCopy(utf8, 0, result, 4, utf8.Length);
        return result;
    }

    public static PipeMessage FromByteArray(byte[] data)
    {
        var json = Encoding.UTF8.GetString(data);
        return FromJson(json);
    }

    public static PipeMessage FromJson(string json)
    {
        return JsonConvert.DeserializeObject<PipeMessage>(json) ?? new PipeMessage();
    }
}
