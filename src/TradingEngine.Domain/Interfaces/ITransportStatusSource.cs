namespace TradingEngine.Domain;

public enum TransportPhase
{
    Disconnected,
    Connecting,
    HandshakeReceived,
    HandshakeAcknowledged,
    Connected,
    Error,
}

public sealed record TransportStatus(
    TransportPhase Phase,
    DateTime? ConnectedAtUtc,
    DateTime? DisconnectedAtUtc,
    DateTime? LastMessageAtUtc,
    int BarsReceived,
    int CommandsSent,
    int ExecutionsReceived,
    string? LastError);

public interface ITransportStatusSource
{
    TransportStatus Current { get; }
    event Action<TransportStatus>? StatusChanged;
}
