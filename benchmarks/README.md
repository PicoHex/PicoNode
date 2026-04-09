# Benchmarks

This folder contains standalone performance projects for PicoNode components.

## PicoNode.Http.Benchmarks

Runs PicoBench-based microbenchmarks for the public `PicoNode.Http` entry points:

- `HttpConnectionHandler.OnReceivedAsync`
- `HttpRouter.HandleAsync` hit / fallback / 405 paths
- in-memory `HttpConnectionHandler + HttpRouter` full pipeline
- `TcpNode + HttpConnectionHandler + HttpRouter` localhost round-trip on one reused connection (excludes connect/accept cost)
- native PicoBench baseline/candidate comparison suites for `direct handler` vs `routed handler`

### Comparison suites

The benchmark project now includes PicoBench-native baseline comparisons:

- in-memory GET: direct handler (**baseline**) vs routed handler
- in-memory POST echo: direct handler (**baseline**) vs routed handler
- localhost GET round-trip: direct handler (**baseline**) vs routed handler
- localhost POST echo round-trip: direct handler (**baseline**) vs routed handler

These comparisons are meaningful because each pair shares the same request shape, response shape, parameters, and setup. The only variable is whether dispatch goes through `HttpRouter`.

### Run

```powershell
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- default
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- precise
```

If no mode is specified, the project uses `default`.
