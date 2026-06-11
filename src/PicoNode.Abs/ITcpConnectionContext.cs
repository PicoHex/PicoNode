namespace PicoNode.Abs;

public interface ITcpConnectionContext
{
    long ConnectionId { get; }
    IPEndPoint RemoteEndPoint { get; }
    DateTimeOffset ConnectedAtUtc { get; }
    DateTimeOffset LastActivityUtc { get; }
    object? UserState { get; set; }
    Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken = default);
    void Close();

    /// <summary>ALPN-negotiated protocol, e.g. "h2", "http/1.1". Null when not negotiated.</summary>
    string? NegotiatedProtocol { get; }
}
