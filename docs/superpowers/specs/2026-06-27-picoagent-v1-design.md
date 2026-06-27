# PicoAgent v1.0 — Design Spec

> **状态**: 最终设计 | **日期**: 2026-06-27 | **依赖**: PicoNode.Agent, PicoNode.AI, PicoWeb, PicoNode.Web

---

## 0. 核心原则

Agent 是 HTTP SSE 服务器。所有 UI（CLI/TUI/Web/GUI/Python/移动端）是平等的 HTTP 客户端。

---

## 1. 架构

```
┌──────────────────────────────────────────┐
│ Agent (HTTP SSE 服务器)                   │
│                                          │
│ POST /session/{id}/message  → SSE 流     │
│ POST /model/switch                       │
│ POST /provider/switch                    │
│ POST /thinking                           │
│ GET  /models[?provider=X]                │
│ GET  /health                             │
│ GET  /sessions                           │
│ POST /session/{id}/create                │
│ POST /session/{id}/delete                │
│ GET  /session/{id}/messages              │
│ POST /session/{id}/save                  │
│ POST /reload                             │
└──────────────────────────────────────────┘
         ▲          ▲          ▲
         │          │          │
    ┌────┴───┐ ┌───┴────┐ ┌───┴─────┐
    │ CLI    │ │ TUI    │ │ Web GUI │
    │ (C#)   │ │ (C#)   │ │ (HTML)  │
    └────────┘ └────────┘ └─────────┘
```

---

## 2. Agent — 服务器

### 2.1 类型

```csharp
// src/PicoAgent/Agent.cs — 唯一 public 类型
public sealed class Agent : IAsyncDisposable
{
    // ── 工厂 ──
    public static Task<Agent> CreateAsync(AgentConfig config, string? homeDir = null);

    // ── HTTP 生命周期 ──
    public Task ListenAsync(string uri, CancellationToken ct = default);
    public Task RunAsync(string uri, CancellationToken ct = default); // Listen + 阻塞
    public Task StopAsync(CancellationToken ct = default);              // 仅对 ListenAsync 有效

    // ── 程序化控制 ──
    // 需先调用 ListenAsync。如果尚未监听，抛出 InvalidOperationException。
    public Task<AgentResult> SendAsync(string sessionId, string message, CancellationToken ct = default);
    public Task<AgentResult> SendAsync(string sessionId, string message,
        Func<AssistantMessageEvent, CancellationToken, ValueTask> onEvent,
        CancellationToken ct = default);

    // SwitchModel: modelId 无效时返回 false (不抛异常)
    public bool SwitchModel(string modelId);
    // SwitchProvider: providerName 无效时返回 false (不抛异常)
    public bool SwitchProvider(string providerName);
    public void SwitchThinking(bool enabled, ThinkingLevel level);
    public Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(string? provider = null, CancellationToken ct = default);
    public Task SaveAsync(string sessionId = "default", CancellationToken ct = default);
    public Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId = "default");

    // ── 会话管理 ──
    public Task<List<string>> ListSessionsAsync(CancellationToken ct = default);
    public Task CreateSessionAsync(string sessionId, CancellationToken ct = default);
    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);
}
```

### 2.2 AgentConfig

`ProviderEntry` 和 `ModelThinkingOverride` 保持现有实现不变（位于 `PicoNode.Agent`）。

```csharp
[PicoSerializable]
public sealed class AgentConfig
{
    public Dictionary<string, ProviderEntry> Providers { get; set; } = [];
    public string? Model { get; set; }
    public string? ThinkingLevel { get; set; }
    public bool ThinkingEnabled { get; set; } = true;
    public int? MaxTokens { get; set; }
}
```

### 2.3 AgentResult

```csharp
public sealed class AgentResult
{
    public string Text { get; init; }
    public IReadOnlyList<Message> NewMessages { get; init; }
    public string? StopReason { get; init; }
    public TokenUsage? Usage { get; init; }
}
```

### 2.4 内部结构

