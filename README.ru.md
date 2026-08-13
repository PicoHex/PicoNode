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

### Веб-приложение (DI-сначала + делегат)

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

### Веб-приложение (на основе контроллеров)

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

var settings = CfgBind.Bind<AppSettings>(config, "App");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // required
var node = new TcpNode(options);
```

### Перезагрузка в рантайме

```csharp
// TcpNode поддерживает перезагрузку конфигурации (кроме Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
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

PicoNode.Web требует `ISvcContainer` при создании (DI-сначала). Скоупы создаются автоматически для каждого запроса.

### Ручное разрешение DI в обработчиках

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

### Автоматическое внедрение параметров через делегат

Параметры обработчика разрешаются автоматически (требуется `using PicoNode.Web;`):
- `WebContext` → текущий контекст
- `CancellationToken` → токен отмены запроса
- Любой зарегистрированный сервис → разрешается из DI-скоупа

```csharp
app.MapGet("/users/{id}", async (WebContext ctx, IUserService svc) =>
{
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});
```

### AOT-совместимая сериализация

Генераторы исходного кода PicoJetson работают на этапе компиляции. Обработчики должны вызывать `SerializeToUtf8Bytes<T>()` непосредственно в пользовательском коде, чтобы запустить генератор:

```csharp
// ✅ Triggers PicoJetson.Gen — UserDto serializer generated
var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);

// ❌ Does NOT trigger generator (cross-assembly generic)
Results.Json<UserDto>(200, user);
```

### WebApiBuilder (удобный способ)

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

### WebApiBuilder с контроллерами (трёхэтапная регистрация эндпоинтов)

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

Генераторы исходного кода Controllers.Gen и PicoWeb.Gen:
- Сканируют папку `Controllers/` и вызовы `app.MapGet/MapPost`
- Генерируют `[PicoJsonSerializable]` для обнаруженных DTO
- Генерируют заглушки эндпоинтов, разрешающие контроллеры из DI

> **Примечание:** паттерн на основе контроллеров требует PicoJetson.Gen для автоматической регистрации сериализации DTO.
> Для паттерна MapXX явно вызывайте `PicoJetson.JsonSerializer.SerializeToUtf8Bytes<T>()` в обработчике.
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
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Content.Length bytes)");
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
// UDP counters available via internal state
// (UdpNode tracks datagrams, bytes, and drops internally)
```

## Проекты

| Project | Target | Description |
|---------|--------|-------------|
| **PicoNode.Abs** | netstandard2.0 | Основные интерфейсы: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, коды ошибок, перечисления |
| **PicoNode** | net10.0 | `TcpNode` и `UdpNode` — асинхронные сокетные транспорты production-уровня |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1, HTTP/2, WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, middleware, статические файлы, сжатие, CORS, DI |
| **PicoWeb** | net10.0 | `WebServer` — тонкий хост, подключающий `WebApp` к `TcpNode` |

## Примеры

| Sample | Port | Description |
|--------|------|-------------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | Простой TCP/UDP эхо-сервер |
| `PicoNode.Samples.Http` | 7003 | HTTP-маршрутизация с `HttpRouter` |
| `PicoWeb.Samples` | 7004 | Полноценное веб-приложение с middleware и DI |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## Сборка и тестирование

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

## Бенчмарки

Микробенчмарки предоставляются через [PicoBench](https://github.com/PicoHex/PicoBench):

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

Бенчмарки охватывают разбор HTTP, диспетчеризацию маршрутизатора (попадание/промах/405), полный конвейер и локальные round-trips.

## Требования

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET Standard 2.0** (PicoNode.Abs — максимальная совместимость)
- PicoHex ecosystem (опционально): PicoDI, PicoLog, PicoCfg

## Лицензия

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — многоуровневая сеть для .NET
</p>
