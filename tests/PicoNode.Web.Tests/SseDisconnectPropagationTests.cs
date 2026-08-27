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

        var endpoint = SseEndpoint.Create(async (sse, ct) =>
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
        });

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
