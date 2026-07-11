# Agent API JSON Contract

PicoAgent 前后端之间的 JSON 约定。单一真相来源（single source of truth）。

> **策略说明**：JSON 序列化分层策略见 `src/Agent/JSON_ARCHITECTURE.md`。

---

## 1. SSE 事件流

**端点**：`POST /api/session/{id}/message`
**Content-Type**：`text/event-stream`
**帧格式**：`data: {json}\n\n`

### 1.1 事件类型总览

| type | content | toolCallId | toolName | 说明 |
|------|:---:|:---:|:---:|------|
| `start` | `""` | — | — | 流开始标记 |
| `thinking` | `""` 或文本 | — | — | LLM 思考过程 |
| `delta` | `""` 或文本 | — | — | 增量文本输出 |
| `tool_call_start` | `""` | index (int) | — | 工具调用开始 |
| `tool_call_delta` | `""` 或参数片段 | index (int) | — | 工具参数增量 |
| `tool_call_end` | JSON 参数字符串 | index (int) | 工具名 | 工具调用完成 |
| `tool_result` | 结果文本 | ContentBlock Guid | 工具名 | 工具执行结果 |
| `done` | — | — | — | 当前 turn 完成 |
| `error` | 错误信息 | — | — | 错误 |

### 1.2 字段规则

**`content`**
- **始终存在**（`done` 除外）。无内容时为 `""`，不含 `null`。
- `tool_call_end` 的 content 是序列化后的 JSON 参数字符串，如 `"{\"path\":\"/tmp\"}"`。
- `tool_call_delta` 的 content 是单个字符或短片段，需要前端累积拼接。

**`toolCallId` / `toolName`**
- `done`、`start`、`thinking`、`delta`、`error`：**不包含**这些字段。前端读取为 `undefined` 视为不存在。
- `tool_call_start`、`tool_call_delta`、`tool_call_end`：`toolCallId` 是当前消息中的调用序号（从 0 开始计数）。当 LLM 在一次回复中调用多个工具时，序号递增。注意：**每轮对话重新从 0 开始**。
- `tool_result`：`toolCallId` 是 Agent 内部 `ContentBlock` 的 Guid 字符串（格式 `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`）。它独立于流序号。**前端应使用 `toolName` 而非 `toolCallId` 做跨事件匹配**，因为 `toolCallId` 在 `tool_result` 中使用的是 Guuid 而非 index。

### 1.3 每种事件的精确格式

```jsonc
// start
{"type":"start","content":""}

// thinking（空内容）
{"type":"thinking","content":""}

// thinking（有内容）
{"type":"thinking","content":"The user is asking"}

// delta（空内容）
{"type":"delta","content":""}

// delta（有内容）
{"type":"delta","content":"你好，我是 PicoAgent"}

// tool_call_start
{"type":"tool_call_start","content":"","toolCallId":"0"}

// tool_call_delta（参数流式传输）
{"type":"tool_call_delta","content":"{","toolCallId":"0"}
{"type":"tool_call_delta","content":"\"path\"","toolCallId":"0"}

// tool_call_end
{"type":"tool_call_end","content":"{\"path\":\"/tmp/test.txt\"}","toolCallId":"0","toolName":"read"}

// tool_result
{"type":"tool_result","content":"Hello World\n","toolCallId":"019be0a4-1234-5678-9abc-def012345678","toolName":"read"}

// done
{"type":"done"}

// error
{"type":"error","content":"Something went wrong"}
```

---

## 2. HTTP 响应端点

所有响应 `Content-Type: application/json; charset=utf-8`。
所有字段均为 `camelCase`。

### 2.1 `GET /api/health`

```jsonc
{
  "status":  "ok",                       // string, 固定 "ok"
  "model":   "deepseek-chat",            // string, 当前模型 ID
  "provider":"deepseek"                  // string, 当前提供商名
}
```

### 2.2 `GET /api/models`

```jsonc
[
  {"id":"gpt-4o",       "ownedBy":"openai"},       // 模型 ID 和归属
  {"id":"claude-3-5",   "ownedBy":"anthropic"}
]
```
空列表时为 `[]`。
前端在请求失败（非 2xx）时显示 "Failed"；列表为空时显示 "No models"。

