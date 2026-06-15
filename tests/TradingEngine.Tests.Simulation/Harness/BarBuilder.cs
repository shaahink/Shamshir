namespace TradingEngine.Tests.Simulation.Harness;

public sealed class BarBuilder
{
    private readonly List<Bar> _bars = [];
    private readonly Symbol _symbol;
    private readonly Timeframe _timeframe;
    private readonly decimal _pipSize;
    private DateTime _nextTime;
    private decimal _lastClose;
    private readonly Random _random = new(42);

    public BarBuilder(Symbol symbol, Timeframe timeframe, DateTime start, decimal initialClose, decimal pipSize = 0.0001m)
    {
        _symbol = symbol;
        _timeframe = timeframe;
        _nextTime = start;
        _lastClose = initialClose;
        _pipSize = pipSize;
    }

    private decimal PipsToPrice(int pips) => pips * _pipSize;

    public BarBuilder Trend(string direction, int pipCount, int barCount)
    {
        var deltaPerBar = PipsToPrice(pipCount) / barCount;
        var wiggle = PipsToPrice(3);
        for (var i = 0; i < barCount; i++)
        {
            var delta = direction == "up" ? deltaPerBar : -deltaPerBar;
            var open = _lastClose;
            _lastClose += delta;
            var high = Math.Max(open, _lastClose) + wiggle;
            var low = Math.Min(open, _lastClose) - wiggle;
            _bars.Add(new Bar(_symbol, _timeframe, _nextTime, open, high, low, _lastClose, 1000));
            _nextTime += TimeSpan.FromHours(1);
        }
        return this;
    }

    public BarBuilder Range(decimal centerPrice, int widthPips, int barCount)
    {
        var halfRange = PipsToPrice(widthPips) / 2m;
        var low = centerPrice - halfRange;
        var high = centerPrice + halfRange;
        var wiggle = PipsToPrice(1);

        for (var i = 0; i < barCount; i++)
        {
            var close = low + (decimal)_random.NextDouble() * (high - low);
            var open = close + (decimal)(_random.NextDouble() - 0.5) * wiggle * 2m;
            var barHigh = Math.Max(open, close) + wiggle;
            var barLow = Math.Min(open, close) - wiggle;
            _bars.Add(new Bar(_symbol, _timeframe, _nextTime, open, barHigh, barLow, close, 1000));
            _nextTime += TimeSpan.FromHours(1);
        }
        _lastClose = _bars[^1].Close;
        return this;
    }

    public BarBuilder Spike(int magnitudePips, int barCount)
    {
        var deltaPerBar = PipsToPrice(magnitudePips) / barCount;
        for (var i = 0; i < barCount; i++)
        {
            var open = _lastClose;
            _lastClose += deltaPerBar;
            var wick = Math.Abs(deltaPerBar) * 3m;
            var high = Math.Max(open, _lastClose) + wick;
            var low = Math.Min(open, _lastClose) - wick;
            _bars.Add(new Bar(_symbol, _timeframe, _nextTime, open, high, low, _lastClose, 1000));
            _nextTime += TimeSpan.FromHours(1);
        }
        return this;
    }

    public BarBuilder Gap(int pips, int barCount)
    {
        _lastClose += PipsToPrice(pips);
        var wiggle = PipsToPrice(2);
        for (var i = 0; i < barCount; i++)
        {
            var open = _lastClose;
            _lastClose += PipsToPrice(1) * (decimal)(_random.NextDouble() - 0.5);
            var high = Math.Max(open, _lastClose) + wiggle;
            var low = Math.Min(open, _lastClose) - wiggle;
            _bars.Add(new Bar(_symbol, _timeframe, _nextTime, open, high, low, _lastClose, 1000));
            _nextTime += TimeSpan.FromHours(1);
        }
        return this;
    }

    public IReadOnlyList<Bar> Build() => _bars;
}

public static class Bars
{
    public static BarBuilder Trend(Symbol symbol, Timeframe timeframe, DateTime start, decimal startPrice, int pips, int barCount)
        => new BarBuilder(symbol, timeframe, start, startPrice).Trend(pips >= 0 ? "up" : "down", Math.Abs(pips), barCount);

    public static IReadOnlyList<Bar> TrendUpThenDown(Symbol symbol, Timeframe timeframe, DateTime start, decimal startPrice,
        int upPips, int upBars, int downPips, int downBars)
    {
        return new BarBuilder(symbol, timeframe, start, startPrice)
            .Trend("up", upPips, upBars)
            .Trend("down", downPips, downBars)
            .Build();
    }
}
