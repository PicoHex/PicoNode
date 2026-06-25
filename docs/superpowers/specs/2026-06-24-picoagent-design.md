# PicoAgent — Design Spec

> 基于第一性原理的 AOT-first AI Agent 框架。
>
> **状态**: 修订 v0.3 | **日期**: 2026-06-25 | **依赖**: PicoNode.AI（共享层）

---

## 1. 前置：AI 中转站与本 spec 的关系

[PicoRelay Spec](./2026-06-24-picorelay-design.md) 和 PicoAgent **共享 PicoNode.AI 层**：

### 共享层：PicoNode.AI

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
├── PicoNode.Relay (net10.0) — 中转站
│   └── RelayMiddleware + 云端中间件
│
├── PicoNode.Agent (net10.0) — Agent 框架
│   ├── AgentHost     (Session 管理 + 消息路由)
│   ├── AgentLoop     (双重循环编排)
│   └── CapabilityRunner (子进程通信)
│
└── PicoAgent (net10.0) — 开箱即用宿主
    ├── 交互式 chat (stdin/stdout 流式)
    └── HTTP serve 模式
```

PicoAgent 的 LLM 通信通过 `ResilientLLmClient`（包装 ProviderRouter + CircuitBreaker）调用 `ILLmClient`。和中转站共享 PicoNode.AI。

---

## 2. 第一性原理

从"Agent 是什么"推导出的 5 条原则：

### 2.1 一切皆事件流

Agent 是事件的生产者和消费者。系统的完整状态由事件流的有向图定义：

```
Agent 产生: turn_start → message_start → text_delta × N → tool_call → tool_result → turn_end
Agent 消费: user_input → hook_response → tool_result → model_response
```

### 2.2 能力 = 外部进程

Agent 本身不"做"任何事。所有行动能力来自外部子进程。

```
工具 = 能力 (LLM 驱动调用)
钩子 = 能力 (生命周期驱动调用)

从 Agent 的角度：不区分。统一抽象：
  stdin 进 JSON → stdout 出 JSON
```

### 2.3 知识 = 文件

Agent 本身不"知道"任何事。所有知识来自文件系统。

```
Skill = SKILL.md → 注入 system prompt
Prompt 模板 = .md 文件 → 展开为消息
配置 = config.json → 控制行为
```

### 2.4 协议 = JSON Lines + SSE

```
Agent ↔ 能力: stdin/stdout JSON Lines (每行一个 JSON 消息)
Agent ↔ LLM:    HTTP SSE 流
Agent ↔ 用户:   SSE / WebSocket / stdout
```

没有共享内存、没有动态加载、没有插件 API。只有进程间通信协议。

### 2.5 生态 = 约定优于配置

```
~/.pico-agent/
├── capabilities/         ← Agent 扫描 → 自动注册
├── knowledge/            ← Agent 扫描 → 自动注入
└── config.json           ← 覆盖约定
```

---

## 3. 核心架构

### 3.0 Sidecar 模式

> **实现约束**：PicoJetson 不支持 `record` / `init` 属性。所有类型使用 `class` + `{ get; set; }`。

> **Phase 1 更新**：TUI 未实现。当前 `pico-agent`（无参数）= 交互式 chat（stdin/stdout），`pico-agent serve` = HTTP 模式。

Agent 核心分为两个独立进程：

```
┌─────────────────────────┐        SSE 事件流       ┌──────────────────┐
│      Agent Host          │ ◄──────────────────────► │   TUI 客户端      │
│   (pico-agent serve)     │     HTTP + JSON Lines     │  (pico-agent      │
│                          │                           │   chat)           │
│  ┌─────────────────────┐ │                          │                   │
│  │   事件循环           │ │   session_start          │  • 纯渲染         │
│  │   while(外层) {     │ │   text_delta × N        │  • 用户输入       │
│  │     while(内层) {   │ │   tool_call             │  • /reload 触发   │
│  │       观察→思考→行动 │ │   turn_end              │                   │
│  │     }              │ │   agent_idle            │  可关闭 TUI       │
│  │   }                │ │                          │  Agent 继续运行   │
│  └─────────────────────┘ │                          │                   │
│                          │                          │  可重新连接       │
│  ┌──────────┐ ┌────────┐ │                          │  恢复事件流       │
│  │LLM 客户端 │ │Capability│                          │                   │
│  │(HTTP)    │ │Runner  │ │                          │                   │
│  └──────────┘ └────────┘ │                          │                   │
│       │           │       │                          │                   │
└───────┼───────────┼───────┘                          └──────────────────┘
        │           │
        ▼           ▼
   LLM API      子进程
 (Anthropic/   (js/py/sh/...)
  OpenAI)      stdin/stdout
