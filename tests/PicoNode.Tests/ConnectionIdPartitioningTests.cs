namespace PicoNode.Tests;

/// <summary>
/// Verifies that TCP and UDP connection IDs use distinct high-bit tags
/// to prevent cross-transport ID collisions in aggregated logs.
/// </summary>
/// <remarks>
/// Each test pins the <b>exact</b> tag. The previous version asserted only that the high byte was non-zero, so
/// swapping the two tags (TCP = <c>2 &lt;&lt; 56</c>, UDP = <c>1 &lt;&lt; 56</c>) left it green — and a swap is
/// exactly the mistake that makes a TCP id indistinguishable from a UDP one in a log line.
/// </remarks>
public sealed class ConnectionIdPartitioningTests
{
    private const long TcpTag = 0x0100_0000_0000_0000;
    private const long UdpTag = 0x0200_0000_0000_0000;
    private const long TagMask = unchecked((long)0xFF00_0000_0000_0000);

    [Test]
    public async Task Tcp_connection_id_carries_the_tcp_tag_in_its_high_byte()
    {
        var handler = new CapturingTcpHandler();
        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = handler,
                MaxConnections = 10,
            }
        );
        await node.StartAsync();

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client.ConnectAsync(node.LocalEndPoint!);

        var connectionId = await handler.WaitForConnectionIdAsync();

        await Assert.That(connectionId & TagMask).IsEqualTo(TcpTag);
        await Assert
            .That(connectionId & TagMask)
            .IsNotEqualTo(UdpTag)
            .Because("a TCP id must never look like a UDP id in an aggregated log");
    }

    [Test]
    public async Task Udp_connection_id_carries_the_udp_tag_in_its_high_byte()
    {
        var handler = new CapturingUdpHandler();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = handler,
            }
        );
        await node.StartAsync();
        var endpoint = (IPEndPoint)node.LocalEndPoint!;

        using var client = new UdpClient();
        await client.SendAsync("ping"u8.ToArray(), endpoint);

        var connectionId = await handler.WaitForConnectionIdAsync();

        await Assert.That(connectionId & TagMask).IsEqualTo(UdpTag);
        await Assert
            .That(connectionId & TagMask)
            .IsNotEqualTo(TcpTag)
            .Because("a UDP id must never look like a TCP id in an aggregated log");
    }
}

file sealed class CapturingTcpHandler : ITcpConnectionHandler
{
    private readonly TaskCompletionSource<long> _connectionId = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task<long> WaitForConnectionIdAsync() =>
        _connectionId.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public Task OnConnectedAsync(
        ITcpConnectionContext connection,
        CancellationToken cancellationToken
    )
    {
        _connectionId.TrySetResult(connection.ConnectionId);
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

file sealed class CapturingUdpHandler : IUdpDatagramHandler
{
    private readonly TaskCompletionSource<long> _connectionId = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task<long> WaitForConnectionIdAsync() =>
        _connectionId.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public ValueTask OnDatagramAsync(
        IUdpDatagramContext context,
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken
    )
    {
        _connectionId.TrySetResult(context.ConnectionId);
        return ValueTask.CompletedTask;
    }
}
