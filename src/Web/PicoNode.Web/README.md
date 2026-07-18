# PicoNode.Web

PicoNode Web middleware framework. Provides full web application capabilities on top of the HTTP layer.

## Package Info

- **NuGet**: `PicoNode.Web`
- **TFM**: `net10.0`
- **AOT**: ✅
- **Dependencies**: `PicoNode.Abs`, `PicoNode.Http`, `PicoNode.Web.Session.Abs`, `PicoDI.Abs`, `PicoLog.Abs`, `PicoCfg.Abs`

## Key Types

| Type | Description |
|---|---|
| `WebApp` | Web application builder: middleware pipeline, route registration |
| `WebRouter` | Web routing: combines RouteTable (exact) + RadixTree (parameterized) |
| `CorsMiddleware` | CORS middleware |
| `CompressionMiddleware` | Response compression (gzip, deflate, brotli) |
| `CacheMiddleware` | Cache control |
| `SecurityHeadersMiddleware` | Security headers (HSTS, CSP, X-Frame-Options, etc.) |
| `SseConnection` | Server-Sent Events connection |
| `MultipartFormDataParser` | multipart/form-data parser |
| `SessionMiddleware` | Session middleware |
| `AuthMiddleware` | Authentication middleware |
| `StaticFileMiddleware` | Static file serving |

## Usage

```csharp
var app = new WebApp();
app.UseCors();
app.UseCompression();
app.MapGet("/hello", () => Results.Text("Hello, World!"));
```