```

**为什么 Sidecar？**
- Agent Host 是长生命周期进程，管理会话、事件循环、子进程
- TUI 是消费者——可以随时连接/断开/替换（TUI、GUI、Web UI 都是消费同一个事件流）
- Host 用 PicoDI 管理 LLM 客户端等，TUI 不需要 DI
- Agent Host 更新 → TUI 不受影响。TUI 更新 → Agent 继续运行。

### 3.1 事件循环（双重 while）

```
外层 while: 处理 follow-up 队列
  内层 while: 处理工具调用链
    stream = await llm.StreamAsync(context)
    await foreach (event in stream)
      emit(event)                         // 实时推送
      如果是 toolCall → 执行 → 检查拦截
      如果是 stop → 检查 followUp 队列
```

### 3.2 LLM 客户端

```
stream(model, context) → IAsyncEnumerable<AssistantMessageEvent>
complete(model, context) → ValueTask<AssistantMessage>  // 语法糖

底层: HttpClient → SSE 解析 → EventStream
```

使用 `PicoNode.AI.ILLmClient`。类型体系使用 PicoNode.Http 自有类型，不依赖 BCL。

### 3.3 Capability Runner

```csharp
// 统一的能力抽象——不区分工具和钩子
public interface ICapability
{
    string Name { get; }
    // Handler 命令的相对路径基准 = Capability 根目录
    // （manifest.json 所在目录，即 packages/<host>/<path>/）
    // 例: "node tools/search.js" → 在 Capability 根目录执行
    string Handler { get; }
    IReadOnlyList<CapabilityTrigger> Triggers { get; }
    LifecycleKind Lifecycle { get; }               // Persistent | Oneshot
    string? SchemaPath { get; }                    // JSON Schema 文件路径
    int Priority { get; }                          // 执行优先级
}

// Trigger 定义何时调用
// Note: PicoJetson does not support record/init; use class with get; set;
public sealed class CapabilityTrigger
{
    public TriggerKind Kind { get; set; }          // OnToolCall | OnMessageStart | OnTurnStart | ...
    public string? ToolName { get; set; }          // 仅 OnToolCall 时需要，null = 所有工具
}

public enum TriggerKind
{
    OnToolCall, OnMessageStart, OnMessageEnd,
    OnCompaction, OnPreCompact,
    OnModelPreRequest, OnModelPostResponse, OnModelError,
    OnUserInput, OnBash,
    OnSessionStart, OnSessionEnd,
    OnTurnStart, OnTurnEnd,
    OnAgentStart, OnAgentEnd
}

public enum LifecycleKind { Persistent, Oneshot }
```

**执行流程**:

```csharp
// Agent 内部
async Task<JsonElement> ExecuteCapabilityAsync(
    ICapability cap,
    string contextKind,  // "tool_call" | "hook"
    JsonElement input,
    CancellationToken ct)
{
    // 持久进程：启动时已 spawn，复用
    // Oneshot：每次 spawn 新进程
    var process = GetOrSpawnProcess(cap);

    // 写一行 JSON（使用 PicoJetson）
    // 对于单行写入，SerializeToUtf8Bytes + \n 即可
    // 批量场景可用 SerializeLines<T>()
    var request = new { kind = contextKind, ...input };
    var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(request);
    await process.StdIn.BaseStream.WriteAsync(json, ct);
    await process.StdIn.BaseStream.WriteAsync(NewLineBytes, ct);
    await process.StdIn.BaseStream.FlushAsync(ct);

    // 读一行 JSON（使用 PicoJetson 异步 JSONL 流）
    await foreach (var response in PicoJetson.JsonSerializer
        .DeserializeAsyncEnumerable<JsonElement>(
            process.StdOut.BaseStream, topLevelValues: true, ct: ct))
    {
        return response;
    }
}
```

**输入格式（Agent → 子进程）**:

```json
// 作为工具被 LLM 调用
{"kind":"tool_call","toolCallId":"abc","toolName":"bash","args":{"command":"ls"}}

