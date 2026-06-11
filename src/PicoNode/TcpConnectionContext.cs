namespace PicoNode;

public sealed class TcpConnectionContext : ITcpConnectionContext
{
    private readonly TcpConnection _connection;

    internal TcpConnectionContext(TcpConnection connection)
    {
        _connection = connection;
    }

    public long ConnectionId => _connection.Id;

    public IPEndPoint RemoteEndPoint => _connection.RemoteEndPoint;

    public DateTimeOffset ConnectedAtUtc => _connection.ConnectedAtUtc;

    public DateTimeOffset LastActivityUtc => _connection.LastActivityUtc;

    public object? UserState { get; set; }

    public string? NegotiatedProtocol => _connection.NegotiatedProtocol;

    public Task SendAsync(
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken = default
    ) => _connection.SendAsync(buffer, cancellationToken);

    public void Close() => _connection.Close();
}