### 2.3 `GET /api/config/status`

```jsonc
{
  "configured":      true,               // bool, 是否有有效提供商
  "model":           "deepseek-chat",    // string
  "provider":        "deepseek",         // string
  "providers":       ["deepseek"],       // string[]
  "thinkingEnabled": true,               // bool
  "thinkingLevel":   "xhigh",            // string, minimal|low|medium|high|xhigh
  "maxTokens":        4096               // int
}
```

### 2.4 `GET /api/config/providers`

```jsonc
[
  {"name":"openai",    "label":"OpenAI",    "baseUrl":"https://api.openai.com/v1",       "apiFormat":"openai"},
  {"name":"anthropic", "label":"Anthropic", "baseUrl":"https://api.anthropic.com",        "apiFormat":"anthropic"},
  {"name":"deepseek",  "label":"DeepSeek",  "baseUrl":"https://api.deepseek.com/v1",      "apiFormat":"openai"},
  {"name":"groq",      "label":"Groq",      "baseUrl":"https://api.groq.com/openai/v1",   "apiFormat":"openai"},
  {"name":"ollama",    "label":"Ollama",     "baseUrl":"http://localhost:11434/v1",        "apiFormat":"openai"}
]
```

### 2.5 `POST /api/config/validate`

Request：
```jsonc
{"provider":"openai", "apiKey":"sk-...", "baseUrl":"https://api.openai.com/v1"}
```

Response（成功）：
```jsonc
[{"id":"gpt-4o","ownedBy":"openai"}, ...]
```

Response（失败）：
```jsonc
{"error":"No models found. Check your API key or base URL."}
```

### 2.6 `GET /api/system-prompt`

```jsonc
{"prompt":"You are PicoAgent, an AI coding assistant..."}
```

### 2.7 `POST /api/session/{id}/compact`

```jsonc
{"compressedCount":0, "summary":null, "tokensSaved":0}
```

### 2.8 `GET /api/sessions`

```jsonc
{"sessions":["default"]}
```

### 2.9 `GET /api/session/{id}/messages`

```jsonc
[]
```

### 2.10 通用响应

```jsonc
// OK
{"status":"ok"}

// Error
{"error":"message"}
```

---

## 3. LLM 请求体

本节描述 PicoAgent 向 LLM API 发送的请求格式。

### 3.1 OpenAI Chat Completions

```jsonc
{
  "model":            "gpt-4o",          // string
  "max_tokens":       4096,              // int
  "stream":           true,              // bool, 固定 true
  "messages": [
    {"role":"system",  "content":"You are PicoAgent..."},
    {"role":"user",    "content":"Hello"},
    {"role":"assistant","content":null, "tool_calls":[
      {"id":"call_123","type":"function","function":{"name":"read","arguments":"{\"path\":\"/tmp\"}"}}
    ]},
    {"role":"tool",    "tool_call_id":"call_123","content":"file contents"}
  ],
  "thinking":          {"type":"enabled"},     // DeepSeek 思考模式，optional
  "reasoning_effort":  "high",                 // low|medium|high, optional
  "tools": [
    {"type":"function","function":{
      "name":"read",
      "description":"Read file contents",
      "parameters":{"type":"object","properties":{"path":{"type":"string"}}}
                          // ← 注意：此处为 JSON 对象，非字符串
    }}
  ],
  "tool_choice":"auto"                        // 有 tools 时固定 "auto"
}
```

**关键约定**：
- `parameters` 字段是 **JSON 对象**，不是 JSON 字符串。后端在序列化后手动注入此段（PicoJetson 无法处理 `string` 类型中存储的 raw JSON）。
- `tool_calls[].function.arguments` 是 **JSON 字符串**（`"{\"path\":...}"`），不是 JSON 对象。OpenAI API 将此字段定义为 string 类型。
- `role: "tool"` 消息的 `tool_call_id` 指向 assistant 消息中的 `tool_calls[].id`。

### 3.2 Anthropic Messages

