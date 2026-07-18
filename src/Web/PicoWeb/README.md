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
| `Controllers.Gen` | Classes implementing `IController` | Auto-generated route registration |
| `PicoWeb.Gen` | `builder.MapMethods<T>()` calls | Compile-time route binding |
