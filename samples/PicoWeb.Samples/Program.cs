using System.Buffers;
using System.Net;
using PicoDI;
using PicoDI.Abs;
using PicoNode.Web;
using PicoNode.Http;
using PicoWeb;

var container = new SvcContainer();
container.RegisterScoped<PicoWeb.Samples.Controllers.UsersController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.ProductsController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.PostsController>();
container.Build();

// WebSocket echo handler — echoes received messages back
WebSocketMessageHandler wsEcho = static async (msg, conn, ct) =>
{
    if (msg.OpCode == WebSocketOpCode.Close) return;
    var size = WebSocketFrameCodec.MeasureFrameSize(msg.Payload.Length, mask: false);
    var buf = new byte[size];
    WebSocketFrameCodec.WriteFrame(buf, msg.OpCode, msg.Payload.Span);
    await conn.SendAsync(new ReadOnlySequence<byte>(buf), ct);
};

var app = PicoWeb.Samples.ShowcaseApp.Create(container, webSocketHandler: wsEcho);
EndpointRegistrar.RegisterAll(app);

// ── Inline endpoints ──
app.MapGet("/api/health", static (WebContext ctx, CancellationToken _) =>
    ValueTask.FromResult(WebResults.Json(200, """{"status":"ok"}""", "OK")));

// Server info including HTTP version
app.MapGet("/api/info", static (WebContext ctx, CancellationToken _) =>
{
    var ver = ctx.Request.Version switch
    {
        PicoNode.Http.HttpVersion.Http10 => "HTTP/1.0",
        PicoNode.Http.HttpVersion.Http11 => "HTTP/1.1",
        _ => "unknown",
    };
    return ValueTask.FromResult(WebResults.Json(200,
        $$"""{"server":"PicoWeb","http":"{{ver}}"}""", "OK"));
});

// WebSocket echo endpoint
app.MapGet("/ws/echo", static (WebContext ctx, CancellationToken _) =>
{
    var upgrade = PicoNode.Http.WebSocketUpgrade.TryUpgrade(ctx.Request);
    return ValueTask.FromResult(upgrade ?? WebResults.Text(400, "WebSocket upgrade failed"));
});

// ── Start ──
var server = new WebServer(
    app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 7004) }
);
await server.StartAsync();
Console.WriteLine($"Listening on {server.LocalEndPoint}");
Console.WriteLine("Open http://localhost:7004/ in your browser");
await Task.Delay(Timeout.Infinite);
await server.DisposeAsync();
