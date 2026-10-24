// A minimal SSE test server that streams data through the full HTTP stack.
// No API keys or external dependencies required.

const int Port = 18789;

var builder = new WebApiBuilder().ConfigureApp(o => new WebAppOptions
{
    ServerHeader = "SseE2ETest",
});

var api = builder.Build();

// Static SSE endpoint: streams 5 events then completes
api.App.MapGet(
    "/sse/stream",
    SseEndpoint.Create(
        async (sse, ct) =>
        {
            for (var i = 1; i <= 5; i++)
            {
                await sse.WriteEventAsync("update", $"{{\"seq\":{i}}}", ct);
                await Task.Delay(50, ct); // simulate async work
            }
            await sse.CompleteAsync(ct);
        }
    )
);

// Server-side observation seam for the cancellation test: a client can only see
// its own socket close, which says nothing about whether the server noticed.
var cancelProbe = new CancelProbe();

// Endpoint that tests cancellation — writes one event, then blocks until the
// connection-level token fires (peer FIN/RST or node shutdown).
api.App.MapGet(
    "/sse/cancel-test",
    SseEndpoint.Create(
        async (sse, ct) =>
        {
            cancelProbe.Entered();
            try
            {
                await sse.WriteEventAsync("start", "begin", ct);
                // Wait indefinitely — the handler token fires on connection close.
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelProbe.CancelObserved();
                throw;
            }
            finally
            {
                cancelProbe.Exited();
            }
        }
    )
);

// What the server observed on /sse/cancel-test, read by the E2E test so the
// assertion target is the server's view of the disconnect, not the client's.
api.App.MapGet(
    "/sse/cancel-test/probe",
    (_, _) => ValueTask.FromResult(WebResults.Json(200, cancelProbe.ToJson()))
);

// Endpoint that simulates a thinking-mode response via SSE
api.App.MapGet(
    "/sse/thinking",
    SseEndpoint.Create(
        async (sse, ct) =>
        {
            await sse.WriteEventAsync("thinking", "Let me think", ct);
            await Task.Delay(50, ct);
            await sse.WriteEventAsync("thinking", " about this carefully", ct);
            await Task.Delay(50, ct);
            await sse.WriteEventAsync("delta", "The answer is 42", ct);
            await sse.CompleteAsync(ct);
        }
    )
);

Console.Error.WriteLine($"SseTestServer starting on port {Port}...");
await api.RunAsync($"http://localhost:{Port}");

/// <summary>
/// Counters recording what the server-side SSE handler observed for
/// <c>/sse/cancel-test</c>: the handler started, the handler's token fired
/// (i.e. the server noticed the client go away), and the handler unwound.
/// Exposed over HTTP so the E2E test can assert on server-side observation
/// instead of only on its own socket reaching EOF.
/// </summary>
internal sealed class CancelProbe
{
    private int _entered;
    private int _cancelObserved;
    private int _exited;

    public void Entered() => Interlocked.Increment(ref _entered);

    public void CancelObserved() => Interlocked.Increment(ref _cancelObserved);

    public void Exited() => Interlocked.Increment(ref _exited);

    public string ToJson() =>
        $"{{\"entered\":{Volatile.Read(ref _entered)},"
        + $"\"cancel_observed\":{Volatile.Read(ref _cancelObserved)},"
        + $"\"exited\":{Volatile.Read(ref _exited)}}}";
}
