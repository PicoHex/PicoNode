namespace PicoNode.PerfHarness;

public sealed class PerfHarnessTests
{
    [Test]
    public async Task Tcp_throughput_succeeds()
    {
        var metrics = await RunTcpThroughputAsync(port: 7201, messageCount: 200, payloadSize: 128);
        await Assert.That(metrics.OpsPerSecond > 0).IsTrue();
        await Assert.That(metrics.MiBPerSecond > 0).IsTrue();
    }

    [Test]
    public async Task Tcp_throughput_parallel_succeeds()
    {
        var metrics = await RunTcpThroughputParallelAsync(
            port: 7204,
            clients: 4,
            messagesPerClient: 150,
            payloadSize: 128
        );
        await Assert.That(metrics.OpsPerSecond > 0).IsTrue();
        await Assert.That(metrics.MiBPerSecond > 0).IsTrue();
    }

    [Test]
    public async Task Tcp_throughput_sweep_succeeds()
    {
        var metrics = await RunTcpThroughputSweepAsync(
            port: 7210,
            payloads: [64, 256],
            messages: 120
        );
        await Assert.That(metrics.Length).IsEqualTo(2);
        await Assert.That(metrics.All(x => x.OpsPerSecond > 0)).IsTrue();
    }

    [Test]
    public async Task Tcp_churn_succeeds()
    {
        var metrics = await RunTcpChurnAsync(port: 7202, connectionCount: 120, payloadSize: 8);
        await Assert.That(metrics.OpsPerSecond > 0).IsTrue();
        await Assert.That(metrics.MiBPerSecond > 0).IsTrue();
    }

    [Test]
    public async Task Udp_rate_succeeds()
    {
        var metrics = await RunUdpRateAsync(
            port: 7203,
            datagramCount: 250,
            payloadSize: 96,
            workers: 2
        );
        await Assert.That(metrics.OpsPerSecond > 0).IsTrue();
        await Assert.That(metrics.MiBPerSecond > 0).IsTrue();
    }

    [Test]
    public async Task Udp_rate_parallel_succeeds()
    {
        var metrics = await RunUdpRateParallelAsync(
            port: 7205,
            clients: 4,
            datagramsPerClient: 120,
            payloadSize: 96,
            workers: 2
        );
        await Assert.That(metrics.OpsPerSecond > 0).IsTrue();
        await Assert.That(metrics.MiBPerSecond > 0).IsTrue();
    }

    private static async Task<PerfMetrics> RunTcpThroughputAsync(
        int port,
        int messageCount,
        int payloadSize
    )
    {
        var payload = CreatePayload(payloadSize, 7);

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = new TcpEchoHandler(),
                MaxConnections = 64,
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
        var metrics = CreateMetrics(messageCount, totalBytes, stopwatch.Elapsed);
        PrintMetrics(
            "tcp-throughput",
            messageCount,
            totalBytes,
            stopwatch.Elapsed,
            "messages/s",
            metrics
        );
        return metrics;
    }

