# PicoNode.Web

PicoNode Web 中间件框架。在 HTTP 层之上提供完整的 Web 应用功能。

## 包信息

- **NuGet**: `PicoNode.Web`
- **TFM**: `net10.0`
- **AOT**: ✅
- **依赖**: `PicoNode.Abs`, `PicoNode.Http`, `PicoNode.Web.Session.Abs`, `PicoDI.Abs`, `PicoLog.Abs`, `PicoCfg.Abs`

## 核心类型

| 类型 | 说明 |
|---|---|
| `WebApp` | Web 应用构建器: 中间件管道, 路由注册 |
| `WebRouter` | Web 路由: 结合 RouteTable (精确) + RadixTree (参数化) |
| `CorsMiddleware` | CORS 中间件 |
| `CompressionMiddleware` | 响应压缩 (gzip, deflate, brotli) |
| `CacheMiddleware` | 缓存控制 |
| `SecurityHeadersMiddleware` | 安全头部 (HSTS, CSP, X-Frame-Options 等) |
| `SseConnection` | Server-Sent Events 连接 |
| `MultipartFormDataParser` | multipart/form-data 解析 |
| `SessionMiddleware` | 会话中间件 |
| `AuthMiddleware` | 认证中间件 |
| `StaticFileMiddleware` | 静态文件服务 |

## 使用

```csharp
var app = new WebApp();
app.UseCors();
app.UseCompression();
app.MapGet("/hello", () => Results.Text("Hello, World!"));
```
