var tcpPort = 7001;
if (args.Length >= 2 && args[0] == "--tcp-port" && int.TryParse(args[1], out var parsedTcp))
{
    tcpPort = parsedTcp;
}

var tcpNode = new TcpNode(
    new TcpNodeOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, tcpPort),
        ConnectionHandler = new EchoTcpHandler(),
        EnableKeepAlive = true,
    }
);

var udpNode = new UdpNode(
    new UdpNodeOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 7002),
        DatagramHandler = new EchoUdpHandler(),
    }
);

await tcpNode.StartAsync();
await udpNode.StartAsync();

Console.WriteLine($"TCP echo listening on {tcpNode.LocalEndPoint}");
Console.WriteLine($"UDP echo listening on {udpNode.LocalEndPoint}");

// Keep the server alive without depending on stdin — a redirected/absent
// stdin would end the process immediately (breaks CI and scripted runs).
await Task.Delay(Timeout.Infinite);

file sealed class EchoTcpHandler : ITcpConnectionHandler
{
    public Task OnConnectedAsync(
        ITcpConnectionContext connection,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public ValueTask OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        _ = connection.SendAsync(buffer, cancellationToken);
        return ValueTask.FromResult(buffer.End);
    }
}

file sealed class EchoUdpHandler : IUdpDatagramHandler
{
    public ValueTask OnDatagramAsync(
        IUdpDatagramContext context,
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken
    ) => new ValueTask(context.SendAsync(datagram, cancellationToken));
}