```jsonc
{
  "model":       "claude-sonnet-4-20250514",   // string
  "max_tokens":  4096,                          // int
  "stream":      true,                          // bool, 固定 true
  "messages": [
    {"role":"user","content":"Hello"},
    {"role":"assistant","content":[
      {"type":"text","text":"..."},
      {"type":"tool_use","id":"toolu_xxx","name":"read","input":{"path":"/tmp"}}
    ]},
    {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_xxx","content":"...","is_error":false}]}
  ],
  "system":      "You are PicoAgent...",        // string, optional
  "thinking":    {"type":"enabled","budget_tokens":16000},  // optional
  "tools": [
    {"name":"read","description":"Read file contents","input_schema":{"type":"object","properties":{...}}}
                                               // ← 同样：input_schema 为 JSON 对象
  ]
}
```

**关键约定**：
- Anthropic 的 `content` 字段类型多变：user 为 **string**，assistant 为 **ContentBlock 数组**，tool_result 包装在 user 消息的 `tool_result` 类型块中。
- `input_schema` 和 `input` 都是 **JSON 对象**（非字符串）。
- 无 system 角色消息——system prompt 作为顶层 `system` 字段发送。
- 由于 content 的 polymorphic 特性，Anthropic 请求体**全部手写**，不使用 PicoJetson 序列化。

---

## 4. 字段语义约定

### 4.1 null / `""` / 省略

| 场景 | 规则 | 原因 |
|------|------|------|
| SSE content 无内容 | `""` | 前端 JS `event.content || ""` 避免 `undefined` |
| SSE toolCallId/toolName 不适用 | 省略整个字段 | 前端 `event.toolCallId` 为 `undefined` 判断不存在 |
| SS done 事件 | 仅 `{"type":"done"}` | 旧格式兼容 |
| HTTP 响应字段无值 | `""` 或 `null`（PicoJetson 默认） | HTTP 响应不依赖字段存在性判断 |

### 4.2 ID 格式

| 场景 | 格式 | 示例 | 唯一性 |
|------|------|------|--------|
| SSE `tool_call_start/end` 的 toolCallId | 当前消息中的整数序号 | `"0"`, `"1"` | 仅当前 turn 内唯一 |
| SSE `tool_result` 的 toolCallId | Guid（36 字符 hex） | `"019be0a4-..."` | 全局唯一（`Guid.CreateVersion7()`） |
| LLM 请求中 assistant 的 `tool_calls[].id` | LLM 提供商返回的 ID | `"call_123"` (OpenAI) / `"toolu_xxx"` (Anthropic) | 提供商保证 |
| 前端展示 ID | 使用 `toolName` 区分工具 | `"read"`, `"bash"` | 前端需按 toolName 分组 |

> **注意**：由于 `tool_result` 使用 Guid 而 `tool_call_end` 使用 index，**前端不能用 toolCallId 跨事件匹配**。
> 推荐：**使用 toolName 匹配**，前端维护 toolName → 展开状态 的映射。

### 4.3 字符串转义

- SSE 事件 content 中的特殊字符（`\n`, `\r`, `\t`, `\\`, `\"`）遵循 JSON 标准转义
- `\n` 在 content 中代表实际的换行符，前端渲染时按需转为 `<br>` 或 markdown 换行
- `\\` 代表 Windows 路径中的反斜杠（如 `D:\\MyProjects`）

### 4.4 思考内容（thinking）

- `thinking` 事件和 `delta` 事件交替出现
- `thinking` 内容对应 Claude 的 "extended thinking" 或 DeepSeek 的 `reasoning_content`
- 前端可选择折叠/展开 thinking 内容，默认折叠

---

## 5. 已知限制

1. **PicoJetson SG 无法序列化 `List<ModelListItem>`**（CS0305）→ `/api/models` 和 `/api/config/validate` 使用手动 JSON 构建
2. **`parameters` / `input_schema` 需要 raw JSON 注入** → OpenAI tools 段在 DTO 序列化后手动拼接
3. **Anthropic content polymorphic** → Anthropic 请求体全部手写
4. **`Dictionary<string, object?>` SG 不支持** → tool call arguments 手动序列化（`DictToJson`）
5. **`tool_call_start/end` 的 `toolCallId` 与 `tool_result` 的 `toolCallId` 不一致**（index vs Guid）→ 前端用 `toolName` 做匹配

---

*最后更新：2026-07-11*
