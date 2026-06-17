// ────────────────────────────────────────────────────────────
// PicoNode.Http samples
// ────────────────────────────────────────────────────────────

WebSocketMessageHandler wsHandler = async (message, connection, ct) =>
{
    var frame = WebSocketFrameCodec.EncodeFrame(message.OpCode, message.Payload.Span, rsv1: false);
    await connection.SendAsync(new ReadOnlySequence<byte>(frame), ct);
};

var app = new WebApp(
    new SvcContainer(),
    new WebAppOptions { ServerHeader = "PicoNode.Samples", WebSocketMessageHandler = wsHandler }
);

app.Use(new SecurityHeadersMiddleware().InvokeAsync);
app.Use(new CacheMiddleware(maxAge: TimeSpan.FromMinutes(5)).InvokeAsync);

// ── Routes ─────────────────────────────────────────────────

app.MapGet(
    "/",
    (WebContext _, CancellationToken _) =>
        ValueTask.FromResult(WebResults.Text(200, "hello from PicoNode.Http", "OK"))
);

app.MapGet(
    "/info",
    (WebContext ctx, CancellationToken _) =>
    {
        var protocol =
            ctx.Request.Version == PicoNode.Http.HttpVersion.Http11 ? "HTTP/1.1" : "HTTP/2";
        var body =
            $$"""{"protocol":"{{protocol}}","path":"{{ctx.Path}}","method":"{{ctx.Request.Method}}"}""";
        return ValueTask.FromResult(WebResults.Json(200, body, "OK"));
    }
);

app.MapPost(
    "/stream",
    async (WebContext ctx, CancellationToken ct) =>
    {
        using var reader = new StreamReader(ctx.Request.BodyStream);
        var body = await reader.ReadToEndAsync(ct);
        return WebResults.Text(200, $"received {body.Length} chars via streaming", "OK");
    }
);

app.MapPost(
    "/echo",
    (WebContext ctx, CancellationToken _) =>
    {
        var body = Encoding.UTF8.GetString(ctx.Request.Body.Span);
        return ValueTask.FromResult(WebResults.Text(200, body, "OK"));
    }
);

// SSE
var sseHandler = SseEndpoint.Create(
    async (sse, ct) =>
    {
        var events = new[] { "Hello", ", ", "world", "!" };
        foreach (var token in events)
        {
            await sse.WriteJsonAsync($$"""{"token":"{{token}}"}""", ct);
            await Task.Delay(200, ct);
        }
    }
);
app.MapGet("/sse", sseHandler);

// WebSocket
app.MapGet(
    "/ws",
    (WebContext ctx, CancellationToken _) =>
    {
        var upgrade = WebSocketUpgrade.TryUpgrade(ctx.Request);
        if (upgrade is not null)
            return ValueTask.FromResult(upgrade);
        return ValueTask.FromResult(
            WebResults.Text(400, "WebSocket upgrade failed", "Bad Request")
        );
    }
);

// ── Start ──────────────────────────────────────────────────

var node = new TcpNode(
    new TcpNodeOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 7003),
        ConnectionHandler = app.Build(),
    }
);

await node.StartAsync();
Console.WriteLine($"Listening on {node.LocalEndPoint}");
Console.WriteLine("Press Enter to stop...");
Console.ReadLine();
await node.DisposeAsync();
