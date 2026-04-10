# AGENTS.md

Purpose: give AI coding agents immediate, practical orientation for working in this repo.

Scope
- Covers `src/`, `tests/`, `samples/`, `benchmarks/`. Start from `PicoNode.slnx` to see project boundaries.

Architecture (read this order)
- `src/PicoNode.Abs/` — core contracts: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, `NodeFaultCode`.
- `src/PicoNode/` — transports: `TcpNode`, `TcpConnection`, `UdpNode` (lifecycle, backpressure, pooling, metrics).
- `src/PicoNode.Http/` — HTTP framing over TCP: `HttpConnectionHandler`, `HttpRouter`.
- `src/PicoNode.Web/` + `src/PicoWeb/` — higher-level web app model and host (`WebApp`, middleware, `WebServer`).

Critical data flows & invariants
- TCP: socket -> `Pipe` -> `TcpConnection` -> handler `OnReceivedAsync` must return the consumed `SequencePosition`.
- UDP: receive -> `UdpDatagramLease` -> per-worker bounded `Channel` -> `IUdpDatagramHandler`; leases return buffers to ArrayPool.
- HTTP: parse result enum -> map to (Incomplete / Success / Rejected); protocol errors map to 4xx/5xx and usually close connection.
- Web: `WebApp.Use` middleware composes in reverse registration order; `WebApp.Build()` produces `HttpConnectionHandler`.

Project-specific conventions
- Use `ValueTask` on hot-path delegates (`HttpRequestHandler`, `WebRequestHandler`) to minimize allocations.
- Faults are reported via optional `FaultHandler` delegates; use `NodeHelper.ReportFault` (never assume it throws).
- Route patterns are strict: paths must start with `'/'` and cannot contain query components.
- Shutdown: `StopAsync` drains in-flight work (honor `DrainTimeout`) — tests assert this (see `tests/PicoNode.Smoke/SmokeTests.cs`).

Developer workflows (examples)
- Build solution: `dotnet build D:\MyProjects\PicoHex\PicoNode\PicoNode.slnx -c Release`
- Run tests (per project):
  `dotnet test --project D:\MyProjects\PicoHex\PicoNode\tests\PicoNode.Http.Tests\PicoNode.Http.Tests.csproj -c Release`
- Run benchmarks: `dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick`

Integration touchpoints (examples)
- HTTP server: `new TcpNode(new TcpNodeOptions { ConnectionHandler = new HttpConnectionHandler(...)} )` (see `samples/PicoNode.Samples.Http/Program.cs`).
- Web app: build with `WebApp` then host with `WebServer` (see `samples/PicoWeb.Samples/Program.cs`).

Where to look first for changes
- Protocol bugfix: `src/PicoNode.Http/HttpConnectionHandler.cs` and related parser tests in `tests/PicoNode.Http.Tests/`.
- Transport/runtime changes: `src/PicoNode/TcpNode.cs`, `src/PicoNode/TcpConnection.cs`, `src/PicoNode/UdpNode.cs` and smoke tests.

Keep it small: prefer incremental changes that preserve pool/array usage and drain semantics.

