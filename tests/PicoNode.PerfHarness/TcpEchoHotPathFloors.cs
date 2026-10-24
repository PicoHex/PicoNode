namespace PicoNode.PerfHarness;

/// <summary>
/// The performance floor for the TCP echo hot path: a lower bound on round trips per second and an
/// upper bound on the bytes the round trip allocates. Both are bounds with headroom, never a snapshot
/// of the number the harness printed — the printed numbers are recorded in
/// <c>.superpowers/sdd/2026-10-22-test-quality/task-T4.5-report.md</c> together with the spread they were
/// measured over (1-CPU and 2-CPU affinity masks, unpinned), and every constant below is derived from it.
/// </summary>
/// <remarks>
/// <para>
/// The two bounds catch different regressions. The rate floor is a catastrophe tripwire: a lock added
/// around the send path, an extra syscall per message or a per-message buffer copy halves the rate and
/// trips it. The allocation budget is the sharp one: a per-message <c>ToArray()</c>, a cloned buffer or
/// a closure per receive costs a few hundred bytes per message and barely moves the rate on a machine
/// with spare GC headroom, but it cannot hide from the budget.
/// </para>
/// <para>
/// Noise is handled by taking the <b>best</b> of <see cref="Rounds"/> rounds: scheduler noise can only
/// make a round slower, never faster, so the maximum is the closest thing to the machine's real rate
/// and a single descheduled round cannot fail the floor. A real regression is not a single slow round —
/// it is present in every one of them.
/// </para>
/// </remarks>
[NotInParallel]
public sealed class TcpEchoHotPathFloors
{
    static TcpEchoHotPathFloors() => PerfHarnessEnvironment.RaiseThreadPoolMinimum();

    private const int Port = 7220;
    private const int PayloadSize = 128;

    /// <summary>
    /// The allocation budget is measured on a payload large enough that a per-message copy of the
    /// payload cannot hide inside the async plumbing: at 4 KiB a `ToArray()` or a clone costs 4096 B
    /// per message, while the plumbing itself is payload-independent.
    /// </summary>
    private const int AllocationPayloadSize = 4 * 1024;

    /// <summary>Round trips spent before any measurement: JIT, pipe setup, pool warm-up.</summary>
    private const int WarmUpRoundTrips = 100;

    /// <summary>Measured rounds; the floor is asserted against the fastest one.</summary>
    private const int Rounds = 3;

    private const int RoundTripsPerRound = 300;

    /// <summary>
    /// ~1/3 of the fastest round trip rate measured on a 1-CPU affinity mask (see the report for the
    /// measured spread). Below this the echo path is not the code this harness measured.
    /// </summary>
    private const double RoundTripsPerSecondFloor = 2_000;

    /// <summary>
    /// The echo path is expected to be allocation-free per message apart from the async plumbing.
    /// Measured ~N B/round trip (see report); the budget is ~4x that, so it tolerates a different
    /// runtime's async bookkeeping but not a per-message payload copy.
    /// </summary>
    private const double AllocatedBytesPerRoundTripCeiling = 2_048;

    [Test]
    public async Task Echo_round_trip_rate_stays_above_the_floor()
    {
        var payload = CreatePayload();
        var readBuffer = new byte[payload.Length];

        await using var harness = await EchoHarness.StartAsync(Port);
        await using var client = await harness.ConnectAsync();

        await RoundTripsAsync(client.Stream, payload, readBuffer, WarmUpRoundTrips);

        var rates = new double[Rounds];
        for (var round = 0; round < Rounds; round++)
        {
            var stopwatch = Stopwatch.StartNew();
            await RoundTripsAsync(client.Stream, payload, readBuffer, RoundTripsPerRound);
            stopwatch.Stop();
            rates[round] = RoundTripsPerRound / Math.Max(stopwatch.Elapsed.TotalSeconds, 1e-6);
        }

        var best = rates.Max();
        Console.WriteLine(
            $"tcp-echo-hot-path rate rounds=[{string.Join(", ", rates.Select(r => r.ToString("F0")))}] "
                + $"best={best:F0} round-trips/s floor={RoundTripsPerSecondFloor:F0} "
                + $"headroom={best / RoundTripsPerSecondFloor:F1}x"
        );

        await Assert
            .That(best)
            .IsGreaterThan(RoundTripsPerSecondFloor)
            .Because(
                $"the echo hot path must sustain {RoundTripsPerSecondFloor:F0} round trips/s; the best of "
                    + $"{Rounds} rounds was {best:F0}/s (rounds: {string.Join(", ", rates.Select(r => r.ToString("F0")))}). "
                    + "A single slow round is noise (the floor takes the best), all rounds slow is a regression."
            );
    }

