namespace PicoNode.PerfHarness;

/// <summary>
/// Scenario smoke tests for the throughput harness. Each one asserts a <b>floor</b>, not the number
/// itself: the harness prints what it measured, and the assertion fails only when the hot path has
/// degraded by an order of magnitude (the measured spread and the headroom of every constant are
/// recorded in <c>.superpowers/sdd/2026-10-22-test-quality/task-T4.5-report.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// These used to assert <c>OpsPerSecond &gt; 0</c>, which a single round trip satisfies — i.e. no number
/// the harness could ever print would fail them. A floor is the cheapest thing that can: it is a
/// bound with headroom, so a per-message allocation, an extra syscall per message or a serialized send
/// loop drops the rate far below it while ordinary machine noise does not.
/// </para>
/// <para>
/// <see cref="NotInParallelAttribute"/> because these tests measure wall time: with six harness loops
/// and the allocation test competing for the same core (the repo runs the perf suite under a 1-CPU
/// affinity mask), the measured rate is a property of the scheduler rather than of the code, and the
/// floor would have to be lowered until it caught nothing.
/// </para>
/// </remarks>
[NotInParallel]
public sealed class PerfHarnessTests
{
    static PerfHarnessTests() => PerfHarnessEnvironment.RaiseThreadPoolMinimum();

    /// <summary>
    /// Round trips spent on the same connection before the clock starts. Without them the timed window
    /// contains the JIT of the receive/send loops and the pipe setup of the first exchange — tens of
    /// milliseconds on a masked core, i.e. more than a short measured window — so the printed rate is
    /// dominated by fixed cost instead of by the hot path under test.
    /// </summary>
    private const int WarmUpRoundTrips = 100;

    // Floors for the six scenarios. They are catastrophe tripwires, not the performance bound: this
    // assembly runs concurrently with seven other test assemblies when the solution is tested, so the
    // rate these short windows measure says as much about the other processes as about the node
    // (measured: 10 300-13 600 round trips/s when the harness is alone on a 1-CPU mask, 2 200-4 500 when
    // the whole solution runs, and individual runs at ~500 when eight CLRs are starting at once). A
    // floor derived from the quiet measurement is a flaky test, which is worse than a weak one. The
    // bound that is meant to bite lives in TcpEchoHotPathFloors, which warms up, measures three rounds
    // and takes the best.
    private const double TcpRoundTripsFloor = 150;
    private const double TcpParallelRoundTripsFloor = 700;
    private const double TcpChangeoversFloor = 200;
    private const double UdpDatagramsFloor = 1_500;
    private const double UdpParallelDatagramsFloor = 3_000;

    [Test]
    public async Task Tcp_throughput_succeeds()
    {
        var metrics = await RunTcpThroughputAsync(port: 7201, messageCount: 200, payloadSize: 128);
        await AssertFloorsAsync("tcp-throughput", metrics, TcpRoundTripsFloor);
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
        await AssertFloorsAsync("tcp-throughput-parallel", metrics, TcpParallelRoundTripsFloor);
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
        foreach (var point in metrics)
        {
            // The sweep covers the per-message overhead at two payload sizes; the small payload is the
            // one that exposes a fixed cost per round trip, so both points are held to the same floor.
            await AssertFloorsAsync("tcp-throughput-sweep", point, TcpRoundTripsFloor);
        }
    }

    [Test]
    public async Task Tcp_churn_succeeds()
    {
        var metrics = await RunTcpChurnAsync(port: 7202, connectionCount: 120, payloadSize: 8);
        await AssertFloorsAsync("tcp-churn", metrics, TcpChangeoversFloor);
        await Assert.That(metrics.MiBPerSecond).IsGreaterThan(0d);
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
        await AssertFloorsAsync("udp-rate", metrics, UdpDatagramsFloor);
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
        await AssertFloorsAsync("udp-rate-parallel", metrics, UdpParallelDatagramsFloor);
    }

