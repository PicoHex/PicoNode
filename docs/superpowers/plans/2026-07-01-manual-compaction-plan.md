# Manual Session Compaction — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 PicoAgent 添加手动会话压缩——用户点击按钮，Agent 调用 LLM 为历史消息生成摘要，插入 `CompactionEntry`。

**Architecture:** 复用已有 `Compactor` 类和 `IAgentLlm` 基础设施。Agent 侧新增 `CompactSessionAsync` 方法和 HTTP 端点，Web 代理透传，前端 session 列表加压缩按钮。

**Tech Stack:** C# / .NET 10 / PicoAgent / PicoNode.Agent / vanilla JS / TUnit

**Spec:** `docs/superpowers/specs/2026-07-01-manual-compaction-design.md`

---

## Global Constraints

- 不修改 `AgentLoop` / `RunTurnAsync`
- `Compactor` 复用主模型（`_clients.Values.First()` + `_router`）
- JSON 响应用手写（与 Agent 现有端点模式一致）
- 前端 vanilla JS，无外部依赖
- **TDD: 每个 Task 先写失败测试，再实现，测试通过后 commit**

---

### Task 1: Agent 侧会话锁 + `CompactSessionAsync` + HTTP 端点

**Files:**
- Modify: `src/PicoNode.Agent/Host/AgentHost.cs`
- Modify: `src/PicoAgent/Agent.cs`
- Test: `tests/PicoNode.Agent.Tests/Host/AgentHostSessionLockTests.cs`
- Test: `tests/PicoNode.Agent.Tests/PicoAgent/AgentCompactSessionTests.cs`

**Interfaces:**
- Produces: `AgentHost.LockSessionAsync(string, CancellationToken)` → `Task<IDisposable>`
- Produces: `Agent.CompactSessionAsync(string, int, CancellationToken)` → `Task<(CompactionEntry?, int, long)>`
- Produces: `POST /session/{id}/compact` → `{"compressedCount":N,"summary":"...","tokensSaved":N}`

- [ ] **T1.1: Write failing test — `LockSessionAsync`**

`tests/PicoNode.Agent.Tests/Host/AgentHostSessionLockTests.cs`:

```csharp
namespace PicoNode.Agent.Tests.Host;

public class AgentHostSessionLockTests
{
    [Test]
    public async Task LockSessionAsync_PreventsConcurrentAccess()
    {
        var host = new AgentHost(new AgentLoop(
            new NoopAgentLlm(), new CapabilityRegistry(), new CapabilityRunner()));

        using var lock1 = await host.LockSessionAsync("test", CancellationToken.None);
        Assert.That(lock1).IsNotNull();

        // Second lock should block, verify with a short timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            using var lock2 = await host.LockSessionAsync("test", cts.Token);
        });
    }

    [Test]
    public async Task LockSessionAsync_DifferentSessions_AreIndependent()
    {
        var host = new AgentHost(new AgentLoop(
            new NoopAgentLlm(), new CapabilityRegistry(), new CapabilityRunner()));

        using var lock1 = await host.LockSessionAsync("a", CancellationToken.None);
        using var lock2 = await host.LockSessionAsync("b", CancellationToken.None);
        // Both acquired without blocking — different sessions
    }
}
```

- [ ] **T1.2: Run test — expect fail: `'AgentHost' does not contain a definition for 'LockSessionAsync'`**

```bash
dotnet test tests/PicoNode.Agent.Tests --filter "AgentHostSessionLockTests"
```

- [ ] **T1.3: Implement `LockSessionAsync` + `SessionLockReleaser` in `AgentHost.cs`**

```csharp
/// <summary>
/// Acquire the per-session lock for external operations (e.g. compaction)
/// that must not race with ProcessMessageAsync.
/// </summary>
public async Task<IDisposable> LockSessionAsync(string sessionId, CancellationToken ct)
{
    var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
    await sessionLock.WaitAsync(ct);
    return new SessionLockReleaser(sessionLock);
}

private sealed class SessionLockReleaser : IDisposable
{
    private SemaphoreSlim? _sem;
    public SessionLockReleaser(SemaphoreSlim sem) => _sem = sem;
    public void Dispose() { Interlocked.Exchange(ref _sem, null)?.Release(); }
}
```

