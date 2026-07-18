# PicoNode.Http.Benchmarks

PicoNode.Http performance benchmarks. Covers HTTP routing, connection handler, and end-to-end pipeline micro-benchmarks.

## Run

```powershell
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- default
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- precise
```
