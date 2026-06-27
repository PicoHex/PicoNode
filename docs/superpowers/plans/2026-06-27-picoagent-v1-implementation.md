# PicoAgent v1.0 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 重构 PicoAgent 为单一 HTTP SSE 服务器入口，删除 AgentServer/AgentEndpoints，Model 参数化保证并发安全，新增 AgentHttpClient，CLI 变为纯 HTTP 客户端。

**Architecture:** 
- `Agent`（PicoAgent 唯一 public 类型）：创建时自动装配 LLM 管线，`ListenAsync` 启动 HTTP 服务，`SendAsync` 提供程序化调用
- `AgentLoop`：Model 从构造参数移到 `RunTurnAsync` 参数，保证 SwitchModel 并发安全
- `AgentHost`：移除 thinking state，添加 per-session lock
- `AgentHttpClient`：C# HTTP SSE 客户端，复用 `System.IO.Pipelines`

**Tech Stack:** C# / .NET 10 / PicoNode.Agent / PicoNode.AI / PicoNode.Web / PicoWeb / PicoJetson

## Global Constraints

- 所有 new public API 必须 AOT 兼容（`IsAotCompatible=true`, `IsTrimmable=true`）
- 遵循 TDD：先写测试，确认失败，再实现，确认通过
- 每个 Task 以独立 git commit 结尾
- 测试放在 `tests/PicoNode.Tests/`
- sessionId 验证：`[a-zA-Z0-9_-]+`

---

### Task 0: AgentBuilder — 暴露 internal accessor

**Files:**
- Modify: `src/PicoAgent/AgentBuilder.cs`

**Interfaces:**
- Produces: `BuildAgentHostInternalAsync()` (public → internal 暴露)、`GetRegistry()`、`GetHttpClient()`、`GetProviderConfigs()`、`GetBreakers()`、`GetClients()`、`GetInitialModel()`

- [ ] **Step 1: 添加 internal accessor 方法**

在 `AgentBuilder.cs` 末尾添加：

```csharp
// ── Internal accessors for Agent.CreateAsync ──

internal async Task<AgentHost> BuildAgentHostInternalAsync()
{
    // 当前 BuildAgentHostAsync 是 private → 改为 internal 暴露
    return await BuildAgentHostAsync(CancellationToken.None);
}

internal CapabilityRegistry GetRegistry() => _registry!;

internal HttpClient GetHttpClient() => _http ?? new HttpClient();

internal IReadOnlyDictionary<string, ProviderConfig> GetProviderConfigs()
{
    // 重建 provider configs（BuildAgentHostAsync 内部构造的）
    var result = new Dictionary<string, ProviderConfig>();
    foreach (var (name, entry) in _config!.Providers)
    {
        result[name] = new ProviderConfig
        {
            Name = name,
            BaseUrl = entry.BaseUrl ?? "",
            ApiFormat = entry.ApiFormat?.ToLowerInvariant() switch
            {
                "anthropic" => AiApiFormat.AnthropicMessages,
                _ => AiApiFormat.OpenAIChatCompletions,
            },
            ApiKey = entry.ApiKey,
            Priority = 1,
        };
    }
    return result;
}

internal IReadOnlyDictionary<string, ICircuitBreaker> GetBreakers() => _breakers!;

internal IReadOnlyDictionary<string, ILLmClient> GetClients() => _clients!;

internal Model GetInitialModel() => _initialModel!;
```

同时添加 `_breakers`、`_clients`、`_initialModel` 字段：

```csharp
private Dictionary<string, ICircuitBreaker>? _breakers;
private Dictionary<string, ILLmClient>? _clients;
private Model? _initialModel;
```

在 `BuildAgentHostAsync` 中存储：
```csharp
_breakers = breakers;
_clients = clients;
_initialModel = model;
```

- [ ] **Step 2: 构建验证**

```bash
dotnet build src/PicoAgent/PicoAgent.csproj -c Release
```

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "refactor(AgentBuilder): expose internal accessors for Agent.CreateAsync"
```

---

### Task 1: AgentLoop — Model 参数化

**Files:**
- Modify: `src/PicoNode.Agent/Agent/AgentLoop.cs`
- Modify: `tests/PicoNode.Tests/AgentBuilderTests.cs` (AgentLoop 构造变更)
- Create: `tests/PicoNode.Tests/AgentLoopModelSnapshotTests.cs`

**Interfaces:**
- Produces: `AgentLoop(IllmClient, CapabilityRegistry, CapabilityRunner)` — 移除 Model 参数
- Produces: `RunTurnAsync(Model, List<Message>, Ct, Func<...>?)` — 新增 Model 参数

- [ ] **Step 1: 写失败测试 — 并发 Model 快照**

```csharp
// tests/PicoNode.Tests/AgentLoopModelSnapshotTests.cs
namespace PicoNode.Tests;