```
Agent
├── AgentHost (PicoNode.Agent)     ← 会话管理 + 委托 AgentLoop
├── AgentLoop (PicoNode.Agent)     ← LLM 循环编排
│   └── RunTurnAsync(model, messages, ct, onEvent)
├── HttpClient                     ← ModelDiscovery
├── ProviderRouter + CircuitBreakers
├── LLM Clients (Anthropic, OpenAI)
├── CapabilityRegistry + Runner
├── WebServer (PicoWeb)            ← 仅在 ListenAsync 时创建
└── WebApp (PicoNode.Web)          ← HTTP 路由注册
```

AgentLoop 修改：`Model` 从构造参数改为 `RunTurnAsync` 参数，每次调用拍快照，保证 `SwitchModel` 并发安全。

并发安全：
- `SwitchModel` / `SwitchProvider` / `SwitchThinking` 写 `_pendingModel` 字段（带锁）
- `SendAsync` / SSE 请求开始时从 `_pendingModel` 拍快照传入 AgentLoop
- AgentHost 需按 sessionId 串行化并发请求（per-session lock）

资源管理：
- `ListenAsync` 创建 WebServer；`StopAsync` 停止 + `DisposeAsync` 释放

---

## 3. HTTP 端点

### 3.1 通用约定

- 所有响应 `Content-Type: application/json` 或 `text/event-stream`
- 错误统一格式 `{"type":"error","code":"...","message":"..."}`
  错误码参考: `EMPTY_INPUT`, `NOT_FOUND`, `CONFLICT`, `INVALID_ARGUMENT`, `PROVIDER_UNAVAILABLE`, `NO_HOME_DIR`
- SSE 连接断开 → 对应的 LLM 调用自动取消
- `Agent.CreateAsync` 未传 `homeDir` → 会话保存/恢复端点返回 400

### 3.2 端点清单

| 方法 | 路径 | 请求体 | 响应 | 错误 |
|------|------|--------|------|------|
| POST | `/session/{id}/message` | `string` (纯文本) | SSE 流 | 空 body → 400 |
| POST | `/model/switch` | `{"modelId":"gpt-4o"}` | `{"status":"ok"}` | 未知 model → 404 |
| POST | `/provider/switch` | `{"provider":"deepseek"}` | `{"status":"ok"}` | 未配置 provider → 404 |
| POST | `/thinking` | `{"enabled":true,"level":"high"}` | `{"status":"ok"}` | 无效 level → 400 |
| GET | `/models[?provider=X]` | — | `[{"id":"...","ownedBy":"..."}]` | — |
| GET | `/health` | — | `{"status":"ok","model":"...","provider":"..."}` | — |
| GET | `/sessions` | — | `["default","project-x"]` | — |
| POST | `/session/{id}/create` | — | `{"status":"created"}` | 已存在 → 409 |
| POST | `/session/{id}/delete` | — | `{"status":"deleted"}` | 不存在 → 404 |
| GET | `/session/{id}/messages` | — | `[Message, ...]` | 不存在 → 404 |
| POST | `/session/{id}/save` | — | `{"status":"saved"}` | — |
| POST | `/reload` | — | `{"status":"reloaded"}` | — |

### 3.3 SSE 事件类型 (v1.0)

基于现有 `AssistantMessageEvent` 子类型：

```
{"type":"delta","content":"你好"}
{"type":"thinking","content":"分析用户意图..."}
{"type":"done","stopReason":"end_turn","usage":{"inputTokens":10,"outputTokens":5}}
{"type":"error","code":"PROVIDER_UNAVAILABLE","message":"DeepSeek API returned 502"}
```

> `tool_call` / `tool_result` 事件将在 v1.1 工具调用流中追加。

---

## 4. AgentHttpClient — C# 客户端

### 4.1 类型

