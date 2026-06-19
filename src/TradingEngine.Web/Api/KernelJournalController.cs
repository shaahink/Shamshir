using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TradingEngine.Domain;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/runs/{runId}/kernel-journal")]
public sealed class KernelJournalController : ControllerBase
{
    private readonly IJournalQueryRepository _repo;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public KernelJournalController(IJournalQueryRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> Get(
        string runId, [FromQuery] long? afterSeq, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var entries = await _repo.GetByRunAsync(runId, afterSeq, Math.Min(limit, 1000), ct);
        return Ok(entries);
    }

    [HttpGet("export")]
    public async Task Export(string runId, [FromQuery] long? afterSeq, CancellationToken ct = default)
    {
        Response.ContentType = "application/x-ndjson";
        Response.Headers.TryAdd("Content-Disposition", $"attachment; filename=\"{runId}-journal.ndjson\"");

        await foreach (var entry in _repo.StreamByRunAsync(runId, afterSeq, ct))
        {
            var line = JsonSerializer.Serialize(entry, JsonOpts);
            await Response.WriteAsync(line + "\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}
