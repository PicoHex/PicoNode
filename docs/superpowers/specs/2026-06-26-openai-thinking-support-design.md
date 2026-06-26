# OpenAILlmClient Thinking Support — Design

> 2026-06-26

## 概述

当前 `/thinking` 命令已实现了 Anthropic Extended Thinking 支持，但 OpenAI 和后续其他 LLM 的 reasoning 参数尚未对接。本方案将 thinking 配置**从代码硬编码迁移到 `settings.json` 驱动**，使所有 provider 的思考/推理功能由配置控制，无需重新编译。

---

## 配置结构

### `settings.json`

```json
{
  "thinkingEnabled": true,
  "thinkingLevel": "medium",
  "providers": {
    "anthropic": {
      "apiKey": "$ANTHROPIC_API_KEY",
      "thinking": {
        "minimal": "2000",
        "low": "8000",
        "medium": "16000",
        "high": "32000",
        "xhigh": "64000"
      },
      "models": {
        "claude-sonnet-4": {
          "thinkingEnabled": true,
          "thinkingLevel": "high"
        },
        "claude-haiku-4": {
          "thinkingEnabled": false
        }
      }
    },
    "openai": {
      "apiKey": "$OPENAI_API_KEY",
      "thinking": {
        "minimal": "low",
        "low": "low",
        "medium": "medium",
        "high": "high",
        "xhigh": "high"
      }
    }
  }
}
```

### 字段语义

| 字段 | 层级 | 含义 |
|------|------|------|
| `thinkingEnabled` | 全局 / model 级 | 是否默认启用思维模式 |
| `thinkingLevel` | 全局 / model 级 | 默认思维级别（`minimal`/`low`/`medium`/`high`/`xhigh`） |
| `thinking` | provider 级 | `ThinkingLevel` → provider 特定 API 参数映射表（key 是 level 的小写名） |

### 回退链

```
thinkingEnabled : models.{id}.thinkingEnabled → 全局 thinkingEnabled → true
thinkingLevel   : models.{id}.thinkingLevel   → 全局 thinkingLevel   → Medium
thinking (map)  : provider.thinking（仅 provider 级，model 间不区分）

map 缺 level    : MapResolver 自动找最近级别（见下文）
map 为 null     : 此 provider 不支持 thinking
```

---

## 数据模型

### 拆分：`AgentConfig.cs`（从 `ConfigLoader.cs` 拆出数据类）

```csharp
// 新文件：src/PicoNode.Agent/Config/AgentConfig.cs
// 原数据类 AgentConfig, ProviderEntry 从 ConfigLoader.cs 移到此文件

[PicoJsonSerializable]
public sealed class AgentConfig
{
    public Dictionary<string, ProviderEntry> Providers { get; set; } = [];
    public string? Model { get; set; }
    public string? ThinkingLevel { get; set; }          // 对应 thinkingLevel
    public bool ThinkingEnabled { get; set; } = true;   // 对应 thinkingEnabled
    public int? MaxTokens { get; set; }
}

[PicoJsonSerializable]
public sealed class ProviderEntry
{
    public string ApiKey { get; set; } = "";
    public string? ApiFormat { get; set; }
    public string? BaseUrl { get; set; }
    public Dictionary<string, string>? Thinking { get; set; }              // 新增
    public Dictionary<string, ModelThinkingOverride>? Models { get; set; }  // 新增
}

[PicoJsonSerializable]
public sealed class ModelThinkingOverride                                 // 新增
{
    public bool ThinkingEnabled { get; set; }
    public string ThinkingLevel { get; set; } = "";
}
```

`ConfigLoader.cs` 只保留 `Load`、`Validate`、`ExpandEnvVars` 方法，导入 `AgentConfig` 等类型。

`ModelThinkingOverride.ThinkingLevel` 是字符串，需在启动时解析为 `ThinkingLevel` 枚举：

```csharp
static ThinkingLevel? ParseThinkingLevel(string? s) => s?.ToLower() switch
{
    "minimal" => ThinkingLevel.Minimal,
    "low"     => ThinkingLevel.Low,
    "medium"  => ThinkingLevel.Medium,
    "high"    => ThinkingLevel.High,
    "xhigh"   => ThinkingLevel.XHigh,
    _         => null
};
```

### `Model.cs`

```csharp
public sealed class Model
{
    // 现有属性...
    public bool ThinkingEnabled { get; set; }               // 重命名自 Reasoning
    public ThinkingLevel ThinkingLevel { get; set; }        // 新增（非 nullable；初始化时显式赋值）
    public Dictionary<string, string>? ThinkingLevelMap { get; set; }  // 新增
}
```

