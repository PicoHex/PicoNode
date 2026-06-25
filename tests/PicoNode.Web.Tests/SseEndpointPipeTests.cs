namespace PicoNode.Web.Tests;

/// <summary>
/// Tests that reproduce the P0 SSE streaming issue.
/// Suspected root causes:
/// 1. Cancellation token timing — CT used by both writer and reader
/// 2. Pipe flush behavior — data written but not visible to reader
/// 3. Async timing — send delays causing reader/writer race conditions
/// </summary>
public sealed class SseEndpointPipeTests
{
    /// <summary>
    /// Simulates real async connection behavior where SendAsync
    /// has a small delay. Tests that Pipe + BodyStream still delivers data.
    /// </summary>
    [Test]
    public async Task SseEndpoint_with_delayed_connection_send_still_delivers_data()
    {
        // Arrange — connection with small async delay on SendAsync
        var endpoint = SseEndpoint.Create(
            async (sse, ct) =>
            {
                await sse.WriteAsync("data: hello\n\n", ct);
                await sse.CompleteAsync(ct);
            }
        );

        var app = new WebApp(new TestContainer());
        app.MapGet("/events", endpoint);
        var handler = app.Build();

        var context = new DelayedSendConnectionContext(delayMs: 5);
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /events HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        // Act
        await handler.OnReceivedAsync(context, request, CancellationToken.None);

        // Assert
        var allText = string.Concat(context.AllSent.Select(b => Encoding.ASCII.GetString(b)));
        await Assert
            .That(allText)
            .Contains("data: hello")
            .Because("SSE data must be delivered even with async send delays");
    }

    /// <summary>
    /// Tests that the background writer task gets a chance to start
    /// before the reader exhausts its first read. If the writer hasn't
    /// written anything, the reader's first ReadAsync should wait for data.
    /// </summary>
    [Test]
    public async Task SseEndpoint_reader_waits_for_writer_data()
    {
        // Arrange — writer delays before writing
        var endpoint = SseEndpoint.Create(
            async (sse, ct) =>
            {
                await Task.Delay(50, ct); // Simulate initial work before first write
                await sse.WriteAsync("data: delayed\n\n", ct);
                await sse.CompleteAsync(ct);
            }
        );

        var app = new WebApp(new TestContainer());
        app.MapGet("/delayed", endpoint);
        var handler = app.Build();

        var context = new RecordingSseConnectionContext();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /delayed HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        // Act
        await handler.OnReceivedAsync(context, request, CancellationToken.None);

        // Assert
        var allText = string.Concat(context.AllSent.Select(b => Encoding.ASCII.GetString(b)));
        await Assert
            .That(allText)
            .Contains("data: delayed")
            .Because("reader should wait for writer data even when writer starts late");
    }

    /// <summary>
    /// Tests cancellation token propagation. The same ct passed to
    /// the SSE handler is also used by SendStreamingResponseAsync.
    /// If ct is cancelled, RunSseWriterAsync completes the pipe so reader
    /// exits cleanly instead of throwing OCE or hanging.
    /// </summary>
    [Test]
    public async Task SseEndpoint_cancelled_token_does_not_hang_reader()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var writerStarted = new TaskCompletionSource();

        var endpoint = SseEndpoint.Create(
            async (sse, ct) =>
            {
                writerStarted.TrySetResult();
                // Will throw OperationCanceledException when cancelled
                await sse.WriteAsync("data: will be cancelled\n\n", ct);
            }
        );

        var app = new WebApp(new TestContainer());
        app.MapGet("/cancel", endpoint);
        var handler = app.Build();

