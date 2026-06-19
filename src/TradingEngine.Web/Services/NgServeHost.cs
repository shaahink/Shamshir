using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace TradingEngine.Web.Services;

public sealed class NgServeHost : IHostedService, IDisposable
{
    private Process? _process;
    private readonly IHostEnvironment _env;
    private readonly ILogger<NgServeHost> _logger;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public NgServeHost(IHostEnvironment env, ILogger<NgServeHost> logger)
    {
        _env = env;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_env.IsDevelopment()) return;

        var webUiDir = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "web-ui"));
        if (!Directory.Exists(webUiDir))
        {
            _logger.LogWarning("NgServe: web-ui dir not found at {Dir}", webUiDir);
            return;
        }

        var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = isWin ? "cmd.exe" : "npm",
                Arguments = isWin ? "/c npm start" : "start",
                WorkingDirectory = webUiDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        _process.OutputDataReceived += (_, e) =>
        { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.LogInformation("[ng] {Msg}", e.Data); };
        _process.ErrorDataReceived += (_, e) =>
        { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.LogWarning("[ng:err] {Msg}", e.Data); };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _logger.LogInformation("NgServe started — waiting for Angular to be ready...");

        for (var i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await _http.GetAsync("http://localhost:4200", ct);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Angular ready on http://localhost:4200");
                    return;
                }
            }
            catch { /* not ready yet */ }
            await Task.Delay(1000, ct);
        }
        _logger.LogWarning("Angular did not become ready within 60s");
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.Dispose();
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _process?.Dispose();
}
