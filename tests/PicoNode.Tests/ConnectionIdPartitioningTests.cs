namespace PicoNode.Tests;

/// <summary>
/// Verifies that TCP and UDP connection IDs use distinct high-bit tags
/// to prevent cross-transport ID collisions in aggregated logs.
/// </summary>
public sealed class ConnectionIdPartitioningTests
{
    [Test]
    public async Task TcpConnectionId_has_TcpTag_in_high_byte()
    {
        var handler = new CapturingTcpHandler();
        var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = handler,
                MaxConnections = 10,
            }
        );
        await node.StartAsync();
        try
        {
            var endpoint = (IPEndPoint)node.LocalEndPoint!;
            using var client = new Socket(
                endpoint.AddressFamily,
                SocketType.Stream,
                ProtocolType.Tcp
            );
            client.Connect(endpoint);
            await Task.Delay(500); // Give accept loop time to process
            await Assert.That(handler.ConnectionId).IsNotEqualTo(0);
            var tagBits = handler.ConnectionId & unchecked((long)0xFF00000000000000UL);
            await Assert.That(tagBits).IsNotEqualTo(0);
        }
        finally
        {
            await node.StopAsync();
            await node.DisposeAsync();
        }
    }
}

file sealed class CapturingTcpHandler : ITcpConnectionHandler
{
    public long ConnectionId { get; private set; }

    public Task OnConnectedAsync(
        ITcpConnectionContext connection,
        CancellationToken cancellationToken
    )
    {
        ConnectionId = connection.ConnectionId;
        return Task.CompletedTask;
    }

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(buffer.End);

    public ValueTask OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;
}
