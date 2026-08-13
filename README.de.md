# PicoNode

> Ein geschichteter, AOT-nativer Networking-Stack für .NET — von rohen TCP/UDP-Sockets bis zu einem vollwertigen HTTP-Webframework.

[![NuGet](https://img.shields.io/nuget/v/PicoNode.svg)](https://www.nuget.org/packages/PicoNode)
[![License](https://img.shields.io/github/license/PicoHex/PicoNode)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

[English](README.md) | [简体中文](README.zh.md) | [繁體中文](README.zh-TW.md) | **Deutsch** | [Español](README.es.md) | [Français](README.fr.md) | [日本語](README.ja.md) | [Português (Brasil)](README.pt-BR.md) | [Русский](README.ru.md)

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
| **Abhängigkeitsmodell** | Keine erforderlichen Laufzeit-Dependencies; Ebenen frei wählbar | `Microsoft.AspNetCore.App`-Frameworkreferenz |
| **Request-Parsing** | Span-basiertes Streaming, Zero-Copy mit `System.IO.Pipelines` | String-basiert mit `IO.Pipelines`-Adapter |
| **HTTP/2** | Integrierter HPACK-Decoder, Frame-Level-Steuerung | Transparent via Kestrel; eingeschränkter Low-Level-Zugriff |
| **AOT-Support** | ✅ Nativ — alle net10.0-Bibliotheken | ⚠️ Erfordert Trimming |
| **DI / Logging / Config** | PicoDI + PicoLog + PicoCfg (PicoHex-nativ) | Microsoft.Extensions.* |
| **WebSocket** | RFC-6455-Frame-Codec mit Message-Handler-Abstraktion | Transparent via Middleware |
| **Codezeilen** | ~15K für den gesamten Stack | ~1M+ für ASP.NET Core |

> **Entwurfspriorität:** PicoNode legt Wert auf Allokationseffizienz und AOT-Kompatibilität. `ValueTask` auf Hot-Path-Delegates, ArrayPool-basiertes Puffermanagement und optionale Delegates (keine erzwungenen Allokationen) sind bewusste Kompromisse. Sie halten die Transportschicht kompakt und vorhersagbar.

### Das PicoHex-Ökosystem

PicoNode ist Teil der PicoHex-Familie und lässt sich nativ mit folgenden Bibliotheken integrieren:

| Bibliothek | Zweck | NuGet |
|---------|---------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | Reflexionsfreie Compilezeit-DI | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | Strukturiertes Logging mit AOT-Sicherheit | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | Quellgenerierte Konfigurationsbindung | `PicoCfg.Abs` |

```
PicoNode.Abs        Kernschnittstellen                      (netstandard2.0, keine Dependencies)
    ↓
PicoNode             TCP- & UDP-Transporte + ILogger         (net10.0)
    ↓
PicoNode.Http        HTTP/1.1 + HTTP/2 + WebSocket           (net10.0)
    ↓
PicoNode.Web         Webframework + PicoDI ISvcContainer     (net10.0)
    ↓
PicoWeb              Startbereiter Webserver + PicoCfg       (net10.0)
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
| **PicoNode.Http** | Du rohes HTTP-Protokoll-Handling brauchst | HTTP/1.1 + HTTP/2 + WebSocket, HttpRouter |
| **PicoNode** | Du rohe TCP/UDP-Transporte brauchst | TcpNode, UdpNode, Socket-Lebenszyklus, Metriken |
| **PicoNode.Abs** | Du einen Handler oder eine Erweiterung schreibst | INode, ITcpConnectionHandler, Kernverträge |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode  →  PicoNode.Abs
(Host)      (Web/DI)         (HTTP)            (Transport)   (Schnittstellen)
```

### TCP-Echo-Server

```csharp
using System.Net;
using PicoNode;
using PicoNode.Abs;

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
    ConnectionHandler = new EchoHandler(),
});

await node.StartAsync();
Console.ReadLine();
await node.DisposeAsync();

sealed class EchoHandler : ITcpConnectionHandler
{
    public Task OnConnectedAsync(ITcpConnectionContext c, CancellationToken ct)
        => Task.CompletedTask;
    public Task OnClosedAsync(ITcpConnectionContext c, TcpCloseReason r,
        Exception? e, CancellationToken ct) => Task.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken ct)
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
                HttpRoute.MapGet("/", static (_, _) =>
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

api.MapGet("/", (WebContext ctx) =>
    Results.Text(200, "Hello, World!"));

api.MapGet("/users/{id}", async (WebContext ctx, IUserService svc) =>
{
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});

api.MapPost("/echo", async (WebContext ctx) =>
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

// Controllers.Gen auto-generates endpoint stubs + [PicoJsonSerializable]
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
    .AddEnvironmentVariables("PICONODE_")
    .BuildAsync();

var settings = CfgBind.Bind<AppSettings>(config, "App");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // erforderlich
var node = new TcpNode(options);
```

### Laufzeit-Neuladen

```csharp
// TcpNode unterstützt das Neuladen der Konfiguration zur Laufzeit (außer Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
};
// Der Knoten startet eine Reload-Schleife, die auf Konfigurationsänderungen achtet
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
| `Logger` | `null` | PicoLog `ILogger` |

## Logging

PicoNode verwendet PicoLog für strukturierte Diagnose. Alle nicht-fatalen Fehler werden mit Operationskontext protokolliert:

```csharp

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
    ConnectionHandler = handler,
    Logger = logger, // Alle Transportfehler werden hier protokolliert
});

// Log-Ausgabe:
// [Error] Operation tcp.accept failed: AcceptFailed - System.Net.Sockets.SocketException
// [Warning] Operation tcp.reject.limit failed: SessionRejected
// [Debug] Socket shutdown during TLS teardown failed
```

**Log-Level nach Fehlercode:**
- `Error`: StartFailed, StopFailed, AcceptFailed, ReceiveFailed, SendFailed, HandlerFailed, TlsFailed, DatagramReceiveFailed, DatagramHandlerFailed
- `Warning`: SessionRejected, DatagramDropped
- `Debug`: Socket-Shutdown während der Bereinigung (Best-Effort-Operationen)

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
app.MapGet("/db", async (WebContext ctx) =>
{
    var db = ctx.Services.GetService<IDatabase>() as IDatabase;
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
app.MapGet("/users/{id}", async (WebContext ctx, IUserService svc) =>
{
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

// ❌ Does NOT trigger generator (cross-assembly generic)
Results.Json<UserDto>(200, user);
```

### WebApiBuilder (Komfort)

```csharp
using PicoNode.Web;
using PicoWeb;

var api = new WebApiBuilder()
    .RegisterScoped<IUserService, UserService>()
    .ConfigureJson(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    .Build();

api.MapGet("/api/users/{id}", async (WebContext ctx, IUserService svc) =>
{
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

// 2. Register controller in DI
builder.RegisterScoped<UsersController>();

// 3. Call EndpointRegistrar (auto-generated by Controllers.Gen)
EndpointRegistrar.RegisterAll(app);

// 4. Or use WebApiBuilder (calls it automatically)
new WebApiBuilder()
    .RegisterScoped<UsersController>()
    .Build()
    .RunAsync("http://+:5000");
```

Die Source-Generatoren Controllers.Gen und PicoWeb.Gen:
- Scannen den Ordner `Controllers/` und `app.MapGet/MapPost`-Aufrufe
- Erzeugen `[PicoJsonSerializable]` für gefundene DTOs
- Erzeugen Endpunkt-Stubs, die Controller aus DI auflösen

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

Liefert Dateien aus einem Stammverzeichnis aus. Verhindert Directory Traversal. Bildet über 30 Dateierweiterungen auf MIME-Typen ab.

### CORS

```csharp
app.Use((ctx, next, ct) =>
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
// Cookie-Parsing
var cookies = CookieParser.Parse(context.Request.HeaderFields);

// Set-Cookie
var setCookie = new SetCookieBuilder("session", "abc123")
    .Path("/").HttpOnly().Secure().SameSite("Strict").MaxAge(3600)
    .Build();

// Multipart-Formulardaten
var form = MultipartFormDataParser.Parse(context.Request);
foreach (var field in form?.Fields ?? [])
    Console.WriteLine($"{field.Name} = {field.Value}");
foreach (var file in form?.Files ?? [])
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Content.Length bytes)");
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
// UDP counters available via internal state
// (UdpNode tracks datagrams, bytes, and drops internally)
```

## Projekte

| Project | Target | Description |
|---------|--------|-------------|
| **PicoNode.Abs** | netstandard2.0 | Kern-Interfaces: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, Fehlercodes, Enums |
| **PicoNode** | net10.0 | `TcpNode` und `UdpNode` — produktionsreife asynchrone Socket-Transporte |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1, HTTP/2, WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, Middleware, statische Dateien, Kompression, CORS, DI |
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

# AOT publish check
dotnet publish src/PicoWeb/PicoWeb.csproj -c Release -r win-x64 -p:PublishAot=true
```

## Benchmarks

Microbenchmarks werden über [PicoBench](https://github.com/PicoHex/PicoBench) bereitgestellt:

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

Die Benchmarks decken HTTP-Parsing, Router-Dispatch (Treffer/Fehlschlag/405), die vollständige Pipeline und localhost-Roundtrips ab.

## Anforderungen

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET Standard 2.0** (PicoNode.Abs — maximale Kompatibilität)
- PicoHex ecosystem (optional): PicoDI, PicoLog, PicoCfg

## Lizenz

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — geschichtetes Networking für .NET
</p>
