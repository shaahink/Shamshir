using System.Globalization;

namespace TradingEngine.Tests.Simulation.Data;

public sealed class CsvDataGenerator
{
    public void GenerateToFile(string filePath, GeneratorConfig config)
    {
        using var writer = new StreamWriter(filePath);
        writer.WriteLine("DateTime,Open,High,Low,Close,Volume");

        var rng = new Random(config.Seed);
        var price = config.StartPrice;
        var currentTime = config.StartTime;
        var barDuration = GetBarDuration(config.Timeframe);

        for (int i = 0; i < config.BarCount; i++)
        {
            var drift = config.DriftPerBar;
            var noise = (decimal)(rng.NextDouble() - 0.5) * 2 * config.NoiseAmplitude;
            var closePrice = price + drift + noise;

            var halfRange = config.NoiseAmplitude * 0.5m;
            var high = Math.Max(price, closePrice) + halfRange;
            var low = Math.Min(price, closePrice) - halfRange;

            writer.WriteLine(
                $"{currentTime:yyyy-MM-dd HH:mm:ss},{price:F5},{high:F5},{low:F5},{closePrice:F5},{1000 + rng.Next(500):F1}");

            price = closePrice;
            currentTime = currentTime.Add(barDuration);
        }
    }

    public string GenerateToMemory(GeneratorConfig config)
    {
        var path = Path.GetTempFileName();
        GenerateToFile(path, config);
        return path;
    }

    private static TimeSpan GetBarDuration(Timeframe tf) => tf switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.H1 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(1),
    };
}

public sealed record GeneratorConfig(
    Symbol Symbol,
    decimal StartPrice,
    decimal DriftPerBar,
    decimal NoiseAmplitude,
    int BarCount,
    Timeframe Timeframe,
    DateTime StartTime,
    int Seed = 42);
