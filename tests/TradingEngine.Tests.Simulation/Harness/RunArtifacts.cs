namespace TradingEngine.Tests.Simulation.Harness;

public sealed class RunArtifacts : IAsyncDisposable
{
    public string RunId { get; }
    public string ResultsDir { get; }
    public string EngineLogPath { get; }
    public string CtraderLogPath { get; }
    public string ReportJsonPath { get; }
    public string ReportHtmlPath { get; }
    public string EventsJsonPath { get; }
    public string DbPath { get; }
    /// <summary>Directory the cBot writes its own report.json + events.json into (--ReportPath).</summary>
    public string CbotReportDir { get; }

    public static bool KeepOnFailure { get; set; } = true;

    private RunArtifacts(string runId, string resultsDir)
    {
        RunId = runId;
        ResultsDir = resultsDir;
        EngineLogPath = Path.Combine(resultsDir, $"{runId}-engine.log");
        CtraderLogPath = Path.Combine(resultsDir, $"{runId}-ctrader.log");
        ReportJsonPath = Path.Combine(resultsDir, $"{runId}-report.json");
        ReportHtmlPath = Path.Combine(resultsDir, $"{runId}-report.html");
        EventsJsonPath = Path.Combine(resultsDir, $"{runId}-events.json");
        DbPath = Path.Combine(resultsDir, $"{runId}.db");
        CbotReportDir = Path.Combine(resultsDir, "cbot");
    }

    public static RunArtifacts Create(string testName)
    {
        var runId = $"{Sanitize(testName)}-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..48];
        var dir = Path.Combine(Path.GetTempPath(), "shamshir-e2e", runId);
        Directory.CreateDirectory(dir);
        return new RunArtifacts(runId, dir);
    }

    private static string Sanitize(string name) =>
        new string(name.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()).Trim('-');

    public async ValueTask DisposeAsync()
    {
        if (KeepOnFailure && Directory.Exists(ResultsDir))
            return;

        for (var i = 0; i < 5; i++)
        {
            try { Directory.Delete(ResultsDir, recursive: true); break; }
            catch (IOException) { await Task.Delay(100); }
        }
    }
}
