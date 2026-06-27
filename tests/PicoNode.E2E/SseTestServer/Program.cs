using PicoNode.Web;
using PicoWeb;

// A minimal SSE test server that streams data through the full HTTP stack.
// No API keys or external dependencies required.

const int Port = 18789;

var builder = new WebApiBuilder().ConfigureApp(o => new WebAppOptions
{
    ServerHeader = "SseE2ETest",
});

var api = builder.Build();

// Static SSE endpoint: streams 5 events then completes
api.MapGet(
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

// Endpoint that tests cancellation — writes one event then waits
api.MapGet(
    "/sse/cancel-test",
    SseEndpoint.Create(
        async (sse, ct) =>
        {
            await sse.WriteEventAsync("start", "begin", ct);
            // Wait indefinitely — cancellation token will fire on connection close
            await Task.Delay(Timeout.Infinite, ct);
        }
    )
);

// Endpoint that simulates a thinking-mode response via SSE
api.MapGet(
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
