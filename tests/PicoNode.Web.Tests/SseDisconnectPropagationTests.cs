namespace PicoNode.Web.Tests;

/// <summary>
/// SSE disconnect propagation: the handler's cancellation token must be
/// linked to the transport's remote-close signal (peer FIN/RST), so a
/// long-lived SSE handler stops when the client disconnects.
/// </summary>
public sealed class SseDisconnectPropagationTests
{
    [Test]
    public async Task RemoteCloseToken_cancellation_stops_handler_and_ends_stream()
    {
        var handlerCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var endpoint = SseEndpoint.Create(
            async (sse, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    handlerCancelled.TrySetResult();
                    throw;
                }
            }
        );

        var app = new WebApp(new TestContainer());
        app.MapGet("/events", endpoint);
        var handler = app.Build();

        var context = new RemoteCloseAwareConnectionContext();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /events HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        var processing = handler.OnReceivedAsync(context, request, CancellationToken.None);

        // Let the SSE handler start streaming, then simulate the peer closing
        // the connection (graceful FIN observed by the transport).
        await Task.Delay(100);
        context.RemoteCloseCts.Cancel();

        // The handler must observe cancellation and the stream must end
        // cleanly (pipe writer completed), unblocking the HTTP streaming read.
        await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await processing.AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task KeepAlive_write_failure_cancels_stream_token()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, keepAliveInterval: TimeSpan.FromMilliseconds(50));
        var streamCts = new CancellationTokenSource();
        sse.StreamCts = streamCts;

        // The keep-alive loop starts lazily on the first write. Kick it off
        // with a successful write, THEN terminate the downstream pipeline:
        // the next keep-alive ping write throws InvalidOperationException
        // and the loop must cancel the stream token instead of breaking
        // silently.
        await sse.WriteAsync(": kick\n\n", CancellationToken.None);
        pipe.Writer.Complete();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!streamCts.IsCancellationRequested)
        {
            await Task.Delay(20, timeout.Token);
        }

        await Assert.That(streamCts.IsCancellationRequested).IsTrue();
    }

    /// <summary>ITcpConnectionContext double whose RemoteCloseToken can be cancelled by the test.</summary>
    private sealed class RemoteCloseAwareConnectionContext : ITcpConnectionContext
    {
        public CancellationTokenSource RemoteCloseCts { get; } = new();

        public CancellationToken RemoteCloseToken => RemoteCloseCts.Token;

        public long ConnectionId => 1;

        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 0);

        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.UtcNow;

        public DateTimeOffset LastActivityUtc => DateTimeOffset.UtcNow;

        public object? UserState { get; set; }

        public string? NegotiatedProtocol => null;

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public void Close() { }
    }
}
