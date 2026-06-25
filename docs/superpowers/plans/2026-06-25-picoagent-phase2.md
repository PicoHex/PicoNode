# PicoAgent Phase 2 — Multi-Provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans.

**Goal:** Add multi-provider support to PicoAgent — OpenAI/DeepSeek/Groq/Kimi/GLM clients, model discovery from API, provider presets, and ResilientLLmClient.

**Architecture:** New `OpenAILlmClient` + `OpenAISseParser` in PicoNode.AI. `ResilientLLmClient` wraps routing + circuit breaker. PicoAgent Program.cs gains `/model` `/thinking` `/list-models` commands.

**Tech Stack:** .NET 10, PicoNode.AI, PicoJetson, TUnit

## Global Constraints

- Target: net10.0, `IsAotCompatible=true`
- TDD: test first, see it fail, implement, see it pass
- Commit after each task
- No hardcoded strings — use constants
- Keep 18 existing tests passing

---

### Task 1: ProviderPresets — built-in provider registry

**Files:**
- Create: `src/PicoNode.AI/Types/ProviderPresets.cs`
- Create: `tests/PicoNode.AI.Tests/Types/ProviderPresetsTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
[Test]
public async Task GetPreset_Anthropic_ReturnsConfig()
{
    var config = ProviderPresets.Get("anthropic");
    await Assert.That(config).IsNotNull();
    await Assert.That(config!.ApiFormat).IsEqualTo(AiApiFormat.AnthropicMessages);
    await Assert.That(config.BaseUrl).IsEqualTo("https://api.anthropic.com");
}

[Test]
public async Task GetPreset_OpenAI_ReturnsConfig()
{
    var config = ProviderPresets.Get("openai");
    await Assert.That(config!.ApiFormat).IsEqualTo(AiApiFormat.OpenAIChatCompletions);
    await Assert.That(config.BaseUrl).IsEqualTo("https://api.openai.com/v1");
}

[Test]
public async Task GetPreset_Unknown_ReturnsNull()
{
    var config = ProviderPresets.Get("nonexistent");
    await Assert.That(config).IsNull();
}
```

- [ ] **Step 2-5:** Implement + test + commit

```csharp
public static class ProviderPresets
{
    private static readonly Dictionary<string, ProviderConfig> _presets = new()
    {
        ["anthropic"] = new() { Name="anthropic", ApiFormat=AiApiFormat.AnthropicMessages, BaseUrl="https://api.anthropic.com" },
        ["openai"]    = new() { Name="openai",    ApiFormat=AiApiFormat.OpenAIChatCompletions, BaseUrl="https://api.openai.com/v1" },
        ["deepseek"]  = new() { Name="deepseek",  ApiFormat=AiApiFormat.OpenAIChatCompletions, BaseUrl="https://api.deepseek.com/v1" },
        ["kimi"]      = new() { Name="kimi",      ApiFormat=AiApiFormat.OpenAIChatCompletions, BaseUrl="https://api.moonshot.cn/v1" },
        ["glm"]       = new() { Name="glm",       ApiFormat=AiApiFormat.OpenAIChatCompletions, BaseUrl="https://open.bigmodel.cn/api/paas/v4" },
        ["groq"]      = new() { Name="groq",      ApiFormat=AiApiFormat.OpenAIChatCompletions, BaseUrl="https://api.groq.com/openai/v1" },
    };

    public static ProviderConfig? Get(string name) => _presets.TryGetValue(name, out var c) ? c : null;
    public static IReadOnlyDictionary<string, ProviderConfig> All => _presets;
}
```

---

### Task 2: OpenAI SSE Parser

**Files:**
- Create: `src/PicoNode.AI/Sse/OpenAISseParser.cs`
- Create: `tests/PicoNode.AI.Tests/Sse/OpenAISseParserTests.cs`

- [ ] **Step 1: Write failing test — text streaming**

