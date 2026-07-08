using System.Net;
using System.Net.Sockets;

namespace TradingEngine.Tests.Unit.Infrastructure;

// P0.2 (F5): the audited "every cTrader run saved failed" bug. The orchestrator disconnects the
// transport twice per cTrader run — the BarStream safety-net force-disconnect, then host disposal
// (adapter.DisposeAsync -> transport.DisconnectAsync). NetMQPoller.Stop()/Dispose() throw
// "ObjectDisposedException: NetMQPoller" on the second call; that exception used to propagate to the
// orchestrator's outer catch and overwrite a COMPLETE run's status with `failed`. Root-cause repro:
// a standalone NetMQPoller proved Stop() on a disposed poller throws that exact message. The fix makes
// DisconnectAsync single-shot so the second teardown is a no-op.
[Trait("Category", "Infrastructure")]
public sealed class NetMqTransportTeardownTests
{
    private static (string data, string command) AllocateEndpoints()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        // data endpoint is only connected-to (never bound here), command endpoint is bound by the router.
        return ($"tcp://127.0.0.1:{port}", $"tcp://*:{port}");
    }

    [Fact(Timeout = 15_000)]
    public async Task DisconnectAsync_CalledTwice_DoesNotThrow_DisposedPoller()
    {
        var (data, command) = AllocateEndpoints();
        var transport = new NetMqMessageTransport(data, command,
            Substitute.For<ILogger<NetMqMessageTransport>>());

        await transport.ConnectAsync(CancellationToken.None);

        // First teardown — the real one (mirrors the orchestrator's safety-net force-disconnect).
        await transport.DisconnectAsync(CancellationToken.None);

        // Second teardown — host disposal path. Before P0.2 this hit NetMQPoller.Stop() on a disposed
        // poller and threw ObjectDisposedException("NetMQPoller"); now it must be a silent no-op.
        var second = async () => await transport.DisconnectAsync(CancellationToken.None);
        await second.Should().NotThrowAsync(
            "a second teardown must be idempotent — the F5 crash was a disposed NetMQPoller re-Stop()");
    }

    [Fact(Timeout = 15_000)]
    public async Task DisposeAsync_AfterDisconnect_DoesNotThrow()
    {
        var (data, command) = AllocateEndpoints();
        var transport = new NetMqMessageTransport(data, command,
            Substitute.For<ILogger<NetMqMessageTransport>>());

        await transport.ConnectAsync(CancellationToken.None);
        await transport.DisconnectAsync(CancellationToken.None);

        // DisposeAsync() calls DisconnectAsync() again — this is the exact double-teardown the
        // orchestrator performs (force-disconnect then host disposal).
        var dispose = async () => await transport.DisposeAsync();
        await dispose.Should().NotThrowAsync();
    }
}
