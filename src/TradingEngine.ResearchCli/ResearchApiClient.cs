namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.1 (Q3) — the thin HTTP shell to the RUNNING Web app (default <c>https://localhost:7108</c>). No
/// second in-process runner: one engine instance, the UI sees everything live, the agent never touches
/// Angular. Auth: none today (don't invent one — PLAN §12). All parsing/decisions live in the pure
/// helpers (<see cref="RunJson"/>, <see cref="GateEvaluator"/>); this only fetches text.
/// </summary>
public sealed class ResearchApiClient : IDisposable
{
    private readonly HttpClient _http;

    public ResearchApiClient(string baseUrl, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            // Local dev cert; the CLI drives localhost only.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = timeout,
        };
    }

    public async Task<string> GetRunAsync(string runId, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"api/runs/{runId}", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> GetReconcileAsync(string left, string right, CancellationToken ct)
    {
        var url = $"api/backtest/analytics/reconcile?left={Uri.EscapeDataString(left)}&right={Uri.EscapeDataString(right)}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public void Dispose() => _http.Dispose();
}
