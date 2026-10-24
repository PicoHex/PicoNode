# PicoNode

> Ein geschichteter, AOT-nativer Networking-Stack für .NET — von rohen TCP/UDP-Sockets bis zu einem vollwertigen HTTP-Webframework.

[![NuGet](https://img.shields.io/nuget/v/PicoNode.svg)](https://www.nuget.org/packages/PicoNode)
[![License](https://img.shields.io/github/license/PicoHex/PicoNode)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

[English](README.md) | [简体中文](README.zh.md) | [繁體中文](README.zh-TW.md) | **Deutsch** | [Español](README.es.md) | [Français](README.fr.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Português (Brasil)](README.pt-BR.md) | [Русский](README.ru.md)

```
┌─────────────────────────────────────────────────────────────┐
│  PicoNode: geschichtetes Networking für .NET                │
│  ✓ Rohe TCP/UDP-Socket-Transports mit asynchroner E/A      │
│  ✓ HTTP/1.1 + HTTP/2 + WebSocket-Protokolle                │
│  ✓ Webframework mit Middleware, Routing, statischen Dateien │
│  ✓ Integriert ins PicoHex-Ökosystem (PicoDI/PicoLog/PicoCfg)│
│  ✓ Native AOT-Kompatibilität über alle net10.0-Ebenen       │
│  ✓ Minimale Laufzeitabhängigkeiten                          │
└─────────────────────────────────────────────────────────────┘
```

## Warum PicoNode?

| Merkmal | PicoNode | ASP.NET Core |
|---------|----------|-------------|
| **Abhängigkeitsmodell** | Keine Microsoft-Framework-Abhängigkeiten (nur PicoHex-native Bibliotheken); Ebenen frei wählbar | `Microsoft.AspNetCore.App`-Frameworkreferenz |
| **Request-Parsing** | Span-basiertes Streaming, Zero-Copy mit `System.IO.Pipelines` | String-basiert mit `IO.Pipelines`-Adapter |
| **HTTP/2** | Integrierter HPACK-Decoder, Frame-Level-Steuerung | Transparent via Kestrel; eingeschränkter Low-Level-Zugriff |
| **AOT-Support** | ✅ Nativ — alle net10.0-Bibliotheken | ⚠️ Erfordert Trimming |
| **DI / Logging / Config** | PicoDI + PicoLog + PicoCfg (PicoHex-nativ) | Microsoft.Extensions.* |
| **WebSocket** | RFC-6455-Frame-Codec mit Message-Handler-Abstraktion | Transparent via Middleware |
| **Codezeilen** | 25.125 Zeilen C# (178 Dateien in `src/`, ohne `bin/`/`obj/`) | in diesem Repository nicht gemessen |

> **Entwurfspriorität:** PicoNode legt Wert auf Allokationseffizienz und AOT-Kompatibilität. `ValueTask` auf Hot-Path-Delegates, ArrayPool-basiertes Puffermanagement und optionale Delegates (keine erzwungenen Allokationen) sind bewusste Kompromisse. Sie halten die Transportschicht kompakt und vorhersagbar.

### Das PicoHex-Ökosystem

PicoNode ist Teil der PicoHex-Familie und lässt sich nativ mit folgenden Bibliotheken integrieren:

| Bibliothek | Zweck | NuGet |
|---------|---------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | Reflexionsfreie Compilezeit-DI | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | Strukturiertes Logging mit AOT-Sicherheit | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | Quellgenerierte Konfigurationsbindung | `PicoCfg.Abs` |

```
PicoNode.Abs        Kernschnittstellen                      (net10.0, keine Dependencies)
    ↓
PicoNode             TCP- & UDP-Transporte + ILogger         (net10.0)
    ↓
PicoNode.Http        HTTP/1.1 + HTTP/2 + WebSocket           (net10.0)
    ↓
PicoNode.Web         Webframework + PicoDI ISvcContainer     (net10.0)
    ↓
PicoWeb              Startbereiter Webserver + PicoCfg       (net10.0)
PicoJsonRpc           JSON-RPC 2.0 über stdio (NDJSON)     (net10.0)
```

## Schnellstart

### Installation

```bash
dotnet add package PicoNode
```

> Mit der Installation von `PicoNode` erhältst du den TCP/UDP-Transport. Für höhere Abstraktionsebenen referenziere `PicoNode.Http` oder `PicoNode.Web`.

### Paketarchitektur

PicoNode wird als geschichtete NuGet-Pakete ausgeliefert. Wähle genau die Abstraktionsebene, die du brauchst:

| Paket | Installation, wenn … | Was du bekommst |
|---------|--------------|-------------|
| **PicoWeb** | Du einen startbereiten Webserver willst | WebServer + WebApp + HTTP + TCP (alles transitiv) |
| **PicoNode.Web** | Du das Webframework ohne Hosting-Umgebung willst | WebApp, Routing, Middleware, statische Dateien, DI |
| **PicoNode.Web.Session.Abs** | Du einen Session-Store schreibst | `ISession` / `ISessionStore` — die Session-Abstraktionen, die `PicoNode.Web` nutzt |
| **PicoNode.Http** | Du rohes HTTP-Protokoll-Handling brauchst | HTTP/1.1 + HTTP/2 + WebSocket, HttpRouter |
| **PicoJsonRpc** | Du sprichst JSON-RPC 2.0 mit einem Kindprozess | `NdjsonFramer`, `JsonRpcClient`, `ProcessLifecycle` |
| **PicoNode** | Du rohe TCP/UDP-Transporte brauchst | TcpNode, UdpNode, Socket-Lebenszyklus, Metriken |
| **PicoNode.Abs** | Du einen Handler oder eine Erweiterung schreibst | INode, ITcpConnectionHandler, Kernverträge |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode.Abs
PicoWeb  →  PicoNode  →  PicoNode.Abs
```

### TCP-Echo-Server

```csharp
using System.Buffers;
using System.Net;
using PicoNode;
using PicoNode.Abs;

var node = new TcpNode(
    new TcpNodeOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
        ConnectionHandler = new EchoHandler(),
    }
);

await node.StartAsync();
Console.ReadLine();
await node.DisposeAsync();

sealed class EchoHandler : ITcpConnectionHandler
{
    public Task OnConnectedAsync(ITcpConnectionContext c, CancellationToken ct) =>
        Task.CompletedTask;

    public ValueTask OnClosedAsync(
        ITcpConnectionContext c,
        TcpCloseReason r,
        Exception? e,
        CancellationToken ct
    ) => ValueTask.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken ct
    )
    {
        _ = connection.SendAsync(buffer, ct);
        return ValueTask.FromResult(buffer.End);
    }
}
```

### HTTP-Server (niedrige Ebene)

```csharp
using System.Net;
using PicoNode;
using PicoNode.Http;

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7002),
    ConnectionHandler = new HttpConnectionHandler(new HttpConnectionHandlerOptions
    {
        RequestHandler = new HttpRouter(new HttpRouterOptions
        {
            Routes =
            [
                Route<HttpRequestHandler>.MapGet("/", static (_, _) =>
                    ValueTask.FromResult(new HttpResponse
                    {
                        StatusCode = 200, ReasonPhrase = "OK",
                        Headers = [new("Content-Type", "text/plain")],
                        Body = "Hello from PicoNode.Http"u8.ToArray(),
                    })),
            ],
        }).HandleAsync,
        ServerHeader = "PicoNode",
    }),
});

