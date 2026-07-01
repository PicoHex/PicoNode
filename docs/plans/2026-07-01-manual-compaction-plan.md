# Manual Session Compaction — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 PicoAgent 添加手动会话压缩——用户点击按钮，Agent 调用 LLM 为历史消息生成摘要，插入 `CompactionEntry`。

**Architecture:** 复用已有 `Compactor` 类和 `IAgentLlm` 基础设施。Agent 侧新增 `CompactSessionAsync` 方法和 HTTP 端点，Web 代理透传，前端 session 列表加压缩按钮。

**Tech Stack:** C# / .NET 10 / PicoAgent / PicoNode.Agent / vanilla JS

**Spec:** `docs/2026-07-01-manual-compaction-design.md`

---

## Global Constraints

- 不修改 `AgentLoop` / `RunTurnAsync`
- `Compactor` 复用主模型（`_pendingModel.Id`）
- 复用已有 `AgentLlmAdapter`（`_clients.First().Value` + `_router`）
- JSON 响应用手写（与 Agent 现有端点模式一致）
- 前端 vanilla JS，无外部依赖

---

### Task 1: Agent 侧 `CompactSessionAsync` + HTTP 端点

**Files:**
- Modify: `src/PicoAgent/Agent.cs`

**Interfaces:**
- Consumes: `_host`, `_clients`, `_router`, `_pendingModel`, `_loop`
- Produces: `CompactSessionAsync(string sessionId, int keepRecent, CancellationToken ct)` → HTTP 响应 JSON

- [ ] **Step 1: Add `CompactSessionAsync` method to Agent class**

In `Agent.cs`, add below the `ReloadProviderConfigs` region:

```csharp
public async Task<CompactionEntry?> CompactSessionAsync(
    string sessionId,
    int keepRecent = 20,
    CancellationToken ct = default)
{
    if (_clients.Count == 0)
        return null;

    var session = _host.GetOrCreateSession(sessionId);
    var adapter = new AgentLlmAdapter(_clients.Values.First(), _router);
    var compactor = new Compactor(adapter);
    var settings = new CompactionSettings
    {
        Enabled = true,
        KeepRecentTokens = keepRecent * 256,  // rough estimate: ~256 tokens per msg
        ReserveTokens = 4096,
    };

    return await compactor.CompactAsync(session, settings, ct);
}
```

- [ ] **Step 2: Add HTTP endpoint in `BuildWebApp`**

After the existing `/session/{id}/save` endpoint, add:

```csharp
// POST /session/{id}/compact — compress history into summary
app.MapPost(
    "/session/{id}/compact",
    async (ctx, ct) =>
    {
        var id = ctx.RouteValues["id"] ?? "default";
        var keepRecent = 20;
        try
        {
            var body = await ReadJsonBodyAsync(ctx, ct);
            if (body?.TryGetProperty("keepRecent", out var kr) == true)
                keepRecent = kr.GetInt32();
        }
        catch { /* use default */ }

        var result = await CompactSessionAsync(id, keepRecent, ct);
        if (result is null)
            return JsonResponse(200,
                "{\"compressedCount\":0,\"summary\":null,\"tokensSaved\":0}");

        var json = "{"
            + $"\"summary\":\"{EscapeJsonString(result.Summary)}\","
            + $"\"compressedCount\":1,"
            + $"\"tokensSaved\":{result.TokensBefore}"
            + "}";
        return JsonResponse(200, json);
    }
);
```

- [ ] **Step 3: Build and verify**

```bash
dotnet build src/PicoAgent/PicoAgent.csproj
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/PicoAgent/Agent.cs
git commit -m "feat(agent): add CompactSessionAsync and /session/{id}/compact endpoint"
```

---

### Task 2: AgentHttpClient 透传方法

**Files:**
- Modify: `src/PicoAgent/AgentHttpClient.cs`

**Interfaces:**
- Consumes: `_http`
- Produces: `CompactSessionAsync(string sessionId, int keepRecent, CancellationToken ct)`

- [ ] **Step 1: Add method**

After `SaveSessionAsync`, add:

```csharp
public async Task<string> CompactSessionAsync(
    string sessionId,
    int keepRecent = 20,
    CancellationToken ct = default)
{
    var json = keepRecent != 20
        ? $"{{\"keepRecent\":{keepRecent}}}"
        : "{}";
    var response = await _http.PostAsync(
        $"/session/{sessionId}/compact",
        new StringContent(json, Encoding.UTF8, "application/json"),
        ct);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync(ct);
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build src/PicoAgent/PicoAgent.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/PicoAgent/AgentHttpClient.cs
git commit -m "feat(client): add CompactSessionAsync to AgentHttpClient"
```

---

### Task 3: Web 代理端点

**Files:**
- Modify: `samples/PicoAgent.Web/Program.cs`

**Interfaces:**
- Consumes: `client` (AgentHttpClient)
- Produces: `POST /api/session/{id}/compact`

- [ ] **Step 1: Add proxy route**

After the existing `/api/session/save/{id}` endpoint:

