namespace TradingEngine.CTraderRunner;

/// <summary>
/// Harvests cTrader's native backtest report output from its Backtesting directory.
///
/// We deliberately do NOT use the <c>--report-json</c> CLI flag: it crashes cTrader-cli's
/// <c>BacktestReportSavingStateStrategy</c> ("Message expected" → NotImplementedException) when
/// the full NetMQ cBot tears down the process-global NetMQ context in OnStop, and that crash also
/// suppresses the report.html + events.json cTrader would otherwise write.
///
/// Instead we read what cTrader writes by default:
///   • <c>report.html</c>  — human-viewable report that EMBEDS the full summary as a
///                            &lt;script type="application/json" id="backtesting-report"&gt; blob.
///                            That blob is the exact same schema --report-json would have produced
///                            (main / equity / tradeStatistics / history[] / positions / orders …).
///   • <c>events.json</c>  — per-event ledger (Create Position / SL Hit / TP Hit …) with the
///                            per-event equity curve.
/// </summary>
public static class CtraderReportHarvester
{
    public sealed record HarvestResult(
        string? ReportHtmlPath,
        string? EventsJsonPath,
        string? ReportJsonPath,
        string? SourceBacktestDir);

    // Filenames the cBot's ShamshirTradeLogger writes into its --ReportPath dir. Mirror of the
    // constants in TradingEngine.Adapters.CTrader.ShamshirTradeLogger (kept in sync by hand — the
    // net6 algo project and this runner can't reference each other). Distinct from cTrader's own
    // report.json / events.json on purpose.
    public const string CbotReportFileName = "shamshir-report.json";
    public const string CbotEventsFileName = "shamshir-events.json";

    private const string ScriptOpenMarker = "id=\"backtesting-report\">";
    private const string ScriptCloseMarker = "</script>";

    /// <summary>
    /// Finds the Backtesting directory written by the run that started at/after <paramref name="sinceUtc"/>
    /// and copies report.html + (non-empty) events.json to the destination paths, plus extracts the
    /// embedded summary JSON to <paramref name="destReportJsonPath"/>.
    /// </summary>
    public static HarvestResult Harvest(
        string algoPath, DateTime sinceUtc,
        string destReportHtmlPath, string destEventsJsonPath, string destReportJsonPath)
    {
        var dir = FindBacktestDir(algoPath, sinceUtc);
        if (dir is null)
            return new HarvestResult(null, null, null, null);

        string? html = null, events = null, reportJson = null;

        var htmlSrc = Path.Combine(dir, "report.html");
        if (File.Exists(htmlSrc))
        {
            try
            {
                File.Copy(htmlSrc, destReportHtmlPath, overwrite: true);
                html = destReportHtmlPath;

                var summary = ExtractEmbeddedReportJson(htmlSrc);
                if (summary is not null)
                {
                    File.WriteAllText(destReportJsonPath, summary);
                    reportJson = destReportJsonPath;
                }
            }
            catch { /* best-effort harvest */ }
        }

        var eventsSrc = Path.Combine(dir, "events.json");
        // Skip a 0-byte events.json: cTrader writes an empty placeholder when report-saving was
        // interrupted (e.g. the --report-json crash) — copying it would mask the real failure.
        if (File.Exists(eventsSrc) && new FileInfo(eventsSrc).Length > 0)
        {
            try
            {
                File.Copy(eventsSrc, destEventsJsonPath, overwrite: true);
                events = destEventsJsonPath;
            }
            catch { /* best-effort harvest */ }
        }

        return new HarvestResult(html, events, reportJson, dir);
    }

    /// <summary>
    /// Locates the most-recent <c>Backtesting</c> directory under the algo's <c>data</c> tree whose
    /// report output was written at/after <paramref name="sinceUtc"/> (with a small clock skew
    /// allowance). Returns null if none — e.g. the report-saving crashed before writing anything.
    /// </summary>
    public static string? FindBacktestDir(string algoPath, DateTime sinceUtc)
    {
        var algoDir = Path.GetDirectoryName(algoPath);
        if (algoDir is null) return null;

        var dataDir = Path.Combine(algoDir, "data");
        if (!Directory.Exists(dataDir)) return null;

        var threshold = sinceUtc.AddSeconds(-2); // tolerate minor clock skew between us and cTrader

        return Directory.EnumerateDirectories(dataDir, "Backtesting", SearchOption.AllDirectories)
            .Select(d => new
            {
                Dir = d,
                Stamp = LatestReportWriteUtc(d),
            })
            .Where(x => x.Stamp >= threshold)
            .OrderByDescending(x => x.Stamp)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    private static DateTime LatestReportWriteUtc(string backtestDir)
    {
        var stamps = new List<DateTime>();
        foreach (var name in new[] { "report.html", "events.json" })
        {
            var p = Path.Combine(backtestDir, name);
            if (File.Exists(p)) stamps.Add(File.GetLastWriteTimeUtc(p));
        }
        return stamps.Count > 0 ? stamps.Max() : DateTime.MinValue;
    }

    /// <summary>
    /// Pulls the embedded summary JSON out of a cTrader report.html
    /// (<c>&lt;script type="application/json" id="backtesting-report"&gt;{…}&lt;/script&gt;</c>).
    /// Returns null if the marker isn't present.
    /// </summary>
    public static string? ExtractEmbeddedReportJson(string htmlPath)
    {
        if (!File.Exists(htmlPath)) return null;
        var html = File.ReadAllText(htmlPath);

        var open = html.IndexOf(ScriptOpenMarker, StringComparison.Ordinal);
        if (open < 0) return null;
        open += ScriptOpenMarker.Length;

        var close = html.IndexOf(ScriptCloseMarker, open, StringComparison.Ordinal);
        if (close < 0) return null;

        var json = html[open..close].Trim();
        return json.Length > 0 ? json : null;
    }
}