await node.StartAsync();
Console.ReadLine();
await node.DisposeAsync();
```

### Webanwendung (DI-First + Delegat)

```csharp
using PicoNode.Web;
using PicoWeb;

var api = new WebApiBuilder()
    .ConfigureApp(_ => new WebAppOptions { ServerHeader = "MyApp" })
    // ConfigureApp receives the CURRENT options — later calls can build on
    // earlier configuration instead of starting from defaults.
    .RegisterScoped<IUserService, UserService>()
    .Build();

api.App.MapGet("/", static (WebContext ctx, CancellationToken _) =>
    ValueTask.FromResult(Results.Text(200, "Hello, World!")));

api.App.MapGet("/users/{id}", async (WebContext ctx, CancellationToken _) =>
{
    var svc = (IUserService)ctx.Services.GetService(typeof(IUserService))!;
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});

api.App.MapPost("/echo", async (WebContext ctx, CancellationToken _) =>
{
    using var reader = new StreamReader(ctx.Request.BodyStream);
    var body = await reader.ReadToEndAsync();
    return Results.Text(200, body);
});

await api.RunAsync("http://+:8080");
```

### Webanwendung (Controller-basiert)

```csharp
// Controllers/UsersController.cs
using PicoJetson;

public class UsersController
{
    public UserDto GetUser(int id) { return new UserDto { Id = id }; }
}

