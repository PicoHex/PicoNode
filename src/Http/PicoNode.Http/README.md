# PicoNode.Http

PicoNode HTTP protocol layer. Implements HTTP/1.1, HTTP/2 (h2c upgrade), WebSocket (RFC 6455), and HPACK header compression.

## Package Info

- **NuGet**: `PicoNode.Http`
- **TFM**: `net10.0`
- **AOT**: ✅
- **Dependencies**: `PicoNode.Abs`, `PicoLog.Abs`

## Key Types

| Type | Description |
|---|---|
| `HttpConnectionHandler` | HTTP connection handler (implements `ITcpConnectionHandler`) |
| `HttpConnectionHandlerOptions` | Handler options: request handler, size limits, streaming buffer, request timeout, WebSocket message cap |
| `HttpRouter` / `HttpRouterOptions` | HTTP routing: exact path match, 405 Method Not Allowed, fallback handler |
| `HttpRequest` / `HttpResponse` | Protocol-level request and response |
| `HttpHeaderNames` | HTTP header name constants |
| `Http2FrameCodec` | HTTP/2 frame codec |
| `WebSocketUpgrade`, `WebSocketMessageHandler`, `WebSocketFrameCodec` | WebSocket (RFC 6455) handshake, message handler, and frame codec |

`HpackEncoder` / `HpackDecoder` (HPACK, RFC 7541) are internal to the protocol layer.

## Usage

```csharp
using System.Net;
using PicoNode;
using PicoNode.Http;

var router = new HttpRouter(new HttpRouterOptions
{
    Routes =
    [
        Route<HttpRequestHandler>.MapGet("/", static (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })),
    ],
});

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    ConnectionHandler = new HttpConnectionHandler(new HttpConnectionHandlerOptions
    {
        RequestHandler = router.HandleAsync,
        ServerHeader = "PicoNode",
    }),
});
```
