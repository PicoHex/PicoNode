# Manual Session Compaction — Design Spec

## 1. 概述

为 PicoAgent 添加手动会话压缩功能。用户通过 Web UI 触发，Agent 调用 LLM 为会话历史生成摘要，减少后续请求的 token 消耗。

**范围**：手动触发，不接入自动压缩逻辑。自动压缩（阈值、触发策略）留待后续 spec。

---

## 2. 动机

会话消息数线性增长，PicoNode.Agent 的 `Compactor` 类已完整实现（LLM 驱动的摘要生成 + 树形会话压缩条目），但从未接入 AgentLoop 或 HTTP 端点。本 spec 聚焦最小可行集成——让用户能手动触发压缩。

---

## 3. 设计决策

| 决策 | 选择 | 理由 |
|------|------|------|
| 压缩模型 | 复用主模型 | 零配置，频次极低 |
| 触发方式 | 手动（按钮） | 先验证质量再谈自动化 |
| 压缩范围 | 除最新 N 条外全部 | 简单直接，默认保留 20 条 |
| LLM 客户端 | 复用当前 provider | 和聊天用同一套连接 |
| 幂等 | 已压缩消息跳过 | 重复点击安全 |

---

## 4. API 设计

### 4.1 `POST /session/{id}/compact`

**请求**（可选 body）：

```json
{ "keepRecent": 20 }
```

| 字段 | 类型 | 默认 | 说明 |
|------|------|------|------|
| keepRecent | int | 20 | 保留最近 N 条消息不压缩 |

**成功响应** (`200`):

```json
{
  "summary": "用户讨论了 PicoAgent 的配置流程和 portable 数据目录...",
  "compressedCount": 47,
  "tokensSaved": 8230
}
```

**错误响应**:

| 状态码 | 场景 | body |
|--------|------|------|
| 404 | Session 不存在 | `{"error": "Session not found"}` |
| 200 | 无需压缩（消息数 ≤ keepRecent） | `{"compressedCount": 0, "summary": null, "tokensSaved": 0}` |

**幂等**：已压缩的消息不重复计算，`compressedCount` 只计本轮新压缩的条数。

## 5. 实现链路

### 5.1 后端（PicoAgent）

```
Agent.CompactSessionAsync(sessionId, keepRecent, ct)
  → _host.GetOrCreateSession(sessionId)
  → new Compactor(new AgentLlmAdapter(_clients.First().Value, _router))
  → compactor.CompactAsync(session, settings, ct)
  → 返回 CompactionResult { Summary, CompressedCount, TokensSaved }
```

**CompactionSettings**：
- `CompactionModelId` = `_pendingModel.Id`（复用主模型）
- `KeepRecentMessages` = 前端传入的 `keepRecent`
- `SystemPrompt` = `_loop.SystemPrompt`

**HTTP 端点**（Agent 侧 `BuildWebApp` 内）：

```csharp
app.MapPost("/session/{id}/compact", async (ctx, ct) => {
    var id = ctx.RouteValues["id"] ?? "default";
    var keepRecent = 20;
    var body = await ReadJsonBodyAsync(ctx, ct);
    if (body?.TryGetProperty("keepRecent", out var kr) == true)
        keepRecent = kr.GetInt32();
    var result = await CompactSessionAsync(id, keepRecent, ct);
    return result is { CompressedCount: > 0 }
        ? JsonResponse(200, Serialize(result))
        : JsonResponse(200, "{\"compressedCount\":0,\"summary\":null,\"tokensSaved\":0}");
});
```

### 5.2 HTTP 客户端（AgentHttpClient）

新增方法：

```csharp
public async Task<CompactionResult?> CompactSessionAsync(
    string sessionId, int keepRecent = 20, CancellationToken ct = default)
```

### 5.3 Web 代理（Program.cs）

```csharp
app.MapPost("/api/session/{id}/compact", async (ctx, ct) => {
    var id = ctx.RouteValues["id"] ?? "default";
    var keepRecent = 20;
    // parse body, forward to agent
    return OkJson(await client.CompactSessionAsync(id, keepRecent, ct));
});
```

### 5.4 前端（app.js）

**位置**：session 列表每行右侧 🗜️ 按钮

```
┌─ Sessions ─────────────┐
│ default              🗜️ │
│ chat1                🗜️ │
│ 123                  🗜️ │
└────────────────────────┘
```

**交互**：
1. 点击 🗜️ → 变为 `...`（loading 态）
2. 成功：`✓` 1.5s → 恢复 `🗜️`，toast "已压缩 47 条，节省 8230 tokens"
3. 成功但无需压缩：toast "当前无需压缩（消息数 ≤ 20）"
4. 失败：`✗` 1.5s → 恢复 `🗜️`，toast 显示错误
5. 如果压缩的是**当前活跃 session**，压缩成功后自动调用 `loadMessages(id)` 刷新消息区

---

## 6. 涉及文件

| 文件 | 变更 |
|------|------|
| `src/PicoAgent/Agent.cs` | + `CompactSessionAsync` 方法，+ `/session/{id}/compact` 端点 |
| `src/PicoAgent/AgentHttpClient.cs` | + `CompactSessionAsync` 方法 |
| `samples/PicoAgent.Web/Program.cs` | + `/api/session/{id}/compact` 代理端点 |
| `samples/PicoAgent.Web/wwwroot/app.js` | + 压缩按钮渲染 + 事件处理 + toast |

---

## 7. 风险与限制

| 风险 | 缓解 |
|------|------|
| 压缩期间 LLM 调用阻塞当前请求 | 压缩频次极低，几秒延迟可接受 |
| 摘要质量不可控 | 手动触发，用户可自行判断 |
| 压缩后丢失关键信息 | `Session` 树形结构保留完整历史，`BuildContext` 可选是否使用压缩摘要 |
| 并发压缩同一 session | `AgentHost.ProcessMessageAsync` 已有 `SemaphoreSlim` per-session 锁，压缩应复用同一锁 |
| bootstrap 状态（无 provider）无法压缩 | 前端只在 `configured: true` 时渲染压缩按钮，API 端返回 400 |

---

## 8. 与自动压缩的关系

本 spec 只做手动触发。自动压缩（阈值、触发策略、异步/同步）留待后续 spec。手动压缩的 API 和实现（`CompactSessionAsync`）可直接被自动压缩复用。

`AgentLoop` 的 `RunTurnAsync` 本次**不做任何修改**——压缩入口完全在 HTTP 端点层面。
