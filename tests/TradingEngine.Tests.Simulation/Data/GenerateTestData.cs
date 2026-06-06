namespace TradingEngine.Tests.Simulation.Data;

public static class GenerateTestData
{
    public static void GenerateAll(string outputDir)
    {
        var seed = 42;

        var scenarios = new[]
        {
            ("eurusd-h1-bull-2024.csv", 1.0800m, 0.00005m, 0.0003m, 2000),
            ("eurusd-h1-bear-2024.csv", 1.1000m, -0.00005m, 0.0003m, 2000),
            ("eurusd-h1-ranging-2024.csv", 1.0900m, 0m, 0.0005m, 2000),
            ("eurusd-h1-ddcrash-2024.csv", 1.0800m, -0.0005m, 0.0008m, 500),
            ("eurusd-h1-maxdd-2024.csv", 1.0800m, -0.0001m, 0.0006m, 500),
            ("usdjpy-h1-bull-2024.csv", 149.00m, 0.001m, 0.01m, 2000),
        };

        foreach (var (filename, startPrice, drift, noise, count) in scenarios)
        {
            var path = Path.Combine(outputDir, filename);
            if (File.Exists(path)) continue;

            var gen = new CsvDataGenerator();
            gen.GenerateToFile(path, new GeneratorConfig(
                Symbol.Parse(filename.StartsWith("usdjpy") ? "USDJPY" : "EURUSD"),
                startPrice, drift, noise, count, Timeframe.H1,
                new DateTime(2024, 1, 1), seed));
            seed++;
        }
    }
}
