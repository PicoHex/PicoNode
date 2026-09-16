# PicoWeb

PicoNode Web hosting layer. Combines WebApp and TcpNode into a full WebServer with DI container integration.

## Package Info

- **NuGet**: `PicoWeb`
- **TFM**: `net10.0`
- **AOT**: ✅
- **Dependencies**: `PicoNode`, `PicoNode.Web`, `PicoDI`, `PicoJetson`
- **Embeds**: `Controllers.Gen` (source generator), `PicoWeb.Gen` (build-time diagnostics)

## Key Types

| Type | Description |
|---|---|
| `WebServer` | Web server: manages HTTP server lifecycle, DI integration |
| `WebApiBuilder` | Web API builder: configures routes, middleware, services |
| `WebApiApp` | Web API application entry point (`App`, `RunAsync`) |
| `Results` | HTTP response factory: `Json`, `Text`, `Empty`, `Redirect` |

## Usage

```csharp
using PicoNode.Web;
using PicoWeb;

var api = new WebApiBuilder()
    .ConfigureApp(_ => new WebAppOptions { ServerHeader = "MyApp" })
    .Build();

api.App.MapGet("/api/hello", static (WebContext ctx, CancellationToken _) =>
    ValueTask.FromResult(Results.Text(200, "Hello!")));

await api.RunAsync("http://127.0.0.1:5000");
```

## Source Generators

PicoWeb embeds two analyzers:

| Generator | Trigger | Output |
|---|---|---|
| `Controllers.Gen` | Classes in `Controllers/` folder or `[ApiController]` | Endpoint stubs + DI registration + `EndpointRegistrar` |
| `PicoWeb.Gen` | `app.MapGet`/`MapPost`/`MapPut`/`MapDelete` calls | `PWR001` build-time diagnostic only — no source is emitted |

Controllers return DTOs (serialized by `PicoJetson.Gen`) or `IWebResult` types
(`HtmlResult`, `TextResult`, `RedirectResult`, `EmptyResult`).
`WebContext` and `CancellationToken` parameters are passed by the framework.

```csharp
public class UsersController
{
    public UserDto GetUser(int id) => new UserDto { Id = id };      // → JSON
    public HtmlResult GetPage() => new HtmlResult("<h1>Hi</h1>");   // → HTML
}
```

Register endpoints in startup:

```csharp
EndpointRegistrar.RegisterAll(app);  // registers all discovered controllers
```

When the project has no controllers of its own and references an application that
already carries an `EndpointRegistrar` (e.g. an integration-test Exe referencing a
sample app), `Controllers.Gen` emits nothing and the call binds to the referenced
app's registrar — a local empty shim would shadow it (CS0436) and register nothing.