// Program.cs
var api = new WebApiBuilder()
    .RegisterScoped<UsersController>()
    .Build();

// Controllers.Gen auto-generates endpoint stubs (DTO serializers: PicoJetson.Gen)
await api.RunAsync("http://+:8080");
```
## Konfiguration

PicoNode unterstützt zwei Konfigurationsmodi:

### Code-First (Inline)

```csharp
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Any, 8080),
    MaxConnections = 500,
    IdleTimeout = TimeSpan.FromMinutes(5),
};
var node = new TcpNode(options);
```

### PicoCfg-Bindung (AOT-sicher, quellgeneriert)

```csharp
var config = await Cfg.CreateBuilder()
    .Add(new Dictionary<string, string>
    {
        ["App:Name"] = "PicoCfg",
        ["App:Enabled"] = "true",
    })
    .BuildAsync();

var settings = CfgBind.Bind<AppSettings>(config, "App");

public sealed class AppSettings
{
    public string? Name { get; set; }
    public bool Enabled { get; set; }
}
```

### Laufzeit-Neuladen

```csharp
// TcpNode supports runtime config reload (except Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    Config = config, // ICfgRoot for live reload
};
// Node starts a reload loop watching for config changes
```

### Wichtige Optionen

#### TcpNodeOptions

| Option | Standard | Beschreibung |
|--------|---------|-------------|
| `Endpoint` | *(erforderlich)* | Lokaler Endpunkt zum Binden |
| `ConnectionHandler` | *(erforderlich)* | `ITcpConnectionHandler` |
| `MaxConnections` | 1000 | Maximale gleichzeitige Verbindungen |
| `IdleTimeout` | 2 Min. | Zeit, bevor eine Leerlaufverbindung geschlossen wird |
| `DrainTimeout` | 5 Sek. | Gnadenfrist beim Herunterfahren |
| `SslOptions` | `null` | TLS/SSL-Konfiguration |
| `NoDelay` | `true` | TCP_NODELAY (Nagle deaktiviert) |
| `Logger` | `null` | PicoLog `ILogger` für strukturierte Diagnose |

#### UdpNodeOptions

| Option | Standard | Beschreibung |
|--------|---------|-------------|
| `Endpoint` | *(erforderlich)* | Lokaler Endpunkt zum Binden |
| `DatagramHandler` | *(erforderlich)* | `IUdpDatagramHandler` |
| `DispatchWorkerCount` | 1 | Gleichzeitige Datagramm-Worker |
| `DatagramQueueCapacity` | 1024 | Warteschlangentiefe pro Worker |
| `QueueOverflowMode` | `DropNewest` | Verhalten bei vollen Warteschlangen |
| `Logger` | `null` | PicoLog `ILogger` |

#### HttpConnectionHandlerOptions

| Option | Standard | Beschreibung |
|--------|---------|-------------|
| `RequestHandler` | *(erforderlich)* | HttpRequestHandler-Delegat |
| `ServerHeader` | `null` | Wert für den `Server`-Header |
| `MaxRequestBytes` | 8192 | Maximale Request-Größe in Bytes |
| `MaxRequestBodySize` | 67108864 (64 MB) | Maximale Request-Body-Größe in Bytes |
| `StreamingResponseBufferSize` | 4096 | Puffergröße für gestreamte Response-Bodies |
| `RequestTimeout` | 30 s | Maximale Zeit zum Empfangen einer vollständigen Anfrage |
| `WebSocketMessageHandler` | `null` | WebSocket-Nachrichtenhandler |
| `WebSocketMaxMessageSize` | 262144 (256 KB) | Maximale Größe einer reassemblierten WebSocket-Nachricht |
| `Logger` | `null` | PicoLog `ILogger` |

## Logging

PicoNode verwendet PicoLog für strukturierte Diagnose. Alle nicht-fatalen Fehler werden mit Operationskontext protokolliert:

```csharp
var logger = new LoggerFactory([new ColoredConsoleSink(new ConsoleFormatter())])
    .CreateLogger("PicoNode.Tcp");

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
    ConnectionHandler = handler,
    Logger = logger, // All transport faults logged here
});