- `ThinkingLevel` 非 nullable，由配置或命令显式赋值，**不依赖 `default(ThinkingLevel)`**
- `ThinkingLevelMap` 为 null 表示此 provider 不支持 thinking

### `StreamOptions.cs`

```csharp
public sealed class StreamOptions
{
    // 现有属性...
    public ThinkingLevel? Reasoning { get; set; }
    public Dictionary<string, string>? ThinkingLevelMap { get; set; }  // 新增
}
```

### `SessionData.cs`（新文件）

```csharp
// 新文件：src/PicoNode.Agent/Session/SessionData.cs

public sealed class SessionData
{
    public List<Message> Messages { get; set; } = [];
    public bool ThinkingEnabled { get; set; }
    public ThinkingLevel ThinkingLevel { get; set; }
}
```

---

## 数据流

### 启动

```
settings.json
  → AgentConfig.ThinkingEnabled → Model.ThinkingEnabled
  → AgentConfig.ThinkingLevel → ParseThinkingLevel → Model.ThinkingLevel（null → Medium）
  → 当前 provider.Thinking → Model.ThinkingLevelMap
  → 当前 model 设了 models.{id}:
       models.{id}.ThinkingEnabled 覆写 Model.ThinkingEnabled
       models.{id}.ThinkingLevel → ParseThinkingLevel → 覆写 Model.ThinkingLevel
  → resume session → SessionData 覆写 Model.ThinkingEnabled / ThinkingLevel
```

### CLI 命令

| 命令 | ThinkingEnabled | ThinkingLevel |
|------|:--:|:--:|
| `/thinking on` | `true` | 不变 |
| `/thinking off` | `false` | 不变 |
| `/thinking` | 切换 | 不变 |
| `/thinking high` | `true` | `High` |
| `/thinking medium` | `true` | `Medium` |
| `/thinking low` | `true` | `Low` |
| `/thinking minimal` | `true` | `Minimal` |
| `/thinking xhigh` | `true` | `XHigh` |

| 命令 | 行为 |
|------|------|
| `/model X` | 查 `models.X`，有则覆写 Model.ThinkingEnabled / ThinkingLevel；无则保留当前值 |
| `/provider Y` | 查 `providers.Y.Thinking` → Model.ThinkingLevelMap（不存在 → null） |

### LLM 调用

```
AgentLoop.CallLLMAsync:
  if !model.ThinkingEnabled || model.ThinkingLevelMap == null:
    StreamOptions = null  （不传 thinking）
  else:
    StreamOptions {
      Reasoning = model.ThinkingLevel,
      ThinkingLevelMap = model.ThinkingLevelMap
    }
```

AgentLoop 不调用 MapResolver，只把 `ThinkingLevel` 和 `ThinkingLevelMap` 打包进 `StreamOptions` 传给 LLM Client。Resolve 推迟到各 LLM Client 的 `BuildRequestJson` 内部——因为 Anthropic 要对 value 做 `int.Parse`，OpenAI 要直接当字符串用，AgentLoop 不应关心 API 参数的格式差异。

### LLM Client 序列化

```
AnthropicLLmClient.BuildRequestJson:
  if options?.Reasoning != null && options?.ThinkingLevelMap != null:
    value = MapResolver.Resolve(options.Reasoning, options.ThinkingLevelMap)
    if value != null:
      JSON += "thinking":{"type":"enabled","budget_tokens":{int.Parse(value)}}
    // value == null → map 为空 → 不拼 thinking 参数
  // Reasoning == null || ThinkingLevelMap == null → 不拼

OpenAILlmClient.BuildRequestJson:
  if options?.Reasoning != null && options?.ThinkingLevelMap != null:
    value = MapResolver.Resolve(options.Reasoning, options.ThinkingLevelMap)
    if value != null:
      JSON += "reasoning_effort":"{value}"
    // value == null → map 为空 → 不拼 reasoning_effort
  // Reasoning == null || ThinkingLevelMap == null → 不拼
```

---

## Level 映射器（`MapResolver.cs`）

`ThinkingLevel` 枚举值有大小顺序：

```
Minimal (0) < Low (1) < Medium (2) < High (3) < XHigh (4)
```

### 匹配算法

```
输入: requestedLevel (ThinkingLevel), map (Dictionary<string,string>)

1. 精确匹配 → requestedLevel.ToString().ToLower()
2. 向下找最近的（优先省钱）→ 遍历所有小于当前级别的值
3. 向上找最近的 → 遍历所有大于当前级别的值
4. 找不到 → 返回 null
```

