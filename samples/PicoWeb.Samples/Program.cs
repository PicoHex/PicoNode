using System.Buffers;
using System.Net;
using PicoDI;
using PicoDI.Abs;
using PicoNode.Http;
using PicoNode.Web;
using PicoWeb;

var container = new SvcContainer();
container.RegisterScoped<PicoWeb.Samples.Controllers.UsersController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.ProductsController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.PostsController>();
container.Build();

// WebSocket echo handler
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

var server = new WebServer(
    app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 7004) }
);
await server.StartAsync();
Console.WriteLine("Listening on 7004 · Open http://localhost:7004/");
await Task.Delay(Timeout.Infinite);
await server.DisposeAsync();
