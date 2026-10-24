using System.Globalization;

namespace PicoNode.Http.Tests;

/// <summary>
/// Chunked-framing guards for streamed responses (SSE and anything else that sets
/// <see cref="HttpResponse.BodyStream"/>), audit F-26.
/// </summary>
/// <remarks>
/// The framing used to be built as four objects per chunk — three <c>ReadOnlySequence</c>
/// segments plus a fresh hex-prefix array — and handed to <c>SendAsync</c>. It now goes into
/// the very buffer the body was read into, so the production path costs no garbage per chunk.
/// </remarks>
public sealed class StreamingChunkFramingTests
{
    private const string ChunkedHead =
        "HTTP/1.1 200 OK\r\n"
        + "Content-Type: text/event-stream\r\n"
        + "Cache-Control: no-cache\r\n"
        + "Transfer-Encoding: chunked\r\n"
        + "\r\n";

    /// <summary>
    /// 16 B is below the 24-byte object header of any array or segment a re-introduced
    /// allocation would have to allocate; a percentage of a zero measurement is meaningless.
    /// </summary>
    private const double ChunkFrameAllocationCeilingBytes = 16.0;

    [Test]
    public async Task OnReceivedAsync_streaming_response_allocates_nothing_per_chunk()
    {
        const int chunkSize = 4096;

        // Two warm-up responses: JIT, the pool's first segment, the parser's statics.
        await MeasureStreamingResponseAsync(chunkSize, 2);
        await MeasureStreamingResponseAsync(chunkSize, 2);

        // Marginal cost of one chunk: everything per-request (request parse, the header
        // block, the response object, the rented buffer) is identical in both runs and
        // cancels out of the difference.
        const int smallChunks = 5;
        const int largeChunks = 55;
        var small = await MeasureStreamingResponseAsync(chunkSize, smallChunks);
        var large = await MeasureStreamingResponseAsync(chunkSize, largeChunks);
        var perChunk = (double)(large - small) / (largeChunks - smallChunks);

        await Assert
            .That(perChunk)
            .IsLessThan(ChunkFrameAllocationCeilingBytes)
            .Because(
                $"measured {perChunk:F1} B per chunk ({small} B for {smallChunks} chunks, "
                    + $"{large} B for {largeChunks} chunks)"
            );
    }

    [Test]
    public async Task Streaming_response_frames_every_chunk_with_its_hex_length()
    {
        // The wire shape the allocation guard must not change: "<hex>\r\n<data>\r\n" per
        // chunk and one "0\r\n\r\n" terminator. The lengths straddle the hex-width
        // boundaries (f -> 10, ff -> 100, fff -> 1000).
        foreach (var (dataLength, bufferSize) in new[]
        {
            (0, 16),
            (1, 1),
            (5, 5),
            (15, 15),
            (16, 16),
            (255, 255),
            (256, 256),
            (4095, 4096),
            (4096, 4096),
            (8192, 4096),
        })
        {
            var bytes = new byte[dataLength];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)('a' + (i % 26));
            }

            var connection = new RecordingConnectionContext();
            var handler = CreateStreamingHandler(bytes, bufferSize);

            await handler.OnReceivedAsync(
                connection,
                RequestBytes,
                CancellationToken.None
            );

            await Assert
                .That(Encoding.Latin1.GetString(connection.Bytes))
                .IsEqualTo(ExpectedWire(bytes, bufferSize))
                .Because(
                    $"chunked framing for {dataLength} bytes in {bufferSize}-byte reads"
                );
        }
    }

    /// <summary>The exact octets a streamed 200 of <paramref name="body"/> must produce.</summary>
    private static string ExpectedWire(byte[] body, int bufferSize)
    {
        var wire = new StringBuilder(ChunkedHead);

        for (var offset = 0; offset < body.Length; offset += bufferSize)
        {
            var length = Math.Min(bufferSize, body.Length - offset);
            wire.Append(length.ToString("x", CultureInfo.InvariantCulture))
                .Append("\r\n")
                .Append(Encoding.Latin1.GetString(body, offset, length))
                .Append("\r\n");
        }

        return wire.Append("0\r\n\r\n").ToString();
    }

    private static readonly ReadOnlySequence<byte> RequestBytes = new(
        Encoding.ASCII.GetBytes("GET /events HTTP/1.1\r\nHost: example.com\r\n\r\n")
    );

    private static HttpConnectionHandler CreateStreamingHandler(byte[] body, int bufferSize) =>
        new(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = (_, _) =>
                    ValueTask.FromResult(
                        new HttpResponse
                        {
                            StatusCode = 200,
                            ReasonPhrase = "OK",
                            Headers =
                            [
                                new KeyValuePair<string, string>(
                                    "Content-Type",
                                    "text/event-stream"
                                ),
                                new KeyValuePair<string, string>("Cache-Control", "no-cache"),
                            ],
                            BodyStream = new MemoryStream(body),
                        }
                    ),
                StreamingResponseBufferSize = bufferSize,
            }
        );

    /// <summary>
    /// Drives one streaming 200 response whose body is exactly <paramref name="chunks"/> x
    /// <paramref name="chunkSize"/> bytes and returns the bytes allocated on this thread while
    /// <see cref="HttpConnectionHandler.OnReceivedAsync"/> framed and sent it. The read loop can
    /// only suspend on a real wait and a MemoryStream never waits, so the whole call stays on
    /// this thread — asserted, because a migration would make the count a lie.
    /// </summary>
    private static async Task<long> MeasureStreamingResponseAsync(int chunkSize, int chunks)
    {
        var connection = new CountingConnectionContext();
        var handler = CreateStreamingHandler(new byte[chunkSize * chunks], chunkSize);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var pending = handler.OnReceivedAsync(connection, RequestBytes, CancellationToken.None);
        if (!pending.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                "the streaming response did not complete synchronously, so per-thread allocation counting is meaningless"
            );
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        _ = pending.Result;

        // A response that framed and sent nothing would otherwise look free.
        await Assert
            .That(connection.BytesSent)
            .IsGreaterThan(chunkSize * chunks)
            .Because("the measured run must have framed and sent every chunk");
        return after - before;
    }

    /// <summary>
    /// A sink that only counts the octets it is handed: storing them would put its own
    /// allocations inside the measured window of the guard above.
    /// </summary>
    private sealed class CountingConnectionContext : ITcpConnectionContext
    {
        public long BytesSent { get; private set; }

        public long ConnectionId => 1;

        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc => DateTimeOffset.UnixEpoch;

        public object? UserState { get; set; }

        public CancellationToken RemoteCloseToken => CancellationToken.None;

        public string? NegotiatedProtocol => "http/1.1";

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            BytesSent += buffer.Length;
            return Task.CompletedTask;
        }

        public void Close() { }
    }

    private sealed class RecordingConnectionContext : ITcpConnectionContext
    {
        public byte[] Bytes { get; private set; } = [];

        public long ConnectionId => 1;

        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc => DateTimeOffset.UnixEpoch;

        public object? UserState { get; set; }

        public CancellationToken RemoteCloseToken => CancellationToken.None;

        public string? NegotiatedProtocol => "http/1.1";

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var previous = Bytes;
            Bytes = new byte[previous.Length + (int)buffer.Length];
            previous.CopyTo(Bytes, 0);
            buffer.CopyTo(Bytes.AsSpan(previous.Length));
            return Task.CompletedTask;
        }

        public void Close() { }
    }
}
