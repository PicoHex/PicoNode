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
| `Web/` | `Controllers.Gen`, `PicoWeb.Gen` | (source generators) | Compile-time endpoint/controller generation, packed as analyzers inside the `PicoWeb` package (`analyzers\dotnet\cs\`) rather than as NuGet packages of their own |
| `Rpc/` | `PicoJsonRpc` | `PicoJsonRpc` | JSON-RPC 2.0 envelopes, framing and lifecycle |

Layout rules: one directory per package, `PackageId` set in the csproj. The release workflow packs each
package through an explicit `dotnet pack` step in `.github/workflows/release.yml` (seven of them, and it
fails the run when the number of `.nupkg` files differs from the number of pack steps) — a new package
needs a step added there too, because `IsPackable` alone ships nothing. Each package's public surface
lands in `api/<PackageId>.public.txt` (see `AGENTS.md`).
