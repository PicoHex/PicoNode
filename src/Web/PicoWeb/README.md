# PicoWeb

PicoNode Web hosting layer. Combines WebApp and TcpNode into a full WebServer with DI container integration.

## Package Info

- **NuGet**: `PicoWeb`
- **TFM**: `net10.0`
- **AOT**: ✅
- **Dependencies**: `PicoNode`, `PicoNode.Web`, `PicoDI`, `PicoCfg.Abs`, `PicoJetson`
- **Embeds**: `Controllers.Gen` (source generator), `PicoWeb.Gen` (source generator)

## Key Types

| Type | Description |
|---|---|
| `WebServer` | Web server: manages HTTP server lifecycle, DI integration |
| `WebApiBuilder` | Web API builder: configures routes, middleware, services |
| `WebApiApp` | Web API application entry point |
| `Results` | HTTP response factory: Text, Json, File, StatusCode, etc. |

## Usage

```csharp
var builder = WebApiBuilder.CreateEmpty();
builder.MapGet("/api/hello", () => Results.Text("Hello!"));
var app = builder.Build();
await app.StartAsync();
```

## Source Generators

PicoWeb embeds two source generators:

| Generator | Trigger | Output |
|---|---|---|
| `Controllers.Gen` | Classes in `Controllers/` folder or `[ApiController]` | Auto-generated endpoint registration + DI |
| `PicoWeb.Gen` | `builder.MapMethods<T>()` calls | Compile-time route binding |

Controllers return DTOs (auto-JSON-serialized) or `IWebResult` types (`HtmlResult`, `RedirectResult`, `JsonResult<T>`, `HtmxResult` etc.).
`WebContext` and `CancellationToken` parameters are passed by the framework.

```csharp
public class UsersController
{
    public UserDto[] GetAll() => db.Users.ToArray();           // → JSON
    public HtmlResult GetPage() => new HtmlResult("<h1>Hi</h1>"); // → HTML
}
```

Register endpoints in startup:

```csharp
EndpointRegistrar.RegisterAll(app);  // registers all discovered controllers
```