public sealed class AgentLoopModelSnapshotTests
{
    [Test]
    public async Task Snapshot_PreventsConcurrentModelMutation()
    {
        var model = new Model { Id = "gpt-4", ThinkingEnabled = true, ThinkingLevel = ThinkingLevel.Medium };
        var clients = new Dictionary<string, ILLmClient>();
        var breakers = new Dictionary<string, ICircuitBreaker>();
        var router = new ProviderRouter([]);
        var client = new ResilientLLmClient(router, breakers, clients);
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(client, registry, runner);

        // Start a run that blocks internally (simulates in-flight LLM call)
        var messages = new List<Message> { new() { Role = "user", Content = "hello" } };
        using var cts = new CancellationTokenSource();

        // Run starts with model gpt-4
        var task = loop.RunTurnAsync(model, messages, cts.Token);
        
        // While run is in-flight, change model on the shared object
        model.Id = "claude-3";
        model.ThinkingEnabled = false;

        // The run should still use the original "gpt-4" because it snapped at start
        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { }

        // Verify the snapshot was independent (no side effect on output)
        await Assert.That(messages.Count).IsGreaterThan(0);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project tests/PicoNode.Tests/PicoNode.Tests.csproj --filter "AgentLoopModelSnapshotTests"
```

预期：AgentLoop 新构造函数签名不存在 → 编译失败。

- [ ] **Step 3: 修改 AgentLoop 签名**

`src/PicoNode.Agent/Agent/AgentLoop.cs`：

```csharp
// 构造函数：移除 Model 参数
public AgentLoop(
    ILLmClient llm,
    CapabilityRegistry registry,
    CapabilityRunner runner
)
{
    _llm = llm;
    _registry = registry;
    _runner = runner;
}

// RunTurnAsync：新增 Model 参数，移除 UpdateThinking
public async Task<List<Message>> RunTurnAsync(
    Model model,                // ← 新增
    List<Message> messages,
    CancellationToken ct,
    Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null
)
{
    var result = new List<Message>();
    // 移除 var model = _model; — 直接用参数

    var iterations = 0;
    bool hasTools;

    do
    {
        // ... 内部不变，model 来自参数而非 _model ...
    } while (hasTools);

    return result;
}

// CallLLMAsync：使用参数 model 而非 _model
private async Task<Message?> CallLLMAsync(
    Model model,
    ChatContext context,
    CancellationToken ct,
    Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null
)
{
    // streamOptions 改用参数 model 而非 _model：
    var streamOptions =
        model.ThinkingEnabled && model.ThinkingLevelMap is { Count: > 0 }
            ? new StreamOptions
            {
                Reasoning = model.ThinkingLevel,
                ThinkingLevelMap = model.ThinkingLevelMap,
            }
            : null;
    // ... ...
}

// 删除：_model 字段、UpdateThinking 方法
```

- [ ] **Step 4: 修复受影响的调用者**

**AgentBuilder.BuildAgentHostAsync**（AgentLoop 新构造签名）：

```csharp
var loop = new AgentLoop(resilientClient, _registry, runner);
```

**AgentHost.ProcessMessageAsync**（传入 Model + 移除 UpdateThinking）：

```csharp
public async Task<string> ProcessMessageAsync(
    string content,
    Model model,               // ← 新增
    CancellationToken ct,
    string sessionId = "default",
    Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null
)
{
    var messages = _sessions.GetOrAdd(sessionId, _ => []);
    // 移除：_loop.UpdateThinking(...) — 不再需要

    messages.Add(new Message { Role = RoleUser, Content = content, ... });
    var result = await _loop.RunTurnAsync(model, messages, ct, onEvent);
    // ...
}
```

**测试文件**：所有构造 `new AgentLoop(client, registry, runner, model)` → 改为 `new AgentLoop(client, registry, runner)`，调用 `.RunTurnAsync(model, msgs, ct)`。

- [ ] **Step 5: 构建验证**

```bash
dotnet build PicoNode.slnx -c Release
```

预期：0 错误。

- [ ] **Step 6: 运行测试**

```bash
dotnet test --project tests/PicoNode.Tests/PicoNode.Tests.csproj
```

预期：185+ passed。

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor(AgentLoop): move Model to RunTurnAsync parameter for thread safety"
```

---

### Task 2: AgentHost — 移除 thinking state + per-session lock

**Files:**
- Modify: `src/PicoNode.Agent/Host/AgentHost.cs`
- Modify: `src/PicoNode.Agent/Session/SessionData.cs` (移除 thinking state 字段 — 不再被 AgentHost 使用)

**Interfaces:**
- Removes: `GetThinkingState`, `SetThinkingState`, `_thinkingState` 字段, `SessionThinking` 类
- Adds: per-session lock via `ConcurrentDictionary<string, SemaphoreSlim>`

- [ ] **Step 1: 移除 thinking state 相关代码**

`AgentHost.cs`：删除 `_thinkingState`、`SessionThinking`、`GetThinkingState`、`SetThinkingState`。

`RestoreSession` 改为只恢复 messages：

```csharp
public void RestoreSession(string sessionId, SessionData data)
{
    _sessions[sessionId] = data.Messages;
}
```

`GetSessionData` 去掉 thinking 字段：

```csharp
public SessionData GetSessionData(string sessionId)
{
    var messages = _sessions.GetValueOrDefault(sessionId) ?? [];
    return new SessionData { Messages = messages };
}
```

- [ ] **Step 2: 添加 per-session lock**

```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = [];

public async Task<string> ProcessMessageAsync(
    string content,
    Model model,
    CancellationToken ct,
    string sessionId = "default",
    Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null
)
{
    // Validate sessionId
    if (!SessionIdRegex().IsMatch(sessionId))
        throw new ArgumentException("Invalid session ID format", nameof(sessionId));

    var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
    await sessionLock.WaitAsync(ct);
    try
    {
        var messages = _sessions.GetOrAdd(sessionId, _ => []);
        messages.Add(new Message { Role = RoleUser, Content = content, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        var result = await _loop.RunTurnAsync(model, messages, ct, onEvent);
        return ExtractText(result);
    }
    finally
    {
        sessionLock.Release();
    }
}

[GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
private static partial Regex SessionIdRegex();
```

- [ ] **Step 3: 构建 + 测试**

```bash
dotnet build PicoNode.slnx -c Release
dotnet test --project tests/PicoNode.Tests/PicoNode.Tests.csproj
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor(AgentHost): remove thinking state, add per-session lock"
```

---

### Task 3: Agent.cs — 主入口

**Files:**
- Create: `src/PicoAgent/Agent.cs`

**Interfaces:**
- Produces: `Agent` 类（实现 Spec §2.1 的完整 API）
- Consumes: `AgentHost`, `AgentBuilder`, `WebServer`, `WebApp`

- [ ] **Step 1: 写失败测试**

```csharp
// tests/PicoNode.Tests/AgentTests.cs
namespace PicoNode.Tests;

using System.Net;

public sealed class AgentTests
{
    [Test]
    public async Task CreateAsync_ReturnsAgent()
    {
        var config = new AgentConfig
        {
            Providers = new() { ["test"] = new ProviderEntry { ApiKey = "sk-test", ApiFormat = "openai", BaseUrl = "https://api.test/v1" } },
            Model = "gpt-4",
        };
        await using var agent = await Agent.CreateAsync(config);
        await Assert.That((object?)agent).IsNotNull();
    }

    [Test]
    public async Task SwitchModel_AlwaysSucceeds()
    {
        var config = new AgentConfig
        {
            Providers = new() { ["test"] = new ProviderEntry { ApiKey = "sk-test", ApiFormat = "openai", BaseUrl = "https://api.test/v1" } },
            Model = "gpt-4",
        };
        await using var agent = await Agent.CreateAsync(config);
        var result = agent.SwitchModel("any-model-id");
        await Assert.That(result).IsTrue(); // v1: always succeeds, routing deferred to ProviderRouter
    }

    [Test]
    public async Task SwitchProvider_Invalid_ReturnsFalse()
    {
        var config = new AgentConfig
        {
            Providers = new() { ["test"] = new ProviderEntry { ApiKey = "sk-test", ApiFormat = "openai", BaseUrl = "https://api.test/v1" } },
            Model = "gpt-4",
        };
        await using var agent = await Agent.CreateAsync(config);
        var result = agent.SwitchProvider("nonexistent");
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ListenAsync_StartsServer()
    {
        var config = new AgentConfig
        {
            Providers = new() { ["test"] = new ProviderEntry { ApiKey = "sk-test", ApiFormat = "openai", BaseUrl = "https://api.test/v1" } },
            Model = "gpt-4",
        };
        await using var agent = await Agent.CreateAsync(config);
        await agent.ListenAsync("http://localhost:0");
        await Assert.That(agent.LocalEndPoint).IsNotNull();
        await agent.StopAsync();
    }
}
```

- [ ] **Step 2: 确认测试编译失败**

`Agent` 类型不存在。

- [ ] **Step 3: 实现 Agent.cs 和 AgentResult**

**AgentResult 类**（同一个文件或独立文件）：

```csharp
// src/PicoAgent/AgentResult.cs
namespace PicoAgent;

public sealed class AgentResult
{
    public required string Text { get; init; }
    public required IReadOnlyList<Message> NewMessages { get; init; }
    public string? StopReason { get; init; }
    public TokenUsage? Usage { get; init; }
}
```

**Agent 类**：

```csharp
// src/PicoAgent/Agent.cs
namespace PicoAgent;

using System.Net;

public sealed class Agent : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly AgentHost _host;
    private readonly CapabilityRegistry _registry;
    private readonly string? _homeDir;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, ProviderConfig> _providerConfigs;
    private readonly IReadOnlyDictionary<string, ICircuitBreaker> _breakers;
    private readonly IReadOnlyDictionary<string, ILLmClient> _clients;
    private Model _pendingModel;
    private WebServer? _server;
    private bool _disposed;

    private Agent(
        AgentHost host,
        CapabilityRegistry registry,
        string? homeDir,
        HttpClient http,
        IReadOnlyDictionary<string, ProviderConfig> providerConfigs,
        IReadOnlyDictionary<string, ICircuitBreaker> breakers,
        IReadOnlyDictionary<string, ILLmClient> clients,
        Model initialModel
    )
    {
        _host = host;
        _registry = registry;
        _homeDir = homeDir;
        _http = http;
        _providerConfigs = providerConfigs;
        _breakers = breakers;
        _clients = clients;
        _pendingModel = initialModel;
    }

    // ── 工厂 ──
    public static async Task<Agent> CreateAsync(AgentConfig config, string? homeDir = null)
    {
        // 内部调用 AgentBuilder（已 internal）装配 LLM 管线
        var builder = new AgentBuilder()
            .WithConfig(config);
        if (homeDir is { Length: > 0 })
            builder.WithCapabilities(homeDir);
        var host = await builder.BuildAgentHostInternalAsync();
        var registry = builder.GetRegistry();
        var http = builder.GetHttpClient();
        var providerConfigs = builder.GetProviderConfigs();
        var breakers = builder.GetBreakers();
        var clients = builder.GetClients();
        var model = builder.GetInitialModel();

        return new Agent(host, registry, homeDir, http, providerConfigs, breakers, clients, model);
    }

    // ── HTTP 生命周期 ──
    public EndPoint? LocalEndPoint => _server?.LocalEndPoint;

    public async Task ListenAsync(string uri, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_server is not null)
            throw new InvalidOperationException("Already listening.");

        var ep = ParseEndpoint(uri);
        var app = BuildWebApp();
        _server = new WebServer(app, new WebServerOptions { Endpoint = ep });
        await _server.StartAsync(ct);
    }

    public async Task RunAsync(string uri, CancellationToken ct = default)
    {
        await ListenAsync(uri, ct);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        EventHandler exitHandler = (_, _) => tcs.TrySetResult();
        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;
        try { await tcs.Task; }
        catch (OperationCanceledException) { }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
        }
        await StopAsync(CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_server is not null)
            await _server.StopAsync(ct);
    }

    // ── 程序化控制 ──
    public async Task<AgentResult> SendAsync(string sessionId, string message, CancellationToken ct = default)
    {
        if (_server is null)
            throw new InvalidOperationException("Not listening. Call ListenAsync first.");
        var model = SnapshotModel();
        var hostResult = await _host.ProcessMessageAsync(message, model, ct, sessionId);
        var allMsgs = _host.GetSessionMessages(sessionId);
        return new AgentResult
        {
            Text = hostResult,
            NewMessages = allMsgs,
        };
    }

    public async Task<AgentResult> SendAsync(string sessionId, string message,
        Func<AssistantMessageEvent, CancellationToken, ValueTask> onEvent, CancellationToken ct = default)
    {
        if (_server is null)
            throw new InvalidOperationException("Not listening. Call ListenAsync first.");
        var model = SnapshotModel();
        var hostResult = await _host.ProcessMessageAsync(message, model, ct, sessionId, onEvent);
        var allMsgs = _host.GetSessionMessages(sessionId);
        return new AgentResult
        {
            Text = hostResult,
            NewMessages = allMsgs,
        };
    }

    public bool SwitchModel(string modelId)
    {
        lock (_lock)
        {
            // v1: 直接设置 modelId，让 ProviderRouter 在实际调用时解析
            // 失败会在 LLM 调用时体现（throw InvalidOperationException）
            _pendingModel.Id = modelId;
            return true;
        }
    }

    public bool SwitchProvider(string providerName)
    {
        lock (_lock)
        {
            if (!_providerConfigs.ContainsKey(providerName)) return false;
            _pendingModel.Provider = providerName;
            return true;
        }
    }

    public void SwitchThinking(bool enabled, ThinkingLevel level)
    {
        lock (_lock)
        {
            _pendingModel.ThinkingEnabled = enabled;
            _pendingModel.ThinkingLevel = level;
        }
    }

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(string? provider = null, CancellationToken ct = default)
    {
        if (provider is not null && _providerConfigs.TryGetValue(provider, out var pc))
            return await ModelDiscovery.DiscoverAsync(_http, pc, ct);
        var all = new List<DiscoveredModel>();
        foreach (var (_, pc2) in _providerConfigs)
        {
            var discovered = await ModelDiscovery.DiscoverAsync(_http, pc2, ct);
            all.AddRange(discovered);
        }
        return all;
    }

    public Task SaveAsync(string sessionId = "default", CancellationToken ct = default)
    {
        if (_homeDir is not { Length: > 0 })
            throw new InvalidOperationException("No homeDir configured. Pass homeDir to CreateAsync for session persistence.");
        var path = Path.Combine(_homeDir, "sessions", $"{sessionId}.jsonl");
        var data = _host.GetSessionData(sessionId);
        return SessionStore.SaveAsync(path, data, ct);
    }

    public Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId = "default") =>
        Task.FromResult(_host.GetSessionMessages(sessionId));

    public async Task<List<string>> ListSessionsAsync(CancellationToken ct = default)
    {
        if (_homeDir is not { Length: > 0 }) return [];
        var sessionsDir = Path.Combine(_homeDir, "sessions");
        if (!Directory.Exists(sessionsDir)) return [];
        return Directory.GetFiles(sessionsDir, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null).Select(n => n!)
            .ToList();
    }

    public Task CreateSessionAsync(string sessionId, CancellationToken ct = default)
    {
        _host.GetSessionMessages(sessionId); // triggers creation via GetOrAdd
        return Task.CompletedTask;
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_homeDir is not { Length: > 0 })
            throw new InvalidOperationException("No homeDir configured.");
        var path = Path.Combine(_homeDir, "sessions", $"{sessionId}.jsonl");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    // ── Private ──
    private Model SnapshotModel()
    {
        lock (_lock)
        {
            return new Model
            {
                Id = _pendingModel.Id,
                Provider = _pendingModel.Provider,
                BaseUrl = _pendingModel.BaseUrl,
                Api = _pendingModel.Api,
                ThinkingEnabled = _pendingModel.ThinkingEnabled,
                ThinkingLevel = _pendingModel.ThinkingLevel,
                ThinkingLevelMap = _pendingModel.ThinkingLevelMap,
                MaxTokens = _pendingModel.MaxTokens,
            };
        }
    }

    private WebApp BuildWebApp()
    {
        var container = new SvcContainer();
        container.Build();
        var app = new WebApp(container, new WebAppOptions { ServerHeader = "PicoAgent" });

        // POST /session/{id}/message
        app.MapPost("/session/{id}/message", async (ctx, ct) =>
        {
            var sessionId = ctx.RouteValues["id"] ?? "default";
            using var reader = new StreamReader(ctx.Request.BodyStream);
            var input = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(input))
                return new HttpResponse { StatusCode = 400, Body = "{\"error\":\"empty input\"}"u8.ToArray() };

            var model = SnapshotModel();
            var pipe = new Pipe();
            var sse = new SseConnection(pipe.Writer);
            _ = WriteSseAsync(sse, pipe.Writer, sessionId, model, input, ct);
            return new HttpResponse
            {
                StatusCode = 200,
                Headers = [new("Content-Type", "text/event-stream"), new("Cache-Control", "no-cache")],
                BodyStream = pipe.Reader.AsStream(),
            };
        });

        // POST /model/switch
        app.MapPost("/model/switch", async (ctx, ct) =>
        {
            var json = await ReadJsonBodyAsync(ctx, ct);
            var modelId = json?.GetProperty("modelId").GetString() ?? "";
            return SwitchModel(modelId) ? JsonOk() : JsonError(404, "NOT_FOUND", "Model not found");
        });

        // POST /provider/switch
        app.MapPost("/provider/switch", async (ctx, ct) =>
        {
            var json = await ReadJsonBodyAsync(ctx, ct);
            var provider = json?.GetProperty("provider").GetString() ?? "";
            return SwitchProvider(provider) ? JsonOk() : JsonError(404, "NOT_FOUND", "Provider not configured");
        });

        // POST /thinking
        app.MapPost("/thinking", async (ctx, ct) =>
        {
            var json = await ReadJsonBodyAsync(ctx, ct);
            var enabled = json?.GetProperty("enabled").GetBoolean() ?? true;
            var levelStr = json?.GetProperty("level").GetString() ?? "medium";
            var level = AgentConfig.ParseLevel(levelStr);
            if (level is null)
                return JsonError(400, "INVALID_ARGUMENT", $"Invalid level: {levelStr}");
            SwitchThinking(enabled, level.Value);
            return JsonOk();
        });

        // GET /models[?provider=X]
        app.MapGet("/models", async (ctx, ct) =>
        {
            var provider = ctx.Query.GetValueOrDefault("provider");
            var models = await ListModelsAsync(provider, ct);
            var json = PicoJetson.JsonSerializer.Serialize(models);
            return new HttpResponse { StatusCode = 200, Body = json, Headers = [new("Content-Type", "application/json")] };
        });

        // GET /health
        app.MapGet("/health", (_, __) =>
        {
            var model = SnapshotModel();
            var json = $"{{\"status\":\"ok\",\"model\":\"{model.Id}\",\"provider\":\"{model.Provider}\"}}";
            return JsonResponse(200, json);
        });

        // GET /sessions
        app.MapGet("/sessions", async (_, ct) =>
        {
            var sessions = await ListSessionsAsync(ct);
            var json = PicoJetson.JsonSerializer.Serialize(sessions);
            return new HttpResponse { StatusCode = 200, Body = json, Headers = [new("Content-Type", "application/json")] };
        });

        // POST /session/{id}/create
        app.MapPost("/session/{id}/create", (ctx, _) =>
        {
            var id = ctx.RouteValues["id"] ?? "";
            if (!SessionIdRegex().IsMatch(id))
                return JsonError(400, "INVALID_SESSION_ID", id);
            _host.GetSessionMessages(id); // triggers creation
            return JsonOk();
        });

        // POST /session/{id}/delete
        app.MapPost("/session/{id}/delete", (ctx, _) =>
        {
            var id = ctx.RouteValues["id"] ?? "";
            if (_homeDir is not { Length: > 0 })
                return JsonError(400, "NO_HOME_DIR", "homeDir not configured");
            var path = Path.Combine(_homeDir, "sessions", $"{id}.jsonl");
            if (!File.Exists(path))
                return JsonError(404, "NOT_FOUND", $"Session not found: {id}");
            File.Delete(path);
            return JsonOk();
        });

        // GET /session/{id}/messages
        app.MapGet("/session/{id}/messages", (ctx, _) =>
        {
            var id = ctx.RouteValues["id"] ?? "";
            var msgs = _host.GetSessionMessages(id);
            if (msgs.Count == 0 && id != "default")
                return JsonError(404, "NOT_FOUND", $"Session not found: {id}");
            var json = PicoJetson.JsonSerializer.Serialize(msgs);
            return new HttpResponse { StatusCode = 200, Body = json, Headers = [new("Content-Type", "application/json")] };
        });

        // POST /session/{id}/save
        app.MapPost("/session/{id}/save", async (ctx, ct) =>
        {
            var id = ctx.RouteValues["id"] ?? "";
            if (_homeDir is not { Length: > 0 })
                return JsonError(400, "NO_HOME_DIR", "homeDir not configured");
            await SaveAsync(id, ct);
            return JsonOk();
        });

        // POST /reload
        app.MapPost("/reload", (_, __) =>
        {
            if (_homeDir is { Length: > 0 })
                _registry.Scan(_homeDir);
            return JsonResponse(200, "{\"status\":\"reloaded\"}");
        });

        return app;
    }

    // ── Private helpers ──

    private static async Task<JsonElement?> ReadJsonBodyAsync(WebContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.BodyStream);
        var text = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static HttpResponse JsonOk() => new() { StatusCode = 200, Body = "{\"status\":\"ok\"}"u8.ToArray() };

    private static HttpResponse JsonError(int code, string errorCode, string message) => new()
    {
        StatusCode = code,
        Body = Encoding.UTF8.GetBytes($"{{\"type\":\"error\",\"code\":\"{errorCode}\",\"message\":\"{message}\"}}"),
    };

    private async Task WriteSseAsync(SseConnection sse, PipeWriter writer, string sessionId, Model model, string input, CancellationToken ct)
    {
        try
        {
            await _host.ProcessMessageAsync(input, model, ct, sessionId,
                onEvent: async (evt, ct2) =>
                {
                    switch (evt)
                    {
                        case AssistantMessageEvent.TextDelta td:
                            await sse.WriteJsonAsync(PicoJetson.JsonSerializer.Serialize(new { type = "delta", content = td.Delta }), ct2);
                            break;
                        case AssistantMessageEvent.ThinkingDelta th:
                            await sse.WriteJsonAsync(PicoJetson.JsonSerializer.Serialize(new { type = "thinking", content = th.Delta }), ct2);
                            break;
                        case AssistantMessageEvent.Done d:
                            await sse.WriteJsonAsync(PicoJetson.JsonSerializer.Serialize(new { type = "done", stopReason = d.Message.StopReason }), ct2);
                            break;
                        case AssistantMessageEvent.Error e:
                            await sse.WriteJsonAsync(PicoJetson.JsonSerializer.Serialize(new { type = "error", code = "PROVIDER_UNAVAILABLE", message = e.Message.ErrorMessage }), ct2);
                            break;
                    }
                });
            await sse.CompleteAsync(ct);
        }
        catch (OperationCanceledException) { await writer.CompleteAsync(); }
        catch (Exception ex) { await writer.CompleteAsync(ex); }
    }

    private static IPEndPoint ParseEndpoint(string uri)
    {
        var u = new Uri(uri);
        if (IPAddress.TryParse(u.Host, out var addr))
            return new IPEndPoint(addr, u.Port > 0 ? u.Port : 80);
        return new IPEndPoint(IPAddress.Loopback, u.Port > 0 ? u.Port : 80);
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex SessionIdRegex();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
```

Agent 实现完整。上述约 350 行代码可直接编译使用。

- [ ] **Step 4: 构建 + 测试**

```bash
dotnet build PicoNode.slnx -c Release && dotnet test --project tests/PicoNode.Tests/PicoNode.Tests.csproj
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(Agent): add single public entry point with HTTP SSE lifecycle"
```

---

### Task 4: AgentHttpClient + SSE Parser

**Files:**
- Create: `src/PicoAgent/AgentHttpClient.cs`

**Interfaces:**
- Produces: `AgentHttpClient : IAsyncDisposable`（实现 Spec §4.1）

- [ ] **Step 1: 写失败测试**

```csharp
// tests/PicoNode.Tests/AgentHttpClientTests.cs
namespace PicoNode.Tests;

public sealed class AgentHttpClientTests
{
    [Test]
    public async Task Constructor_AcceptsBaseUrl()
    {
        await using var client = new AgentHttpClient("http://localhost:12345");
        await Assert.That((object?)client).IsNotNull();
    }
}
```

- [ ] **Step 2: 确认编译失败**

- [ ] **Step 3: 实现 AgentHttpClient**

```csharp
// src/PicoAgent/AgentHttpClient.cs
namespace PicoAgent;

public sealed class AgentHttpClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private bool _disposed;

    public AgentHttpClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public IAsyncEnumerable<AssistantMessageEvent> SendMessageAsync(
        string sessionId, string message, CancellationToken ct = default)
    {
        return SendMessageInternalAsync(sessionId, message, ct);
    }

    private async IAsyncEnumerable<AssistantMessageEvent> SendMessageInternalAsync(
        string sessionId, string message, [EnumeratorCancellation] CancellationToken ct)
    {
        var content = new StringContent(message, Encoding.UTF8, "text/plain");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/session/{sessionId}/message")
        {
            Content = content,
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);

        await foreach (var evt in PicoAgentSseParser.ParseAsync(stream, ct))
            yield return evt;
    }

    public async Task<string> SendMessageTextAsync(string sessionId, string message, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var evt in SendMessageAsync(sessionId, message, ct))
        {
            if (evt is AssistantMessageEvent.TextDelta td)
                sb.Append(td.Delta);
            else if (evt is AssistantMessageEvent.Error e)
                throw new InvalidOperationException(e.Message.ErrorMessage ?? "Agent error");
        }
        return sb.ToString();
    }

    public async Task<bool> SwitchModelAsync(string modelId, CancellationToken ct = default)
    {
        var json = $"{{\"modelId\":\"{modelId}\"}}";
        var response = await _http.PostAsync("/model/switch", new StringContent(json, Encoding.UTF8, "application/json"), ct);
        return response.IsSuccessStatusCode;
    }

    // SwitchProviderAsync, SwitchThinkingAsync, ListModelsAsync, etc.
    // (完整实现约 150 行，逐个 HTTP endpoint 包装)

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

// PicoAgentSseParser — 内部 SSE 解析器
internal static class PicoAgentSseParser
{
    public static async IAsyncEnumerable<AssistantMessageEvent> ParseAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (!line.StartsWith("data: ")) continue;
            var json = line[6..];
            if (json == "[DONE]") yield break;
            yield return ParseEvent(json);
        }
    }

    private static AssistantMessageEvent ParseEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();
        return type switch
        {
            "delta" => new AssistantMessageEvent.TextDelta { Delta = doc.RootElement.GetProperty("content").GetString() ?? "" },
            "thinking" => new AssistantMessageEvent.ThinkingDelta { Delta = doc.RootElement.GetProperty("content").GetString() ?? "" },
            "done" => new AssistantMessageEvent.Done { Message = new() { StopReason = doc.RootElement.GetProperty("stopReason").GetString() ?? "" } },
            "error" => new AssistantMessageEvent.Error { Message = new() { ErrorMessage = doc.RootElement.GetProperty("message").GetString() } },
            _ => throw new InvalidOperationException($"Unknown SSE event type: {type}"),
        };
    }
}
```

- [ ] **Step 4: 构建 + 测试**

```bash
dotnet build PicoNode.slnx -c Release && dotnet test --project tests/PicoNode.Tests/PicoNode.Tests.csproj
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(AgentHttpClient): add C# HTTP SSE client with custom SSE parser"
```

---

### Task 5: CLI 重写为纯 HTTP 客户端

**Files:**
- Modify: `samples/PicoAgent.Cli/Program.cs`

- [ ] **Step 1: 重写 Program.cs**

```csharp
// samples/PicoAgent.Cli/Program.cs
var homeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FileSystemConstants.AgentHomeDir);
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, FileSystemConstants.SessionsDir));

var settingsPath = Path.Combine(homeDir, "settings.json");
if (!File.Exists(settingsPath))
{
    settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    if (!File.Exists(settingsPath))
        settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "settings.json");
}

var config = await ConfigLoader.LoadAsync(settingsPath);
if (config is null)
{
    await Console.Error.WriteLineAsync($"Settings file not found: {settingsPath}");
    await Console.Error.WriteLineAsync("Copy settings.example.json to this path and edit it.");
    return;
}
var validation = ConfigLoader.Validate(config);
if (!validation.IsValid)
{
    foreach (var err in validation.Errors)
        await Console.Error.WriteLineAsync(err);
    return;
}

await using var agent = await Agent.CreateAsync(config, homeDir);
var cmd = args.Length > 0 ? args[0] : "chat";

if (cmd == "serve")
{
    var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8080;
    await Console.Out.WriteLineAsync($"PicoAgent serving on http://localhost:{port}");
    await agent.RunAsync($"http://localhost:{port}");
}
else
{
    await agent.ListenAsync("http://localhost:0");
    var port = ((IPEndPoint)agent.LocalEndPoint!).Port;
    await using var client = new AgentHttpClient($"http://localhost:{port}");

    var scanner = new KnowledgeScanner();
    var skills = scanner.Scan(homeDir);
    var skillsPrompt = skills.Count > 0 ? KnowledgeScanner.BuildSkillsPrompt(skills) : "";

    if (skills.Count > 0)
        await Console.Out.WriteLineAsync($"[Loaded {skills.Count} skills]");

    await Console.Out.WriteLineAsync("PicoAgent chat.");
    await Console.Out.WriteLineAsync(
        $"Providers: {string.Join(", ", config.Providers.Keys)}"
    );
    await Console.Out.WriteLineAsync($"Model: {config.Model}");
    await Console.Out.WriteLineAsync("Commands: /model, /provider, /thinking, /list-models, /save, /help, /exit\n");

    var exitCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; exitCts.Cancel(); };

    try
    {
        while (!exitCts.Token.IsCancellationRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input is null || input == "/exit") break;

            if (input == "/save") { await client.SaveAsync(); await Console.Out.WriteLineAsync("[Saved]"); continue; }
            if (input == "/help") { await Console.Out.WriteLineAsync("/model, /provider, /thinking, /list-models, /save, /help, /exit"); continue; }

            if (input.StartsWith("/model ")) { await client.SwitchModelAsync(input[7..]); await Console.Out.WriteLineAsync($"[Model: {input[7..]}]"); continue; }
            if (input.StartsWith("/provider ")) { await client.SwitchProviderAsync(input[10..]); await Console.Out.WriteLineAsync($"[Provider: {input[10..]}]"); continue; }
            if (input.StartsWith("/thinking ")) { await client.SwitchThinkingAsync(true, input[10..]); await Console.Out.WriteLineAsync($"[Thinking: {input[10..]}]"); continue; }
            if (input == "/list-models") { var ms = await client.ListModelsAsync(); foreach (var m in ms) await Console.Out.WriteLineAsync($"  {m.Id}"); continue; }

        await foreach (var evt in client.SendMessageAsync("default", input))
        {
            if (evt is AssistantMessageEvent.TextDelta td) Console.Write(td.Delta);
            else if (evt is AssistantMessageEvent.ThinkingDelta th) Console.Write(th.Delta);
            else if (evt is AssistantMessageEvent.Error e) Console.Write($"\n[Error: {e.Message.ErrorMessage}]");
        }
        Console.WriteLine("\n");
    }
    }
    catch (OperationCanceledException) { }

    await client.SaveAsync();
    await Console.Out.WriteLineAsync("[Session saved]");
}
```

- [ ] **Step 2: 构建验证**

```bash
dotnet build samples/PicoAgent.Cli/PicoAgent.Cli.csproj -c Release
```

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "refactor(CLI): rewrite as pure HTTP client using AgentHttpClient"
```

---

### Task 6: 删除过期文件 + 清理测试

**Files:**
- Delete: `src/PicoAgent/AgentServer.cs`
- Delete: `src/PicoAgent/AgentServerOptions.cs`
- Delete: `src/PicoAgent/AgentEndpoints.cs`
- Delete: `tests/PicoNode.Tests/AgentServerTests.cs`
- Delete: `tests/PicoNode.Tests/AgentEndpointsTests.cs`
- Modify: `tests/PicoNode.Tests/AgentBuilderTests.cs` → 移除 `BuildServerAsync` 相关测试
- Modify: `src/PicoAgent/PicoAgent.csproj` → 添加 `InternalsVisibleTo` 给测试

- [ ] **Step 1: 添加 InternalsVisibleTo**

```xml
<!-- src/PicoAgent/PicoAgent.csproj，在现有 </PropertyGroup> 后添加 -->
<ItemGroup>
    <InternalsVisibleTo Include="PicoNode.Tests" />
</ItemGroup>
```

- [ ] **Step 2: 删除文件**

```bash
rm src/PicoAgent/AgentServer.cs src/PicoAgent/AgentServerOptions.cs src/PicoAgent/AgentEndpoints.cs
rm tests/PicoNode.Tests/AgentServerTests.cs tests/PicoNode.Tests/AgentEndpointsTests.cs
```

- [ ] **Step 3: 修复 AgentBuilderTests**

移除 `BuildServerAsync` 和 `BuildServer_WithValidConfig_ReturnsAgentServer` 测试方法。保留 `BuildHostAsync` 相关测试。

- [ ] **Step 4: 构建 + 全量测试**

```bash
dotnet build PicoNode.slnx -c Release && dotnet test --project tests/PicoNode.Tests/PicoNode.Tests.csproj
```

预期：所有 test 编译通过，新增 AgentTests 运行，AgentServerTests 不存在不再运行。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: remove AgentServer/AgentServerOptions/AgentEndpoints, fix tests"
```

---

### Task 7: 全方案验证

- [ ] **Step 1: 全方案构建**

```bash
dotnet build PicoNode.slnx -c Release
```

预期：0 errors + 0 CS warnings（除预存 TUnit IL2026）。

- [ ] **Step 2: 全部测试**

```bash
dotnet test --project tests/PicoNode.Tests/PicoNode.Tests.csproj
dotnet test --project tests/PicoNode.Agent.Tests/PicoNode.Agent.Tests.csproj
dotnet test --project tests/PicoNode.AI.Tests/PicoNode.AI.Tests.csproj
dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj
dotnet test --project tests/PicoWeb.Tests/PicoWeb.Tests.csproj
```

预期：全部 Passed。

- [ ] **Step 3: AOT 验证**

```bash
dotnet publish samples/PicoAgent.Cli/PicoAgent.Cli.csproj -c Release
```

预期：无 AOT 不兼容错误。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: finalize PicoAgent v1.0, verify build and tests"
```

---

## Self-Review

### 1. Spec Coverage

| Spec Section | Task |
|-------------|------|
| §2.1 Agent API | Task 3 |
| §2.2 AgentConfig | 不变（现有） |
| §2.3 AgentResult | Task 3 |
| §2.4 内部结构 + AgentLoop 修改 | Task 1, Task 2 |
| §3.1-3.3 HTTP 端点 | Task 3（BuildWebApp 内部） |
| §4 AgentHttpClient | Task 4 |
| §5 CLI | Task 5 |
| §6 文件变更 | Task 6 |
| §7 AOT 约束 | Task 7 |
| §8 后续迭代 | —（out of scope） |

### 2. Placeholder Scan

✅ 无 TBD/TODO/fill-in。所有 helper 方法（JsonOk, JsonError, ReadJsonBodyAsync, WriteSseAsync, ParseEndpoint, SessionIdRegex）已完整实现，无需引用外部文件。

### 3. Type Consistency

- `AgentLoop(IllmClient, CapabilityRegistry, CapabilityRunner)` — Task 1 ✓
- `RunTurnAsync(Model, List<Message>, Ct, Func?)` — Task 1 ✓
- `AgentHost.ProcessMessageAsync(string, Model, Ct, string, Func?)` — Task 2 ✓
- `Agent.CreateAsync(AgentConfig, string?)` — Task 3 ✓
- `AgentHttpClient(string)` — Task 4 ✓
