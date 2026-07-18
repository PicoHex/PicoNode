# PicoNode

PicoNode 传输层实现。提供 TcpNode 和 UdpNode,支持 TLS、metrics、连接管理。

## 包信息

- **NuGet**: `PicoNode`
- **TFM**: `net10.0`
- **AOT**: ✅
- **依赖**: `PicoNode.Abs`, `PicoDI`, `PicoDI.Abs`, `PicoLog.Abs`

## 核心类型

| 类型 | 说明 |
|---|---|
| `TcpNode` | TCP 传输节点: accept loop, 连接数限制, keep-alive, TLS |
| `TcpNodeOptions` | TCP 节点配置: Endpoint, SslOptions, maxConnections, drainTimeout |
| `UdpNode` | UDP 传输节点: receive loop, datagram dispatch |
| `TcpNodeMetrics` | TCP 传输指标: accept/reject/close 计数, 字节速率 |
| `TcpConnection` | 单个 TCP 连接生命周期管理 |

## TLS 支持

```csharp
var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 443),
    SslOptions = new SslServerAuthenticationOptions { ServerCertificate = cert },
    ConnectionHandler = new MyHandler(),
});
```
