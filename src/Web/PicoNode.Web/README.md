# PicoNode.Web

PicoNode Web middleware framework. Provides full web application capabilities on top of the HTTP layer.

## Package Info

- **NuGet**: `PicoNode.Web`
- **TFM**: `net10.0`
- **AOT**: ✅
- **Dependencies**: `PicoNode.Abs`, `PicoNode.Http`, `PicoNode.Web.Session.Abs`, `PicoDI.Abs`, `PicoLog.Abs`

## Key Types

| Type | Description |
|---|---|
| `WebApp` / `WebAppOptions` | Web application builder: DI-first middleware pipeline, route registration |
| `WebContext` | Per-request context: request, route values, query, items, session, DI scope |
| `WebResults` / `IWebResult` (`HtmlResult`, `TextResult`, `RedirectResult`, `EmptyResult`) | Response factories and result types |
| `CompressionMiddleware` | Response compression (gzip, deflate, brotli) |
| `StaticFileMiddleware` | Static file serving with MIME mapping |
| `CacheMiddleware` | Cache control |
| `SecurityHeadersMiddleware` | Security headers (HSTS, CSP, X-Frame-Options, etc.) |
| `CorsHandler` / `CorsOptions` | CORS preflight and response headers |
| `RateLimitMiddleware` / `InMemoryRateLimitStore` | Token-bucket rate limiting |
| `AuthMiddleware` / `AuthOptions` | Bearer token authentication |
| `SessionMiddleware`, `SessionCookie` / `SessionCookieOptions`, `InMemorySessionStore` | Session management |
| `SseEndpoint` / `SseConnection` | Server-Sent Events |
| `CookieParser`, `SetCookieBuilder`, `MultipartFormDataParser` | Cookie and multipart form helpers |
| `UiEventHub` | Bounded pub/sub hub for UI event fan-out |

## Usage

```csharp
using PicoDI;
using PicoNode.Web;

var app = new WebApp(new SvcContainer());
app.Use(new CompressionMiddleware().InvokeAsync);
app.MapGet("/hello", static (WebContext ctx, CancellationToken _) =>
    ValueTask.FromResult(WebResults.Text(200, "Hello, World!")));

app.Build();
```
