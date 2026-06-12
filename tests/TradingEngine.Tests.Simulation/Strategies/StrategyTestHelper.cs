namespace TradingEngine.Tests.Simulation.Strategies;

public static class StrategyTestHelper
{
    public static List<Bar> GenerateTrendingBars(string symbol = "EURUSD", int count = 200, decimal startPrice = 1.1000m, decimal trend = 0.0001m)
    {
        var rng = new Random(42);
        var bars = new List<Bar>();
        var price = startPrice;
        var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
        {
            var open = price;
            var close = price + trend + (decimal)(rng.NextDouble() - 0.5) * 0.002m;
            var high = Math.Max(open, close) + (decimal)rng.NextDouble() * 0.0005m;
            var low = Math.Min(open, close) - (decimal)rng.NextDouble() * 0.0005m;
            bars.Add(new Bar(Symbol.Parse(symbol), Timeframe.H1, time, open, high, low, close, 1000));
            price = close;
            time = time.AddHours(1);
        }
        return bars;
    }

    public static IReadOnlyDictionary<string, double> ComputeIndicators(List<Bar> bars, IReadOnlyList<IndicatorRequest> requests)
    {
        var service = new SkenderIndicatorService();
        var values = new Dictionary<string, double>();
        foreach (var req in requests)
        {
            var key = req.Key;
            values[key] = req.Type switch
            {
                IndicatorType.Atr => service.Atr(bars, req.Period),
                IndicatorType.Ema => service.Ema(bars, req.Period),
                IndicatorType.Sma => service.Sma(bars, req.Period),
                IndicatorType.Rsi => service.Rsi(bars, req.Period),
                IndicatorType.Adx => service.Adx(bars, req.Period),
                IndicatorType.Macd => service.Macd(bars, req.Period, req.Param1 > 0 ? req.Param1 : 26, (int)(req.Param2 > 0 ? req.Param2 : 9)).Histogram,
                IndicatorType.SuperTrend => service.SuperTrend(bars, req.Period, req.Param2 > 0 ? req.Param2 : 3.0).Line,
                IndicatorType.BollingerBands => service.BollingerBands(bars, req.Period, req.StdDev).Middle,
                _ => 0,
            };
            // Emit multi-key values
            if (req.Type == IndicatorType.BollingerBands)
            {
                var bb = service.BollingerBands(bars, req.Period, req.StdDev);
                values[$"{key}_Upper"] = bb.Upper;
                values[$"{key}_Lower"] = bb.Lower;
            }
            if (req.Type == IndicatorType.Macd)
            {
                var macd = service.Macd(bars, req.Period, req.Param1 > 0 ? req.Param1 : 26, (int)(req.Param2 > 0 ? req.Param2 : 9));
                values[$"{key}_Signal"] = macd.Signal;
                values[$"{key}_Histogram"] = macd.Histogram;
            }
            if (req.Type == IndicatorType.SuperTrend)
            {
                var st = service.SuperTrend(bars, req.Period, req.Param2 > 0 ? req.Param2 : 3.0);
                values[$"{key}_Direction"] = st.Direction;
            }
        }
        return values;
    }

    public static MarketContext MakeContext(Bar bar, string symbol, List<Bar> bars, IReadOnlyDictionary<string, double> indicatorValues)
    {
        var tick = new Tick(Symbol.Parse(symbol), bar.Close, bar.Close, bar.OpenTimeUtc);
        return new MarketContext(Symbol.Parse(symbol), tick,
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            indicatorValues, bar.OpenTimeUtc);
    }
}