// Log output:
// [Error] Operation tcp.accept failed: AcceptFailed - System.Net.Sockets.SocketException
// [Warning] Operation tcp.reject.limit failed: SessionRejected
// [Debug] Socket shutdown during TLS teardown failed
```

**Log-Level nach Fehlercode:**
- `Error`: StartFailed, StopFailed, AcceptFailed, ReceiveFailed, SendFailed, HandlerFailed, DatagramReceiveFailed, DatagramHandlerFailed
- `Warning`: SessionRejected, DatagramDropped
- `Debug`: TlsFailed, Socket-Shutdown während der Bereinigung (Best-Effort-Operationen)

## Dependency Injection

PicoNode.Web benötigt `ISvcContainer` zur Konstruktionszeit (DI First). Scopes werden pro Anfrage automatisch erstellt.

### Manuelle DI-Auflösung in Handlern

```csharp
using PicoNode.Web;
using PicoWeb;
using PicoJetson;

var container = new SvcContainer();
container.RegisterScoped<IDatabase, SqlDatabase>();

var app = new WebApp(container);
app.MapGet("/db", async (WebContext ctx, CancellationToken _) =>
{
    var db = (IDatabase)ctx.Services.GetService(typeof(IDatabase))!;
    var data = await db!.QueryAsync("...");
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(data);
    return Results.Json(200, bytes);
});

app.Build();
```

### Automatische Parameter-Injektion per Delegat

Handler-Parameter werden automatisch aufgelöst (erfordert `using PicoNode.Web;`):
- `WebContext` → aktueller Kontext
- `CancellationToken` → Abbruch-Token der Anfrage
- Jeder registrierte Dienst → aus dem DI-Scope aufgelöst

```csharp
app.MapGet("/users/{id}", async (WebContext ctx, CancellationToken _) =>
{
    var svc = (IUserService)ctx.Services.GetService(typeof(IUserService))!;
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});
```

### AOT-kompatible Serialisierung

Die PicoJetson-Source-Generatoren laufen zur Kompilierzeit. Handler müssen `SerializeToUtf8Bytes<T>()` direkt im Benutzercode aufrufen, um den Generator auszulösen:

```csharp
// ✅ Triggers PicoJetson.Gen — UserDto serializer generated
var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);

// ❌ Does NOT trigger the generator: a controller's DTO is serialised by Controllers.Gen's
//    GENERATED code, which PicoJetson.Gen never sees — an unmarked DTO still compiles and
//    then throws on every request (NativeAOT cannot fall back to reflection). Mark it
//    [PicoJsonSerializable]; forgetting it surfaces as PCW003.
```

### WebApiBuilder (Komfort)

```csharp
using PicoNode.Web;
using PicoWeb;

