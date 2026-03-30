using System.Buffers;
using System.IO.Pipelines;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Pico.Node;
using Pico.Node.Abs;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "tcp-throughput";
var options = ParseOptions(args.Skip(1).ToArray());

switch (mode)
{
    case "tcp-throughput":
        await RunTcpThroughputAsync(options);
        break;
    case "tcp-throughput-parallel":
        await RunTcpThroughputParallelAsync(options);
        break;
    case "tcp-churn":
        await RunTcpChurnAsync(options);
        break;
    case "udp-rate":
        await RunUdpRateAsync(options);
        break;
    case "udp-rate-parallel":
        await RunUdpRateParallelAsync(options);
        break;
    default:
        Console.WriteLine("Modes: tcp-throughput | tcp-throughput-parallel | tcp-churn | udp-rate | udp-rate-parallel");
        Environment.ExitCode = 1;
        break;
}

static async Task RunTcpThroughputAsync(Dictionary<string, string> options)
{
    var port = GetInt(options, "port", 7201);
    var messageCount = GetInt(options, "messages", 5000);
    var payloadSize = GetInt(options, "payload", 256);
    var payload = CreatePayload(payloadSize, 7);

    await using var node = new TcpNode(
        new TcpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, port),
            ConnectionHandler = new TcpEchoHandler(),
            MaxConnections = GetInt(options, "max-connections", 128),
        }
    );

    await node.StartAsync();

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    using var stream = client.GetStream();
    var readBuffer = new byte[payload.Length];

    var stopwatch = Stopwatch.StartNew();
    for (var i = 0; i < messageCount; i++)
    {
        await stream.WriteAsync(payload);
        await ReadExactAsync(stream, readBuffer, payload.Length);
    }

    stopwatch.Stop();
    var totalBytes = (long)messageCount * payload.Length * 2;
    PrintMetrics("tcp-throughput", messageCount, totalBytes, stopwatch.Elapsed, "messages/s");
}

static async Task RunTcpThroughputParallelAsync(Dictionary<string, string> options)
{
    var port = GetInt(options, "port", 7204);
    var clients = GetInt(options, "clients", 8);
    var messagesPerClient = GetInt(options, "messages", 2000);
    var payloadSize = GetInt(options, "payload", 256);
    var payload = CreatePayload(payloadSize, 13);

    await using var node = new TcpNode(
        new TcpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, port),
            ConnectionHandler = new TcpEchoHandler(),
            MaxConnections = Math.Max(clients * 2, 128),
        }
    );

    await node.StartAsync();

    var stopwatch = Stopwatch.StartNew();
    await Task.WhenAll(
        Enumerable.Range(0, clients).Select(_ => RunTcpClientLoopAsync(port, messagesPerClient, payload))
    );
    stopwatch.Stop();

    var totalMessages = (long)clients * messagesPerClient;
    var totalBytes = totalMessages * payload.Length * 2;
    PrintMetrics(
        "tcp-throughput-parallel",
        totalMessages,
        totalBytes,
        stopwatch.Elapsed,
        "messages/s"
    );
}

static async Task RunTcpChurnAsync(Dictionary<string, string> options)
{
    var port = GetInt(options, "port", 7202);
    var connectionCount = GetInt(options, "connections", 2000);
    var payload = CreatePayload(GetInt(options, "payload", 4), 17);

    await using var node = new TcpNode(
        new TcpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, port),
            ConnectionHandler = new TcpEchoHandler(),
            MaxConnections = GetInt(options, "max-connections", 256),
        }
    );

    await node.StartAsync();

    var stopwatch = Stopwatch.StartNew();
    for (var i = 0; i < connectionCount; i++)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        await stream.WriteAsync(payload);
        var buffer = new byte[payload.Length];
        await ReadExactAsync(stream, buffer, buffer.Length);
    }

    stopwatch.Stop();
    var totalBytes = (long)connectionCount * payload.Length * 2;
    PrintMetrics("tcp-churn", connectionCount, totalBytes, stopwatch.Elapsed, "connections/s");
}