// 作为钩子被生命周期触发
{"kind":"hook","event":"on_tool_call","toolName":"bash","args":{"command":"rm -rf /"}}

// Schema 描述请求（启动时）
{"kind":"describe"}

// 健康检查
{"kind":"ping"}
```

**输出格式（子进程 → Agent）**：由输入 `kind` 决定 Agent 如何解释返回值。

- `kind == "tool_call"` → 返回工具结果：`{"content":[...], "details":{}}`
- `kind == "hook"` → 返回钩子指令：`{"action":"block","reason":"..."}` / `{"action":"allow"}` / `{"action":"modify",...}`
- `kind == "describe"` → 返回 JSON Schema
- `kind == "ping"` → 返回 `{"kind":"pong"}`

子进程本身检查输入 `kind` 字段来切换行为模式。Agent 不区分工具和钩子——只传 contextKind，读返回值。

### 3.4 工具 Schema 发现

LLM 需要知道工具的参数 Schema 才能生成 tool call。

**发现顺序**：

1. Manifest 指定 `schema` 路径 → Agent 读取 JSON Schema 文件
2. Manifest 未指定 → Agent 启动时向子进程发送 `{"kind":"describe"}` → 子进程返回 JSON Schema
3. 两者都没有 → 工具注册为 schema-free（LLM 可以调用，参数不经 Agent 验证）

```json
// manifest.json
{
  "capabilities": [
    {
      "name": "bash",
      "handler": "bash tools/runner.sh",
      "triggerKinds": [0, 9],
      "triggerToolNames": ["bash", null],
      "schema": "tools/bash.schema.json",
      "lifecycle": "oneshot",
      "priority": 50,
      "description": "Execute shell commands"
    }
  ]
}
```

```json
// tools/bash.schema.json
{
  "type": "object",
  "properties": {
    "command": { "type": "string", "description": "The shell command to execute" },
    "workdir": { "type": "string", "description": "Working directory" }
  },
  "required": ["command"]
}
```

### 3.5 钩子执行顺序与短路

多个 Capability 注册同一 Trigger 时：

```csharp
// Agent 按 Priority 排序，从低到高执行
// 安全拦截钩子 Priority = 0（最先跑）
// 日志/观察钩子 Priority = 100（最后跑）

foreach (var cap in GetCapabilities(trigger).OrderBy(c => c.Priority))
{
    var result = await ExecuteCapabilityAsync(cap, "hook", input, ct);

    // 短路：如果钩子返回 block，后续钩子不再执行
    if (result.Action == "block")
    {
        LogHookBlock(cap, result.Reason);
        return result;  // 不继续
    }

    // modify：将修改后的结果传递给下一个钩子
    if (result.Action == "modify")
        input = result.ModifiedInput;

    // allow / no-return：继续下一个钩子
}
```

**默认 Priority**：

| Trigger | 默认 Priority | 原因 |
|---------|:---:|------|
| `OnBash` | 0 | 安全敏感，应先跑 |
| `OnToolCall` | 50 | 中等——大多数钩子是观察 |
| `OnMessageStart` | 50 | 中等 |
| 其他 | 50 | 中等 |

用户可在 manifest 中覆盖：`"priority": 0`。

### 3.6 持久钩子生命周期

```csharp
// Persistent 钩子的进程管理

// Spawn:  Agent 启动时（不是首次触发时）
// Kill:   Agent 停止时 (SIGTERM → 5s → SIGKILL)
// Health: Agent 每 30s 发送 {"kind":"ping"} → 期望 {"kind":"pong"}
//         3 次连续无响应 → 标记 unhealthy，尝试重启
// Crash:  自动重启，最多 3 次，然后标记 unhealthy
//         标记 unhealthy 后：本次事件跳过该钩子，记录日志
// Timeout: 单次调用超过 30s → 标记 timed_out，跳过本次，不 block 事件循环

class CapabilityProcessManager
{
    async Task<JsonElement> SendAsync(
        ICapability cap, JsonElement input, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, timeoutCts.Token);

