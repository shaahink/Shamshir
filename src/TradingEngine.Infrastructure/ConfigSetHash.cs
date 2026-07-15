using System.Security.Cryptography;
using System.Text;

namespace TradingEngine.Infrastructure;

public static class ConfigSetHash
{
    public static string Compute(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
