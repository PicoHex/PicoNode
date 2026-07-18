# samples

PicoNode 示例项目:

| 项目 | 说明 |
|---|---|
| `PicoNode.Samples.Echo` | TCP echo + UDP echo 服务器 |
| `PicoNode.Samples.Http` | HTTP 服务器 (纯 HTTP) |
| `PicoNode.Samples.Https` | HTTPS 服务器 (TLS) |
| `PicoWeb.Samples` | Web API 服务器 (PicoWeb + DI) |
| `PicoWeb.Samples.Abs` | Web API 抽象示例 |

### 运行

```powershell
dotnet run --project samples/PicoNode.Samples.Echo/PicoNode.Samples.Echo.csproj
dotnet run --project samples/PicoNode.Samples.Http/PicoNode.Samples.Http.csproj
```
