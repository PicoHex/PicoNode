# PicoNode

PicoNode transport layer implementation. Provides TcpNode and UdpNode with TLS, metrics, and connection management.

## Package Info

- **NuGet**: `PicoNode`
- **TFM**: `net10.0`
- **AOT**: ✅
- **Dependencies**: `PicoNode.Abs`, `PicoDI`, `PicoDI.Abs`, `PicoLog.Abs`

## Key Types

| Type | Description |
|---|---|
| `TcpNode` | TCP transport node: accept loop, connection limits, keep-alive, TLS |
| `TcpNodeOptions` | TCP node config: Endpoint, SslOptions, maxConnections, drainTimeout |
| `UdpNode` | UDP transport node: receive loop, datagram dispatch |
| `TcpNodeMetrics` | TCP transport metrics: accept/reject/close counts, byte rates |
| `TcpConnection` | Single TCP connection lifecycle management |

## TLS

```csharp
var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 443),
    SslOptions = new SslServerAuthenticationOptions { ServerCertificate = cert },
    ConnectionHandler = new MyHandler(),
});
```
