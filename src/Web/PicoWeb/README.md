# PicoWeb

PicoNode Web 托管层。将 WebApp 与 TcpNode 结合为完整的 WebServer,集成 DI 容器。

## 包信息

- **NuGet**: `PicoWeb`
- **TFM**: `net10.0`
- **AOT**: ✅
- **依赖**: `PicoNode`, `PicoNode.Web`, `PicoDI`, `PicoCfg.Abs`, `PicoJetson`
- **嵌入**: `Controllers.Gen` (源生成器), `PicoWeb.Gen` (源生成器)

## 核心类型

| 类型 | 说明 |
|---|---|
| `WebServer` | Web 服务器: 管理 HTTP server 生命周期, DI 集成 |
| `WebApiBuilder` | Web API 构建器: 配置路由、中间件、服务 |
| `WebApiApp` | Web API 应用入口 |
| `Results` | HTTP 响应工厂: Text, Json, File, StatusCode 等 |

## 使用

```csharp
var builder = WebApiBuilder.CreateEmpty();
builder.MapGet("/api/hello", () => Results.Text("Hello!"));
var app = builder.Build();
await app.StartAsync();
```

## 源生成器

PicoWeb 嵌入两个源生成器:

| 生成器 | 触发条件 | 输出 |
|---|---|---|
| `Controllers.Gen` | 实现 `IController` 的类 | 自动路由注册 |
| `PicoWeb.Gen` | `builder.MapMethods<T>()` | 编译时路由绑定 |