- [ ] **T1.4: Run test — expect pass**

```bash
dotnet test tests/PicoNode.Agent.Tests --filter "AgentHostSessionLockTests"
```

- [ ] **T1.5: Commit session lock**

```bash
git add src/PicoNode.Agent/Host/AgentHost.cs tests/PicoNode.Agent.Tests/Host/AgentHostSessionLockTests.cs
git commit -m "feat(agent): add LockSessionAsync with safe releaser for session-level mutual exclusion"
```

---

- [ ] **T1.6: Write failing test — `CompactSessionAsync`**

`tests/PicoNode.Agent.Tests/PicoAgent/AgentCompactSessionTests.cs`:

```csharp
namespace PicoNode.Agent.Tests.PicoAgent;

public class AgentCompactSessionTests
{
    private static Agent CreateTestAgent(
        IAgentLlm? llm = null,
        Dictionary<string, ILLmClient>? clients = null)
    {
        var c = clients ?? new Dictionary<string, ILLmClient> { ["test"] = new NoopLLmClient() };
        var host = new AgentHost(new AgentLoop(
            llm ?? new NoopAgentLlm(), new CapabilityRegistry(), new CapabilityRunner()));
        var router = new ProviderRouter(new[] {
            new ProviderConfig { Name = "test", ApiFormat = AiApiFormat.OpenAIChatCompletions }
        });
        return Agent.CreateForTest(host, new CapabilityRegistry(),
            new Model { Id = "test-model", Provider = "test" },
            providerConfigs: c.ToDictionary(kv => kv.Key, kv => new ProviderConfig { Name = kv.Key }),
            breakers: new Dictionary<string, ICircuitBreaker> { ["test"] = new CircuitBreaker() },
            clients: c);
    }

    [Test]
    public async Task CompactSessionAsync_NoClients_ReturnsNull()
    {
        var agent = CreateTestAgent(clients: new Dictionary<string, ILLmClient>());
        var (entry, count, saved) = await agent.CompactSessionAsync("test");
        await Assert.That(entry).IsNull();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task CompactSessionAsync_FewMessages_ReturnsNull()
    {
        var agent = CreateTestAgent();
        var session = agent.GetHostForTest().GetOrCreateSession("test");
        await session.AppendMessage(new Message { Role = "user", Content = "hi" });
        await session.AppendMessage(new Message { Role = "assistant", Content = "hello" });

        var (entry, count, _) = await agent.CompactSessionAsync("test", keepRecent: 20);
        await Assert.That(entry).IsNull();
        await Assert.That(count).IsEqualTo(0);
    }
}
```

- [ ] **T1.7: Run test — expect fail**

```bash
dotnet test tests/PicoNode.Agent.Tests --filter "AgentCompactSessionTests"
```

- [ ] **T1.8: Implement `CompactSessionAsync`**

```csharp
public async Task<(CompactionEntry? Entry, int CompressedCount, long TokensSaved)> CompactSessionAsync(
    string sessionId,
    int keepRecent = 20,
    CancellationToken ct = default)
{
    if (_clients.Count == 0)
        return (null, 0, 0);

    using var _ = await _host.LockSessionAsync(sessionId, ct);
    var session = _host.GetOrCreateSession(sessionId);

    var entries = await session.GetEntries();
    var path = entries.Where(e => e is not LeafEntry).ToArray();
    var msgInPath = path.OfType<MessageEntry>().Count();
    if (msgInPath <= keepRecent)
        return (null, 0, 0);

    var adapter = new AgentLlmAdapter(_clients.Values.First(), _router);
    var compactor = new Compactor(adapter);
    var settings = new CompactionSettings
    {
        Enabled = true,
        KeepRecentTokens = keepRecent * 256,
        ReserveTokens = 4096,
    };

    var result = await compactor.CompactAsync(session, settings, ct);
    if (result is null)
        return (null, 0, 0);

    await session.MoveTo(result.Id);

    var compressedCount = msgInPath - keepRecent;
    var summaryTokens = (result.Summary?.Length ?? 0) / 4;
    var tokensSaved = Math.Max(0, result.TokensBefore - summaryTokens);

    return (result, compressedCount, tokensSaved);
}
```

