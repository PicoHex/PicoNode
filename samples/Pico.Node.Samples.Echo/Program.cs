using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Pico.Node;
using Pico.Node.Abs;

var tcpNode = new TcpNode(
    new TcpNodeOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
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
Console.WriteLine("Press Enter to stop...");
Console.ReadLine();

await udpNode.DisposeAsync();
await tcpNode.DisposeAsync();

file sealed class EchoTcpHandler : ITcpConnectionHandler
{
    public Task OnConnectedAsync(
        ITcpConnectionContext connection,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public Task OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        // Echo: 将接收到的数据原样发送回去，并消费整个缓冲区
        _ = connection.SendAsync(buffer, cancellationToken);
        return ValueTask.FromResult(buffer.End);
    }
}

file sealed class EchoUdpHandler : IUdpDatagramHandler
{
    public Task OnDatagramAsync(
        IUdpDatagramContext context,
        ArraySegment<byte> datagram,
        CancellationToken cancellationToken
    ) => context.SendAsync(datagram, cancellationToken);
}
