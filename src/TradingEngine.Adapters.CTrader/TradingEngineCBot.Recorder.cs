using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using cAlgo.API;
using cAlgo.API.Internals;
using NetMQ;
using NetMQ.Sockets;

namespace TradingEngine.Adapters.CTrader;

// Recorder mode (iter-marketdata-tape P2): --Record=true short-circuits the engine path and
// appends closed bars to NDJSON shards for MarketDataIngester. Split from TradingEngineCBot.cs
// verbatim — file organization only.
public partial class TradingEngineCBot
{
    // ── iter-marketdata-tape P2 — recorder mode ─────────────────────────────────────────────────────────
    // Run a plain backtest over the target range with --Record=true --ReportPath=<dir> --Periods=<tf(s)>.
    // cTrader replays history; each closed bar is appended to <ReportPath>/<SYMBOL>_<TF>.ndjson in the
    // MarketDataShardIo format. The .NET 8 MarketDataIngester then loads those shards into marketdata.db.
    // No engine, no NetMQ. Verification is owner-run (needs cTrader) — the wire format is locked by
    // MarketDataShardIoTests.Parses_the_exact_cbot_recorder_line.
    private void StartRecording()
    {
        if (string.IsNullOrWhiteSpace(ReportPath))
        {
            Print("CBOT|RECORD_ERROR|--ReportPath is required for --Record");
            Stop();
            return;
        }
        System.IO.Directory.CreateDirectory(ReportPath);

        var symbols = SymbolString.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var periods = Periods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var sym in symbols)
        {
            foreach (var period in periods)
            {
                var tf = ParseTimeFrame(period);
                var bars = MarketData.GetBars(tf, sym);
                _subscriptions.Add(bars);
                bars.BarClosed += OnBarClosed;
            }
        }
        Print($"CBOT|RECORD_START|path={ReportPath}|symbols={SymbolString}|periods={Periods}");
    }

    private void RecordBar(BarClosedEventArgs args)
    {
        var bars = args.Bars;
        var bar = bars.Last(1);
        if (bar.Open == 0 && bar.High == 0) return;

        var openUtc = DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc);
        // camelCase keys match MarketDataShardIo exactly (symbol/timeframe/openTimeUtc/open/high/low/close/volume).
        var symInfo = Symbols.GetSymbol(bars.SymbolName);
        var line = JsonSerializer.Serialize(new
        {
            symbol = bars.SymbolName,
            timeframe = bars.TimeFrame.ShortName,
            openTimeUtc = openUtc,
            open = bar.Open,
            high = bar.High,
            low = bar.Low,
            close = bar.Close,
            volume = bar.TickVolume,
            spread = (double)(symInfo?.Spread ?? 0),
        }, JsonOpts);

        GetShardWriter(bars.SymbolName, bars.TimeFrame.ShortName).WriteLine(line);
        _recordedBars++;
    }

    private System.IO.StreamWriter GetShardWriter(string symbol, string tf)
    {
        var key = $"{symbol}_{tf}";
        if (!_shardWriters.TryGetValue(key, out var w))
        {
            var path = System.IO.Path.Combine(ReportPath, $"{key}.ndjson");
            w = new System.IO.StreamWriter(path, append: true) { AutoFlush = false };
            _shardWriters[key] = w;
        }
        return w;
    }

    private void StopRecording()
    {
        foreach (var kv in _shardWriters)
        {
            try { kv.Value.Flush(); kv.Value.Dispose(); }
            catch (Exception ex) { Print($"CBOT|RECORD_FLUSH_ERR|{kv.Key}|{ex.Message}"); }
        }
        Print($"CBOT|RECORD_DONE|bars={_recordedBars}|shards={_shardWriters.Count}|path={ReportPath}");
    }
}