    private static async Task<PerfMetrics> RunTcpThroughputParallelAsync(
        int port,
        int clients,
        int messagesPerClient,
        int payloadSize
    )
    {
        var payload = CreatePayload(payloadSize, 13);

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = new TcpEchoHandler(),
                MaxConnections = Math.Max(clients * 2, 64),
            }
        );

        await node.StartAsync();

        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(
            Enumerable
                .Range(0, clients)
                .Select(_ => RunTcpClientLoopAsync(port, messagesPerClient, payload))
        );
        stopwatch.Stop();

        var totalMessages = (long)clients * messagesPerClient;
        var totalBytes = totalMessages * payload.Length * 2;
        var metrics = CreateMetrics(totalMessages, totalBytes, stopwatch.Elapsed);
        PrintMetrics(
            "tcp-throughput-parallel",
            totalMessages,
            totalBytes,
            stopwatch.Elapsed,
            "messages/s",
            metrics
        );
        return metrics;
    }

    private static async Task<PerfMetrics[]> RunTcpThroughputSweepAsync(
        int port,
        int[] payloads,
        int messages
    )
    {
        var results = new List<PerfMetrics>(payloads.Length);

        foreach (var payloadSize in payloads)
        {
            var payload = CreatePayload(payloadSize, payloadSize);

            await using var node = new TcpNode(
                new TcpNodeOptions
                {
                    Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                    ConnectionHandler = new TcpEchoHandler(),
                    MaxConnections = 64,
                }
            );

            await node.StartAsync();

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();
            var readBuffer = new byte[payload.Length];

            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < messages; i++)
            {
                await stream.WriteAsync(payload);
                await ReadExactAsync(stream, readBuffer, payload.Length);
            }

            stopwatch.Stop();
            var totalBytes = (long)messages * payload.Length * 2;
            var metrics = CreateMetrics(messages, totalBytes, stopwatch.Elapsed);
            PrintMetrics(
                $"tcp-throughput-sweep payload={payloadSize}",
                messages,
                totalBytes,
                stopwatch.Elapsed,
                "messages/s",
                metrics
            );
            results.Add(metrics);
            port++;
        }

        return results.ToArray();
    }

    private static async Task<PerfMetrics> RunTcpChurnAsync(
        int port,
        int connectionCount,
        int payloadSize
    )
    {
        var payload = CreatePayload(payloadSize, 17);

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = new TcpEchoHandler(),
                MaxConnections = 128,
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
        var metrics = CreateMetrics(connectionCount, totalBytes, stopwatch.Elapsed);
        PrintMetrics(
            "tcp-churn",
            connectionCount,
            totalBytes,
            stopwatch.Elapsed,
            "connections/s",
            metrics
        );
        return metrics;
    }

    private static async Task<PerfMetrics> RunUdpRateAsync(
        int port,
        int datagramCount,
        int payloadSize,
        int workers
    )
    {
        var payload = CreatePayload(payloadSize, 11);

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = new UdpEchoHandler(),
                DispatchWorkerCount = workers,
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
        var metrics = CreateMetrics(datagramCount, totalBytes, stopwatch.Elapsed);
        PrintMetrics(
            "udp-rate",
            datagramCount,
            totalBytes,
            stopwatch.Elapsed,
            "datagrams/s",
            metrics
        );
        return metrics;
    }

    private static async Task<PerfMetrics> RunUdpRateParallelAsync(
        int port,
        int clients,
        int datagramsPerClient,
        int payloadSize,
        int workers
    )
    {
        var payload = CreatePayload(payloadSize, 23);

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = new UdpEchoHandler(),
                DispatchWorkerCount = workers,
            }
        );

        await node.StartAsync();

        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(
            Enumerable
                .Range(0, clients)
                .Select(_ => RunUdpClientLoopAsync(port, datagramsPerClient, payload))
        );
        stopwatch.Stop();

        var totalDatagrams = (long)clients * datagramsPerClient;
        var totalBytes = totalDatagrams * payload.Length * 2;
        var metrics = CreateMetrics(totalDatagrams, totalBytes, stopwatch.Elapsed);
        PrintMetrics(
            "udp-rate-parallel",
            totalDatagrams,
            totalBytes,
            stopwatch.Elapsed,
            "datagrams/s",
            metrics
        );
        return metrics;
    }

    private static async Task RunTcpClientLoopAsync(int port, int messageCount, byte[] payload)
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

    private static async Task RunUdpClientLoopAsync(int port, int datagramCount, byte[] payload)
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

    private static byte[] CreatePayload(int size, int seed)
    {
        var payload = new byte[size];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    private static void PrintMetrics(
        string name,
        long operations,
        long totalBytes,
        TimeSpan elapsed,
        string rateLabel,
        PerfMetrics metrics
    )
    {
        Console.WriteLine(
            $"{name} ops={operations} bytes={totalBytes} elapsed={elapsed.TotalMilliseconds:F0}ms rate={metrics.OpsPerSecond:F0} {rateLabel} throughput={metrics.MiBPerSecond:F2} MiB/s"
        );
    }

    private static PerfMetrics CreateMetrics(long operations, long totalBytes, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.000001d);
        return new PerfMetrics(operations / seconds, totalBytes / seconds / 1024d / 1024d);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
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
}

file sealed class TcpEchoHandler : ITcpConnectionHandler
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

readonly record struct PerfMetrics(double OpsPerSecond, double MiBPerSecond);