    /// <summary>
    /// One place for the two assertions every scenario makes, so the failure message carries the floor,
    /// the measurement and the headroom instead of a bare "expected true".
    /// </summary>
    private static async Task AssertFloorsAsync(
        string scenario,
        PerfMetrics metrics,
        double operationsPerSecondFloor
    )
    {
        // The byte rate is derived from the same clock, so it only adds a bound when the payload is
        // large enough for it to be non-zero at the floor: the churn scenario moves 8-byte payloads and
        // its byte floor would be a rounding error, not a bound.
        await Assert
            .That(metrics.OpsPerSecond)
            .IsGreaterThan(operationsPerSecondFloor)
            .Because(
                $"{scenario} ran at {metrics.OpsPerSecond:F0} ops/s, floor is {operationsPerSecondFloor:F0} "
                    + $"({metrics.OpsPerSecond / operationsPerSecondFloor:F1}x headroom)"
            );
        await Assert
            .That(metrics.MiBPerSecond)
            .IsGreaterThan(0d)
            .Because($"{scenario} moved no bytes at all");
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

        await WarmUpTcpAsync(stream, payload, readBuffer);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < messageCount; i++)
        {
            await stream.WriteAsync(payload);
            await ReadExactAsync(stream, readBuffer, payload);
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

            await WarmUpTcpAsync(stream, payload, readBuffer);

            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < messages; i++)
            {
                await stream.WriteAsync(payload);
                await ReadExactAsync(stream, readBuffer, payload);
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
            await ReadExactAsync(stream, buffer, payload);
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

        await WarmUpUdpAsync(client, endpoint, payload);

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

        await WarmUpTcpAsync(stream, payload, buffer);

        for (var i = 0; i < messageCount; i++)
        {
            await stream.WriteAsync(payload);
            await ReadExactAsync(stream, buffer, payload);
        }
    }

    private static async Task RunUdpClientLoopAsync(int port, int datagramCount, byte[] payload)
    {
        using var client = new UdpClient();
        client.Client.ReceiveTimeout = 5000;
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        await WarmUpUdpAsync(client, endpoint, payload);

        for (var i = 0; i < datagramCount; i++)
        {
            await client.SendAsync(payload, payload.Length, endpoint);
            await client.ReceiveAsync();
        }
    }

    private static async Task WarmUpTcpAsync(NetworkStream stream, byte[] payload, byte[] buffer)
    {
        for (var i = 0; i < WarmUpRoundTrips; i++)
        {
            await stream.WriteAsync(payload);
            await ReadExactAsync(stream, buffer, payload);
        }
    }

    private static async Task WarmUpUdpAsync(
        UdpClient client,
        IPEndPoint endpoint,
        byte[] payload
    )
    {
        for (var i = 0; i < WarmUpRoundTrips; i++)
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

    /// <summary>
    /// Reads exactly <paramref name="expected"/>.Length bytes and verifies that they are the bytes the
    /// echo was supposed to return. Comparing the length alone is not enough: an echo that answers with
    /// the right number of <b>zero</b> bytes — or with another message's bytes — keeps the round trip
    /// and the throughput number alive while the payload itself is wrong.
    /// </summary>
    private static async Task ReadExactAsync(
        NetworkStream stream,
        byte[] buffer,
        byte[] expected
    )
    {
        var count = expected.Length;
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

        var written = buffer.AsSpan(0, count);
        if (!written.SequenceEqual(expected))
        {
            var firstDiff = 0;
            while (firstDiff < count && written[firstDiff] == expected[firstDiff])
            {
                firstDiff++;
            }

            throw new InvalidOperationException(
                $"echo payload mismatch at byte {firstDiff} of {count}: "
                    + $"got 0x{written[firstDiff]:X2}, want 0x{expected[firstDiff]:X2}"
            );
        }
    }
}

file sealed class TcpEchoHandler : ITcpConnectionHandler
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

file sealed class UdpEchoHandler : IUdpDatagramHandler
{
    public ValueTask OnDatagramAsync(
        IUdpDatagramContext context,
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken
    ) => new ValueTask(context.SendAsync(datagram, cancellationToken));
}

readonly record struct PerfMetrics(double OpsPerSecond, double MiBPerSecond);
