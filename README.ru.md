# PicoNode

> Многоуровневый AOT-совместимый сетевой стек для .NET — от сырых TCP/UDP сокетов до полнофункционального HTTP веб-фреймворка.

[![NuGet](https://img.shields.io/nuget/v/PicoNode.svg)](https://www.nuget.org/packages/PicoNode)
[![License](https://img.shields.io/github/license/PicoHex/PicoNode)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

**English** | [简体中文](README.zh.md) | [繁體中文](README.zh-TW.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [日本語](README.ja.md) | [Português (Brasil)](README.pt-BR.md) | **Русский**

```
┌─────────────────────────────────────────────────────────────┐
│  PicoNode: многоуровневый сетевой стек для .NET             │
│  ✓ Сырые TCP/UDP транспорты с асинхронным вводом-выводом   │
│  ✓ Протоколы HTTP/1.1 + HTTP/2 + WebSocket                  │
│  ✓ Веб-фреймворк с middleware, маршрутизацией, статикой     │
│  ✓ Интеграция с экосистемой PicoHex (PicoDI/PicoLog/PicoCfg)│
│  ✓ Нативная AOT-совместимость на всех уровнях net10.0       │
│  ✓ Минимальные зависимости времени выполнения               │
└─────────────────────────────────────────────────────────────┘
```

## Почему PicoNode?

| Возможность | PicoNode | ASP.NET Core |
|-------------|----------|-------------|
| **Модель зависимостей** | Ноль обязательных зависимостей; выбирай уровень | Ссылка на `Microsoft.AspNetCore.App` |
| **Разбор запросов** | Потоковый на основе Span, zero-copy `System.IO.Pipelines` | Строковый с адаптером `IO.Pipelines` |
| **HTTP/2** | Встроенный HPACK-декодер, управление на уровне фреймов | Прозрачно через Kestrel; ограниченный низкоуровневый доступ |
| **AOT-поддержка** | ✅ Нативно — все библиотеки net10.0 | ⚠️ Требует trimming |
| **DI / Логирование / Конфиг** | PicoDI + PicoLog + PicoCfg (родные PicoHex) | Microsoft.Extensions.* |
| **WebSocket** | Кодек фреймов RFC 6455 с абстракцией обработчика сообщений | Прозрачно через middleware |
| **Строк кода** | ~15K на весь стек | ~1M+ для ASP.NET Core |

> **Приоритет дизайна:** PicoNode ставит во главу угла эффективность выделения памяти и AOT-совместимость. `ValueTask` в горячих делегатах, управление буферами через ArrayPool и опциональные делегаты (без принудительных аллокаций) — это осознанные компромиссы, которые делают транспортный уровень компактным и предсказуемым.

### Экосистема PicoHex

PicoNode — часть семейства PicoHex и нативно интегрируется с:

| Библиотека | Назначение | NuGet |
|------------|-----------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | Компиля-time DI без рефлексии | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | Структурированное логирование с AOT-безопасностью | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | Привязка конфигурации через source generation | `PicoCfg.Abs` |

```
PicoNode.Abs        Базовые интерфейсы                       (netstandard2.0, zero deps)
    ↓
PicoNode             TCP и UDP транспорты + ILogger           (net10.0)
    ↓
PicoNode.Http        HTTP/1.1 + HTTP/2 + WebSocket            (net10.0)
    ↓
PicoNode.Web         Веб-фреймворк + PicoDI ISvcContainer     (net10.0)
    ↓
PicoWeb              Готовый к запуску веб-сервер + PicoCfg   (net10.0)
```

## Быстрый старт

### Установка

```bash
dotnet add package PicoNode
```

> Установка `PicoNode` подтягивает TCP/UDP транспорт. Добавляйте `PicoNode.Http` или `PicoNode.Web` для работы на более высоких уровнях.

### Архитектура пакетов

PicoNode поставляется в виде уровневых NuGet-пакетов. Выбирайте ровно тот уровень абстракции, который нужен:

| Пакет | Установи, когда… | Что получишь |
|-------|-----------------|-------------|
| **PicoWeb** | Нужен готовый веб-сервер | WebServer + WebApp + HTTP + TCP (всё транзитивно) |
| **PicoNode.Web** | Нужен веб-фреймворк без хостинга | WebApp, маршрутизация, middleware, статика, DI |
| **PicoNode.Http** | Нужна работа с HTTP на уровне протокола | HTTP/1.1 + HTTP/2 + WebSocket, HttpRouter |
| **PicoNode** | Нужны сырые TCP/UDP транспорты | TcpNode, UdpNode, жизненный цикл сокетов, метрики |
| **PicoNode.Abs** | Пишешь обработчик или расширение | INode, ITcpConnectionHandler, базовые контракты |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode  →  PicoNode.Abs
(хостинг)   (веб/DI)         (HTTP)            (транспорт)   (интерфейсы)
```

### TCP эхо-сервер

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

### HTTP-сервер (низкоуровневый)

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

### Веб-приложение (с экосистемой PicoHex)

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

// DI-aware hosting
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

## Конфигурация

PicoNode поддерживает два режима конфигурации:

### Сначала код (inline)

```csharp
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Any, 8080),
    MaxConnections = 500,
    IdleTimeout = TimeSpan.FromMinutes(5),
};
var node = new TcpNode(options);
```

### Привязка через PicoCfg (AOT-безопасно, source-генерация)

```csharp
var config = await Cfg.CreateBuilder()
    .AddEnvironmentVariables("PICONODE_")
    .BuildAsync();

var options = CfgBind.Bind<TcpNodeOptions>(config, "TcpNode");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // required
var node = new TcpNode(options);
```

### Перезагрузка в рантайме

```csharp
// TcpNode поддерживает перезагрузку конфигурации (кроме Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    Config = config, // ICfgRoot для live reload
};
// Узел запускает цикл перезагрузки, отслеживающий изменения конфига
```

### Ключевые параметры

#### TcpNodeOptions

| Параметр | По умолчанию | Описание |
|----------|-------------|----------|
| `Endpoint` | *(обязательно)* | Локальная конечная точка для привязки |
| `ConnectionHandler` | *(обязательно)* | `ITcpConnectionHandler` |
| `MaxConnections` | 1000 | Максимум одновременных подключений |
| `IdleTimeout` | 2 мин | Время до закрытия неактивного соединения |
| `DrainTimeout` | 5 сек | Время ожидания при завершении работы |
| `SslOptions` | `null` | Настройки TLS/SSL |
| `NoDelay` | `true` | TCP_NODELAY (отключение алгоритма Нейгла) |
| `Logger` | `null` | PicoLog `ILogger` для структурированной диагностики |

#### UdpNodeOptions

| Параметр | По умолчанию | Описание |
|----------|-------------|----------|
| `Endpoint` | *(обязательно)* | Локальная конечная точка для привязки |
| `DatagramHandler` | *(обязательно)* | `IUdpDatagramHandler` |
| `DispatchWorkerCount` | 1 | Количество параллельных обработчиков датаграмм |
| `DatagramQueueCapacity` | 1024 | Глубина очереди на одного рабочего |
| `QueueOverflowMode` | `DropNewest` | Поведение при переполнении очереди |
| `Logger` | `null` | PicoLog `ILogger` |

#### HttpConnectionHandlerOptions

| Параметр | По умолчанию | Описание |
|----------|-------------|----------|
| `RequestHandler` | *(обязательно)* | Делегат HttpRequestHandler |
| `ServerHeader` | `null` | Значение заголовка `Server` |
| `MaxRequestBytes` | 8192 | Максимальный размер запроса в байтах |
| `Logger` | `null` | PicoLog `ILogger` |

## Логирование

PicoNode использует PicoLog для структурированной диагностики. Все нефатальные ошибки логируются с контекстом операции:

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

**Уровни логирования по кодам ошибок:**
- `Error`: StartFailed, StopFailed, AcceptFailed, ReceiveFailed, SendFailed, HandlerFailed, TlsFailed, DatagramReceiveFailed, DatagramHandlerFailed
- `Warning`: SessionRejected, DatagramDropped
- `Debug`: Завершение сокета при очистке (best-effort операции)

## Внедрение зависимостей

Веб-уровень PicoNode интегрируется с PicoDI для обработки запросов в скоупе:

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

## Встроенный Middleware

### Сжатие

```csharp
var compression = new CompressionMiddleware(
    CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

Поддерживает Brotli, Gzip и Deflate. Автоматически выбирает наилучшее кодирование из заголовка `Accept-Encoding` клиента.

### Статические файлы

```csharp
var staticFiles = new StaticFileMiddleware(
    "/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

Раздаёт файлы из корневой директории. Предотвращает directory traversal. Сопоставляет 30+ расширений файлов с MIME-типами.

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

### Cookies и Multipart

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

## Метрики

`TcpNode` и `UdpNode` предоставляют счётчики в реальном времени:

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

## Проекты

| Проект | Target | Описание |
|--------|--------|----------|
| **PicoNode.Abs** | netstandard2.0 | Базовые интерфейсы: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, коды ошибок, перечисления |
| **PicoNode** | net10.0 | `TcpNode` и `UdpNode` — production-ready асинхронные сокет-транспорты |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1, HTTP/2, WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, middleware, статика, сжатие, CORS, DI |
| **PicoWeb** | net10.0 | `WebServer` — тонкая обвязка, соединяющая `WebApp` с `TcpNode` |

## Примеры

| Пример | Порт | Описание |
|--------|------|----------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | Сырой TCP/UDP эхо-сервер |
| `PicoNode.Samples.Http` | 7003 | HTTP-маршрутизация через `HttpRouter` |
| `PicoWeb.Samples` | 7004 | Полноценное веб-приложение с middleware и DI |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## Сборка и тестирование

```bash
# Собрать всё решение
dotnet build PicoNode.slnx -c Release

# Запустить все тесты
dotnet test --solution PicoNode.slnx -c Release

# Запустить конкретный тестовый проект
dotnet test --project tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release

# Проверка AOT-публикации
dotnet publish src/PicoWeb/PicoWeb.csproj -c Release -r win-x64 -p:PublishAot=true
```

## Бенчмарки

Микробенчмарки предоставляются через [PicoBench](https://github.com/PicoHex/PicoBench):

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

Бенчмарки покрывают разбор HTTP, диспетчеризацию маршрутизатора (hit/miss/405), полный конвейер и round-trip через localhost.

## Требования

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET Standard 2.0** (PicoNode.Abs — максимальная совместимость)
- Экосистема PicoHex (опционально): PicoDI, PicoLog, PicoCfg

## Лицензия

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — многоуровневый сетевой стек для .NET
</p>