    [Test]
    public async Task Echo_round_trip_allocation_stays_within_the_budget()
    {
        var payload = CreatePayload(AllocationPayloadSize);
        var readBuffer = new byte[payload.Length];

        await using var harness = await EchoHarness.StartAsync(Port + 1);
        await using var client = await harness.ConnectAsync();

        await RoundTripsAsync(client.Stream, payload, readBuffer, WarmUpRoundTrips);

        // One settle round outside the measured window: it pays for the async state machines and pipe
        // segments that only allocate on the second lap through the same code path.
        await RoundTripsAsync(client.Stream, payload, readBuffer, RoundTripsPerRound);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        await RoundTripsAsync(client.Stream, payload, readBuffer, RoundTripsPerRound);
        var bytes = GC.GetTotalAllocatedBytes(precise: true) - before;

        var bytesPerRoundTrip = bytes / (double)RoundTripsPerRound;
        Console.WriteLine(
            $"tcp-echo-hot-path allocation roundTrips={RoundTripsPerRound} bytes={bytes} "
                + $"bytesPerRoundTrip={bytesPerRoundTrip:F0} budget={AllocatedBytesPerRoundTripCeiling:F0}"
        );

        await Assert
            .That(bytesPerRoundTrip)
            .IsLessThan(AllocatedBytesPerRoundTripCeiling)
            .Because(
                $"one {AllocationPayloadSize}-byte echo round trip allocated {bytesPerRoundTrip:F0} bytes "
                    + $"(budget {AllocatedBytesPerRoundTripCeiling:F0}); the echo path must not copy the "
                    + "payload per message"
            );
    }

    private static async Task RoundTripsAsync(
        NetworkStream stream,
        byte[] payload,
        byte[] readBuffer,
        int count
    )
    {
        for (var i = 0; i < count; i++)
        {
            await stream.WriteAsync(payload);
            var offset = 0;
            while (offset < readBuffer.Length)
            {
                var read = await stream.ReadAsync(readBuffer.AsMemory(offset));
                if (read == 0)
                {
                    throw new InvalidOperationException("echo closed the connection mid-round-trip");
                }

                offset += read;
            }

            // The bound is about the hot path, not the payload: a wrong echo is still a wrong echo, so
            // the check is here as well — an allocation-free path that returns garbage is not a result
            // worth a performance floor.
            if (!readBuffer.AsSpan().SequenceEqual(payload))
            {
                throw new InvalidOperationException("echo payload mismatch");
            }
        }
    }

    private static byte[] CreatePayload(int size = PayloadSize)
    {
        var payload = new byte[size];
        new Random(7).NextBytes(payload);
        return payload;
    }

    /// <summary>A started node plus the single connection the rounds run over.</summary>
    private sealed class EchoHarness : IAsyncDisposable
    {
        private readonly TcpNode _node;

        private EchoHarness(TcpNode node) => _node = node;

        internal static async Task<EchoHarness> StartAsync(int port)
        {
            var node = new TcpNode(
                new TcpNodeOptions
                {
                    Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                    ConnectionHandler = new EchoHandler(),
                    MaxConnections = 8,
                }
            );
            await node.StartAsync();
            return new EchoHarness(node);
        }

        internal async Task<EchoClient> ConnectAsync()
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_node.LocalEndPoint!).Port);
            return new EchoClient(client);
        }

        public async ValueTask DisposeAsync()
        {
            await _node.StopAsync();
            await _node.DisposeAsync();
        }
    }

    private sealed class EchoClient : IAsyncDisposable
    {
        private readonly TcpClient _client;

        internal EchoClient(TcpClient client)
        {
            _client = client;
            Stream = client.GetStream();
        }

        internal NetworkStream Stream { get; }

        public ValueTask DisposeAsync()
        {
            Stream.Dispose();
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EchoHandler : ITcpConnectionHandler
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
}
