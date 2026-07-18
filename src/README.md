# src

PicoNode source code, organized by module:

| Directory | NuGet Package | Description |
|---|---|---|
| `Network/PicoNode.Abs` | `PicoNode.Abs` | Core abstractions: INode, ITcpConnectionHandler, ITcpConnectionContext, IUdpDatagramHandler |
| `Network/PicoNode` | `PicoNode` | TCP/UDP node implementation: TcpNode, UdpNode, TLS, metrics, connection pooling |
| `Http/PicoNode.Http` | `PicoNode.Http` | HTTP/1.1, HTTP/2 (h2c), WebSocket (RFC 6455), HPACK (RFC 7541) |
| `Web/PicoNode.Web.Session.Abs` | `PicoNode.Web.Session.Abs` | Session abstractions: ISession, ISessionStore |
| `Web/PicoNode.Web` | `PicoNode.Web` | Web middleware framework: routing, CORS, compression, caching, SSE, multipart |
| `Web/PicoWeb` | `PicoWeb` | Web hosting: WebServer = WebApp + TcpNode + DI |
| `Web/Controllers.Gen` | (embedded in PicoWeb) | Source generator: controller method binding |
| `Web/PicoWeb.Gen` | (embedded in PicoWeb) | Source generator: MapMethod API binding |