```csharp
app.MapPost(
    "/api/session/{id}/compact",
    async (ctx, ct) =>
    {
        var id = ctx.RouteValues["id"] ?? "default";
        var keepRecent = 20;
        try
        {
            var json = await ReadJsonAsync(ctx, ct);
            if (json?.TryGetProperty("keepRecent", out var kr) == true)
                keepRecent = kr.GetInt32();
        }
        catch { /* use default */ }

        var result = await client.CompactSessionAsync(id, keepRecent, ct);
        return OkJson(result);
    }
);
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build samples/PicoAgent.Web/PicoAgent.Web.csproj
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add samples/PicoAgent.Web/Program.cs
git commit -m "feat(web): add /api/session/{id}/compact proxy endpoint"
```

---

### Task 4: 前端压缩按钮

**Files:**
- Modify: `samples/PicoAgent.Web/wwwroot/app.js`
- Modify: `samples/PicoAgent.Web/wwwroot/style.css`

**Interfaces:**
- Consumes: session list DOM, `api()` helper
- Produces: 🗜️ button per session row, click → compact → toast feedback

- [ ] **Step 1: Add `compactSession` function**

At the end of the API helpers section, after `saveSession`:

```javascript
async function compactSession(sessionId, btn) {
    const originalText = btn.textContent;
    btn.textContent = '...';
    btn.disabled = true;
    try {
        const result = await api('POST', `/api/session/${sessionId}/compact`, { keepRecent: 20 });
        if (result.compressedCount > 0) {
            showToast(`Compressed ${result.compressedCount} messages, saved ${result.tokensSaved} tokens`);
        } else {
            showToast('Nothing to compress (messages <= 20)');
        }
        btn.textContent = '✓';
        setTimeout(() => { btn.textContent = originalText; btn.disabled = false; }, 1500);
        // Refresh messages if compacting current session
        if (sessionId === currentSession) {
            await loadMessages(sessionId);
        }
    } catch (e) {
        btn.textContent = '✗';
        showToast(`Compression failed: ${e.message}`);
        setTimeout(() => { btn.textContent = originalText; btn.disabled = false; }, 1500);
    }
}

function showToast(msg) {
    const toast = document.createElement('div');
    toast.className = 'toast';
    toast.textContent = msg;
    document.body.appendChild(toast);
    requestAnimationFrame(() => toast.classList.add('show'));
    setTimeout(() => {
        toast.classList.remove('show');
        setTimeout(() => toast.remove(), 300);
    }, 3000);
}
```

- [ ] **Step 2: Add compression button to session list items**

In `loadSessions()`, inside the loop that creates session divs, add after `div.textContent = s`:

```javascript
const compactBtn = document.createElement('button');
compactBtn.className = 'compact-btn';
compactBtn.textContent = '🗜️';
compactBtn.title = 'Compress history';
compactBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    compactSession(s, compactBtn);
});
div.appendChild(compactBtn);
```

- [ ] **Step 3: Add toast CSS**

In `style.css`, append:

```css
.toast {
    position: fixed;
    bottom: 24px;
    left: 50%;
    transform: translateX(-50%) translateY(20px);
    background: var(--accent);
    color: #fff;
    padding: 10px 20px;
    border-radius: 8px;
    font-size: 0.85em;
    opacity: 0;
    transition: opacity 0.3s, transform 0.3s;
    z-index: 1000;
    pointer-events: none;
}
.toast.show {
    opacity: 1;
    transform: translateX(-50%) translateY(0);
}
.compact-btn {
    background: none;
    border: none;
    cursor: pointer;
    font-size: 0.85em;
    padding: 2px 6px;
    opacity: 0.5;
    transition: opacity 0.2s;
}
.compact-btn:hover { opacity: 1; }
.compact-btn:disabled { opacity: 0.3; cursor: not-allowed; }
```

- [ ] **Step 4: Build and verify**

```bash
dotnet build samples/PicoAgent.Web/PicoAgent.Web.csproj
```

- [ ] **Step 5: Commit**

```bash
git add samples/PicoAgent.Web/wwwroot/app.js samples/PicoAgent.Web/wwwroot/style.css
git commit -m "feat(web): add session compaction button and toast UI"
```

---

### Task 5: 手动验证

- [ ] **Step 1: 启动服务**

```bash
rm -rf samples/PicoAgent.Web/bin/Debug/net10.0/data
dotnet run --project samples/PicoAgent.Web/PicoAgent.Web.csproj
```

- [ ] **Step 2: 配置 provider**

浏览器访问 `http://localhost:9000` → 配置页 → 输入真实 API Key → Save

- [ ] **Step 3: 发送多轮对话**（至少 25 条消息，超出默认 keepRecent=20）

- [ ] **Step 4: 点击当前 session 的 🗜️ 按钮**

Expected:
- 按钮变 `...` → `✓` → 恢复 `🗜️`
- Toast 显示 "Compressed N messages, saved X tokens"
- 消息区自动刷新，显示压缩后的上下文

- [ ] **Step 5: 发送新消息验证压缩效果**

新消息应基于压缩后的上下文化仍能正常回复。

- [ ] **Step 6: 测试无需压缩的场景**（消息数 ≤ 20）

Expected: Toast "Nothing to compress (messages <= 20)"
