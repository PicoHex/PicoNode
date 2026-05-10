# PicoNode

> Une stack réseau en couches, compatible AOT native pour .NET — des sockets bruts TCP/UDP jusqu'à un framework HTTP complet.

[![NuGet](https://img.shields.io/nuget/v/PicoNode.svg)](https://www.nuget.org/packages/PicoNode)
[![License](https://img.shields.io/github/license/PicoHex/PicoNode)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

**English** | [简体中文](README.zh.md) | [繁體中文](README.zh-TW.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [日本語](README.ja.md) | [Português (Brasil)](README.pt-BR.md) | [Русский](README.ru.md)

```
┌─────────────────────────────────────────────────────────────┐
│  PicoNode: layered networking for .NET                      │
│  ✓ Raw TCP/UDP socket transports with async I/O             │
│  ✓ HTTP/1.1 + HTTP/2 + WebSocket protocols                  │
│  ✓ Web framework with middleware, routing, static files      │
│  ✓ Integrated with PicoHex ecosystem (PicoDI/PicoLog/PicoCfg)│
│  ✓ Native AOT compatible across all net10.0 layers           │
│  ✓ Minimal runtime dependencies                             │
└─────────────────────────────────────────────────────────────┘
```

## Pourquoi PicoNode ?

| Fonctionnalité | PicoNode | ASP.NET Core |
|----------------|----------|--------------|
| **Modèle de dépendances** | Zéro dépendance runtime obligatoire ; couches au choix | Référence framework `Microsoft.AspNetCore.App` |
| **Analyse des requêtes** | Streaming basé sur Span, zéro copie `System.IO.Pipelines` | Basé sur des chaînes avec adaptateur `IO.Pipelines` |
| **HTTP/2** | Décodeur HPACK intégré, contrôle au niveau des trames | Transparent via Kestrel ; accès bas niveau limité |
| **Support AOT** | ✅ Natif — toutes les bibliothèques net10.0 | ⚠️ Nécessite du trimming |
| **DI / Journalisation / Configuration** | PicoDI + PicoLog + PicoCfg (natifs PicoHex) | Microsoft.Extensions.* |
| **WebSocket** | Codec de trames RFC 6455 avec abstraction de gestionnaire de messages | Transparent via middleware |
| **Nombre de lignes** | ~15K pour toute la stack | ~1M+ pour ASP.NET Core |

> **Priorité de conception :** PicoNode privilégie l'efficacité d'allocation et la compatibilité AOT. `ValueTask` sur les delegates à chaud, la gestion des buffers basée sur ArrayPool et les delegates optionnels (pas d'allocation forcée) sont des compromis délibérés — ils maintiennent la couche de transport compacte et prévisible.

### L'Écosystème PicoHex

PicoNode fait partie de la famille PicoHex et s'intègre nativement avec :

| Bibliothèque | Objectif | NuGet |
|--------------|----------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | Injection de dépendances à la compilation sans réflexion | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | Journalisation structurée avec sécurité AOT | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | Liaison de configuration générée à la compilation | `PicoCfg.Abs` |

```
PicoNode.Abs        Interfaces cœur                          (netstandard2.0, zero deps)
    ↓
PicoNode             Transports TCP & UDP + ILogger           (net10.0)
    ↓
PicoNode.Http        HTTP/1.1 + HTTP/2 + WebSocket            (net10.0)
    ↓
PicoNode.Web         Framework Web + PicoDI ISvcContainer     (net10.0)
    ↓
PicoWeb              Serveur Web prêt à l'emploi + PicoCfg    (net10.0)
```

## Démarrage Rapide

### Installation

```bash
dotnet add package PicoNode
```

> Installer `PicoNode` inclut le transport TCP/UDP. Référencez `PicoNode.Http` ou `PicoNode.Web` pour les couches supérieures.

### Architecture des Paquets

PicoNode est distribué sous forme de paquets NuGet en couches. Choisissez exactement le niveau d'abstraction dont vous avez besoin :

| Paquet | Installer quand… | Ce que vous obtenez |
|--------|------------------|---------------------|
| **PicoWeb** | Vous voulez un serveur Web prêt à l'emploi | WebServer + WebApp + HTTP + TCP (tout transitif) |
| **PicoNode.Web** | Vous voulez le framework Web sans hébergement | WebApp, routage, middleware, fichiers statiques, DI |
| **PicoNode.Http** | Vous voulez la gestion brute du protocole HTTP | HTTP/1.1 + HTTP/2 + WebSocket, HttpRouter |
| **PicoNode** | Vous voulez les transports bruts TCP/UDP | TcpNode, UdpNode, cycle de vie des sockets, métriques |
| **PicoNode.Abs** | Vous écrivez un gestionnaire ou une extension | INode, ITcpConnectionHandler, contrats de base |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode  →  PicoNode.Abs
(hôte)      (web/DI)         (HTTP)            (transport)   (interfaces)
```

### Serveur TCP Echo

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

### Serveur HTTP (Bas Niveau)

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

### Application Web (avec l'Écosystème PicoHex)

```csharp
using System.Net;
using PicoDI.Abs;
using PicoLog.Abs;
using PicoCfg.Abs;
using PicoNode.Web;
using PicoWeb;

// Configuration
var config = await Cfg.CreateBuilder()
    .Add(new Dictionary<string, string>
    {
        ["WebApp:ServerHeader"] = "MyApp",
        ["WebApp:MaxRequestBytes"] = "16384",
    })
    .BuildAsync();

var app = new WebApp(new WebAppOptions
{
    ServerHeader = "MyApp",
    Logger = new ConsoleSink().CreateLogger("PicoNode.Web"),
    Config = config,
});

// Middleware
app.Use(async (context, next, ct) =>
{
    var response = await next(context, ct);
    return response;
});

// Routes
app.MapGet("/", static (_, _) =>
    ValueTask.FromResult(WebResults.Text(200, "Hello, World!", "OK")));

app.MapGet("/users/{id}", static (ctx, _) =>
{
    var id = ctx.RouteValues["id"];
    return ValueTask.FromResult(
        WebResults.Json(200, $$"""{"id":"{{id}}"}""", "OK"));
});

app.MapPost("/echo", static (ctx, _) =>
{
    var body = Encoding.UTF8.GetString(ctx.Request.Body.Span);
    return ValueTask.FromResult(WebResults.Text(200, body, "OK"));
});

// Hébergement avec DI
var container = new SvcContainer();
container.RegisterSingleton<IMyService, MyServiceImpl>();

await using var server = new WebServer(app, new WebServerOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
}, container);

await server.StartAsync();
Console.ReadLine();
await server.StopAsync();
```

## Configuration

PicoNode prend en charge deux modes de configuration :

### Code d'Abord (en ligne)

```csharp
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Any, 8080),
    MaxConnections = 500,
    IdleTimeout = TimeSpan.FromMinutes(5),
};
var node = new TcpNode(options);
```

### Liaison PicoCfg (sûre pour AOT, générée à la compilation)

```csharp
var config = await Cfg.CreateBuilder()
    .AddEnvironmentVariables("PICONODE_")
    .BuildAsync();

var options = CfgBind.Bind<TcpNodeOptions>(config, "TcpNode");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // required
var node = new TcpNode(options);
```

### Rechargement à l'Exécution

```csharp
// TcpNode supports runtime config reload (except Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    Config = config, // ICfgRoot for live reload
};
// Node starts a reload loop watching for config changes
```

### Options Clés

#### TcpNodeOptions

| Option | Défaut | Description |
|--------|--------|-------------|
| `Endpoint` | *(obligatoire)* | Point d'écoute local à lier |
| `ConnectionHandler` | *(obligatoire)* | `ITcpConnectionHandler` |
| `MaxConnections` | 1000 | Nombre maximum de connexions simultanées |
| `IdleTimeout` | 2 min | Durée avant fermeture des connexions inactives |
| `DrainTimeout` | 5 sec | Période de grâce lors de l'arrêt |
| `SslOptions` | `null` | Configuration TLS/SSL |
| `NoDelay` | `true` | TCP_NODELAY (Nagle désactivé) |
| `Logger` | `null` | `ILogger` PicoLog pour les diagnostics structurés |

#### UdpNodeOptions

| Option | Défaut | Description |
|--------|--------|-------------|
| `Endpoint` | *(obligatoire)* | Point d'écoute local à lier |
| `DatagramHandler` | *(obligatoire)* | `IUdpDatagramHandler` |
| `DispatchWorkerCount` | 1 | Travailleurs de datagrammes simultanés |
| `DatagramQueueCapacity` | 1024 | Profondeur de file par travailleur |
| `QueueOverflowMode` | `DropNewest` | Comportement quand les files sont pleines |
| `Logger` | `null` | `ILogger` PicoLog |

#### HttpConnectionHandlerOptions

| Option | Défaut | Description |
|--------|--------|-------------|
| `RequestHandler` | *(obligatoire)* | Délégué HttpRequestHandler |
| `ServerHeader` | `null` | Valeur pour l'en-tête `Server` |
| `MaxRequestBytes` | 8192 | Taille maximum des requêtes en octets |
| `Logger` | `null` | `ILogger` PicoLog |

## Journalisation

PicoNode utilise PicoLog pour les diagnostics structurés. Toutes les erreurs non fatales sont enregistrées avec le contexte de l'opération :

```csharp
var logger = new LoggerFactory([new ConsoleSink()])
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

**Niveaux de journalisation par code d'erreur :**
- `Error` : StartFailed, StopFailed, AcceptFailed, ReceiveFailed, SendFailed, HandlerFailed, TlsFailed, DatagramReceiveFailed, DatagramHandlerFailed
- `Warning` : SessionRejected, DatagramDropped
- `Debug` : Socket shutdown during cleanup (opérations au mieux)

## Injection de Dépendances

La couche Web de PicoNode s'intègre avec PicoDI pour la gestion des requêtes avec portée :

```csharp
var container = new SvcContainer();
container.RegisterScoped<IDatabase, SqlDatabase>();
container.RegisterSingleton<ICache, RedisCache>();

var app = new WebApp();
app.Build(container); // Injects scope middleware per request

// In your route handler:
app.MapGet("/db", async (ctx, ct) =>
{
    var db = ctx.Services!.GetService<IDatabase>();
    var data = await db.QueryAsync("...");
    return WebResults.Json(200, data);
});
```

## Middleware Intégré

### Compression

```csharp
var compression = new CompressionMiddleware(
    CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

Prend en charge Brotli, Gzip et Deflate. Sélectionne automatiquement le meilleur encodage depuis l'en-tête `Accept-Encoding` du client.

### Fichiers Statiques

```csharp
var staticFiles = new StaticFileMiddleware(
    "/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

Sert les fichiers depuis un répertoire racine. Empêche le parcours de répertoires. Mappe plus de 30 extensions de fichiers vers des types MIME.

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
        return ValueTask.FromResult(preflight);
    var response = await next(ctx, ct);
    CorsHandler.ApplyResponseHeaders(response, corsOptions);
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
var form = MultipartFormDataParser.Parse(context.Request);
foreach (var field in form?.Fields ?? [])
    Console.WriteLine($"{field.Name} = {field.Value}");
foreach (var file in form?.Files ?? [])
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Data.Length} bytes)");
```

## Métriques

`TcpNode` et `UdpNode` exposent tous deux des compteurs en temps réel :

```csharp
// TCP
var tcpMetrics = tcpNode.GetMetrics();
Console.WriteLine($"Accepted: {tcpMetrics.TotalAccepted}");
Console.WriteLine($"Active: {tcpMetrics.ActiveConnections}");
Console.WriteLine($"Sent: {tcpMetrics.TotalBytesSent}");
Console.WriteLine($"Received: {tcpMetrics.TotalBytesReceived}");

// UDP
var udpMetrics = udpNode.GetMetrics();
Console.WriteLine($"Datagrams Rx: {udpMetrics.TotalDatagramsReceived}");
Console.WriteLine($"Datagrams Tx: {udpMetrics.TotalDatagramsSent}");
Console.WriteLine($"Dropped: {udpMetrics.TotalDropped}");
```

## Projets

| Projet | Cible | Description |
|--------|-------|-------------|
| **PicoNode.Abs** | netstandard2.0 | Interfaces cœur : `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, codes d'erreur, énumérations |
| **PicoNode** | net10.0 | `TcpNode` et `UdpNode` — transports socket asynchrones de qualité production |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1, HTTP/2, WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, middleware, fichiers statiques, compression, CORS, DI |
| **PicoWeb** | net10.0 | `WebServer` — câblage hôte léger de `WebApp` vers `TcpNode` |

## Exemples

| Exemple | Port | Description |
|---------|------|-------------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | Serveur echo TCP/UDP brut |
| `PicoNode.Samples.Http` | 7003 | Routage HTTP avec `HttpRouter` |
| `PicoWeb.Samples` | 7004 | Application Web complète avec middleware et DI |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## Compilation & Tests

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

Des microbenchmarks sont fournis via [PicoBench](https://github.com/PicoHex/PicoBench) :

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

Les benchmarks couvrent l'analyse HTTP, la distribution du routeur (succès/échec/405), le pipeline complet et les allers-retours en localhost.

## Prérequis

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET Standard 2.0** (PicoNode.Abs — compatibilité maximale)
- Écosystème PicoHex (optionnel) : PicoDI, PicoLog, PicoCfg

## Licence

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — réseau en couches pour .NET
</p>
