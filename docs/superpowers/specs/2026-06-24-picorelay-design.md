# PicoRelay — Design Spec

> 基于 PicoNode 构建 AI API 代理/中转站，支持本地和云端两种部署模式。
>
> **状态**: 修订 v0.2 | **日期**: 2026-06-24 | **关联**: [PicoAgent Spec](./2026-06-24-picoagent-design.md)

---

## 1. 背景与动机

### 1.1 参照物：CC Switch

[CC Switch](https://github.com/farion1231/cc-switch) 是一个管理 Claude Code / Codex / Gemini CLI 等 AI 工具配置的桌面应用。其内置的**本地代理**功能是本 spec 的核心参照：

- 启动本地 HTTP 代理 (127.0.0.1:15721)
- 拦截 CLI 工具的 API 请求（修改 `base_url` → localhost）
- 路由到配置的 AI 提供商
- 格式转换 (Anthropic ↔ OpenAI Chat Completions)
- 熔断器 + 自动 Failover
- 用量记录和日志

### 1.2 目标

构建一个可在 PicoNode 上运行的 AI API 代理核心，通过配置切换两种部署模式：

| 模式 | 说明 | 典型场景 |
|------|------|---------|
| **本地代理** | 单人使用，localhost 运行 | 开发者本地切换 API 提供商 |
| **AI 中转站** | 云端部署，多租户共享 | 团队/企业统一 AI API 网关 |

### 1.3 核心洞察

**AI 中转站 = 本地代理 + 4 层中间件**。代理核心逻辑完全相同，云端模式只是在前面多挂了几层中间件。

### 1.4 与 PicoAgent 的关系

中转站和 PicoAgent 共享 `PicoNode.AI` 层：

```
PicoNode repo
│
├── PicoNode.AI (net10.0, AOT-compatible) — 新 NuGet 包
│   ├── ILLmClient         — LLM HTTP 客户端
│   ├── IFormatConverter   — Anthropic ↔ OpenAI 格式转换
│   ├── IProviderRouter    — Provider 路由
│   ├── ICircuitBreaker    — 熔断器
│   ├── IFailoverService   — Failover 自动切换
│   └── SseParser          — SSE 流解析
│
├── PicoNode.Relay (net10.0) — 中转站（本 spec）
│   └── RelayMiddleware + 云端中间件
│
└── PicoAgent (net10.0) — Agent
    ├── Agent Host    (事件循环 + Capability Runner)
    └── TUI 客户端    (独立进程，SSE 消费)
```

---

## 2. 架构设计

### 2.1 整体分层

```
┌──────────────────────────────────────────────────┐
│                   PicoNode 层                      │
│  TcpNode → HttpConnectionHandler → WebApp         │
│  (提供: HTTP 服务端, 中间件管道, TLS, DI)          │
├──────────────────────────────────────────────────┤
│                 AuthMiddleware      ← 云模式      │
│              RateLimitMiddleware    ← 云模式      │
│              MeteringMiddleware     ← 云模式      │
├──────────────────────────────────────────────────┤
│              PicoNode.AI (共享层)   ← 两种模式    │
│  ┌─────────────────────────────────────────────┐ │
│  │ • 格式转换 (Anthropic ↔ OpenAI)              │ │
│  │ • Provider 路由选择                          │ │
│  │ • 熔断器 (Circuit Breaker)                   │ │
│  │ • Failover 自动切换                          │ │
│  │ • LLM HTTP 客户端 (HttpClient)               │ │
│  │ • SSE 流解析                                 │ │
│  └─────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────┤
│              上游 AI API (Anthropic, OpenAI, ...) │
└──────────────────────────────────────────────────┘
```

### 2.2 请求流

```
Client (Claude Code / 任意客户端)
    │  POST /v1/messages  (base_url → 我们的代理)
    ▼
TcpNode (TLS 可选的 TCP 层)
    ▼
WebApp 中间件管道:
    [Auth] → [RateLimit] → [Metering] → [RelayHandler]
                                              │
                              ┌───────────────┘
                              ▼
                    RelayHandler (使用 PicoNode.AI):
                      1. 解析请求 (识别 API 格式)
                      2. 选择 Provider (IProviderRouter)
                      3. 如有需要，格式转换 (IFormatConverter)
                      4. 通过 ILLmClient 转发
                      5. 如有需要，响应格式转换
                      6. 流式透传 SSE/Stream
                      7. 失败时 Failover + CircuitBreaker 重试
                              │
                              ▼
                    上游 API Provider
```

---

## 3. 共享层设计 (PicoNode.AI)

PicoNode.AI 是中转站和 PicoAgent 的共享基础设施，在 PicoNode repo 中作为新 NuGet 包。

### 3.1 包定位

```
PicoNode.AI   net10.0, AOT-compatible
依赖: PicoNode.Http, PicoJetson, PicoDI
提供: LLM 通信、格式转换、路由、熔断
```

### 3.2 LLM HTTP 客户端

中转站和 Agent 使用同一个 LLM 客户端。类型体系使用 PicoNode.Http 自有类型（不用 BCL HttpRequestMessage）。

**核心类型**（在 PicoNode.AI 中定义）：

```csharp
// 模型引用 — 带 API 类型信息
public record Model
{
    public string Id { get; init; }          // "claude-sonnet-4-20250514"
    public string Name { get; init; }        // "Claude Sonnet 4"
    public string Provider { get; init; }    // "anthropic"
    public AiApiFormat Api { get; init; }    // AnthropicMessages
    public string BaseUrl { get; init; }
    public bool Reasoning { get; init; }
    public ModelCost Cost { get; init; }
    public int ContextWindow { get; init; }
    public int MaxTokens { get; init; }
}

public record ModelCost
{
    public decimal Input { get; init; }      // $/百万 token
    public decimal Output { get; init; }
    public decimal CacheRead { get; init; }
    public decimal CacheWrite { get; init; }
}

// 对话上下文
public record ChatContext
{
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<Message> Messages { get; init; }
    public IReadOnlyList<ToolSchema>? Tools { get; init; }
}

// 消息
public abstract record Message
{
    public string Role { get; init; }        // "user" | "assistant" | "toolResult"
    public long Timestamp { get; init; }
}

// 流调用选项
public record StreamOptions
{
    public string? ApiKey { get; init; }
    public int? MaxTokens { get; init; }
    public float? Temperature { get; init; }
    public ThinkingLevel? Reasoning { get; init; }  // minimal | low | medium | high | xhigh
}
```

**LLM 客户端接口**：

```csharp
// LLM 客户端 — 流是一等公民
public interface ILLmClient
{
    // 流式调用（核心）
    IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model, ChatContext context, StreamOptions? options, CancellationToken ct);
}

// complete() 是语法糖
public static async ValueTask<AssistantMessage> CompleteAsync(
    this ILLmClient client, Model model, ChatContext context, ...)
{
    await foreach (var e in client.StreamAsync(model, context, null, ct))
        if (e is AssistantMessageEvent.Done d) return d.Message;
    throw ...;
}

// 未在此完整定义的辅助类型（在 PicoNode.AI 中实现）：
//   ThinkingLevel enum  — minimal | low | medium | high | xhigh
//   ToolCall record     — Id, Name, Arguments
//   ToolSchema record   — Name, Description, Parameters (JSON Schema)
//   AssistantMessage    — Content, Model, Provider, Api, Usage, StopReason

// AssistantMessageEvent — LLM 流事件的统一类型
public abstract record AssistantMessageEvent
{
    public record Start(AssistantMessage Partial) : AssistantMessageEvent;
    public record TextDelta(int Index, string Delta, AssistantMessage Partial) : AssistantMessageEvent;
    public record ThinkingDelta(int Index, string Delta, AssistantMessage Partial) : AssistantMessageEvent;
    public record ToolCallStart(int Index, AssistantMessage Partial) : AssistantMessageEvent;
    public record ToolCallDelta(int Index, string Delta, AssistantMessage Partial) : AssistantMessageEvent;
    public record ToolCallEnd(int Index, ToolCall Call, AssistantMessage Partial) : AssistantMessageEvent;
    public record Done(AssistantMessage Message) : AssistantMessageEvent;
    public record Error(AssistantMessage Message) : AssistantMessageEvent;
}
```

### 3.3 格式转换

```csharp
public enum AiApiFormat { AnthropicMessages, OpenAIChatCompletions, OpenAIResponses }

public interface IFormatConverter
{
    // 请求转换：PicoNode.Http.HttpRequest → 目标格式的字节流
    bool CanConvertRequest(AiApiFormat from, AiApiFormat to);
    void ConvertRequest(
        ref HttpRequest source,          // PicoNode.Http 自有类型
        AiApiFormat targetFormat,
        IBufferWriter<byte> output);

    // 响应转换：上游响应流 → 目标格式的事件流
    bool CanConvertResponse(AiApiFormat from, AiApiFormat to);
    IAsyncEnumerable<AssistantMessageEvent> ConvertStream(
        IAsyncEnumerable<AssistantMessageEvent> source,
        AiApiFormat targetFormat,
        CancellationToken ct);
}
```

支持三种格式双向转换。转换不是 1:1 无损的——Anthropic tool_use 和 OpenAI function calling 有语义差异，需文档化。

### 3.4 Provider 路由

```csharp
public interface IProviderRouter
{
    ProviderConfig Resolve(string? model, AiApiFormat? preferredFormat);
}

public record ProviderConfig
{
    public string Name { get; init; }
    public string BaseUrl { get; init; }
    public string ApiKey { get; init; }
    public AiApiFormat ApiFormat { get; init; }
    public IReadOnlyDictionary<string, string> ModelMapping { get; init; }
    public int Priority { get; init; }  // Failover 优先级
}
```

### 3.5 熔断器 + Failover

```csharp
public interface ICircuitBreaker
{
    CircuitState State { get; }
    bool TryAcquire();                 // 放行请求？
    void RecordSuccess();              // 记录成功
    void RecordFailure();              // 记录失败
    ProviderHealth GetHealth();        // 当前健康状态
}

public enum CircuitState { Closed, Open, HalfOpen }

public record ProviderHealth
{
    public CircuitState State { get; init; }
    public int ConsecutiveFailures { get; init; }
    public double ErrorRate { get; init; }
    public DateTimeOffset? LastFailure { get; init; }
    public DateTimeOffset? LastSuccess { get; init; }
}

public interface IFailoverService
{
    // 选择下一个可用 Provider
    ProviderConfig? GetNextProvider(
        ProviderConfig current,
        IReadOnlyList<ProviderConfig> queue);

    // 记录切换事件
    void RecordFailover(
        ProviderConfig from,
        ProviderConfig to,
        string reason);
}
```

标准三态熔断器：

```
Closed ──(连续失败 ≥ N)──► Open
Open   ──(等待 T 秒)────► HalfOpen
HalfOpen ──(探测成功 ≥ M)─► Closed
HalfOpen ──(探测失败)────► Open
```

可配置参数：

| 参数 | 默认值 | 说明 |
|------|-------|------|
| FailureThreshold | 4 | 连续失败数触发熔断 |
| RecoverySuccessThreshold | 2 | 半开态需要连续成功数 |
| RecoveryWaitSeconds | 60 | 进入 Open 后等待时间 |
| ErrorRateThreshold | 0.6 | 错误率阈值 |
| MinRequests | 10 | 计算错误率的最小请求数 |

Failover：Provider 按 Priority 排序。主 Provider 熔断时自动切换。记录切换时间和原因。

---

## 4. PicoRelay 设计 (PicoNode.Relay)

### 4.1 包定位

```
PicoNode.Relay   net10.0, AOT-compatible
依赖: PicoNode.Web, PicoNode.AI, PicoDI, PicoCfg
```

### 4.2 核心 Middleware

```csharp
// RelayMiddleware — PicoRelay 的核心
public sealed class RelayMiddleware
{
    public RelayMiddleware(
        ILLmClient llmClient,
        IProviderRouter router,
        IFormatConverter converter,
        ICircuitBreaker breaker,
        IFailoverService failover)
    { ... }

    // 作为 PicoNode.Web 终结处理器（WebRequestHandler）注册
    // 中间件管道: [Auth] → [RateLimit] → [Metering] → [RelayHandler]
    public ValueTask<HttpResponse> InvokeAsync(WebContext ctx, CancellationToken ct)
    {
        // 1. 解析请求 → 识别 API 格式
        // 2. 选择 Provider (router)
        // 3. 格式转换 (converter)
        // 4. 转发 (llmClient)
        // 5. 流式透传 SSE → ctx.Response.Body
        // 6. 失败 → failover 重试 → breaker 记录
    }
}
```

### 4.3 云端中间件

| 中间件 | PicoNode 现有 | 说明 |
|--------|:---:|------|
| TLS 终结 | ✅ `TcpNodeOptions.SslOptions` | 云模式必须 |
| 用户鉴权 | ✅ `AuthMiddleware` | API Key / JWT |
| 租户限流 | ✅ `RateLimitMiddleware` | 每用户/每模型速率控制 |
| 用量计量 | ❌ 需新建 | Token 计数 + 持久化 |

### 4.4 用量计量中间件

```csharp
// MeteringMiddleware — 记录每次请求的 Token 用量
public sealed class MeteringMiddleware
{
    // 从 LLM 响应流中提取 usage 信息
    // 写入持久化存储（JSONL）
    // 暴露查询 API：GET /admin/usage?user=xxx&from=...&to=...
}
```

### 4.5 管理 API

```
GET    /admin/providers              → 列出 Provider
POST   /admin/providers              → 添加 Provider
DELETE /admin/providers/{name}       → 删除 Provider
GET    /admin/providers/{name}/health → 健康状态
GET    /admin/usage                  → 用量查询（支持 user/model/date 过滤）
POST   /admin/reload                 → 热加载 Provider 配置
```

---

## 5. PicoNode 适配分析

### 5.1 可直接使用的

| 能力 | 来源 |
|------|------|
| HTTP 服务端 | `PicoWeb` / `PicoNode.Web` |
| 中间件管道 | `WebApp.Use(WebMiddleware)` |
| SSE 流式输出 | `SseEndpoint` / `SseConnection` |
| 鉴权 | `AuthMiddleware` |
| 限流 | `RateLimitMiddleware` |
| CORS | `CorsHandler` |
| Session | `SessionMiddleware` |
| 静态文件 (管理面板) | `StaticFileMiddleware` |
| DI 容器 | `PicoDI` (ISvcContainer) |
| 配置绑定 | `PicoCfg` (AOT-safe) |
| TLS | `TcpNodeOptions.SslOptions` |
| AOT 编译 | 全栈 AOT |

### 5.2 需要从零构建的

| 组件 | 复杂度 | 位置 |
|------|:---:|------|
| LLM HTTP 客户端 | 低 | PicoNode.AI |
| 格式转换器 | 中 | PicoNode.AI |
| Provider 路由器 | 低 | PicoNode.AI |
| 熔断器 | 低 | PicoNode.AI |
| Failover 逻辑 | 低 | PicoNode.AI |
| 用量计量中间件 | 中 | PicoNode.Relay |
| 管理 API | 中 | PicoNode.Relay |

### 5.3 PicoNode 不需要提供的

- **GUI**: PicoNode 是纯后端，管理界面可以通过 API + 静态文件提供，或独立前端
- **客户端配置修改**: 类似于 CC Switch 修改 Claude Code 的 `base_url`——这不属于代理本身

---

## 6. 部署模式

### 6.1 本地模式

```
pico-relay start --mode local --port 15721
```

- 绑定 `127.0.0.1`
- 无 TLS
- 无鉴权/限流
- Provider 配置来自本地 JSON 文件（`~/.pico-relay/providers.json`）
- 一个二进制，绿色免安装

### 6.2 云模式

```
pico-relay start --mode cloud --port 443 --tls-cert ./cert.pfx
```

- 绑定 `0.0.0.0`
- 必须 TLS
- 启用鉴权/限流/计量中间件
- Provider 配置来自本地 JSON（可挂载 volume）
- 支持 Docker 部署

---

## 7. 设计决策（已确认）

1. **包命名**: 共享层 = `PicoNode.AI`，PicoRelay = `PicoNode.Relay`
2. **HTTP 类型**: 统一使用 PicoNode.Http 自有类型（HttpRequest/HttpResponse），不依赖 BCL
3. **Provider 配置存储**: 本地 JSON 文件，AOT 下用 PicoJetson 反序列化
4. **管理 API**: 提供 REST 管理接口（见 section 4.5）
5. **WebSocket**: 当前不支持。Claude Code/Codex 主流用 HTTP + SSE
6. **范围**: 纯后端代理 + 管理 API。GUI 是独立客户端
7. **模型映射**: v1 简单映射（model name → model name），v2 加降级规则
8. **共享层复用**: 格式转换、路由、熔断在 PicoNode.AI 实现，中转站和 PicoAgent 共用

---

## 8. 下一步

1. **用户审阅本 spec** — 确认方向
2. **实现计划** — 调用 writing-plans skill

---

> **版本**: v0.2 | **状态**: 待用户审阅