> **Note:** `CompactSessionAsync` 被多个测试文件使用，需要 `internal` 或 `public` 访问级别。这里用 `public` — 遵循 Agent 已有模式的 `ListModelsAsync` / `SwitchModelAsync` 等。

- [ ] **T1.9: Run test — expect pass**

```bash
dotnet test tests/PicoNode.Agent.Tests --filter "AgentCompactSessionTests"
```

- [ ] **T1.10: Add HTTP endpoint in `BuildWebApp`**

After the existing `/session/{id}/save` endpoint:

```csharp
app.MapPost("/session/{id}/compact", async (ctx, ct) =>
{
    var id = ctx.RouteValues["id"] ?? "default";
    var keepRecent = 20;
    try
    {
        var body = await ReadJsonBodyAsync(ctx, ct);
        if (body?.TryGetProperty("keepRecent", out var kr) == true)
            keepRecent = kr.GetInt32();
    }
    catch { }

    var (entry, compressedCount, tokensSaved) = await CompactSessionAsync(id, keepRecent, ct);
    if (entry is null)
        return JsonResponse(200, "{\"compressedCount\":0,\"summary\":null,\"tokensSaved\":0}");

    var json = "{" + $"\"summary\":\"{EscapeJsonString(entry.Summary)}\","
        + $"\"compressedCount\":{compressedCount},"
        + $"\"tokensSaved\":{tokensSaved}" + "}";
    return JsonResponse(200, json);
});
```

- [ ] **T1.11: Build and verify + run full Agent test suite**

```bash
dotnet build src/PicoAgent/PicoAgent.csproj
dotnet test tests/PicoNode.Agent.Tests
```

- [ ] **T1.12: Commit**

```bash
git add src/PicoAgent/Agent.cs tests/PicoNode.Agent.Tests/PicoAgent/AgentCompactSessionTests.cs
git commit -m "feat(agent): add CompactSessionAsync and /session/{id}/compact endpoint"
```

---

### Task 2: AgentHttpClient 透传方法

**Files:**
- Modify: `src/PicoAgent/AgentHttpClient.cs`
- Test: `tests/PicoNode.Tests/AgentHttpClientCompactTests.cs`

**Interfaces:**
- Produces: `AgentHttpClient.CompactSessionAsync(string, int, CancellationToken)` → `Task<string>`

- [ ] **T2.1: Write failing test**

`tests/PicoNode.Tests/AgentHttpClientCompactTests.cs`:

```csharp
namespace PicoNode.Tests;

public class AgentHttpClientCompactTests
{
    [Test]
    public async Task CompactSessionAsync_DefaultKeepRecent_SendsEmptyBody()
    {
        var seenBody = "";
        var handler = new TestDelegatingHandler(async (req, ct) =>
        {
            seenBody = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"compressedCount\":0}") };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = AgentHttpClient.CreateForTest(http);

        await client.CompactSessionAsync("s1");
        await Assert.That(seenBody).IsEqualTo("{}");
    }
}
```

- [ ] **T2.2: Run test — expect fail: `'AgentHttpClient' does not contain 'CreateForTest'` or `'CompactSessionAsync'`**

```bash
dotnet test tests/PicoNode.Tests --filter "AgentHttpClientCompactTests"
```

- [ ] **T2.3: Add `CreateForTest` constructor**

In `AgentHttpClient.cs`, add:

```csharp
/// <summary>Test-only constructor accepting a pre-configured HttpClient.</summary>
internal static AgentHttpClient CreateForTest(HttpClient http) => new() { _http = http };

// For the CreateForTest pattern to work, the class needs a parameterless
// constructor that the static factory can set. Add this private ctor:
private AgentHttpClient() { _http = null!; }
```

- [ ] **T2.4: Run test — expect fail: `'CompactSessionAsync'` not found**

- [ ] **T2.5: Implement `CompactSessionAsync`**

After `SaveSessionAsync`:

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

- [ ] **T2.6: Run test — expect pass**

