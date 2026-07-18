# src

PicoNode 源代码,按模块组织:

| 目录 | NuGet 包 | 说明 |
|---|---|---|
| `Network/PicoNode.Abs` | `PicoNode.Abs` | 核心抽象: INode, ITcpConnectionHandler, ITcpConnectionContext, IUdpDatagramHandler |
| `Network/PicoNode` | `PicoNode` | TCP/UDP 节点实现: TcpNode, UdpNode, TLS, metrics, 连接池 |
| `Http/PicoNode.Http` | `PicoNode.Http` | HTTP/1.1, HTTP/2 (h2c), WebSocket (RFC 6455), HPACK (RFC 7541) |
| `Web/PicoNode.Web.Session.Abs` | `PicoNode.Web.Session.Abs` | Session 抽象: ISession, ISessionStore |
| `Web/PicoNode.Web` | `PicoNode.Web` | Web 中间件框架: 路由, CORS, 压缩, 缓存, SSE, multipart |
| `Web/PicoWeb` | `PicoWeb` | Web 托管: WebServer = WebApp + TcpNode + DI |
| `Web/Controllers.Gen` | (嵌入 PicoWeb) | 源生成器: 控制器方法绑定 |
| `Web/PicoWeb.Gen` | (嵌入 PicoWeb) | 源生成器: MapMethod API 绑定 |
