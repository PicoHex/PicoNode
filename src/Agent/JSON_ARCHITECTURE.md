# JSON 序列化分层策略

## 原则

PicoJetson 只适用于**编译期形状固定的 DTO**。以下场景必须手写 JSON：

## PicoJetson 适用 ✅

HTTP 响应端点 — 所有字段在 DTO 中确定，形状不变：

```csharp
JsonHelper.Json(new HealthResponse { ... })   // {"status","model","provider"}
JsonHelper.Json(new ConfigStatusResponse { ... }) // 固定字段
```

请求体（tools 除外） — 已知结构：

```csharp
JsonSerializer.Serialize(new OpenAiChatRequest { ... })  // model, messages, thinking
```

## 必须手写 ❌

### 1. SSE 事件 — 每个事件形状不同

| 事件 | 字段 |
|------|------|
| done | `{"type":"done"}` |
| text | `{"type":"delta","content":"..."}` |
| tool_start | `{"type":"tool_call_start","content":"","toolCallId":"0","toolName":"read"}` |

PicoJetson 对所有属性一视同仁，无法做到 "done 隐藏 content，tool 显示 content"。
→ `Server.BuildSseJson()` 手动构建。

### 2. tools parameters — 内容是 JSON 的 string

```csharp
// Parameters 值是 {"type":"object",...}，存为 string
// PicoJetson 会将它序列化为转义的 JSON 字符串 "{\"type\":\"object\"}"
// LLM API 要求的是原始 JSON 对象 {"type":"object"}
public string Parameters { get; set; } = "{}";
```

→ `OpenAILlmClient.BuildRequestJson()` 在 DTO 序列化后手动注入 tools 段。

### 3. 裸数组返回 — PicoJetson SG 的 CS0305 bug

```csharp
// ❌ SG 生成 List<ModelListItem> 序列化器时报 CS0305
JsonSerializer.Serialize(new List<ModelListItem>())

// ✅ 手动构建
ModelsToJson(models)
```

### 4. Dictionary<string, object?> — SG 无法为 object? 生成代码

→ `OpenAILlmClient.DictToJson()` 手动序列化。

## 为什么不用 PicoJetson ISerializer<T>

`JsonSerializer.Register<ISerializer<T>>()` 只在**没有 SG 生成序列化器的类型**上生效。
被 `[PicoSerializable]` DTO 引用的嵌套类型，SG 自动生成 → Register 无法覆盖。

## 失配根因

PicoJetson 是**编译期形状固定的 DTO 序列化器** — 类型形状决定 JSON 形状。
SSE/LLM API 是**运行时形状可变** — 事件类型决定字段存在性。

两者不可调和。正确策略：固定形状用 PicoJetson，可变/raw JSON 手写。