```bash
dotnet test tests/PicoNode.Tests --filter "AgentHttpClientCompactTests"
```

- [ ] **T2.7: Commit**

```bash
git add src/PicoAgent/AgentHttpClient.cs tests/PicoNode.Tests/AgentHttpClientCompactTests.cs
git commit -m "feat(client): add CompactSessionAsync to AgentHttpClient"
```

---

### Task 3: Web 代理端点

**Files:**
- Modify: `samples/PicoAgent.Web/Program.cs`

**Interfaces:**
- Consumes: `client` (AgentHttpClient)
- Produces: `POST /api/session/{id}/compact`

> **No new test file** — Web 代理是薄透传，不包含业务逻辑，手动验证覆盖。

- [ ] **T3.1: Add proxy route**

After the existing `/api/session/save/{id}` endpoint:

```csharp
app.MapPost("/api/session/{id}/compact", async (ctx, ct) =>
{
    var id = ctx.RouteValues["id"] ?? "default";
    var keepRecent = 20;
    try
    {
        var json = await ReadJsonAsync(ctx, ct);
        if (json?.TryGetProperty("keepRecent", out var kr) == true)
            keepRecent = kr.GetInt32();
    }
    catch { }

    var result = await client.CompactSessionAsync(id, keepRecent, ct);
    return OkJson(result);
});
```

- [ ] **T3.2: Build and verify**

```bash
dotnet build samples/PicoAgent.Web/PicoAgent.Web.csproj
```

- [ ] **T3.3: Commit**

```bash
git add samples/PicoAgent.Web/Program.cs
git commit -m "feat(web): add /api/session/{id}/compact proxy endpoint"
```

---

### Task 4: 前端压缩按钮 + Toast

**Files:**
- Modify: `samples/PicoAgent.Web/wwwroot/app.js`
- Modify: `samples/PicoAgent.Web/wwwroot/style.css`

> **No automated test** — 前端 vanilla JS，手动验证覆盖。

- [ ] **T4.1: Add `compactSession` function**

After `saveSession` in `app.js`:

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

- [ ] **T4.2: Add compression button to session list items**

In `loadSessions()`, after `div.textContent = s`:

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

- [ ] **T4.3: Add toast + button CSS**

Append to `style.css`:

```css
.toast {
    position: fixed; bottom: 24px; left: 50%;
    transform: translateX(-50%) translateY(20px);
    background: var(--accent); color: #fff;
    padding: 10px 20px; border-radius: 8px;
    font-size: 0.85em; opacity: 0;
    transition: opacity 0.3s, transform 0.3s;
    z-index: 1000; pointer-events: none;
}
.toast.show { opacity: 1; transform: translateX(-50%) translateY(0); }
.compact-btn {
    background: none; border: none; cursor: pointer;
    font-size: 0.85em; padding: 2px 6px; opacity: 0.5;
    transition: opacity 0.2s;
}
.compact-btn:hover { opacity: 1; }
.compact-btn:disabled { opacity: 0.3; cursor: not-allowed; }
```

- [ ] **T4.4: Commit**

```bash
git add samples/PicoAgent.Web/wwwroot/app.js samples/PicoAgent.Web/wwwroot/style.css
git commit -m "feat(web): add session compaction button and toast UI"
```

---

### Task 5: 手动验证

- [ ] **T5.1: 启动服务**

```bash
rm -rf samples/PicoAgent.Web/bin/Debug/net10.0/data
dotnet run --project samples/PicoAgent.Web/PicoAgent.Web.csproj
```

- [ ] **T5.2: 配置 provider**

浏览器 `http://localhost:9000` → 输入真实 API Key → Save

- [ ] **T5.3: 发送 ≥25 条消息**

超出默认 `keepRecent=20`

- [ ] **T5.4: 点击 🗜️**

Expected: `...` → `✓` → toast "Compressed N messages, saved X tokens" → 消息区刷新

- [ ] **T5.5: 发送新消息验证上下文**

压缩后 LLM 应能正常回复

- [ ] **T5.6: 消息 ≤20 条时点击 🗜️**

Expected: toast "Nothing to compress"