        try
        {
            await process.StdIn.WriteLineAsync(input, linked.Token);
            var line = await process.StdOut.ReadLineAsync(linked.Token);
            return ParseResponse(line);
        }
        catch (OperationCanceledException)
        {
            // Timeout — 跳过本次，不 block 事件循环
            MarkTimedOut(cap);
            return AllowResult; // 空响应 = allow
        }
        catch when process crashed
        {
            if (RestartCount < 3) { RestartProcess(cap); throw; }
            MarkUnhealthy(cap);
            return AllowResult; // 不 block 事件循环
        }
    }
}
```

### 3.7 TUI 重连协议

TUI 通过 HTTP 连接 Agent Host：

```
初次连接:
  GET /session/{id}/events
    → SSE 流，从当前时刻开始

重连:
  GET /session/{id}/events?since={eventIndex}
    → SSE 流，从 eventIndex 之后的事件开始重放
    → 然后继续实时事件

断连检测:
  Agent Host 每 10s 向 TUI 发 {"type":"heartbeat"}
  TUI 10s 没收到 → 断开
  Agent Host 30s 没收到 TUI 请求 → 清理连接状态
```

TUI 本地维护 `lastEventIndex`，重连时传入 `since`。

### 3.8 热加载

不需要特殊机制——所有可变的东西都在 Agent 编译产物之外：

```
pico-agent.exe          ← AOT 编译，不可变
    │
    ├── 子进程          ← git pull → 新代码生效，Agent 重新 spawn
    ├── SKILL.md       ← git pull → 新内容生效，Agent 重新读取
    └── config.json    ← 编辑 → 信号 Agent 重读（HTTP POST /reload）
```

命令：
```
pico-agent reload            → 重扫 capabilities + knowledge + config
pico-agent reload capabilities → 仅重扫 capabilities
pico-agent reload knowledge    → 仅重扫 knowledge
```

---

## 4. 知识系统

### 4.1 System Prompt 构建

Agent 启动时扫描 `~/.pico-agent/knowledge/`，按字母序收集所有 SKILL.md：

```xml
<!-- 注入 system prompt 末尾 -->
<available_skills>
  <skill>
    <name>pdf-tools</name>
    <description>Extract text and tables from PDF files, fill PDF forms,
    and merge PDFs. Use when working with PDF documents.</description>
  </skill>
  <skill>
    <name>web-search</name>
    <description>Search the web using Brave Search API. Use for finding
    documentation, facts, or any web content.</description>
  </skill>
</available_skills>
```

**渐进式披露**：只有 name + description 始终在 context 中。完整 SKILL.md 内容由 Agent 在需要时通过 `read` 工具加载。

**大小限制**：所有 skill 描述总大小不超过 4096 tokens。超过时按字母序截断，警告用户。

### 4.2 Compaction 策略

当 context 超过配置的 token 阈值时触发。

```
1. 计算当前 context 大小
2. 如果超过阈值 → 触发 OnPreCompact 事件（钩子可修改待压缩消息）
3. v1 策略：截断到最近 N 条消息（默认保留最后 20 条）
4. 触发 OnCompaction 事件（钩子可完全替换压缩结果）
5. 替换 context
```

v2 策略：LLM 总结旧消息 → `{"role":"compaction","summary":"..."}`。

---

## 5. 会话持久化

### 5.1 JSONL 格式

参考 pi agent 的 session format。使用 PicoJetson JSONL API：

```csharp
// 写会话
var jsonl = PicoJetson.JsonSerializer.SerializeLines(entries);
await File.WriteAllBytesAsync(path, jsonl, ct);

// 读会话（同步批量——会话通常几百 KB，一次性加载）
var bytes = await File.ReadAllBytesAsync(path, ct);
var entries = PicoJetson.JsonSerializer.DeserializeLines<Message>(bytes);

