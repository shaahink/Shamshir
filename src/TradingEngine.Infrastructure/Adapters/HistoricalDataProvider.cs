using System.Globalization;
using System.Runtime.CompilerServices;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class HistoricalDataProvider : IMarketDataProvider
{
    private readonly string _dataDirectory;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private DateTime _from;
    private DateTime _to;

    public HistoricalDataProvider(string dataDirectory, ISymbolInfoRegistry? symbolRegistry = null)
    {
        _dataDirectory = dataDirectory;
        _symbolRegistry = symbolRegistry ?? new SymbolInfoRegistry();
    }

    public Task SeekAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        _from = from;
        _to = to;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<Bar> StreamBarsAsync(
        Symbol symbol, Timeframe tf,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var path = BuildPath(symbol, tf);
        if (!File.Exists(path)) yield break;

        using var reader = new StreamReader(path);
        var header = await reader.ReadLineAsync(ct);
        var isHeader = header != null && header.StartsWith("DateTime", StringComparison.OrdinalIgnoreCase);
        if (isHeader && header != null)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {

                var bar = ParseBar(line, symbol, tf);
                if (bar is null) continue;
                if (bar.OpenTimeUtc < _from) continue;
                if (bar.OpenTimeUtc > _to) yield break;

                yield return bar;
            }
        }
    }

    public IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct)
        => StreamTicksAsync(symbol, ct, Timeframe.H1);

    public async IAsyncEnumerable<Tick> StreamTicksAsync(
        Symbol symbol,
        [EnumeratorCancellation] CancellationToken ct,
        Timeframe tf)
    {
        var barDuration = GetBarDuration(tf);
        var halfSpread = ResolveHalfSpread(symbol);

        await foreach (var bar in StreamBarsAsync(symbol, tf, ct))
        {
            var ticks = SynthesizeTicks(bar, barDuration, halfSpread);
            foreach (var tick in ticks)
                yield return tick;
        }
    }

    private decimal ResolveHalfSpread(Symbol symbol)
    {
        try
        {
            var info = _symbolRegistry.Get(symbol);
            return info.TypicalSpread / 2m;
        }
        catch
        {
            return 0.0005m;
        }
    }

    private string BuildPath(Symbol symbol, Timeframe tf)
    {
        var symbolLower = symbol.ToString().ToLowerInvariant();
        var tfStr = tf.ToString().ToLowerInvariant();
        return Path.Combine(_dataDirectory, $"{symbolLower}-{tfStr}-2024.csv");
    }

    private static Bar? ParseBar(string line, Symbol symbol, Timeframe tf)
    {
        var parts = line.Split(',');
        if (parts.Length < 6) return null;

        if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            return null;
        if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open))
            return null;
        if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high))
            return null;
        if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low))
            return null;
        if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
            return null;
        if (!double.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var volume))
            return null;

        return new Bar(symbol, tf, time, open, high, low, close, volume);
    }

    private static TimeSpan GetBarDuration(Timeframe tf) => tf switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.M30 => TimeSpan.FromMinutes(30),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.D1 => TimeSpan.FromDays(1),
        Timeframe.W1 => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1),
    };

    private static List<Tick> SynthesizeTicks(Bar bar, TimeSpan duration, decimal halfSpread)
    {
        var ticks = new List<Tick>(4);
        var quarter = TimeSpan.FromTicks(duration.Ticks / 4);

        var tick0 = new Tick(bar.Symbol, bar.Open, bar.Open + halfSpread, bar.OpenTimeUtc);
        ticks.Add(tick0);

        if (bar.IsBullish)
        {
            var tick1 = new Tick(bar.Symbol, bar.High, bar.High + halfSpread, bar.OpenTimeUtc + quarter);
            ticks.Add(tick1);
            var tick2 = new Tick(bar.Symbol, bar.Low, bar.Low + halfSpread, bar.OpenTimeUtc + 2 * quarter);
            ticks.Add(tick2);
        }
        else
        {
            var tick1 = new Tick(bar.Symbol, bar.Low, bar.Low + halfSpread, bar.OpenTimeUtc + quarter);
            ticks.Add(tick1);
            var tick2 = new Tick(bar.Symbol, bar.High, bar.High + halfSpread, bar.OpenTimeUtc + 2 * quarter);
            ticks.Add(tick2);
        }

        var tick3 = new Tick(bar.Symbol, bar.Close, bar.Close + halfSpread, bar.OpenTimeUtc + 3 * quarter);
        ticks.Add(tick3);

        return ticks;
    }
}
