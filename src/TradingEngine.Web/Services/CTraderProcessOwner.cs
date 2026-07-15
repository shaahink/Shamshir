using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace TradingEngine.Web.Services;

/// <summary>
/// X4 — the single owner of every <c>ctrader-cli</c> invocation, whether it is a backtest run
/// (<see cref="BacktestOrchestrator"/>) or a market-data download (<see cref="Api.DownloadJobService"/>).
/// Before X4 these were three code paths on three hardcoded port pairs (15555/6, 15562/3, and the
/// orchestrator's dynamic pair) with a private serial lane in the orchestrator only.
///
/// It provides three things, all required for the owner's goals of dynamic ports + parallel cTrader
/// + independence from any other process (e.g. a second worktree) driving cTrader:
/// <list type="bullet">
/// <item><b>Dynamic ports</b> — <see cref="AllocatePorts"/> hands out a fresh, currently-free loopback
///   (data, command) pair per invocation, so concurrent invocations never collide on a fixed port.</item>
/// <item><b>One bounded lane</b> — <see cref="AcquireAsync"/> gates all cTrader work through a single
///   semaphore (default 2 = parallel, 1 = serial) shared by backtest + download, so a download never
///   races a backtest beyond the configured bound.</item>
/// <item><b>Owned-PID reaping</b> — <see cref="Register"/>/<see cref="ReapOwned"/> tree-kill ONLY the
///   PIDs we launched. We never kill by image name (the pre-X4 reaper did, which is safe only under a
///   strict serial queue — under parallel cTrader, or alongside another process's ctrader-cli, it
///   cross-kills siblings). Per-run cancellation already tree-kills the owned process (CliWrap ct /
///   <see cref="Process.Kill(bool)"/>) and <c>ChildProcessReaper</c>'s Job Object is the crash net;
///   this owner adds the explicit, parallel-safe orphan sweep for the persistent-web-app case.</item>
/// </list>
/// </summary>
public sealed class CTraderProcessOwner
{
    private readonly SemaphoreSlim _lane;
    private readonly ILogger<CTraderProcessOwner> _logger;
    private readonly ConcurrentDictionary<int, OwnedProc> _owned = new();

    /// <summary>Configured max concurrent cTrader-cli invocations (≥ 1).</summary>
    public int MaxConcurrency { get; }

    public CTraderProcessOwner(IOptions<CTraderProcessOwnerOptions> options, ILogger<CTraderProcessOwner> logger)
    {
        MaxConcurrency = Math.Max(1, options.Value.MaxConcurrency);
        _lane = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        _logger = logger;
    }

    /// <summary>Free lane slots right now (used by the queue's admission check, mirroring the old semaphore).</summary>
    public int AvailableSlots => _lane.CurrentCount;

    /// <summary>A fresh, currently-free loopback (data, command) port pair for one cTrader-cli invocation.</summary>
    public static (int dataPort, int commandPort) AllocatePorts()
    {
        using var a = new TcpListener(IPAddress.Loopback, 0);
        using var b = new TcpListener(IPAddress.Loopback, 0);
        a.Start();
        b.Start();
        var p1 = ((IPEndPoint)a.LocalEndpoint!).Port;
        var p2 = ((IPEndPoint)b.LocalEndpoint!).Port;
        a.Stop();
        b.Stop();
        return (p1, p2);
    }

    /// <summary>Acquire one lane slot; dispose the returned token to release. Shared by backtest + download.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _lane.WaitAsync(ct).ConfigureAwait(false);
        return new LaneToken(_lane);
    }

    /// <summary>Record a PID we just launched so the orphan sweep can tree-kill only our own processes.</summary>
    public void Register(int pid, string tag)
    {
        _owned[pid] = new OwnedProc(tag, DateTime.UtcNow);
        _logger.LogDebug("CTRADER|OWN|pid={Pid}|tag={Tag}", pid, tag);
    }

    /// <summary>Drop a PID from the owned set once it has exited normally.</summary>
    public void Unregister(int pid) => _owned.TryRemove(pid, out _);

    /// <summary>
    /// Tree-kill only the owned PIDs carrying <paramref name="tag"/> (e.g. one run/download) that are
    /// still alive. Per-tag so cancelling run X never touches a sibling parallel run/download. Best-effort.
    /// </summary>
    public int ReapByTag(string tag, string reason)
    {
        var killed = 0;
        foreach (var (pid, info) in _owned.ToArray())
        {
            if (!string.Equals(info.Tag, tag, StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    _logger.LogInformation("CTRADER|REAP|pid={Pid}|tag={Tag}|reason={Reason}", pid, tag, reason);
                    proc.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception ex) { _logger.LogWarning(ex, "CTRADER|REAP_FAIL|pid={Pid}", pid); }
            finally { _owned.TryRemove(pid, out _); }
        }
        return killed;
    }

    /// <summary>
    /// Tree-kill only the PIDs WE own that are still alive (parallel-safe; never by image name).
    /// Best-effort and fully isolated — a reap failure must never propagate to a cancel/finalize path.
    /// </summary>
    public int ReapOwned(string reason)
    {
        var killed = 0;
        foreach (var (pid, info) in _owned.ToArray())
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    _logger.LogInformation("CTRADER|REAP|pid={Pid}|tag={Tag}|reason={Reason}", pid, info.Tag, reason);
                    proc.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch (ArgumentException)
            {
                // Process already gone — GetProcessById throws once the PID is no longer live.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CTRADER|REAP_FAIL|pid={Pid}", pid);
            }
            finally
            {
                _owned.TryRemove(pid, out _);
            }
        }
        return killed;
    }

    private sealed record OwnedProc(string Tag, DateTime StartedUtc);

    private sealed class LaneToken(SemaphoreSlim sem) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                sem.Release();
            }
        }
    }
}

/// <summary>Bound options for <see cref="CTraderProcessOwner"/>, section <c>CTrader:ProcessOwner</c>.</summary>
public sealed class CTraderProcessOwnerOptions
{
    /// <summary>Max concurrent cTrader-cli invocations across backtest + download. Default 2; 1 = serial.</summary>
    public int MaxConcurrency { get; set; } = 2;
}