var api = new WebApiBuilder()
    .RegisterScoped<IUserService, UserService>()
    .ConfigureJson(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    .Build();

api.App.MapGet("/api/users/{id}", async (WebContext ctx, CancellationToken _) =>
{
    var svc = (IUserService)ctx.Services.GetService(typeof(IUserService))!;
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});

await api.RunAsync("http://+:5000");
```

### WebApiBuilder mit Controllern (dreistufige Endpunkt-Registrierung)

```csharp
// 1. Controller in Controllers/ folder (convention)
//    Controllers/UsersController.cs
public class UsersController
{
    public UserDto GetUser(int id) { return new UserDto { ... }; }
    public List<UserDto> GetAllUsers() { return ...; }
}

// 2. Controllers.Gen auto-registers controllers in DI via a generated
//    [ModuleInitializer] (no manual registration needed).
// 3. Call EndpointRegistrar (auto-generated by Controllers.Gen) to wire the routes:
EndpointRegistrar.RegisterAll(app);

// 4. Then run the app:
new WebApiBuilder()
    .RegisterScoped<UsersController>()
    .Build()
    .RunAsync("http://+:5000");
```

Der Controllers.Gen Source-Generator:
- Scannt den Ordner `Controllers/` (oder `[ApiController]`-Klassen)
- Erzeugt Endpunkt-Stubs, die Controller aus DI auflösen und registrieren

PicoWeb.Gen gibt eine Build-Diagnose (PWR001) für Rückgabetypen von
`app.MapGet/MapPost`-Handlern aus; er erzeugt keinen Quellcode. DTO-Serializer
werden von PicoJetson.Gen aus expliziten `SerializeToUtf8Bytes<T>()`-Aufrufen erzeugt.

> **Hinweis:** Hat ein Projekt selbst keine Controller und verweist es auf eine Anwendung, die bereits einen `EndpointRegistrar` exportiert (z. B. ein Integrationstest, der eine Beispiel-App referenziert), erzeugt Controllers.Gen keinen eigenen Registrar; `RegisterAll` bindet an den der referenzierten App — das vermeidet den CS0436-Doppeltypfehler und eine stillschweigende No-op-Registrierung.

> **Hinweis:** Das Controller-basierte Muster erfordert PicoJetson.Gen für die automatische DTO-Serialisierungsregistrierung.
> Beim MapXX-Muster rufen Sie `PicoJetson.JsonSerializer.SerializeToUtf8Bytes<T>()` explizit im Handler auf.
## Integrierte Middleware

### Kompression

```csharp
var compression = new CompressionMiddleware(
    CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

Unterstützt Brotli, Gzip und Deflate. Die beste Kodierung wird automatisch anhand des `Accept-Encoding`-Headers des Clients ausgewählt.

### Statische Dateien

```csharp
var staticFiles = new StaticFileMiddleware(
    "/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

Liefert Dateien aus einem Stammverzeichnis aus. Verhindert Directory Traversal. Bildet 28 Dateierweiterungen auf MIME-Typen ab.

### CORS

```csharp
app.Use(async (ctx, next, ct) =>
{
    var corsOptions = new CorsOptions
    {
        AllowedOrigins = ["https://example.com"],
        AllowedMethods = ["GET", "POST"],
        AllowCredentials = true,
    };
    var preflight = CorsHandler.HandlePreflight(ctx.Request, corsOptions);
    if (preflight is not null)
        return preflight;
    var response = await next(ctx, ct);
    // Add CORS response headers
    foreach (var header in CorsHandler.GetResponseHeaders(ctx.Request, corsOptions))
    {
        response.Headers.Add(header.Key, header.Value);
    }
    return response;
});
```

### Cookies & Multipart

```csharp
// Cookie parsing
var cookies = CookieParser.Parse(context.Request.HeaderFields);

// Set-Cookie
var setCookie = new SetCookieBuilder("session", "abc123")
    .Path("/").HttpOnly().Secure().SameSite("Strict").MaxAge(3600)
    .Build();

// Multipart form data
// Limits (MaxPartSizeBytes/MaxTotalSizeBytes) are enforced on both the in-memory
// Body path and the streaming BodyStream path; exceeding one throws InvalidDataException.
var form = await MultipartFormDataParser.ParseAsync(
    context.Request,
    new MultipartFormDataParserOptions
    {
        MaxPartSizeBytes = 64 * 1024 * 1024, // per part (default)
        MaxTotalSizeBytes = 64 * 1024 * 1024, // all parts (default)
        MaxBoundaryLength = 70, // default
    }
);
foreach (var field in form?.Fields ?? [])
    Console.WriteLine($"{field.Name} = {field.Value}");
foreach (var file in form?.Files ?? [])
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Content.Length} bytes)");
```

### Rate Limiting

Zwei Ebenen: geordnete **Policy-Tiers** (ein Token-Bucket-Store pro Tier) und die klassische Middleware mit einem einzigen Bucket.

```csharp
using var policy = RateLimitPolicy.Create("web")
    .Exempt("/api/health")                                  // OR-accumulating path whitelist (segment-boundary prefix)
    .Applies(RateLimitPath.Prefix("/api"))                  // policy scope: must pass this AND not be exempt
    .Tier("trusted",   RateLimitBudget.PerSecond(300, 30), RateLimitKeys.TokenMatch(ownerToken))
    .Tier("anonymous", RateLimitBudget.PerSecond(60, 1),   RateLimitKeys.Not(RateLimitKeys.TokenMatch(ownerToken), "instance"))
    .OnRejected(r => metrics.CountRejection(r.Policy, r.Tier, r.Reason))   // LimitReached | StoreError
    .Build();

