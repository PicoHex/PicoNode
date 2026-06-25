# PicoNode Project Status

> 2026-06-25

---

## 模块分层

```
宿主层                   应用层                  协议/传输层
─────────────────────────────────────────────────────────────
PicoWeb                 PicoNode.Web            PicoNode.Http
PicoAgent               PicoNode.Agent          PicoNode
                        PicoNode.AI             PicoNode.Abs
```

---

## 各模块状况

### PicoNode (传输层)

| 组件 | 状态 | 说明 |
|------|:--:|------|
| TCP 传输 | ✅ | TcpNode + 连接管理 |
| UDP 传输 | ✅ | UdpNode + 数据报 |
| TLS | ✅ | SslOptions |
| HTTP/1.1 | ✅ | 完整实现 |
| HTTP/2 | ✅ | HPACK、流控制、GOAWAY |
| WebSocket | ✅ | RFC 6455 |

### PicoNode.Http (协议层)

| 组件 | 状态 | 说明 |
|------|:--:|------|
| HTTP 请求解析 | ✅ | Span-based 零拷贝 |
| HTTP 响应序列化 | ✅ | 含 chunked transfer |
| SSE 流式响应 | ⚠️ | `SendStreamingResponseAsync` 存在，但 Pipe → BodyStream 未完全验证 |
| `BufferStreamResponseAsync` | ⚠️ | HTTP/1.0 回退路径，缓冲整个 BodyStream 再返回 |

### PicoNode.Web (应用层 — Web)

| 组件 | 状态 | 说明 |
|------|:--:|------|
| WebApp | ✅ | 中间件管道 + 路由 |
| AuthMiddleware | ✅ | Bearer token |
| RateLimitMiddleware | ✅ | Token bucket |
| CorsHandler | ✅ | CORS |
| SessionMiddleware | ✅ | Cookie/header |
| SseEndpoint | ✅ | `SseConnection` + `Create` 工厂 |
| StaticFileMiddleware | ✅ | 静态文件 |

### PicoNode.AI (应用层 — AI 通信)

| 组件 | 状态 | 说明 |
|------|:--:|------|
| ILLmClient | ✅ | 接口定义 |
| AnthropicLLmClient | ✅ | Anthropic Messages API |
| OpenAILlmClient | ✅ | OpenAI Chat Completions |
| OpenAISseParser | ✅ | OpenAI SSE `data: [DONE]` |
| IFormatConverter | ⏭ | v1 pass-through 桩 |
| IProviderRouter | ✅ | 按 model 名路由 |
| ICircuitBreaker | ✅ | 三态熔断器 |
| IFailoverService | ✅ | 自动切换 |
| ResilientLLmClient | ✅ | Route → Breaker → Call |
| ModelDiscovery | ✅ | `GET /v1/models` |

### PicoNode.Agent (应用层 — Agent 框架)

| 组件 | 状态 | 说明 |
|------|:--:|------|
| AgentLoop | ✅ | 双重 while 编排 |
| AgentHost | ✅ | 会话管理 + 消息路由 |
| CapabilityRegistry | ✅ | 目录扫描、能力发现 |
| CapabilityRunner | ✅ | 子进程 stdin/stdout JSONL |
| KnowledgeScanner | ✅ | SKILL.md 解析 + system prompt |
| SessionStore | ✅ | JSONL 持久化 |
| `onEvent` 回调 | ✅ | `Func<..., ValueTask>` 异步支持 |

### PicoAgent (宿主 — CLI)

| 功能 | 状态 | 说明 |
|------|:--:|------|
| CLI chat | ✅ | stdin/stdout 流式 text_delta |
| `/model` | ✅ | 切换模型 |
| `/list-models` | ✅ | 从 API 发现模型 |
| `/thinking` | ⚠️ | 接受命令但不生效 |
| `/provider` | ✅ | 切换 Provider |
| `/help` | ✅ | 命令列表 |
| `/save`, `/exit` | ✅ | 会话持久化 |

### PicoAgent (宿主 — serve)

| 端点 | 状态 | 说明 |
|------|:--:|------|
| `POST /session/{id}/message` | ⚠️ | 完整 SSE 事件但不流式（MemoryStream 收集后一次性返回） |
| `GET /session/{id}/events` | ⏭ | 桩 |
| `POST /reload` | ✅ | 重扫 capabilities |

---

## 已知问题

### P0

| 问题 | 模块 | 说明 |
|------|------|------|
| SSE 流式不工作 | PicoAgent.Http + PicoAgent | `Pipe` + `BodyStream` 写后数据不送达。HTTP/1.1 用 `SendStreamingResponseAsync`，但 Pipe writer 数据未被 reader 消费。需排查 CT 取消时机或 Pipe flush 行为 |

### P1

| 问题 | 说明 |
|------|------|
| `/thinking` 不生效 | chat 模式接受命令但未传给 StreamOptions |
| ConfigLoader 重复 | `PicoAgent/ConfigLoader.cs`（namespace PicoNode.Agent）与 `PicoNode.Agent/Config/ConfigLoader.cs` 冲突——已删除但 git 残留 |
| System.Text.Json 在 AOT 下 | ConfigLoader 用 STJ 解析配置——不是性能路径，可接受 |

### P2

| 问题 | 说明 |
|------|------|
| 无内置工具 | 没有 bash/read/write 作为默认 Capability |
| 无包管理 CLI | install/remove/list/update 未实现 |
| 无 TUI | 只有终端 cli |
| serve mode GET /events | 返回桩，不推实时事件 |

---

## 测试

| 项目 | 测试数 | 状态 |
|------|:-----:|:--:|
| PicoNode.AI | 42 | ✅ |
| PicoNode.Agent | 28 | ✅ |
| 其他 | 838 | ✅ |
| **总计** | **908** | ✅ |

---

## 配置文件

```json
// ~/.pico-agent/settings.json
{
  "providers": {
    "anthropic": { "apiKey": "$ANTHROPIC_API_KEY", "baseUrl": "https://api.anthropic.com", "apiFormat": "anthropic" },
    "deepseek":  { "apiKey": "$DEEPSEEK_API_KEY",  "baseUrl": "https://api.deepseek.com/v1",   "apiFormat": "openai" },
    "my-custom": { "apiKey": "$MY_KEY",              "baseUrl": "https://...",                  "apiFormat": "openai" }
  }
}
```

- 无此文件 → 自动创建含所有内置 preset 的模板
- `$VARNAME` → 从环境变量展开
- Provider 名匹配预设 → 自动填充 baseUrl/apiFormat
- Provider 名不匹配 → 必须填 baseUrl + apiFormat

---

## 构建

```bash
# Release
dotnet build -c Release
dotnet test --solution PicoNode.slnx -c Release --no-build

# PicoAgent 可执行文件
src/PicoAgent/bin/Release/net10.0/PicoAgent
```