// 或流式读取大文件
using var stream = File.OpenRead(path);
await foreach (var entry in PicoJetson.JsonSerializer
    .DeserializeAsyncEnumerable<Message>(stream, ct: ct))
{
    // 逐行处理
}
```

每行一个 JSON 对象：

```jsonl
{"role":"user","content":"hello","timestamp":1719000000000}
{"role":"assistant","contentBlocks":[{"type":"text","text":"I'll read the file"},{"type":"tool_call","id":"abc","name":"read","arguments":{"path":"/foo"}}],"model":"claude-sonnet-4","provider":"anthropic","api":0,"usage":{"inputTokens":10,"outputTokens":5},"stopReason":"tool_use","timestamp":1719000001000}
{"role":"toolResult","toolCallId":"abc","toolName":"read","contentBlocks":[{"type":"text","text":"file contents..."}],"isError":false,"timestamp":1719000002000}
{"role":"assistant","contentBlocks":[{"type":"text","text":"I read the file."}],"model":"claude-sonnet-4","usage":{"inputTokens":20,"outputTokens":8},"stopReason":"stop","timestamp":1719000003000}
```

**Entry 类型**（使用 PicoNode.AI.Message）：

| Role | 说明 | 主要字段 |
|------|------|------|
| `user` | 用户消息 | `Content` |
| `assistant` | LLM 回复 | `ContentBlocks`, `Model`, `Provider`, `Api`, `Usage`, `StopReason` |
| `toolResult` | 工具执行结果 | `ToolCallId`, `ToolName`, `ContentBlocks`, `IsError` |

**存储位置**：`~/.pico-agent/sessions/<session-id>.jsonl`

---

## 6. 包管理体系

### 6.1 安装源

```
pico-agent install git:github.com/user/my-pack       # 最常用
pico-agent install https://github.com/user/my-pack
pico-agent install /local/path
```

### 6.2 安装流程

```
1. git clone → ~/.pico-agent/packages/<host>/<path>/
2. 读 manifest.json
3. 如果有 "setup": "npm install" → 执行
4. symbollink capabilities → ~/.pico-agent/capabilities/<name>/
5. symbollink knowledge → ~/.pico-agent/knowledge/<name>/
6. Agent 下次 reload 自动发现
```

### 6.3 manifest.json 规范

```json
{
  "name": "my-pack",
  "version": "1.0.0",
  "capabilities": [
    {
      "name": "search-web",
      "handler": "node tools/search.js",
      "triggerKinds": [0],
      "triggerToolNames": ["search-web"],
      "lifecycle": "oneshot",
      "priority": 50,
      "description": "Search the web using Brave API",
      "schema": "tools/search.schema.json"
    },
    {
      "name": "security-guard",
      "handler": "python hooks/guard.py",
      "triggerKinds": [0, 9],
      "triggerToolNames": [null, null],
      "lifecycle": "persistent",
      "priority": 0
    }
  ],
  "knowledge": ["skills/", "prompts/"],
  "setup": "npm install",
  "requires": {
    "runtime": "node >= 18"
  }
}
```

### 6.4 工具语言无关

| 语言 | handler 示例 | 运行时要求 |
|------|-------------|-----------|
| JS/TS | `node tools/search.js` | Node.js |
| Python | `python tools/extract.py` | Python |
| Shell | `bash tools/snapshot.sh` | bash |
| .NET AOT | `./tools/scanner` | 零依赖 |
| 任何 | `./tools/my-tool` | 你自己管理 |

Agent 不关心语言——它只 spawn 进程，写 stdin，读 stdout。

---

## 7. 事件系统

### 7.0 事件来源

Agent Host 的事件有三个来源：

```
用户 (TUI/API) ──► POST /session/{id}/message ──► Agent Host ──► OnUserInput
                    POST /session/{id}/bash     ──► Agent Host ──► OnBash

LLM 响应          ──► ILLmClient.StreamAsync    ──► Agent Host ──► OnMessageStart/End
                                                                    OnModelPreRequest/PostResponse/Error

Agent 生命周期     ──► 内部状态机               ──► Agent Host ──► OnSessionStart/End
                                                                    OnTurnStart/End
                                                                    OnAgentStart/End
                                                                    OnToolCall
                                                                    OnCompaction/PreCompact
