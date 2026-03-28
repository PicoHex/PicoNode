using System.Net;
using System.Net.Sockets;
using Pico.Node;
using Pico.Node.Abs;

var tcpNode = new TcpNode(
    new TcpNodeOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
        Handler = new EchoTcpHandler(),
    }
);

var udpNode = new UdpNode(
    new UdpNodeOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 7002),
        Handler = new EchoUdpHandler(),
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

    public Task OnReceivedAsync(
        ITcpConnectionContext connection,
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken
    ) => connection.SendAsync(buffer, cancellationToken);
}

file sealed class EchoUdpHandler : IUdpDatagramHandler
{
    public Task OnDatagramAsync(
        IUdpDatagramContext context,
        ArraySegment<byte> datagram,
        CancellationToken cancellationToken
    ) => context.SendAsync(datagram, cancellationToken);
}