```csharp
[Test]
public async Task Parse_TextDelta_EmitsEvents()
{
    var sseData = """
        data: {"id":"1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

        data: {"id":"1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":" world"},"finish_reason":null}]}

        data: [DONE]

        """u8.ToArray();

    using var stream = new MemoryStream(sseData);
    var events = new List<AssistantMessageEvent>();
    await foreach (var e in OpenAISseParser.ParseStreamAsync(stream, "gpt-4o", CancellationToken.None))
        events.Add(e);

    var deltas = events.OfType<AssistantMessageEvent.TextDelta>().ToArray();
    await Assert.That(deltas.Length).IsEqualTo(2);
    await Assert.That(deltas[1].Delta).IsEqualTo(" world");
}
```

- [ ] **Step 2-5:** Implement (parses OpenAI SSE → AssistantMessageEvent, handles `[DONE]` terminal), test, commit

---

### Task 3: OpenAILlmClient

**Files:**
- Create: `src/PicoNode.AI/LLm/OpenAILlmClient.cs`
- Create: `tests/PicoNode.AI.Tests/LLm/OpenAILlmClientTests.cs`

Schema: `POST {baseUrl}/chat/completions` with OpenAI-compatible body. Streaming via `stream: true`.

- [ ] **Step 1: Write failing test**
- [ ] **Step 2-5:** Implement, test, commit

---

### Task 4: ConfigLoader — read config.json with $XXX expansion

**Files:**
- Create: `src/PicoNode.Agent/Config/ConfigLoader.cs`
- Create: `tests/PicoNode.Agent.Tests/Config/ConfigLoaderTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
[Test]
public async Task Load_WithEnvVarExpansion_ResolvesKey()
{
    Environment.SetEnvironmentVariable("MY_KEY", "sk-test-123");
    var json = """{"providers":{"test":{"apiKey":"$MY_KEY"}}}""";
    var path = Path.GetTempFileName();
    await File.WriteAllTextAsync(path, json);
    try
    {
        var config = ConfigLoader.Load(path);
        await Assert.That(config.Providers["test"].ApiKey).IsEqualTo("sk-test-123");
    }
    finally { File.Delete(path); Environment.SetEnvironmentVariable("MY_KEY", null); }
}
```

- [ ] **Step 2-5:** Implement `ConfigLoader.Load()`, merge with preset, test, commit

---

### Task 5: ResilientLLmClient

**Files:**
- Create: `src/PicoNode.AI/LLm/ResilientLLmClient.cs`
- Create: `tests/PicoNode.AI.Tests/LLm/ResilientLLmClientTests.cs`

Wraps: ProviderRouter → CircuitBreaker → Physical client call. Exposes `ILLmClient`.

- [ ] **Step 1: Write failing test — route + call**
- [ ] **Step 2-5:** Implement, test with mock breaker + mock clients, commit

---

### Task 6: PicoAgent Program.cs — /model /thinking /list-models

**Files:**
- Modify: `src/PicoAgent/Program.cs`

- [ ] **Step 1:** Add `/list-models` — calls config providers, queries GET /v1/models, displays
- [ ] **Step 2:** Add `/model <name>` — switches model via ProviderRouter
- [ ] **Step 3:** Add `/thinking <level>` — switches thinking level
- [ ] **Step 4:** Handle no config.json → create template + exit
- [ ] **Step 5:** Wire ResilientLLmClient into chat loop
- [ ] **Step 6:** Commit

---

## Plan Summary

| # | Task | Files | Tests |
|---|------|-------|:--:|
| 1 | ProviderPresets | 2 | 3 |
| 2 | OpenAISseParser | 2 | 1+ |
| 3 | OpenAILlmClient | 2 | 1+ |
| 4 | ConfigLoader | 2 | 1+ |
| 5 | ResilientLLmClient | 2 | 1+ |
| 6 | Program.cs integration | 1 | — |
| **Total** | | **11 files** | **7+ tests** |

**Dependencies:** Tasks 1→4→5→6 in order. Task 2→3 in order.