```

### 7.1 生命周期事件（通知型，不等待返回值）

| 事件 | 触发时机 |
|------|---------|
| `OnSessionStart` | 会话开始 |
| `OnSessionEnd` | 会话结束 |
| `OnTurnStart` | 每个 turn 开始 |
| `OnTurnEnd` | 每个 turn 结束 |
| `OnAgentStart` | Agent 开始处理 |
| `OnAgentEnd` | Agent 处理完成 |

### 7.2 拦截型事件（等待返回值）

| 事件 | 触发时机 | 拦截能力 |
|------|---------|---------|
| `OnToolCall` | LLM 要求调用工具 | block / modify / allow |
| `OnMessageStart` | 消息发送给 LLM 前 | inject 额外消息 |
| `OnMessageEnd` | LLM 返回消息后 | 纯观察 |
| `OnCompaction` | 上下文压缩时 | replace 压缩结果 |
| `OnPreCompact` | 压缩前 | modify 待压缩消息 |
| `OnModelPreRequest` | LLM 调用前 | modify 请求 |
| `OnModelPostResponse` | LLM 返回后 | 纯观察 |
| `OnModelError` | LLM 调用失败 | retry / failover / abort |
| `OnUserInput` | 用户输入 | modify / block |
| `OnBash` | 用户执行命令 | block / allow |

### 7.3 钩子返回值约束

| 钩子 | 可返回 |
|------|-------|
| `OnToolCall` | `{"action":"block","reason":"..."}` / `{"action":"allow"}` / `{"action":"modify","args":{...}}` |
| `OnMessageStart` | `{"action":"inject","messages":[...]}` / 无返回 |
| `OnMessageEnd` | 无返回（纯观察） |
| `OnCompaction` | `{"action":"replace","messages":[...]}` |
| `OnPreCompact` | `{"action":"modify","messages":[...]}` |
| `OnModelPreRequest` | `{"action":"modify","request":{...}}` |
| `OnModelPostResponse` | 无返回（纯观察） |
| `OnModelError` | `{"action":"retry"}` / `"failover"` / `"abort"` |
| `OnUserInput` | `{"action":"modify","text":"..."}` / `{"action":"block"}` |
| `OnBash` | `{"action":"block","reason":"..."}` / `{"action":"allow"}` |

---

## 8. 目录约定

```
~/.pico-agent/
├── capabilities/                 # Agent 扫描此目录
│   ├── github.com/user/shell/   # → symlink → packages/...
│   └── github.com/user/guard/   # → symlink → packages/...
│
├── knowledge/                    # Agent 扫描此目录
│   ├── github.com/user/pdf/     # → symlink → packages/...
│   └── local/my-skill/          # 用户自建的 skill
│       └── SKILL.md
│
├── packages/                     # git clone 原始位置
│   └── github.com/
│       └── user/
│           └── my-pack/
│               ├── manifest.json
│               ├── tools/
│               ├── hooks/
│               └── skills/
│
├── sessions/                     # 会话持久化
│   └── <session-id>.jsonl
│
└── config.json                   # 全局配置
```

---

## 9. CLI 命令

```
pico-agent                          → 交互式 chat (stdin/stdout 流式)
pico-agent serve --port 8080        → API 服务模式 (Host 进程)

// Phase 2: 运行时命令 (在 chat 模式内使用)
/model <name>     → 切换模型（自动路由到对应 Provider）
/thinking <level> → 切换思考深度（minimal/low/medium/high/xhigh）
/list-models      → 查询并显示所有 Provider 的可用模型
/provider <name>  → 切换默认 Provider

// 未来
pico-agent install git:github.com/user/pack
pico-agent install /local/path
pico-agent remove my-pack
pico-agent list
pico-agent update [--all | <name>]