app.UseRateLimit(policy);   // register before AuthMiddleware (the pre-auth shield); the host owns disposal
```

Tiers werden in Deklarationsreihenfolge ausgewertet; ein Tier greift, wenn sein `Key` einen Schlüssel liefert (und sein `Applies`, falls gesetzt, zutrifft), und eine Anfrage, auf die kein Tier passt, läuft unverändert durch. Schlüssel: `Identity` (authentifizierter Benutzer, sonst der Fallback), `RemoteAddress` (Peer-IP - ohne Port, IPv4-gemappte IPv6-Adressen normalisiert), `Header`, `Path`, `Constant`, `TokenMatch` (konstantzeitlicher Vergleich, dieselbe Bearer-Analyse wie auf der Auth-Seite) und `Not(inner, fallback)`. Budgets füllen sich zeitanteilig auf, `PerMinute(60, 60)` heißt also „60 Burst + etwa 1/s", nicht 60 auf einmal pro Minute.

Eine erlaubte Anfrage bekommt `X-RateLimit-Limit`/`-Remaining`/`-Reset` und einen `RateLimitState` (mit dem gewinnenden `Policy`/`Tier`) in `context.Items`; eine abgelehnte `429` + `Retry-After`. Wirft der Store, gilt das als Ablehnung (`RateLimitRejectionReason.StoreError`), außer die Policy aktiviert `FailOpen()`. Policy-Namen sind pro App eindeutig - ein Duplikat wirft beim Start.

```csharp
// Single bucket: the original middleware (one store, one key selector)
var bucket = RateLimitMiddleware.Create(new RateLimitOptions
{
    MaxTokens = 60,
    RefillRate = 1,
    RefillInterval = TimeSpan.FromSeconds(1),   // ~1 token/s, burst 60
    KeySelector = RateLimitKeys.RemoteAddress(),   // a null key shares the "anonymous" bucket
});
app.Use(bucket);
```

## Metriken

Sowohl `TcpNode` als auch `UdpNode` stellen Echtzeit-Zähler bereit:

```csharp
// TCP
var tcpMetrics = tcpNode.GetMetrics();
Console.WriteLine($"Accepted: {tcpMetrics.TotalAccepted}");
Console.WriteLine($"Active: {tcpMetrics.ActiveConnections}");
Console.WriteLine($"Sent: {tcpMetrics.TotalBytesSent}");
Console.WriteLine($"Received: {tcpMetrics.TotalBytesReceived}");

// UDP
var udpMetrics = udpNode.GetMetrics();
Console.WriteLine($"Datagrams sent: {udpMetrics.TotalDatagramsSent}");
Console.WriteLine($"Datagrams received: {udpMetrics.TotalDatagramsReceived}");
Console.WriteLine($"Dropped: {udpMetrics.TotalDatagramsDropped}");
Console.WriteLine($"Sent: {udpMetrics.TotalBytesSent}");
Console.WriteLine($"Received: {udpMetrics.TotalBytesReceived}");
```

## Projekte

| Project | Target | Description |
|---------|--------|-------------|
| **PicoNode.Abs** | net10.0 | Kern-Interfaces: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, Fehlercodes, Enums |
| **PicoNode** | net10.0 | `TcpNode` und `UdpNode` — produktionsreife asynchrone Socket-Transporte |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1, HTTP/2, WebSocket |
| **PicoJsonRpc** | net10.0 | JSON-RPC-2.0-Protokollkern: `NdjsonFramer`, `JsonRpcClient`, `ProcessLifecycle` |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, Middleware, statische Dateien, Kompression, CORS, DI |
| **PicoNode.Web.Session.Abs** | net10.0 | Session-Abstraktionen: `ISession`, `ISessionStore` |
| **PicoWeb** | net10.0 | `WebServer` — schlanker Host, der `WebApp` mit `TcpNode` verbindet |

## Beispiele

| Sample | Port | Description |
|--------|------|-------------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | Roher TCP/UDP-Echo-Server |
| `PicoNode.Samples.Http` | 7003 | HTTP-Routing mit `HttpRouter` |
| `PicoWeb.Samples` | 7004 | Vollständige Web-App mit Middleware und DI |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## Build & Tests

```bash
# Build the entire solution
dotnet build PicoNode.slnx -c Release

# Run all tests
dotnet test --solution PicoNode.slnx -c Release

# Run a specific test project
dotnet test --project tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release

# AOT publish check (publishes tests/PicoWeb.AotVerify and runs it; -RuntimeIdentifier is optional)
pwsh ./scripts/test-aot-publish.ps1 -RuntimeIdentifier win-x64
```

## Benchmarks

Microbenchmarks werden über [PicoBench](https://github.com/PicoHex/PicoBench) bereitgestellt:

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

Die Benchmarks decken HTTP-Parsing, Router-Dispatch (Treffer/Fehlschlag/405), die vollständige Pipeline und localhost-Roundtrips ab.

## Anforderungen

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET 10.0** (PicoNode.Abs — maximale Kompatibilität)
- PicoHex ecosystem (optional): PicoDI, PicoLog, PicoCfg

## Lizenz

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — geschichtetes Networking für .NET
</p>
