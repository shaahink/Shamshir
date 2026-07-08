using System.Text;

namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.1 (Q3) — the thin HTTP shell to the RUNNING Web app (default <c>https://localhost:7108</c>). No
/// second in-process runner: one engine instance, the UI sees everything live, the agent never touches
/// Angular. Auth: none today (don't invent one — PLAN §12). All parsing/decisions live in the pure
/// helpers (<see cref="RunJson"/>, <see cref="GateEvaluator"/>, <see cref="InventoryCoverage"/>,
/// <see cref="StartRunPlan"/>); this only moves text over the wire.
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

    public async Task<string> GetInventoryAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("api/data-manager/inventory", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> StartDownloadAsync(string bodyJson, CancellationToken ct)
    {
        using var resp = await _http.PostAsync("api/data-manager/download", Body(bodyJson), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> StartRunAsync(string bodyJson, CancellationToken ct)
    {
        using var resp = await _http.PostAsync("api/runs", Body(bodyJson), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> PostAsync(string path, string bodyJson, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(path, Body(bodyJson), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    public void Dispose() => _http.Dispose();
}