static async Task RunUdpRateAsync(Dictionary<string, string> options)
{
    var port = GetInt(options, "port", 7203);
    var datagramCount = GetInt(options, "datagrams", 10000);
    var payload = CreatePayload(GetInt(options, "payload", 128), 11);

    await using var node = new UdpNode(
        new UdpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, port),
            DatagramHandler = new UdpEchoHandler(),
            DispatchWorkerCount = GetInt(options, "workers", 2),
        }
    );

    await node.StartAsync();

    using var client = new UdpClient();
    client.Client.ReceiveTimeout = 5000;
    var endpoint = new IPEndPoint(IPAddress.Loopback, port);

    var stopwatch = Stopwatch.StartNew();
    for (var i = 0; i < datagramCount; i++)
    {
        await client.SendAsync(payload, payload.Length, endpoint);
        await client.ReceiveAsync();
    }

    stopwatch.Stop();
    var totalBytes = (long)datagramCount * payload.Length * 2;
    PrintMetrics("udp-rate", datagramCount, totalBytes, stopwatch.Elapsed, "datagrams/s");
}

static async Task RunUdpRateParallelAsync(Dictionary<string, string> options)
{
    var port = GetInt(options, "port", 7205);
    var clients = GetInt(options, "clients", 8);
    var datagramsPerClient = GetInt(options, "datagrams", 3000);
    var payload = CreatePayload(GetInt(options, "payload", 128), 23);

    await using var node = new UdpNode(
        new UdpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, port),
            DatagramHandler = new UdpEchoHandler(),
            DispatchWorkerCount = GetInt(options, "workers", 4),
        }
    );

    await node.StartAsync();

    var stopwatch = Stopwatch.StartNew();
    await Task.WhenAll(
        Enumerable.Range(0, clients).Select(_ => RunUdpClientLoopAsync(port, datagramsPerClient, payload))
    );
    stopwatch.Stop();

    var totalDatagrams = (long)clients * datagramsPerClient;
    var totalBytes = totalDatagrams * payload.Length * 2;
    PrintMetrics(
        "udp-rate-parallel",
        totalDatagrams,
        totalBytes,
        stopwatch.Elapsed,
        "datagrams/s"
    );
}

static async Task RunTcpClientLoopAsync(int port, int messageCount, byte[] payload)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    using var stream = client.GetStream();
    var buffer = new byte[payload.Length];

    for (var i = 0; i < messageCount; i++)
    {
        await stream.WriteAsync(payload);
        await ReadExactAsync(stream, buffer, buffer.Length);
    }
}

static async Task RunUdpClientLoopAsync(int port, int datagramCount, byte[] payload)
{
    using var client = new UdpClient();
    client.Client.ReceiveTimeout = 5000;
    var endpoint = new IPEndPoint(IPAddress.Loopback, port);

    for (var i = 0; i < datagramCount; i++)
    {
        await client.SendAsync(payload, payload.Length, endpoint);
        await client.ReceiveAsync();
    }
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = arg[2..];
        var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++i]
            : "true";
        options[key] = value;
    }

    return options;
}

static int GetInt(Dictionary<string, string> options, string key, int fallback)
{
    if (!options.TryGetValue(key, out var value))
    {
        return fallback;
    }

    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : fallback;
}

static byte[] CreatePayload(int size, int seed)
{
    var payload = new byte[size];
    new Random(seed).NextBytes(payload);
    return payload;
}

static void PrintMetrics(
    string name,
    long operations,
    long totalBytes,
    TimeSpan elapsed,
    string rateLabel
)
{
    var seconds = Math.Max(elapsed.TotalSeconds, 0.000001d);
    var opsPerSecond = operations / seconds;
    var mibPerSecond = totalBytes / seconds / 1024d / 1024d;

    Console.WriteLine(
        $"{name} ops={operations} bytes={totalBytes} elapsed={elapsed.TotalMilliseconds:F0}ms rate={opsPerSecond:F0} {rateLabel} throughput={mibPerSecond:F2} MiB/s"
    );
}

static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
{
    var offset = 0;
    while (offset < count)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
        if (read == 0)
        {
            throw new InvalidOperationException("Connection closed while reading.");
        }

        offset += read;
    }
}

file sealed class TcpEchoHandler : ITcpConnectionHandler
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
