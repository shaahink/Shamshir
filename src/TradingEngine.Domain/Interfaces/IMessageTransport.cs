using System.Threading.Channels;

namespace TradingEngine.Domain;

public interface IMessageTransport
{
    ChannelReader<(string Topic, string Json)> SubMessages { get; }
    ChannelReader<(byte[] Identity, string Json)> RouterMessages { get; }
    void Send(byte[] identity, string json);
    bool IsConnected { get; }
    Action? OnConnected { get; set; }
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}
