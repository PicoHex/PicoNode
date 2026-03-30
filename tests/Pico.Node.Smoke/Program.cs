using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Pico.Node;
using Pico.Node.Abs;

await RunTcpSmokeAsync();
await RunUdpSmokeAsync();

Console.WriteLine("Smoke checks passed.");

static async Task RunTcpSmokeAsync()
{
    var node = new TcpNode(
        new TcpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 7101),
            ConnectionHandler = new TcpCollectorHandler(),
            DrainTimeout = TimeSpan.FromSeconds(2),
        }
    );

    await node.StartAsync();

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, 7101);
    using var stream = client.GetStream();

    var payload = new byte[] { 1, 2, 3, 4 };
    await stream.WriteAsync(payload);

    var buffer = new byte[4];
    var read = await stream.ReadAsync(buffer);
    if (read != payload.Length)
    {
        throw new InvalidOperationException("TCP smoke read length mismatch.");
    }

    for (var i = 0; i < payload.Length; i++)
    {
        if (buffer[i] != payload[i])
        {
            throw new InvalidOperationException("TCP smoke payload mismatch.");
        }
    }

    await node.DisposeAsync();
}

static async Task RunUdpSmokeAsync()
{
    var node = new UdpNode(
        new UdpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 7102),
            DatagramHandler = new UdpEchoHandler(),
        }
    );

    await node.StartAsync();

    using var client = new UdpClient();
    var payload = new byte[] { 9, 8, 7, 6 };
    await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, 7102));
    var result = await client.ReceiveAsync();

    if (result.Buffer.Length != payload.Length)
    {
        throw new InvalidOperationException("UDP smoke read length mismatch.");
    }

    for (var i = 0; i < payload.Length; i++)
    {
        if (result.Buffer[i] != payload[i])
        {
            throw new InvalidOperationException("UDP smoke payload mismatch.");
        }
    }

    await node.DisposeAsync();
}

file sealed class TcpCollectorHandler : ITcpConnectionHandler
{
    public Task OnConnectedAsync(ITcpConnectionContext connection, CancellationToken cancellationToken)
        => Task.CompletedTask;

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

file sealed class UdpEchoHandler : IUdpDatagramHandler
{
    public Task OnDatagramAsync(
        IUdpDatagramContext context,
        ArraySegment<byte> datagram,
        CancellationToken cancellationToken
    ) => context.SendAsync(datagram, cancellationToken);
}