        var context = new RecordingSseConnectionContext();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /cancel HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        // Act — start processing, wait for writer to start, then cancel
        var processTask = handler.OnReceivedAsync(context, request, cts.Token).AsTask();
        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        // Assert — the handler must complete (not throw, not hang)
        await processTask.WaitAsync(TimeSpan.FromSeconds(10));
        // Headers were sent before cancellation
        await Assert
            .That(context.SendCount)
            .IsGreaterThanOrEqualTo(1)
            .Because("at least chunked headers should have been sent before cancellation");
    }

    /// <summary>
    /// Tests that multiple sequential SSE writes all make it through the pipe.
    /// Regression test for data loss in transit and ordering.
    /// </summary>
    [Test]
    public async Task SseEndpoint_all_sequential_writes_delivered()
    {
        // Arrange
        const int eventCount = 10;
        var endpoint = SseEndpoint.Create(
            async (sse, ct) =>
            {
                for (var i = 1; i <= eventCount; i++)
                {
                    await sse.WriteAsync($"data: event {i}\n\n", ct);
                }
                await sse.CompleteAsync(ct);
            }
        );

        var app = new WebApp(new TestContainer());
        app.MapGet("/seq", endpoint);
        var handler = app.Build();

        var context = new RecordingSseConnectionContext();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /seq HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        // Act
        await handler.OnReceivedAsync(context, request, CancellationToken.None);

        // Assert — all events should be present in order
        var allText = string.Concat(context.AllSent.Select(b => Encoding.ASCII.GetString(b)));
        var lastIdx = -1;
        for (var i = 1; i <= eventCount; i++)
        {
            var idx = allText.IndexOf($"data: event {i}", StringComparison.Ordinal);
            await Assert
                .That(idx)
                .IsGreaterThan(lastIdx)
                .Because($"event {i} should appear after event {i - 1}");
            lastIdx = idx;
        }
    }

    /// <summary>
    /// Verifies that when connection.SendAsync fails during header send,
    /// the HTTP handler catches the exception gracefully, closes the connection,
    /// and does not crash. Regression test for the BodyStream dispose gap.
    /// </summary>
    [Test]
    public async Task SseEndpoint_handles_send_failure_during_headers_gracefully()
    {
        // Arrange — connection that throws on every SendAsync
        var endpoint = SseEndpoint.Create(
            async (sse, ct) =>
            {
                await sse.WriteAsync("data: never arrives\n\n", ct);
                await sse.CompleteAsync(ct);
            }
        );

        var app = new WebApp(new TestContainer());
        app.MapGet("/fail", endpoint);
        var handler = app.Build();

        var context = new FailOnSendConnectionContext();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /fail HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        // Act — should complete without throwing
        await handler.OnReceivedAsync(context, request, CancellationToken.None);

        // Assert
        await Assert
            .That(context.CloseCount)
            .IsGreaterThanOrEqualTo(1)
            .Because("connection should be closed after send failure");
    }

    /// <summary>
    /// Same scenario as above but exercises SendStreamingResponseAsync
    /// directly with a MemoryStream BodyStream, verifying exception handling
    /// in the non-SSE path.
    /// </summary>
    [Test]
    public async Task Streaming_response_handles_send_failure_during_headers_gracefully()
    {
        // Arrange — MemoryStream BodyStream, connection that throws on send
        var handler = CreateDirectHandler(
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        BodyStream = new MemoryStream(Encoding.ASCII.GetBytes("test body")),
                    }
                )
        );

        var context = new FailOnSendConnectionContext();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        // Act — should complete without throwing
        await handler.OnReceivedAsync(context, request, CancellationToken.None);

        await Assert
            .That(context.CloseCount)
            .IsGreaterThanOrEqualTo(1)
            .Because("connection should be closed after send failure");
    }

    #region Test Helpers

    private sealed class RecordingSseConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId { get; init; } = 1;
        public EndPoint RemoteEndPoint { get; init; } = new IPEndPoint(IPAddress.Loopback, 12345);
        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;
        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;
        public object? UserState { get; set; }
        public string? NegotiatedProtocol => null;

        public byte[] LastSent { get; private set; } = [];
        public List<byte[]> AllSent { get; } = [];
        public int SendCount { get; private set; }
        public int CloseCount { get; private set; }

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            LastSent = buffer.ToArray();
            AllSent.Add(LastSent);
            SendCount++;
            return Task.CompletedTask;
        }

        public void Close() => CloseCount++;
    }

    /// <summary>
    /// Connection context that adds a small async delay on SendAsync
    /// to simulate real network send behavior.
    /// </summary>
    private sealed class DelayedSendConnectionContext : ITcpConnectionContext
    {
        private readonly int _delayMs;

        public DelayedSendConnectionContext(int delayMs = 5)
        {
            _delayMs = delayMs;
        }

        public long ConnectionId { get; init; } = 1;
        public EndPoint RemoteEndPoint { get; init; } = new IPEndPoint(IPAddress.Loopback, 12345);
        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;
        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;
        public object? UserState { get; set; }
        public string? NegotiatedProtocol => null;

        public byte[] LastSent { get; private set; } = [];
        public List<byte[]> AllSent { get; } = [];
        public int SendCount { get; private set; }

        public async Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(_delayMs, cancellationToken);
            LastSent = buffer.ToArray();
            AllSent.Add(LastSent);
            SendCount++;
        }

        public void Close() { }
    }

    /// <summary>
    /// Connection context that throws IOException on every SendAsync call,
    /// simulating a network write failure (e.g. broken pipe).
    /// </summary>
    private sealed class FailOnSendConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId { get; init; } = 1;
        public EndPoint RemoteEndPoint { get; init; } = new IPEndPoint(IPAddress.Loopback, 12345);
        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;
        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;
        public object? UserState { get; set; }
        public string? NegotiatedProtocol => null;

        public int CloseCount { get; private set; }

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        ) => Task.FromException(new IOException("Connection reset by peer"));

        public void Close() => CloseCount++;
    }

    #endregion

    private static HttpConnectionHandler CreateDirectHandler(HttpRequestHandler requestHandler) =>
        new(new HttpConnectionHandlerOptions { RequestHandler = requestHandler });
}
