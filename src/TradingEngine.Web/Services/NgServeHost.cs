using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TradingEngine.Web.Services;

/// <summary>
/// Spawns <c>ng serve</c> when the app starts in development. Angular CLI compiles,
/// watches for changes, and serves on port 4200 with proxy to this API.
/// Intentionally NOT an IHostedService — it's fire-and-forget; the Angular dev server
/// outlives API restarts.
/// </summary>
public sealed class NgServeHost : IHostedService, IDisposable
{
    private Process? _process;
    private readonly IHostEnvironment _env;
    private readonly ILogger<NgServeHost> _logger;

    public NgServeHost(IHostEnvironment env, ILogger<NgServeHost> logger)
    {
        _env = env;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (!_env.IsDevelopment()) return Task.CompletedTask;

        var webUiDir = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "web-ui"));
        if (!Directory.Exists(webUiDir))
        {
            _logger.LogWarning("NgServe: web-ui directory not found at {Dir}", webUiDir);
            return Task.CompletedTask;
        }

        var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var cmd = isWin ? "cmd.exe" : "npm";
        var args = isWin ? "/c npm start" : "start";

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cmd,
                Arguments = args,
                WorkingDirectory = webUiDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _logger.LogInformation("[ng] {Msg}", e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _logger.LogWarning("[ng:err] {Msg}", e.Data);
        };
        _process.Exited += (_, _) => _logger.LogWarning("NgServe exited (code {Code})", _process.ExitCode);

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _logger.LogInformation("NgServe started — Angular on http://localhost:4200");
        return Task.CompletedTask;
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