```csharp
// src/PicoAgent/AgentHttpClient.cs — C# HTTP SSE 客户端
public sealed class AgentHttpClient : IAsyncDisposable
{
    public AgentHttpClient(string baseUrl);

    // 流式
    public IAsyncEnumerable<AssistantMessageEvent> SendMessageAsync(
        string sessionId, string message, CancellationToken ct = default);

    // 阻塞式 (脚本用)
    public Task<string> SendMessageTextAsync(
        string sessionId, string message, CancellationToken ct = default);

    // 模型
    public Task SwitchModelAsync(string modelId, CancellationToken ct = default);
    public Task SwitchProviderAsync(string provider, CancellationToken ct = default);
    public Task SwitchThinkingAsync(bool enabled, string level, CancellationToken ct = default);
    public Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(string? provider = null, CancellationToken ct = default);

    // 会话
    public Task SaveAsync(string sessionId = "default", CancellationToken ct = default);
    public Task<List<string>> ListSessionsAsync(CancellationToken ct = default);
    public Task CreateSessionAsync(string sessionId, CancellationToken ct = default);
    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);
    public Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId = "default", CancellationToken ct = default);
}
```

### 4.2 实现要点

- 底层：`HttpClient` + SSE 解析（需要新建 PicoAgent 专用 SSE parser，`PicoNode.AI.SseParser` 只解析 Anthropic/OpenAI 格式）
- 流式：`System.IO.Pipelines` 逐块读 + 逐行解析 → `AssistantMessageEvent`
- 断开时取消：`CancellationTokenSource` + `finally` 释放

---

## 5. CLI 入口 — 变为纯 HTTP 客户端

可执行文件：`PicoAgent.Cli.exe` (`dotnet run --project samples/PicoAgent.Cli`)

```
PicoAgent.Cli chat         → Agent 启动 (port 0) → CLI 连接 (localhost:{port})
PicoAgent.Cli serve 8080   → Agent 启动 (port 8080)，无 CLI
```

`PicoAgent.Cli`（无参数）= `PicoAgent.Cli chat`。

CLI 内部：
```csharp
var agent = await Agent.CreateAsync(config, homeDir);
var port = 0; // 系统分配
await agent.ListenAsync($"http://localhost:{port}");

var client = new AgentHttpClient($"http://localhost:{port}");
// ... CLI 交互循环使用 client.SendMessageAsync ...
```

---

## 6. 文件变更

### 6.1 删除

| 文件 | 原因 |
|------|------|
| `src/PicoAgent/AgentServer.cs` | 合并到 Agent |
| `src/PicoAgent/AgentServerOptions.cs` | 合并到 AgentConfig |
| `src/PicoAgent/AgentEndpoints.cs` | 变为 Agent 私有方法 |

### 6.2 修改

| 文件 | 变更 |
|------|------|
| `src/PicoAgent/AgentBuilder.cs` | `public` → `internal` |
| `src/PicoNode.Agent/Agent/AgentLoop.cs` | `Model` 从构造函数参数 → `RunTurnAsync` 参数 |
| `samples/PicoAgent.Cli/Program.cs` | 从内嵌 chat 逻辑 → 纯 HTTP 客户端 |
| `tests/*` | 适配新 API |

### 6.3 新增

| 文件 | 职责 |
|------|------|
| `src/PicoAgent/Agent.cs` | 唯一 public 入口 |
| `src/PicoAgent/AgentHttpClient.cs` | C# HTTP SSE 客户端 |

---

## 7. AOT 约束

| 约束 | 影响 | 处理 |
|------|------|------|
| `PicoJetson.Serialize` 匿名类型 | SSE 事件 JSON | 用命名 DTO（后序迭代）或保持现有匿名类型（已有 PICOJETSON002 warning） |
| `Dns.GetHostEntry` | `localhost` 解析 | `IPAddress.Loopback` 替代 |
| `Environment.GetEnvironmentVariable` | 配置 env var 展开 | 已验证 AOT 下正常工作 |

---

## 8. 后续迭代 (v1.1+)

- 钩子系统 (OnBash / OnModelPreRequest / ...)
- FailoverService 接入 ResilientLLmClient
- 包管理 (install/remove/update/list)
- Compaction v2 (LLM 摘要)
- Schema 发现 (describe 协议)
- PicoDI 完全拥抱 (RegisterAgent 扩展方法)
- FormatConverter 真实现 (非 pass-through stub)
- AgentConfig 从手写 ConfigLoader 迁移到 PicoCfg RegisterCfgSingleton

---

> **版本**: v1.0 | **状态**: 待用户审阅
