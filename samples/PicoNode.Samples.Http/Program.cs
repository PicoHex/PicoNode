using System.Buffers;
using System.Net;
using System.Text;
using PicoNode;
using PicoNode.Http;
using PicoNode.Web;

// ────────────────────────────────────────────────────────────
// PicoNode.Http samples — HTTP/1.1, HTTP/2, WebSocket
//
// This server demonstrates:
//   HTTP/1.1  — GET /, POST /echo, routing
//   HTTP/2    — h2c (prior knowledge through TcpNode)
//   WebSocket — /ws with echo messaging
//   BodyStream— /stream reads request body as stream
//   Security  — SecurityHeadersMiddleware
//   Caching   — CacheMiddleware
// ────────────────────────────────────────────────────────────

// WebSocket echo handler
WebSocketMessageHandler wsHandler = async (message, connection, ct) =>
{
    // Echo the message back to the client
    var frame = WebSocketFrameCodec.EncodeFrame(
        message.OpCode,
        message.Payload.Span,
        rsv1: false);

    await connection.SendAsync(new ReadOnlySequence<byte>(frame), ct);
};

var app = new WebApp(new WebAppOptions
{
    ServerHeader = "PicoNode.Samples",
    WebSocketMessageHandler = wsHandler,
});

// Middleware pipeline
app.Use(new SecurityHeadersMiddleware().InvokeAsync);
app.Use(new CacheMiddleware(maxAge: TimeSpan.FromMinutes(5)).InvokeAsync);

// ── HTTP routes ────────────────────────────────────────────

app.MapGet("/", static (_, _) =>
    ValueTask.FromResult(WebResults.Text(200, "hello from PicoNode.Http", "OK")));

app.MapGet("/info", static (ctx, _) =>
{
    var protocol = ctx.Request.Version == PicoNode.Http.HttpVersion.Http11 ? "HTTP/1.1" : "HTTP/2";
    var body = $$"""
        {"protocol":"{{protocol}}","path":"{{ctx.Path}}","method":"{{ctx.Request.Method}}"}
        """;
    return ValueTask.FromResult(WebResults.Json(200, body, "OK"));
});

// Read request body as stream
app.MapPost("/stream", static async (ctx, ct) =>
{
    using var reader = new StreamReader(ctx.Request.BodyStream);
    var body = await reader.ReadToEndAsync(ct);
    return WebResults.Text(200, $"received {body.Length} chars via streaming", "OK");
});

app.MapPost("/echo", static (ctx, _) =>
{
    var body = Encoding.UTF8.GetString(ctx.Request.Body.Span);
    return ValueTask.FromResult(WebResults.Text(200, body, "OK"));
});

// ── SSE endpoint (Server-Sent Events) ─────────────────────

app.MapGet("/sse", SseEndpoint.Create(async (sse, ct) =>
{
    // Simulate a stream of events (e.g., LLM output, live updates)
    var events = new[] { "Hello", ", ", "world", "!" };
    foreach (var token in events)
    {
        await sse.WriteJsonAsync($$"""{"token":"{{token}}"}""", ct);
        await Task.Delay(200, ct);
    }
}));

// ── WebSocket endpoint ─────────────────────────────────────

app.MapGet("/ws", static (ctx, _) =>
{
    var upgrade = WebSocketUpgrade.TryUpgrade(ctx.Request);
    if (upgrade is not null)
        return ValueTask.FromResult(upgrade);

    return ValueTask.FromResult(WebResults.Text(400, "WebSocket upgrade failed", "Bad Request"));
});

// Build and start
var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7003),
    ConnectionHandler = app.Build(),
});

await node.StartAsync();

Console.WriteLine($"Listening on {node.LocalEndPoint}");
Console.WriteLine();
Console.WriteLine("Routes:");
Console.WriteLine("  GET  /        -> text greeting");
Console.WriteLine("  GET  /info    -> JSON with protocol info");
Console.WriteLine("  POST /echo    -> echoes body (in-memory)");
Console.WriteLine("  POST /stream  -> echoes body via BodyStream");
Console.WriteLine("  GET  /sse     -> SSE event stream (text/event-stream)");
Console.WriteLine("  GET  /ws      -> WebSocket (echo)");
Console.WriteLine();
Console.WriteLine("Security headers + ETag caching + SSE streaming active.");
Console.WriteLine("Press Enter to stop...");
Console.ReadLine();

await node.DisposeAsync();
