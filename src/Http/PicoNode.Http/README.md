# PicoNode.Http

PicoNode HTTP 协议层。实现 HTTP/1.1、HTTP/2 (h2c upgrade)、WebSocket (RFC 6455) 和 HPACK 头部压缩。

## 包信息

- **NuGet**: `PicoNode.Http`
- **TFM**: `net10.0`
- **AOT**: ✅
- **依赖**: `PicoNode.Abs`, `PicoDI.Abs`, `PicoLog.Abs`, `PicoJetson`, `PicoCfg.Abs`

## 核心类型

| 类型 | 说明 |
|---|---|
| `HttpConnectionHandler` | HTTP 连接处理器: `ITcpConnectionHandler` 实现 |
| `HttpRouter` | HTTP 路由: 精确路径匹配, 通配符, 405 Method Not Allowed |
| `RouteTable` | 内部路由表 |
| `HttpHeaderNames` | HTTP 头部名称常量 |
| `Http2FrameCodec` | HTTP/2 帧编解码 |
| `HpackEncoder` / `HpackDecoder` | HPACK 编解码 |
| `WebSocketHandler` | WebSocket (RFC 6455) 握手与帧处理 |

## 使用

```csharp
var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    ConnectionHandler = new HttpConnectionHandler(
        HttpConnectionHandlerOptions.Default with { Router = router }
    ),
});
```
