namespace TradingEngine.Tests.Simulation.Harness;

public sealed class BarBuilder
{
    private readonly List<Bar> _bars = [];
    private readonly Symbol _symbol;
    private readonly TimeSpan _interval;
    private DateTime _nextTime;
    private decimal _lastClose;
    private readonly Random _random = new(42);

    public BarBuilder(Symbol symbol, TimeSpan interval, DateTime start, decimal initialClose)
    {
        _symbol = symbol;
        _interval = interval;
        _nextTime = start;
        _lastClose = initialClose;
    }

    public BarBuilder Trend(string direction, int pipCount, int barCount)
    {
        var pipsPerBar = (decimal)pipCount / barCount;
        for (var i = 0; i < barCount; i++)
        {
            var delta = direction == "up" ? pipsPerBar : -pipsPerBar;
            var open = _lastClose;
            _lastClose += delta;
            var high = Math.Max(open, _lastClose) + pipsPerBar * 0.3m;
            var low = Math.Min(open, _lastClose) - pipsPerBar * 0.3m;
            _bars.Add(new Bar(_symbol, Timeframe.H1, _nextTime, high, high, low, _lastClose, 1000));
            _nextTime += _interval;
        }
        return this;
    }

    public BarBuilder Range(decimal center, int widthPips, int barCount)
    {
        var halfRange = (decimal)widthPips / 2;
        var low = center - halfRange;
        var high = center + halfRange;

        for (var i = 0; i < barCount; i++)
        {
            var close = low + (decimal)_random.NextDouble() * (high - low);
            var barHigh = close + 0.0003m * (decimal)_random.NextDouble();
            var barLow = close - 0.0003m * (decimal)_random.NextDouble();
            _bars.Add(new Bar(_symbol, Timeframe.H1, _nextTime, barHigh, barHigh, barLow, close, 1000));
            _nextTime += _interval;
        }
        _lastClose = _bars[^1].Close;
        return this;
    }

    public BarBuilder Spike(int magnitudePips, int barCount)
    {
        var deltaPerBar = (decimal)magnitudePips / barCount;
        for (var i = 0; i < barCount; i++)
        {
            var open = _lastClose;
            _lastClose += deltaPerBar;
            var wickExtent = Math.Abs(deltaPerBar) * 2;
            var high = Math.Max(open, _lastClose) + wickExtent;
            var low = Math.Min(open, _lastClose) - wickExtent;
            _bars.Add(new Bar(_symbol, Timeframe.H1, _nextTime, high, high, low, _lastClose, 1000));
            _nextTime += _interval;
        }
        return this;
    }

    public BarBuilder Gap(int pips, int barCount)
    {
        _lastClose += (decimal)pips;
        for (var i = 0; i < barCount; i++)
        {
            var open = _lastClose;
            _lastClose += 0.0001m * (decimal)(_random.NextDouble() - 0.5);
            var high = open + 0.0005m;
            var low = open - 0.0003m;
            _bars.Add(new Bar(_symbol, Timeframe.H1, _nextTime, high, high, low, _lastClose, 1000));
            _nextTime += _interval;
        }
        return this;
    }

    public IReadOnlyList<Bar> Build() => _bars;
}

public static class Bars
{
    public static BarBuilder Trend(Symbol symbol, TimeSpan interval, DateTime start, decimal startPrice, int pips, int barCount)
    {
        var direction = pips >= 0 ? "up" : "down";
        return new BarBuilder(symbol, interval, start, startPrice).Trend(direction, Math.Abs(pips), barCount);
    }

    public static BarBuilder Range(Symbol symbol, TimeSpan interval, DateTime start, decimal startPrice, decimal center, int widthPips, int barCount)
    {
        return new BarBuilder(symbol, interval, start, startPrice).Range(center, widthPips, barCount);
    }
}