### 示例

| map 配置 | 请求级别 | 匹配结果 |
|---------|:--:|:--:|
| `{minimal,low,medium}` | `high` | `medium`（向下） |
| `{minimal,low,medium}` | `xhigh` | `medium`（向下找，优先省钱） |
| `{low,high}` | `medium` | `low`（向下） |
| `{low,high}` | `minimal` | `low`（向上，没有更小的） |
| `{low,high}` | `xhigh` | `high`（向下） |
| `{}` | any | `null` |
| `null` | any | `null` |

---

## Session 持久化

### 格式

JSONL，首行为 metadata：

```
{"$meta":{"thinkingEnabled":true,"thinkingLevel":"high"}}
{"Role":"user","Content":"Hello","Timestamp":1}
{"Role":"assistant","Content":"Hi","Timestamp":2}
```

### 加载行为

1. 读写通过 `SessionStore.LoadAsync(path)` / `SaveAsync(path, SessionData)`
2. 如果首行不含 `"$meta"` → 旧格式 → `ThinkingEnabled=true`, `ThinkingLevel=Medium`（用配置默认）
3. `/save` 时写当前 `ThinkingEnabled`/`ThinkingLevel` 到 $meta
4. resume 时从 $meta 恢复（覆写 Model）

---

## 部署和初始化

- `settings.example.json` 作为 `Content` 项随 `dotnet publish` 输出，AOT 单文件部署后为独立文件
- 首次启动检查 `~/.pico-agent/settings.json`，不存在时报错提示拷贝 `example` 过去编辑
- 模板字符串从 `Program.cs` 删除

### `PicoAgent.csproj` 改动

```xml
<ItemGroup>
  <Content Include="settings.example.json">
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </Content>
</ItemGroup>
```

---

## 涉及的全部改动

| # | 文件 | 改动 |
|---|------|------|
| 1 | `src/PicoNode.Agent/Config/AgentConfig.cs` | **新建**：从 `ConfigLoader.cs` 移出数据类；`ProviderEntry` + `Thinking` + `Models`；新增 `ModelThinkingOverride` |
| 2 | `src/PicoNode.Agent/Config/ConfigLoader.cs` | 移除数据类（移入 AgentConfig.cs）；Load 方法加 thinking 默认值解析 |
| 3 | `src/PicoNode.AI/Types/Model.cs` | `Reasoning` → `ThinkingEnabled`；+ `ThinkingLevel`；+ `ThinkingLevelMap` |
| 4 | `src/PicoNode.AI/Types/StreamOptions.cs` | + `ThinkingLevelMap` |
| 5 | `src/PicoNode.Agent/ThinkingCommand.cs` | 支持 level 参数；操作 `ThinkingEnabled` + `ThinkingLevel` |
| 6 | `src/PicoNode.Agent/Agent/AgentLoop.cs` | `CallLLMAsync` 判断 `ThinkingEnabled`；传 `ThinkingLevelMap` |
| 7 | `src/PicoNode.AI/LLm/AnthropicLLmClient.cs` | `BuildRequestJson` 从 map + MapResolver 取 budget_tokens |
| 8 | `src/PicoNode.AI/LLm/OpenAILlmClient.cs` | `BuildRequestJson` + `reasoning_effort` 序列化 + MapResolver |
| 9 | `src/PicoNode.AI/LLm/MapResolver.cs` | **新建**：level→value 匹配器 |
| 10 | `src/PicoNode.Agent/Host/AgentHost.cs` | session 维度管理 `ThinkingEnabled`/`ThinkingLevel`；代理 `SessionStore` |
| 11 | `src/PicoNode.Agent/Session/SessionStore.cs` | API → `SessionData`；`$meta` 行读写 |
| 12 | `src/PicoNode.Agent/Session/SessionData.cs` | **新建**：session 元数据包装类 |
| 13 | `src/PicoAgent/Program.cs` | 启动装配置；`/model`/`/provider` 更新 Model；命令行为更新；删除模板字符串 |
| 14 | `src/PicoAgent/PicoAgent.csproj` | + `<Content>` for example |
| 15 | `src/PicoAgent/settings.example.json` | **新建**，含所有内置 provider 默认 thinking 配置 |
| 16 | 测试文件 | 重命名 `Reasoning` 引用；mapper 测试；LLM Client thinking 序列化测试 |

---

## 待处理

- [ ] serve mode `POST /session/{id}/message` 不支持 HTTP 客户端动态控制 thinking（当前仅从 session metadata 读取）
