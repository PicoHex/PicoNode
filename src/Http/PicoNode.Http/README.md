# PicoNode.Http

PicoNode HTTP protocol layer. Implements HTTP/1.1, HTTP/2 (h2c upgrade), WebSocket (RFC 6455), and HPACK header compression.

## Package Info

- **NuGet**: `PicoNode.Http`
- **TFM**: `net10.0`
- **AOT**: ✅
- **Dependencies**: `PicoNode.Abs`, `PicoDI.Abs`, `PicoLog.Abs`, `PicoJetson`, `PicoCfg.Abs`

## Key Types

| Type | Description |
|---|---|
| `HttpConnectionHandler` | HTTP connection handler (implements `ITcpConnectionHandler`) |
| `HttpRouter` | HTTP routing: exact path match, wildcards, 405 Method Not Allowed |
| `RouteTable` | Internal route table |
| `HttpHeaderNames` | HTTP header name constants |
| `Http2FrameCodec` | HTTP/2 frame codec |
| `HpackEncoder` / `HpackDecoder` | HPACK encoder / decoder |
| `WebSocketHandler` | WebSocket (RFC 6455) handshake and frame handling |

## Usage

```csharp
var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    ConnectionHandler = new HttpConnectionHandler(
        HttpConnectionHandlerOptions.Default with { Router = router }
    ),
});
```
