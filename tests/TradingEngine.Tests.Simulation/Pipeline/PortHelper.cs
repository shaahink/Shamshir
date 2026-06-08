using System.Net;
using System.Net.Sockets;

namespace TradingEngine.Tests.Simulation.Pipeline;

internal static class PortHelper
{
    public static (int dataPort, int commandPort) AllocatePair()
    {
        using var a = new TcpListener(IPAddress.Loopback, 0);
        using var b = new TcpListener(IPAddress.Loopback, 0);
        a.Start(); b.Start();
        var p1 = ((IPEndPoint)a.LocalEndpoint).Port;
        var p2 = ((IPEndPoint)b.LocalEndpoint).Port;
        a.Stop(); b.Stop();
        return (p1, p2);
    }
}
