namespace PicoNode.Tests;

public sealed class TcpNodeMetricsTests
{
    [Test]
    public async Task GetMetrics_counts_every_byte_while_readers_hammer_the_snapshot()
    {
        // What this test used to do: connect, never send a byte, and hammer GetMetrics from 8 threads
        // while asserting the total never decreases. With nothing in flight the total was 0 for every
        // sample, so "never decreases" held trivially — the test could not fail, and a counter that
        // lost writes would still have passed it.
        //
        // Overlapping work is what makes the counters observable: four connections write at the same
        // time (so RecordBytesSent/RecordBytesReceived are entered from four threads at once, which is
        // the only situation a lost update or a torn rate snapshot can happen in) while eight readers
        // sample the snapshot the whole time. The total is then asserted exactly: the sum the server
        // counted must be the sum the clients put on the wire, not "roughly".
        const int Connections = 4;
        const int ChunkBytes = 1024;
        const int ChunksPerConnection = 512;
        const int Readers = 8;
        const long TotalBytes = (long)Connections * ChunksPerConnection * ChunkBytes;

        await using var node = CreateNode(new EchoTcpHandler(), maxConnections: Connections);
        await node.StartAsync();
        var endpoint = (IPEndPoint)node.LocalEndPoint!;

        var clients = new Socket[Connections];
        try
        {
            foreach (var index in Enumerable.Range(0, Connections))
            {
                clients[index] = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Stream,
                    ProtocolType.Tcp
                );
                await clients[index].ConnectAsync(endpoint);
            }

            var stop = 0;
            var startGate = new ManualResetEventSlim(false);
            var firstSample = new CountdownEvent(Readers);
            var failures = new ConcurrentQueue<string>();
            long samples = 0;
            long intermediateSamples = 0;

            // Dedicated threads, not Task.Run: a reader spins until the writers are done, and on a
            // 1-CPU mask eight spinning pool threads would delay the very work being observed.
            var readers = new Thread[Readers];
            for (var index = 0; index < Readers; index++)
            {
                var readerIndex = index;
                readers[index] = new Thread(() =>
                {
                    long localSamples = 0;
                    long localIntermediate = 0;
                    var last = 0L;
                    var first = true;
                    startGate.Wait();
                    while (Volatile.Read(ref stop) == 0)
                    {
                        var metrics = node.GetMetrics();
                        if (metrics.TotalBytesSent < last)
                        {
                            failures.Enqueue(
                                $"reader {readerIndex}: TotalBytesSent went backwards "
                                    + $"{last} -> {metrics.TotalBytesSent}"
                            );
                        }

                        if (metrics.TotalBytesSent > TotalBytes)
                        {
                            failures.Enqueue(
                                $"reader {readerIndex}: TotalBytesSent {metrics.TotalBytesSent} "
                                    + $"exceeds the {TotalBytes} bytes on the wire"
                            );
                        }

                        if (metrics.BytesSentPerSecond < 0 || metrics.BytesReceivedPerSecond < 0)
                        {
                            failures.Enqueue(
                                $"reader {readerIndex}: negative rate "
                                    + $"({metrics.BytesSentPerSecond}, {metrics.BytesReceivedPerSecond})"
                            );
                        }

                        // A sample strictly between zero and the total was taken while the writers were
                        // still working: that is what proves the reads overlapped the traffic instead of
                        // bracketing it.
                        if (metrics.TotalBytesSent > 0 && metrics.TotalBytesSent < TotalBytes)
                        {
                            localIntermediate++;
                        }

                        last = metrics.TotalBytesSent;
                        localSamples++;
                        if (first)
                        {
                            first = false;
                            firstSample.Signal();
                        }

                        // Sleep rather than Yield: on a 1-CPU affinity mask twelve runnable threads
                        // share one core in ~15 ms slices, and a yielding reader would get one sample
                        // per slice while starving the writers it is supposed to observe. A reader that
                        // sleeps hands the core back and samples again a millisecond later.
                        Thread.Sleep(1);
                    }

                    Interlocked.Add(ref samples, localSamples);
                    Interlocked.Add(ref intermediateSamples, localIntermediate);
                })
                {
                    IsBackground = true,
                    Name = $"metrics-reader-{index}",
                };
            }

            foreach (var reader in readers)
            {
                reader.Start();
            }

            startGate.Set();

            // Every reader has sampled at least once before the first byte is written, so the samples
            // below span the whole write window rather than only its tail.
            await Assert.That(firstSample.Wait(TimeSpan.FromSeconds(10))).IsTrue();

            await Task.WhenAll(
                clients.Select(client =>
                    Task.Run(() => EchoAsync(client, ChunksPerConnection, ChunkBytes))
                )
            );

            Volatile.Write(ref stop, 1);
            foreach (var reader in readers)
            {
                await Assert.That(reader.Join(TimeSpan.FromSeconds(10))).IsTrue();
            }

            await Assert
                .That(failures)
                .IsEmpty()
                .Because("every snapshot must be self-consistent: " + string.Join("; ", failures));

            // The total is exact, not "at least": both counters are written by four connections at the
            // same time, so a lost update (a plain += instead of Interlocked.Add) shows up here as a
            // total that is short by exactly the increments that were overwritten.
            var final = await WaitForTotalsAsync(node, TotalBytes);
            await Assert
                .That(final.TotalBytesReceived)
                .IsEqualTo(TotalBytes)
                .Because("every byte the clients wrote must have been received");
            await Assert
                .That(final.TotalBytesSent)
                .IsEqualTo(TotalBytes)
                .Because("every byte that was echoed must have been counted as sent");

            Console.WriteLine(
                $"tcp-metrics-overlap samples={samples} intermediate={intermediateSamples} "
                    + $"readers={Readers} writers={Connections} bytes={TotalBytes}"
            );

            // Genuine overlap, not a sequential run that happens to end at the right total.
            await Assert
                .That(intermediateSamples)
                .IsGreaterThan(0)
                .Because(
                    $"no reader saw the totals move ({samples} samples, all zero or final): the reads "
                        + "did not overlap the writes, so the snapshot was only ever taken of a settled counter"
                );
            await Assert.That(samples).IsGreaterThanOrEqualTo(Readers);
        }
        finally
        {
            foreach (var client in clients)
            {
                client?.Dispose();
            }
        }
    }

    /// <summary>Sends <paramref name="chunks"/> chunks over one connection and reads every echo back.</summary>
    private static async Task EchoAsync(Socket socket, int chunks, int chunkBytes)
    {
        var payload = new byte[chunkBytes];
        new Random(chunkBytes ^ Environment.CurrentManagedThreadId).NextBytes(payload);
        var readBuffer = new byte[chunkBytes];

        for (var i = 0; i < chunks; i++)
        {
            await socket.SendAsync(payload, SocketFlags.None);
            var offset = 0;
            while (offset < chunkBytes)
            {
                var read = await socket.ReceiveAsync(readBuffer.AsMemory(offset), SocketFlags.None);
                if (read == 0)
                {
                    throw new InvalidOperationException("the echo closed the connection");
                }

                offset += read;
            }

            if (!readBuffer.AsSpan().SequenceEqual(payload))
            {
                throw new InvalidOperationException("the echo returned different bytes");
            }
        }
    }

    /// <summary>
    /// Polls until both byte counters have reached <paramref name="expected"/>, because a connection's
    /// final records are written after the client has already read the echo. Returns the last snapshot
    /// when the deadline passes, so the assertion shows the actual numbers.
    /// </summary>
    private static async Task<TcpNodeMetrics> WaitForTotalsAsync(TcpNode node, long expected)
    {
        var metrics = node.GetMetrics();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            metrics = node.GetMetrics();
            if (metrics.TotalBytesSent >= expected && metrics.TotalBytesReceived >= expected)
            {
                return metrics;
            }

            await Task.Delay(10);
        }

        return metrics;
    }

    [Test]
    public async Task GetMetrics_returns_zero_counters_before_any_activity()
    {
        await using var node = CreateNode(new EchoTcpHandler());
        await node.StartAsync();

        var metrics = node.GetMetrics();

        await Assert.That(metrics.TotalAccepted).IsEqualTo(0);
        await Assert.That(metrics.TotalRejected).IsEqualTo(0);
        await Assert.That(metrics.TotalClosed).IsEqualTo(0);
        await Assert.That(metrics.ActiveConnections).IsEqualTo(0);
        await Assert.That(metrics.TotalBytesSent).IsEqualTo(0);
        await Assert.That(metrics.TotalBytesReceived).IsEqualTo(0);
    }

    [Test]
    public async Task GetMetrics_tracks_accepted_and_active_connections()
    {
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler);
        await node.StartAsync();

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);

        await handler.WaitConnectedAsync();

        var metrics = node.GetMetrics();

        await Assert.That(metrics.TotalAccepted).IsEqualTo(1);
        await Assert.That(metrics.ActiveConnections).IsEqualTo(1);
    }

    [Test]
    public async Task GetMetrics_tracks_closed_connections()
    {
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler);
        await node.StartAsync();

        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.WaitConnectedAsync();

        client.Shutdown(SocketShutdown.Both);
        client.Dispose();

        await handler.WaitClosedAsync();

        // Wait for the close lifecycle to fully complete (TotalClosed is incremented
        // after OnClosedAsync returns, so we may need to wait)
        var metrics = await WaitForTotalClosedAsync(node, expected: 1);

        await Assert.That(metrics.TotalAccepted).IsEqualTo(1);
        await Assert.That(metrics.ActiveConnections).IsEqualTo(0);
    }

    [Test]
    public async Task GetMetrics_tracks_bytes_received()
    {
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler);
        await node.StartAsync();

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.WaitConnectedAsync();

        var data = "Hello, World!"u8.ToArray();
        await client.SendAsync(data, SocketFlags.None);

        await handler.WaitDataReceivedAsync();

        // On ARM64, RecordBytesReceived may not be immediately visible.
        // Retry with a short delay to allow Interlocked.Add to propagate.
        TcpNodeMetrics metrics = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            metrics = node.GetMetrics();
            if (metrics.TotalBytesReceived >= data.Length)
                break;
            await Task.Delay(10);
        }

        await Assert.That(metrics.TotalBytesReceived).IsGreaterThanOrEqualTo(data.Length);
    }

    [Test]
    public async Task GetMetrics_tracks_bytes_sent()
    {
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler);
        await node.StartAsync();

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.WaitConnectedAsync();

        var data = "Hello!"u8.ToArray();
        await client.SendAsync(data, SocketFlags.None);

        var buffer = new byte[256];
        var received = await client.ReceiveAsync(buffer, SocketFlags.None);

        await Assert.That(received).IsEqualTo(data.Length);

        // On ARM64, RecordBytesSent may not be visible to GetMetrics
        // immediately after the remote client receives the echo.
        // Retry with a short delay to allow Interlocked.Add to propagate.
        TcpNodeMetrics metrics = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            metrics = node.GetMetrics();
            if (metrics.TotalBytesSent >= data.Length)
                break;
            await Task.Delay(10);
        }

        await Assert.That(metrics.TotalBytesSent).IsGreaterThanOrEqualTo(data.Length);
    }

    [Test]
    public async Task GetMetrics_tracks_rejected_connections_at_max()
    {
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler, maxConnections: 1);
        await node.StartAsync();

        using var client1 = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client1.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.WaitConnectedAsync();

        using var client2 = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client2.ConnectAsync((IPEndPoint)node.LocalEndPoint);

        // Wait briefly for the rejection to process
        await Task.Delay(200);

        var metrics = node.GetMetrics();

        await Assert.That(metrics.TotalRejected).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task TcpNodeMetrics_exposes_expected_properties()
    {
        var type = typeof(TcpNodeMetrics);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToArray();

        await Assert.That(type.IsSealed).IsTrue();
        await Assert
            .That(properties)
            .IsEquivalentTo([
                nameof(TcpNodeMetrics.AcceptRatePerSecond),
                nameof(TcpNodeMetrics.ActiveConnections),
                nameof(TcpNodeMetrics.BytesReceivedPerSecond),
                nameof(TcpNodeMetrics.BytesSentPerSecond),
                nameof(TcpNodeMetrics.TotalAccepted),
                nameof(TcpNodeMetrics.TotalBytesSent),
                nameof(TcpNodeMetrics.TotalBytesReceived),
                nameof(TcpNodeMetrics.TotalClosed),
                nameof(TcpNodeMetrics.TotalRejected),
            ]);
    }

    /// <summary>Poll metrics until TotalClosed reaches the expected value or timeout.</summary>
    [Test]
    public async Task GetMetrics_negative_elapsed_call_does_not_reset_rate_baseline()
    {
        await using var node = CreateNode(new EchoTcpHandler());
        await node.StartAsync();

        node.RecordBytesSent(1000);

        // Deterministically force elapsed <= 0: put the snapshot time in the
        // future. The rate reported is 0, but the total must NOT be consumed
        // as the new baseline.
        SetMetricsTime(node, DateTime.UtcNow.AddSeconds(5));
        var zero = node.GetMetrics();
        await Assert.That(zero.BytesSentPerSecond).IsEqualTo(0);

        // One second of real elapsed time: the 1000 bytes must still be counted.
        SetMetricsTime(node, DateTime.UtcNow.AddSeconds(-1));
        var rate = node.GetMetrics();
        await Assert.That(rate.BytesSentPerSecond).IsGreaterThan(0);
    }

    private static void SetMetricsTime(TcpNode node, DateTime value)
    {
        var field = typeof(TcpNode).GetField(
            "_metricsTime",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );
        field!.SetValue(node, value);
    }

    private static async ValueTask<TcpNodeMetrics> WaitForTotalClosedAsync(
        TcpNode node,
        long expected,
        int timeoutMs = 3000
    )
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            var metrics = node.GetMetrics();
            if (metrics.TotalClosed >= expected)
                return metrics;
            await Task.Delay(15);
        }

        return node.GetMetrics(); // last attempt, test assertion will show the actual value
    }

    [Test]
    public async Task TryTrackConnection_rejects_when_count_reaches_MaxConnections()
    {
        // Simulates the TLS path: TryTrackConnection is called after an async
        // handshake, so the advisory count check in the accept loop may have
        // been bypassed. The lock-protected count check must enforce the limit.
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler, maxConnections: 1);
        await node.StartAsync();

        // Fill the single connection slot.
        using var client1 = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client1.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.WaitConnectedAsync();

        // Create a second connection (as TrackAndRunAsync would after TLS).
        using var sock2 = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        var conn2 = new TcpConnection(node, sock2);

        var accepted = node.TryTrackConnection(conn2);

        await Assert.That(accepted).IsFalse();

        await conn2.DisposeAsync();
    }

    private static TcpNode CreateNode(ITcpConnectionHandler handler, int maxConnections = 100) =>
        new(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = handler,
                MaxConnections = maxConnections,
            }
        );

    private sealed class EchoTcpHandler : ITcpConnectionHandler
    {
        private readonly TaskCompletionSource _connected = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        private readonly TaskCompletionSource _closed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        private readonly TaskCompletionSource _dataReceived = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task WaitConnectedAsync() => _connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public Task WaitClosedAsync() => _closed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public Task WaitDataReceivedAsync() =>
            _dataReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        )
        {
            _connected.TrySetResult();
            return Task.CompletedTask;
        }

        public async ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            _dataReceived.TrySetResult();
            await connection.SendAsync(buffer, cancellationToken);
            return buffer.End;
        }

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            _closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
