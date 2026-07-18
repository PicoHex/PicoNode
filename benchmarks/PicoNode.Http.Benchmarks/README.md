# PicoNode.Http.Benchmarks

PicoNode.Http 性能基准测试。覆盖 HTTP 路由、连接处理器和端到端管线的微基准。

## 运行

```powershell
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- default
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- precise
```
