using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace TradingEngine.Web.Api;

/// <summary>
/// Receives frontend JavaScript errors reported by the Angular ErrorLogService.
/// Writes them to the Serilog pipeline so they land in the same log file as backend
/// errors — one log for full-stack troubleshooting. Also writes a structured JSON-lines
/// file at logs/frontend-errors.jsonl for easy machine parsing.
/// </summary>
[ApiController]
[Route("api/log")]
public sealed class LogController : ControllerBase
{
    private readonly ILogger<LogController> _logger;
    private static readonly object WriteLock = new();

    public LogController(ILogger<LogController> logger)
    {
        _logger = logger;
    }

    public sealed record FrontendErrorReport(
        string Kind,
        string? Message,
        string? Stack,
        string? Url,
        int? Line,
        int? Col,
        string? Timestamp);

    [HttpPost("frontend")]
    public IActionResult Post([FromBody] FrontendErrorReport report)
    {
        var summary = $"[{report.Kind}] {report.Message}";

        if (report.Kind == "error" || report.Kind == "unhandled")
            _logger.LogError("Frontend {Kind}: {Message} at {Url}:{Line}:{Col}", report.Kind, report.Message, report.Url, report.Line, report.Col);
        else
            _logger.LogWarning("Frontend {Kind}: {Message}", report.Kind, report.Message);

        // Also append to a JSON-lines file for script-based analysis.
        AppendToJsonLines(report);

        return Ok(new { accepted = true });
    }

    private static void AppendToJsonLines(FrontendErrorReport report)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "frontend-errors.jsonl");
            lock (WriteLock)
                System.IO.File.AppendAllText(path, JsonSerializer.Serialize(report) + Environment.NewLine);
        }
        catch { /* best-effort */ }
    }
}
