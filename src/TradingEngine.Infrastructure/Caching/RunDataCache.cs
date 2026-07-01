using System.Collections.Concurrent;

namespace TradingEngine.Infrastructure.Caching;

public sealed class RunDataCache : IRunDataCache
{
    private readonly ConcurrentDictionary<string, RunEntry> _runs = new();

    public void AppendJournal(string runId, IReadOnlyList<StepRecord> batch)
    {
        var entry = _runs.GetOrAdd(runId, _ => new RunEntry());
        entry.AppendJournal(batch);
    }

    public void AppendEquity(string runId, IReadOnlyList<EquitySnapshot> batch)
    {
        var entry = _runs.GetOrAdd(runId, _ => new RunEntry());
        foreach (var snap in batch)
            entry.Equity.Add(snap);
        entry.DownsampleEquityIfNeeded();
        entry.InvalidateEquityCache();
    }

    public void AppendTrade(string runId, TradeResult trade)
    {
        var entry = _runs.GetOrAdd(runId, _ => new RunEntry());
        entry.Trades.Add(trade);
        entry.InvalidateTradeCache();
    }

    public IReadOnlyList<StepRecord> GetJournal(string runId, int maxEntries = 10000)
    {
        if (_runs.TryGetValue(runId, out var entry))
            return entry.GetJournal(maxEntries);
        return [];
    }

    public IReadOnlyList<EquitySnapshot> GetEquity(string runId)
    {
        if (_runs.TryGetValue(runId, out var entry))
            return entry.EquitySnapshot;
        return [];
    }

    public IReadOnlyList<TradeResult> GetTrades(string runId)
    {
        if (_runs.TryGetValue(runId, out var entry))
            return entry.TradeSnapshot;
        return [];
    }

    public void MarkCompleted(string runId)
    {
        if (_runs.TryGetValue(runId, out var entry))
            entry.MarkCompleted();
    }

    public void Evict(string runId)
    {
        _runs.TryRemove(runId, out _);
    }

    public bool HasRun(string runId) => _runs.ContainsKey(runId);

    public IReadOnlyList<string> GetRunIds() => _runs.Keys.ToList();

    public DateTime? GetCompletedAtUtc(string runId)
    {
        if (_runs.TryGetValue(runId, out var entry) && entry.CompletedAtUtc != DateTime.MaxValue)
            return entry.CompletedAtUtc;
        return null;
    }

    private sealed class RunEntry
    {
        private const int MaxJournal = 10000;

        private readonly ConcurrentQueue<StepRecord> _journal = new();
        private int _journalCount;
        private List<EquitySnapshot>? _equitySnapshot;
        private List<TradeResult>? _tradeSnapshot;
        private List<StepRecord>? _journalSnapshot;

        public ConcurrentBag<EquitySnapshot> Equity { get; } = new();
        public ConcurrentBag<TradeResult> Trades { get; } = new();

        public DateTime CompletedAtUtc { get; private set; } = DateTime.MaxValue;

        public IReadOnlyList<EquitySnapshot> EquitySnapshot
        {
            get
            {
                if (_equitySnapshot is null || CompletedAtUtc == DateTime.MaxValue)
                {
                    _equitySnapshot = Equity.OrderBy(e => e.TimestampUtc).ToList();
                }
                return _equitySnapshot;
            }
        }

        public IReadOnlyList<TradeResult> TradeSnapshot
        {
            get
            {
                if (_tradeSnapshot is null || CompletedAtUtc == DateTime.MaxValue)
                {
                    _tradeSnapshot = Trades.OrderByDescending(t => t.ClosedAtUtc).ToList();
                }
                return _tradeSnapshot;
            }
        }

        public void AppendJournal(IReadOnlyList<StepRecord> batch)
        {
            foreach (var record in batch)
            {
                _journal.Enqueue(record);
                if (Interlocked.Increment(ref _journalCount) > MaxJournal)
                {
                    _journal.TryDequeue(out _);
                    Interlocked.Decrement(ref _journalCount);
                }
            }
            _journalSnapshot = null;
        }

        public IReadOnlyList<StepRecord> GetJournal(int maxEntries)
        {
            if (_journalSnapshot is null || CompletedAtUtc == DateTime.MaxValue)
            {
                _journalSnapshot = _journal.OrderBy(j => j.SimTimeUtc).ThenBy(j => j.Seq)
                    .Take(Math.Min(maxEntries, MaxJournal))
                    .ToList();
            }
            return _journalSnapshot;
        }

        public void MarkCompleted()
        {
            CompletedAtUtc = DateTime.UtcNow;
        }

        public void InvalidateEquityCache() => _equitySnapshot = null;
        public void InvalidateTradeCache() => _tradeSnapshot = null;

        public void DownsampleEquityIfNeeded()
        {
            const int softCap = 20_000;
            const int margin = 2_000;
            if (Equity.Count <= softCap + margin) return;

            var kept = new List<EquitySnapshot>(softCap);
            var all = Equity.OrderBy(e => e.TimestampUtc).ToList();
            var step = Math.Max(2, all.Count / softCap);
            for (int i = 0; i < all.Count && kept.Count < softCap; i += step)
                kept.Add(all[i]);

            while (Equity.TryTake(out _)) { }
            foreach (var s in kept)
                Equity.Add(s);
        }
    }
}