pico-agent reload [capabilities | knowledge | config]
pico-agent status                   → 当前状态
```

---

## 10. AOT 约束

| 约束 | 影响 | 解决方案 |
|------|------|---------|
| 不可动态加载 .dll | 不能像 pi 那样 import .ts | 子进程模型天然绕过 |
| 不可 Assembly.Load | 不能运行时加载 C# 插件 | 同上 |
| 不可反射 | PicoJetson 替代 System.Text.Json | ✅ 源生成器 |
| 不可表达式树 | PicoDI 替代 Microsoft.Extensions.DI | ✅ 编译期工厂 |
| HttpClient 可行 | AOT 下 HTTP 调用正常 | ✅ |
| Process.Start 可行 | AOT 下子进程正常 | ✅ |

---

## 11. 与 pi 的设计对比

| 维度 | pi | PicoAgent |
|------|----|-----------|
| 运行时 | Node.js | .NET AOT |
| 扩展语言 | TypeScript（同进程） | 任何语言（子进程） |
| 热加载 | /reload (in-process) | 子进程重启 + 文件重读 |
| 包管理 | npm + git | git + 可选 setup |
| 依赖隔离 | node_modules per package | 进程隔离 |
| 分发 | npm 包 | git repo |
| 工具类型 | TypeScript 函数 | 任何可执行程序 |

---

## 12. 设计决策（已确认）

1. **Repo 命名**: **PicoAgent**。Pico.Bot 是遗留 repo，不关注。
2. **共享层**: `PicoNode.AI` NuGet 包。中转站和 Agent 共用格式转换、路由、熔断、LLM 客户端。
3. **会话持久化**: JSONL（见 section 5.1），参考 pi agent session format。
4. **版本管理**: 参考 pi packages——git repo + 固定的 tag/commit ref。`update` 做 git pull。
5. **Sidecar 模式**: Agent Host + TUI 独立进程，SSE 事件流通信。TUI 可随时连接/断开/替换。
6. **TUI**: 同一 Repo，独立于核心。消费 SSE 事件流。
7. **Agent Host LLM 客户端**: 通过 `ResilientLLmClient`（ProviderRouter + CircuitBreaker + ILLmClient）。AgentLoop 只看到 ILLmClient 接口。
8. **HTTP 类型**: 统一使用 PicoNode.Http 自有类型（HttpRequest/HttpResponse），不依赖 BCL。
9. **工具 Schema**: manifest 声明优先，子进程 `describe` 兜底，schema-free 最低。

---

## 13. Provider 配置与模型发现

> v0.3 新增——2026-06-25

### 13.1 设计原则

- **配置只写 apiKey**——`url` 和 `apiFormat` 从内置 preset 自动获取
- **Model 列表从 API 获取**——`GET /v1/models` 动态查询。thinking 支持范围来自内置映射（Anthropic 不通过 API 返回）
- **thinkingLevel 运行时选择**——`/thinking` 命令切换
- **Provider 切换不感知**——`/model` 命令背后自动路由

### 13.2 配置文件

```json
// ~/.pico-agent/config.json
{
  "providers": {
    "anthropic": { "apiKey": "$ANTHROPIC_API_KEY" },
    "deepseek":  { "apiKey": "$DEEPSEEK_API_KEY" }
  }
}
```

`$XXX` 从环境变量取值。无此文件 → 创建模板 + 提示编辑后重试。

### 13.3 内置 Provider Preset

| name | apiFormat | baseUrl |
|------|-----------|--------|
| anthropic | anthropic | api.anthropic.com |
| openai | openai | api.openai.com/v1 |
| deepseek | openai | api.deepseek.com/v1 |
| kimi | openai | api.moonshot.cn/v1 |
| glm | openai | open.bigmodel.cn/api/paas/v4 |
| groq | openai | api.groq.com/openai/v1 |
| custom | 用户填 | 用户填 |

用户不需要写 `baseUrl` 和 `apiFormat`——preset 自动提供。

### 13.4 模型发现

所有主流 Provider 支持 `GET /v1/models`。PicoAgent 启动时调用每个已配置 Provider 的此端点，获取可用模型列表。

- Anthropic `/v1/models` 返回 `id`, `display_name`, `created_at`。thinking 支持范围来自文档（非 API），PicoNode.AI 内置静态映射。
- OpenAI `/v1/models` 返回完整模型元数据
- OpenAI 兼容模型附带 `reasoning` 支持范围
- 模型列表缓存在内存，`/list-models` 重新查询

### 13.5 运行时命令

```
/help           → 显示所有命令
/list-models     → 显示所有 Provider 的可用模型
/model <name>    → 切换模型（自动选择 Provider）
/thinking <lvl>  → 切换思考深度
/provider <name> → 切换到指定 Provider，自动恢复上次模型
/save           → 保存 session
/exit           → 退出（自动保存）

例：
  /model gpt-4o        → ProviderRouter → openai → gpt-4o
  /model deepseek-chat → ProviderRouter → deepseek → deepseek-chat
  /thinking medium     → [thinkingLevel: medium]
  /provider openai     → [Switched to openai, model: gpt-4o]
```

`/model` 切换时系统自动查找包含该模型的 Provider。`/provider` 切换时恢复该 Provider 上次使用的模型。

### 13.6 ResilientLLmClient

包装 ProviderRouter + CircuitBreaker + FailoverService，对外暴露 `ILLmClient`。AgentLoop 不感知多 Provider。

```
AgentLoop → ResilientLLmClient → ProviderRouter.Select(provider)
                                → CircuitBreaker.TryAcquire()
                                → OpenAILlmClient | AnthropicLLmClient
```

### 13.7 实现清单

详见 [Phase 2 Implementation Plan](../plans/2026-06-25-picoagent-phase2.md)。

---

> **版本**: v0.3 | **状态**: 待用户审阅
