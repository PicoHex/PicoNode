# PicoNode.Http.Benchmarks

PicoNode.Http performance benchmarks. Covers HTTP routing, connection handler, and end-to-end pipeline micro-benchmarks.

## Run

```powershell
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -f net10.0 -c Release -- quick
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -f net10.0 -c Release -- default
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -f net10.0 -c Release -- precise
```
