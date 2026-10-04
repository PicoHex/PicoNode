# src

Package sources, grouped by area. `PackageId` is what `api/<PackageId>.public.txt` tracks and what the
release workflow packs.

| Area | Project | PackageId | Contents |
|---|---|---|---|
| `Network/` | `PicoNode.Abs` | `PicoNode.Abs` | Core interfaces: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, fault codes, enums |
| `Network/` | `PicoNode` | `PicoNode` | `TcpNode` / `UdpNode` — async socket transports |
| `Http/` | `PicoNode.Http` | `PicoNode.Http` | `HttpConnectionHandler`, `HttpRouter`, HTTP/1.1 + HTTP/2 + WebSocket, HPACK |
| `Web/` | `PicoNode.Web` | `PicoNode.Web` | `WebApp`, `WebRouter`, middleware (compression, CORS, cookies, session, auth, rate limiting), static files, DI |
| `Web/` | `PicoNode.Web.Session.Abs` | `PicoNode.Web.Session.Abs` | Session store abstractions |
| `Web/` | `PicoWeb` | `PicoWeb` | `WebServer` — hosts `WebApp` on `TcpNode` |
| `Web/` | `Controllers.Gen`, `PicoWeb.Gen` | (source generators) | Compile-time endpoint/controller generation (not packed) |
| `Rpc/` | `PicoJsonRpc` | `PicoJsonRpc` | JSON-RPC 2.0 envelopes, framing and lifecycle |

Layout rules: one directory per package, `PackageId` set in the csproj, `IsPackable` decides what the
release workflow packs, and each package's public surface lands in `api/<PackageId>.public.txt` (see
`AGENTS.md`).
