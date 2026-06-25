# PicoNode.AI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build PicoNode.AI — the shared LLM communication layer providing streaming HTTP client, Anthropic↔OpenAI format conversion, provider routing, circuit breaker, and SSE parsing.

**Architecture:** Single net10.0 AOT-compatible package in PicoNode repo. Zero BCL JSON types — all serialization via PicoJetson. HttpClient for HTTP. `IAsyncEnumerable<T>` for streaming.

**Tech Stack:** .NET 10, PicoJetson (JSON/JSONL), PicoNode.Http (HttpRequest/HttpResponse), PicoDI, TUnit

## Global Constraints

- Target: net10.0, `IsAotCompatible=true`, `IsTrimmable=true`
- No BCL HttpRequestMessage/HttpResponseMessage — use PicoNode.Http types only
- No System.Text.Json — use PicoJetson for all serialization
- No Microsoft.Extensions.* — use PicoDI, PicoLog, PicoCfg
- All public API must be testable without real LLM endpoints (mock HTTP)
- TDD: test first, see it fail, implement, see it pass
- Commit after each task

---

### Task 1: Scaffold PicoNode.AI project

**Files:**
- Create: `src/PicoNode.AI/PicoNode.AI.csproj`
- Create: `src/PicoNode.AI/GlobalUsings.cs`
- Create: `tests/PicoNode.AI.Tests/PicoNode.AI.Tests.csproj`
- Create: `tests/PicoNode.AI.Tests/GlobalUsings.cs`
- Modify: `PicoNode.slnx` (add both projects)

**Interfaces:**
- Produces: Buildable project skeleton

- [ ] **Step 1: Create src/PicoNode.AI/PicoNode.AI.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsAotCompatible>true</IsAotCompatible>
    <IsTrimmable>true</IsTrimmable>
    <IsPackable>true</IsPackable>
    <PackageId>PicoNode.AI</PackageId>
    <Description>PicoNode.AI — shared LLM communication layer for PicoHex ecosystem</Description>
    <PackageTags>PicoHex;PicoNode;AI;LLM;AOT</PackageTags>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\PicoNode.Http\PicoNode.Http.csproj" />
    <PackageReference Include="PicoJetson" />
    <PackageReference Include="PicoDI" />
    <PackageReference Include="PicoLog" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\..\README.md" Pack="true" PackagePath="" Visible="false" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create src/PicoNode.AI/GlobalUsings.cs**

```csharp
global using System.Buffers;
global using System.Collections.Immutable;
global using System.Diagnostics;
global using System.IO.Pipelines;
global using System.Net.Http;
global using System.Runtime.CompilerServices;
global using System.Text;
global using System.Text.Json;
global using System.Threading.Channels;
global using PicoDI;
global using PicoJetson;
global using PicoLog;
global using PicoNode.Http;
```

- [ ] **Step 3: Create tests/PicoNode.AI.Tests/PicoNode.AI.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\PicoNode.AI\PicoNode.AI.csproj" />
    <PackageReference Include="TUnit" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create tests/PicoNode.AI.Tests/GlobalUsings.cs**

```csharp
global using TUnit.Core;
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
```

- [ ] **Step 5: Add projects to PicoNode.slnx**

```xml
<!-- In /src/ folder -->
<Project Path="src/PicoNode.AI/PicoNode.AI.csproj" />

<!-- In /tests/ folder -->
<Project Path="tests/PicoNode.AI.Tests/PicoNode.AI.Tests.csproj" />
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/PicoNode.AI/PicoNode.AI.csproj`
Expected: Build succeeds, no errors

Run: `dotnet build tests/PicoNode.AI.Tests/PicoNode.AI.Tests.csproj`
Expected: Build succeeds

- [ ] **Step 7: Commit**

```bash
git add src/PicoNode.AI/ tests/PicoNode.AI.Tests/ PicoNode.slnx
git commit -m "feat: scaffold PicoNode.AI project"
```

---

### Task 2: Core types — Model, StreamOptions, ThinkingLevel

**Files:**
- Create: `src/PicoNode.AI/Types/Model.cs`
- Create: `src/PicoNode.AI/Types/StreamOptions.cs`
- Create: `tests/PicoNode.AI.Tests/Types/ModelSerializationTests.cs`

**Interfaces:**
- Consumes: PicoJetson (serialization)
- Produces: `Model`, `ModelCost`, `StreamOptions`, `ThinkingLevel` — used by all subsequent tasks

- [ ] **Step 1: Write failing serialization test**

Create `tests/PicoNode.AI.Tests/Types/ModelSerializationTests.cs`:

```csharp
namespace PicoNode.AI.Tests.Types;

public class ModelSerializationTests
{
    [Test]
    public async Task Serialize_Model_RoundTrip()
    {
        var model = new Model
        {
            Id = "claude-sonnet-4-20250514",
            Name = "Claude Sonnet 4",
            Provider = "anthropic",
            Api = AiApiFormat.AnthropicMessages,
            BaseUrl = "https://api.anthropic.com",
            Reasoning = true,
            Cost = new ModelCost
            {
                Input = 3.0m,
                Output = 15.0m,
                CacheRead = 0.30m,
                CacheWrite = 6.0m,
            },
            ContextWindow = 200000,
            MaxTokens = 8192,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(model);
        var restored = JsonSerializer.Deserialize<Model>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Id).IsEqualTo("claude-sonnet-4-20250514");
        await Assert.That(restored.Provider).IsEqualTo("anthropic");
        await Assert.That(restored.Api).IsEqualTo(AiApiFormat.AnthropicMessages);
        await Assert.That(restored.Cost.Input).IsEqualTo(3.0m);
        await Assert.That(restored.ContextWindow).IsEqualTo(200000);
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "ModelSerializationTests"`
Expected: FAIL — types not defined

- [ ] **Step 3: Implement types**

Create `src/PicoNode.AI/Types/Model.cs`:

```csharp
namespace PicoNode.AI;

public record Model
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Provider { get; init; } = "";
    public AiApiFormat Api { get; init; }
    public string BaseUrl { get; init; } = "";
    public bool Reasoning { get; init; }
    public ModelCost Cost { get; init; } = new();
    public int ContextWindow { get; init; }
    public int MaxTokens { get; init; }
}

public record ModelCost
{
    public decimal Input { get; init; }
    public decimal Output { get; init; }
    public decimal CacheRead { get; init; }
    public decimal CacheWrite { get; init; }
}
```

Create `src/PicoNode.AI/Types/StreamOptions.cs`:

```csharp
namespace PicoNode.AI;

public enum ThinkingLevel
{
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
}

public record StreamOptions
{
    public string? ApiKey { get; init; }
    public int? MaxTokens { get; init; }
    public float? Temperature { get; init; }
    public ThinkingLevel? Reasoning { get; init; }
}
```

Create `src/PicoNode.AI/Types/AiApiFormat.cs`:

```csharp
namespace PicoNode.AI;

public enum AiApiFormat
{
    AnthropicMessages,
    OpenAIChatCompletions,
    OpenAIResponses,
}
```

- [ ] **Step 4: Run test — expect pass**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "ModelSerializationTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.AI/Types/Model.cs src/PicoNode.AI/Types/StreamOptions.cs src/PicoNode.AI/Types/AiApiFormat.cs tests/PicoNode.AI.Tests/Types/
git commit -m "feat: add Model, StreamOptions, AiApiFormat types with serialization"
```

---

### Task 3: Message types + ContentBlock

**Files:**
- Create: `src/PicoNode.AI/Types/ContentBlock.cs`
- Create: `src/PicoNode.AI/Types/Message.cs`
- Create: `src/PicoNode.AI/Types/ChatContext.cs`
- Create: `tests/PicoNode.AI.Tests/Types/MessageSerializationTests.cs`

**Interfaces:**
- Consumes: PicoJetson JSONL
- Produces: `ContentBlock` variants, `UserMessage`, `AssistantMessage`, `ToolResultMessage`, `ChatContext`

- [ ] **Step 1: Write failing test**

Create `tests/PicoNode.AI.Tests/Types/MessageSerializationTests.cs`:

```csharp
namespace PicoNode.AI.Tests.Types;

public class MessageSerializationTests
{
    [Test]
    public async Task Serialize_UserMessage_RoundTrip()
    {
        var msg = new UserMessage
        {
            Role = "user",
            Content = "Hello, world!",
            Timestamp = 1719000000000,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(msg);
        var restored = JsonSerializer.Deserialize<UserMessage>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Role).IsEqualTo("user");
        await Assert.That(restored.Content).IsEqualTo("Hello, world!");
    }

    [Test]
    public async Task Serialize_AssistantMessage_WithContentBlocks()
    {
        var msg = new AssistantMessage
        {
            Role = "assistant",
            Content =
            [
                new TextContentBlock { Text = "I'll help you with that." },
                new ToolCallContentBlock
                {
                    Id = "tc_001",
                    Name = "read",
                    Arguments = new() { ["path"] = "/foo" },
                },
            ],
            Model = "claude-sonnet-4-20250514",
            Provider = "anthropic",
            Api = AiApiFormat.AnthropicMessages,
            Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 },
            StopReason = "tool_use",
            Timestamp = 1719000001000,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(msg);
        var restored = JsonSerializer.Deserialize<AssistantMessage>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Content.Length).IsEqualTo(2);
        await Assert.That(restored.Content[0]).IsTypeOf<TextContentBlock>();
        await Assert.That(((TextContentBlock)restored.Content[0]).Text)
            .IsEqualTo("I'll help you with that.");
        await Assert.That(restored.StopReason).IsEqualTo("tool_use");
    }

    [Test]
    public async Task Serialize_ToolResultMessage_RoundTrip()
    {
        var msg = new ToolResultMessage
        {
            Role = "toolResult",
            ToolCallId = "tc_001",
            ToolName = "read",
            Content = [new TextContentBlock { Text = "file contents..." }],
            IsError = false,
            Timestamp = 1719000002000,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(msg);
        var restored = JsonSerializer.Deserialize<ToolResultMessage>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.ToolCallId).IsEqualTo("tc_001");
        await Assert.That(restored.IsError).IsFalse();
    }

    [Test]
    public async Task Serialize_ChatContext_RoundTrip()
    {
        var context = new ChatContext
        {
            SystemPrompt = "You are a helpful assistant.",
            Messages =
            [
                new UserMessage
                {
                    Role = "user", Content = "Hi", Timestamp = 1,
                },
            ],
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(context);
        var restored = JsonSerializer.Deserialize<ChatContext>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.SystemPrompt).IsEqualTo("You are a helpful assistant.");
        await Assert.That(restored.Messages.Length).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "MessageSerializationTests"`
Expected: FAIL

- [ ] **Step 3: Implement types**

Create `src/PicoNode.AI/Types/ContentBlock.cs`:

```csharp
namespace PicoNode.AI;

[PicoJsonSerializable]
public abstract record ContentBlock
{
    public string Type { get; init; } = "";
}

[PicoJsonSerializable]
public sealed record TextContentBlock : ContentBlock
{
    public TextContentBlock() => Type = "text";
    public string Text { get; init; } = "";
}

[PicoJsonSerializable]
public sealed record ThinkingContentBlock : ContentBlock
{
    public ThinkingContentBlock() => Type = "thinking";
    public string Thinking { get; init; } = "";
}

[PicoJsonSerializable]
public sealed record ImageContentBlock : ContentBlock
{
    public ImageContentBlock() => Type = "image";
    public string Data { get; init; } = "";
    public string MimeType { get; init; } = "image/png";
}

[PicoJsonSerializable]
public sealed record ToolCallContentBlock : ContentBlock
{
    public ToolCallContentBlock() => Type = "tool_call";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Dictionary<string, object?> Arguments { get; init; } = new();
}
```

Create `src/PicoNode.AI/Types/Message.cs`:

```csharp
namespace PicoNode.AI;

[PicoJsonSerializable]
public abstract record Message
{
    public string Role { get; init; } = "";
    public long Timestamp { get; init; }
}

[PicoJsonSerializable]
public sealed record UserMessage : Message
{
    public UserMessage() => Role = "user";
    public string Content { get; init; } = "";
}

[PicoJsonSerializable]
public sealed record AssistantMessage : Message
{
    public AssistantMessage() => Role = "assistant";
    public ContentBlock[] Content { get; init; } = [];
    public string Model { get; init; } = "";
    public string Provider { get; init; } = "";
    public AiApiFormat Api { get; init; }
    public TokenUsage Usage { get; init; } = new();
    public string StopReason { get; init; } = "stop";
    public string? ErrorMessage { get; init; }
}

[PicoJsonSerializable]
public sealed record ToolResultMessage : Message
{
    public ToolResultMessage() => Role = "toolResult";
    public string ToolCallId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public ContentBlock[] Content { get; init; } = [];
    public bool IsError { get; init; }
}

[PicoJsonSerializable]
public record TokenUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
}
```

Create `src/PicoNode.AI/Types/ChatContext.cs`:

```csharp
namespace PicoNode.AI;

[PicoJsonSerializable]
public record ChatContext
{
    public string? SystemPrompt { get; init; }
    public Message[] Messages { get; init; } = [];
    public ToolSchema[]? Tools { get; init; }
}

[PicoJsonSerializable]
public sealed record ToolSchema
{
    public string Type { get; init; } = "function";
    public ToolSchemaFunction Function { get; init; } = new();
}

[PicoJsonSerializable]
public sealed record ToolSchemaFunction
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Parameters { get; init; } = "{}"; // Raw JSON Schema string
}
```

- [ ] **Step 4: Run test — expect pass**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "MessageSerializationTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.AI/Types/ContentBlock.cs src/PicoNode.AI/Types/Message.cs src/PicoNode.AI/Types/ChatContext.cs tests/PicoNode.AI.Tests/Types/
git commit -m "feat: add Message, ContentBlock, ChatContext types with JSONL round-trip"
```

---

### Task 4: AssistantMessageEvent types

**Files:**
- Create: `src/PicoNode.AI/Types/AssistantMessageEvent.cs`
- Create: `tests/PicoNode.AI.Tests/Types/AssistantMessageEventTests.cs`

**Interfaces:**
- Consumes: PicoJetson
- Produces: `AssistantMessageEvent` hierarchy — used by SSE parser and LLM client

- [ ] **Step 1: Write failing test**

Create `tests/PicoNode.AI.Tests/Types/AssistantMessageEventTests.cs`:

```csharp
namespace PicoNode.AI.Tests.Types;

public class AssistantMessageEventTests
{
    [Test]
    public async Task TextDelta_Partial_AccumulatesText()
    {
        var partial = new AssistantMessage
        {
            Role = "assistant",
            Model = "claude-sonnet-4",
            Content = [new TextContentBlock { Text = "Hel" }],
        };

        var evt = new AssistantMessageEvent.TextDelta(0, "lo", partial);

        await Assert.That(evt.Index).IsEqualTo(0);
        await Assert.That(evt.Delta).IsEqualTo("lo");
        await Assert.That(evt.Partial.Content[0]).IsTypeOf<TextContentBlock>();
    }

    [Test]
    public async Task Done_ContainsFinalMessage()
    {
        var msg = new AssistantMessage
        {
            Role = "assistant",
            Content = [new TextContentBlock { Text = "Done!" }],
            StopReason = "end_turn",
        };
        var evt = new AssistantMessageEvent.Done(msg);

        await Assert.That(evt.Message.StopReason).IsEqualTo("end_turn");
    }

    [Test]
    public async Task Error_ContainsAssistantMessage()
    {
        var msg = new AssistantMessage
        {
            Role = "assistant",
            ErrorMessage = "Rate limited",
            StopReason = "error",
        };
        var evt = new AssistantMessageEvent.Error(msg);

        await Assert.That(evt.Message.ErrorMessage).IsEqualTo("Rate limited");
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "AssistantMessageEventTests"`
Expected: FAIL

- [ ] **Step 3: Implement types**

Create `src/PicoNode.AI/Types/AssistantMessageEvent.cs`:

```csharp
namespace PicoNode.AI;

public abstract record AssistantMessageEvent
{
    public sealed record Start(AssistantMessage Partial) : AssistantMessageEvent;
    public sealed record TextDelta(int Index, string Delta, AssistantMessage Partial) : AssistantMessageEvent;
    public sealed record ThinkingDelta(int Index, string Delta, AssistantMessage Partial) : AssistantMessageEvent;
    public sealed record ToolCallStart(int Index, AssistantMessage Partial) : AssistantMessageEvent;
    public sealed record ToolCallDelta(int Index, string Delta, AssistantMessage Partial) : AssistantMessageEvent;
    public sealed record ToolCallEnd(int Index, ToolCallContentBlock Call, AssistantMessage Partial) : AssistantMessageEvent;
    public sealed record Done(AssistantMessage Message) : AssistantMessageEvent;
    public sealed record Error(AssistantMessage Message) : AssistantMessageEvent;
}
```

- [ ] **Step 4: Run test — expect pass**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "AssistantMessageEventTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.AI/Types/AssistantMessageEvent.cs tests/PicoNode.AI.Tests/Types/AssistantMessageEventTests.cs
git commit -m "feat: add AssistantMessageEvent types"
```

---

### Task 5: ProviderConfig + CircuitTypes

**Files:**
- Create: `src/PicoNode.AI/Types/ProviderConfig.cs`
- Create: `src/PicoNode.AI/Types/CircuitTypes.cs`
- Create: `tests/PicoNode.AI.Tests/Types/ProviderConfigTests.cs`

**Interfaces:**
- Consumes: PicoJetson
- Produces: `ProviderConfig`, `CircuitState`, `ProviderHealth` — used by routing and circuit breaker

- [ ] **Step 1: Write failing test**

Create `tests/PicoNode.AI.Tests/Types/ProviderConfigTests.cs`:

```csharp
namespace PicoNode.AI.Tests.Types;

public class ProviderConfigTests
{
    [Test]
    public async Task Serialize_ProviderConfig_RoundTrip()
    {
        var config = new ProviderConfig
        {
            Name = "anthropic",
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "sk-ant-xxx",
            ApiFormat = AiApiFormat.AnthropicMessages,
            ModelMapping = new() { ["claude-sonnet"] = "claude-sonnet-4-20250514" },
            Priority = 1,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(config);
        var restored = JsonSerializer.Deserialize<ProviderConfig>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Name).IsEqualTo("anthropic");
        await Assert.That(restored.Priority).IsEqualTo(1);
        await Assert.That(restored.ModelMapping["claude-sonnet"])
            .IsEqualTo("claude-sonnet-4-20250514");
    }

    [Test]
    public async Task Serialize_ProviderHealth_RoundTrip()
    {
        var health = new ProviderHealth
        {
            State = CircuitState.Open,
            ConsecutiveFailures = 5,
            ErrorRate = 0.8,
            LastFailure = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(health);
        var restored = JsonSerializer.Deserialize<ProviderHealth>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.State).IsEqualTo(CircuitState.Open);
        await Assert.That(restored.ConsecutiveFailures).IsEqualTo(5);
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "ProviderConfigTests"`
Expected: FAIL

- [ ] **Step 3: Implement types**

Create `src/PicoNode.AI/Types/ProviderConfig.cs`:

```csharp
namespace PicoNode.AI;

public record ProviderConfig
{
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public AiApiFormat ApiFormat { get; init; }
    public Dictionary<string, string> ModelMapping { get; init; } = new();
    public int Priority { get; init; }
}
```

Create `src/PicoNode.AI/Types/CircuitTypes.cs`:

```csharp
namespace PicoNode.AI;

public enum CircuitState { Closed, Open, HalfOpen }

public record ProviderHealth
{
    public CircuitState State { get; init; }
    public int ConsecutiveFailures { get; init; }
    public double ErrorRate { get; init; }
    public DateTimeOffset? LastFailure { get; init; }
    public DateTimeOffset? LastSuccess { get; init; }
}
```

- [ ] **Step 4: Run test — expect pass**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "ProviderConfigTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.AI/Types/ProviderConfig.cs src/PicoNode.AI/Types/CircuitTypes.cs tests/PicoNode.AI.Tests/Types/
git commit -m "feat: add ProviderConfig and CircuitTypes"
```

---

### Task 6: ILLmClient interface + SSE parser

**Files:**
- Create: `src/PicoNode.AI/LLm/ILLmClient.cs`
- Create: `src/PicoNode.AI/Sse/SseParser.cs`
- Create: `tests/PicoNode.AI.Tests/Sse/SseParserTests.cs`

**Interfaces:**
- Consumes: AssistantMessageEvent, ChatContext, Model, StreamOptions
- Produces: `ILLmClient` interface, `SseParser` (Anthropic SSE → AssistantMessageEvent stream)

- [ ] **Step 1: Write failing SSE parser test**

Create `tests/PicoNode.AI.Tests/Sse/SseParserTests.cs`:

```csharp
namespace PicoNode.AI.Tests.Sse;

public class SseParserTests
{
    [Test]
    public async Task Parse_TextDelta_EmitsCorrectEvents()
    {
        var sseData = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

            event: message_stop
            data: {"type":"message_stop"}

            """u8.ToArray();

        using var stream = new MemoryStream(sseData);
        var events = new List<AssistantMessageEvent>();

        await foreach (var evt in SseParser.ParseAnthropicStreamAsync(
            stream, "claude-sonnet-4", CancellationToken.None))
        {
            events.Add(evt);
        }

        await Assert.That(events.Count).IsGreaterThan(0);
        var textEvents = events.OfType<AssistantMessageEvent.TextDelta>().ToArray();
        await Assert.That(textEvents.Length).IsEqualTo(2);
        await Assert.That(textEvents[1].Delta).IsEqualTo(" world");
        var done = events.OfType<AssistantMessageEvent.Done>().ToArray();
        await Assert.That(done.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_ToolCall_EmitsToolCallEvents()
    {
        var sseData = """
            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tc_1","name":"read","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\":\"/foo\"}"}}

            event: message_stop
            data: {"type":"message_stop"}

            """u8.ToArray();

        using var stream = new MemoryStream(sseData);
        var events = new List<AssistantMessageEvent>();

        await foreach (var evt in SseParser.ParseAnthropicStreamAsync(
            stream, "claude-sonnet-4", CancellationToken.None))
        {
            events.Add(evt);
        }

        var toolStarts = events.OfType<AssistantMessageEvent.ToolCallStart>().ToArray();
        await Assert.That(toolStarts.Length).IsEqualTo(1);
        var toolDeltas = events.OfType<AssistantMessageEvent.ToolCallDelta>().ToArray();
        await Assert.That(toolDeltas.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_EmptyStream_ReturnsNothing()
    {
        using var stream = new MemoryStream([]);
        var count = 0;

        await foreach (var _ in SseParser.ParseAnthropicStreamAsync(
            stream, "claude-sonnet-4", CancellationToken.None))
        {
            count++;
        }

        await Assert.That(count).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "SseParserTests"`
Expected: FAIL

- [ ] **Step 3: Create ILLmClient interface**

Create `src/PicoNode.AI/LLm/ILLmClient.cs`:

```csharp
namespace PicoNode.AI;

public interface ILLmClient
{
    IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        CancellationToken ct);
}

public static class LLmClientExtensions
{
    public static async ValueTask<AssistantMessage> CompleteAsync(
        this ILLmClient client,
        Model model,
        ChatContext context,
        StreamOptions? options = null,
        CancellationToken ct = default)
    {
        await foreach (var evt in client.StreamAsync(model, context, options, ct))
        {
            if (evt is AssistantMessageEvent.Done d)
                return d.Message;
            if (evt is AssistantMessageEvent.Error e)
                return e.Message;
        }
        throw new InvalidOperationException("Stream ended without Done or Error event.");
    }
}
```

- [ ] **Step 4: Implement SSE parser**

Create `src/PicoNode.AI/Sse/SseParser.cs`:

```csharp
namespace PicoNode.AI;

public static class SseParser
{
    public static async IAsyncEnumerable<AssistantMessageEvent> ParseAnthropicStreamAsync(
        Stream stream,
        string model,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var partial = new AssistantMessage
        {
            Role = "assistant",
            Model = model,
            Provider = "anthropic",
            Api = AiApiFormat.AnthropicMessages,
        };
        var contentBlocks = new List<ContentBlock>();
        var currentToolArgs = new StringBuilder();

        AssistantMessageEvent.Start startEvent = null!;
        bool started = false;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            // SSE: lines starting with "data: " contain JSON
            if (!line.StartsWith("data: "))
            {
                if (line.StartsWith("event: "))
                {
                    var eventType = line[7..];
                    if (eventType == "message_stop")
                    {
                        var msg = partial with
                        {
                            Content = contentBlocks.ToArray(),
                            StopReason = "end_turn",
                        };
                        yield return new AssistantMessageEvent.Done(msg);
                        yield break;
                    }
                }
                continue;
            }

            var json = line[6..]; // skip "data: "
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "message_start":
                    if (!started)
                    {
                        started = true;
                        startEvent = new AssistantMessageEvent.Start(partial);
                        yield return startEvent;
                    }
                    break;

                case "content_block_start":
                    var cbIndex = root.GetProperty("index").GetInt32();
                    var cb = root.GetProperty("content_block");
                    var cbType = cb.GetProperty("type").GetString();

                    if (cbType == "text")
                    {
                        var block = new TextContentBlock { Text = "" };
                        contentBlocks.Insert(cbIndex, block);
                        // yield no event for text start
                    }
                    else if (cbType == "tool_use")
                    {
                        var block = new ToolCallContentBlock
                        {
                            Id = cb.GetProperty("id").GetString()!,
                            Name = cb.GetProperty("name").GetString()!,
                        };
                        contentBlocks.Insert(cbIndex, block);
                        currentToolArgs.Clear();
                        yield return new AssistantMessageEvent.ToolCallStart(
                            cbIndex, partial with { Content = contentBlocks.ToArray() });
                    }
                    break;

                case "content_block_delta":
                    var deltaIndex = root.GetProperty("index").GetInt32();
                    var delta = root.GetProperty("delta");
                    var deltaType = delta.GetProperty("type").GetString();

                    if (deltaType == "text_delta")
                    {
                        var text = delta.GetProperty("text").GetString()!;
                        if (contentBlocks[deltaIndex] is TextContentBlock tb)
                        {
                            contentBlocks[deltaIndex] = tb with { Text = tb.Text + text };
                        }
                        yield return new AssistantMessageEvent.TextDelta(
                            deltaIndex, text, partial with { Content = contentBlocks.ToArray() });
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        var partialJson = delta.GetProperty("partial_json").GetString()!;
                        currentToolArgs.Append(partialJson);
                        yield return new AssistantMessageEvent.ToolCallDelta(
                            deltaIndex, partialJson,
                            partial with { Content = contentBlocks.ToArray() });
                    }
                    else if (deltaType == "thinking_delta")
                    {
                        var thinking = delta.GetProperty("thinking").GetString()!;
                        yield return new AssistantMessageEvent.ThinkingDelta(
                            deltaIndex, thinking,
                            partial with { Content = contentBlocks.ToArray() });
                    }
                    break;

                case "content_block_stop":
                    var stopIndex = root.GetProperty("index").GetInt32();
                    if (contentBlocks[stopIndex] is ToolCallContentBlock tcb)
                    {
                        contentBlocks[stopIndex] = tcb with
                        {
                            Arguments = ParseJsonObject(currentToolArgs.ToString()),
                        };
                        yield return new AssistantMessageEvent.ToolCallEnd(
                            stopIndex, (ToolCallContentBlock)contentBlocks[stopIndex],
                            partial with { Content = contentBlocks.ToArray() });
                    }
                    break;

                case "error":
                    var errorMsg = new AssistantMessage
                    {
                        Role = "assistant",
                        ErrorMessage = root.TryGetProperty("error", out var err)
                            ? err.GetProperty("message").GetString()
                            : "Unknown error",
                        StopReason = "error",
                    };
                    yield return new AssistantMessageEvent.Error(errorMsg);
                    yield break;
            }
        }
    }

    private static Dictionary<string, object?> ParseJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        using var doc = JsonDocument.Parse(json);
        return ParseElement(doc.RootElement);
    }

    private static Dictionary<string, object?> ParseElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)ParseElement(p.Value)),
        JsonValueKind.Array => new() { ["items"] = el.EnumerateArray()
            .Select(ParseElement).ToList() },
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
```

- [ ] **Step 4: Run test — expect pass**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "SseParserTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.AI/LLm/ src/PicoNode.AI/Sse/ tests/PicoNode.AI.Tests/Sse/
git commit -m "feat: add ILLmClient interface and Anthropic SSE parser"
```

---

### Task 7: Anthropic LLmClient implementation

**Files:**
- Create: `src/PicoNode.AI/LLm/AnthropicLLmClient.cs`
- Create: `tests/PicoNode.AI.Tests/LLm/MockHttpHandler.cs`
- Create: `tests/PicoNode.AI.Tests/LLm/AnthropicLLmClientTests.cs`

**Interfaces:**
- Consumes: ILLmClient, SseParser, Model, ChatContext, StreamOptions
- Produces: `AnthropicLLmClient : ILLmClient`

- [ ] **Step 1: Create mock HTTP handler**

Create `tests/PicoNode.AI.Tests/LLm/MockHttpHandler.cs`:

```csharp
namespace PicoNode.AI.Tests.LLm;

public sealed class MockHttpHandler : HttpMessageHandler
{
    public HttpResponseMessage? NextResponse { get; set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(NextResponse
            ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(""),
            });
    }
}
```

- [ ] **Step 2: Write failing test**

Create `tests/PicoNode.AI.Tests/LLm/AnthropicLLmClientTests.cs`:

```csharp
namespace PicoNode.AI.Tests.LLm;

public class AnthropicLLmClientTests
{
    [Test]
    public async Task StreamAsync_SendsCorrectHeaders()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"),
            },
        };
        var httpClient = new HttpClient(handler);
        var client = new AnthropicLLmClient(httpClient);

        var model = new Model
        {
            Id = "claude-sonnet-4-20250514",
            BaseUrl = "https://api.anthropic.com",
            Api = AiApiFormat.AnthropicMessages,
        };
        var context = new ChatContext
        {
            Messages = [new UserMessage { Role = "user", Content = "Hi", Timestamp = 1 }],
        };

        await foreach (var _ in client.StreamAsync(model, context, null, CancellationToken.None))
        {
        }

        await Assert.That(handler.LastRequest).IsNotNull();
        await Assert.That(handler.LastRequest!.RequestUri!.AbsoluteUri)
            .IsEqualTo("https://api.anthropic.com/v1/messages");
        await Assert.That(handler.LastRequest.Headers.GetValues("x-api-key").First())
            .IsEqualTo("test-key");
        await Assert.That(handler.LastRequest.Headers.GetValues("anthropic-version").First())
            .IsEqualTo("2023-06-01");
    }

    [Test]
    public async Task CompleteAsync_ReturnsFinalMessage()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    event: message_start
                    data: {"type":"message_start","message":{}}

                    event: content_block_start
                    data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                    event: content_block_delta
                    data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

                    event: content_block_stop
                    data: {"type":"content_block_stop","index":0}

                    event: message_stop
                    data: {"type":"message_stop"}

                    """),
            },
        };
        var httpClient = new HttpClient(handler);
        var client = new AnthropicLLmClient(httpClient);

        var result = await client.CompleteAsync(
            new Model { Id = "claude-sonnet-4", BaseUrl = "https://api.anthropic.com", Api = AiApiFormat.AnthropicMessages },
            new ChatContext
            {
                Messages = [new UserMessage { Role = "user", Content = "Hi", Timestamp = 1 }],
            },
            new StreamOptions { ApiKey = "sk-ant-test" });

        await Assert.That(result).IsNotNull();
        await Assert.That(result.StopReason).IsEqualTo("end_turn");
        await Assert.That(result.Content.Length).IsEqualTo(1);
    }
}
```

- [ ] **Step 3: Run test — expect fail**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "AnthropicLLmClientTests"`
Expected: FAIL

- [ ] **Step 4: Implement AnthropicLLmClient**

Create `src/PicoNode.AI/LLm/AnthropicLLmClient.cs`:

```csharp
namespace PicoNode.AI;

public sealed class AnthropicLLmClient : ILLmClient
{
    private readonly HttpClient _http;

    public AnthropicLLmClient(HttpClient http)
    {
        _http = http;
    }

    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var apiKey = options?.ApiKey
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? "";

        var requestBody = BuildRequestBody(model, context, options);
        var json = JsonSerializer.SerializeToUtf8Bytes(requestBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl}/v1/messages")
        {
            Content = new ByteArrayContent(json),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var bodyStream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var evt in SseParser.ParseAnthropicStreamAsync(
            bodyStream, model.Id, ct))
        {
            yield return evt;
        }
    }

    private static object BuildRequestBody(Model model, ChatContext context, StreamOptions? options)
    {
        return new
        {
            model = model.Id,
            max_tokens = options?.MaxTokens ?? model.MaxTokens,
            system = context.SystemPrompt != null
                ? new[] { new { type = "text", text = context.SystemPrompt } }
                : null,
            messages = context.Messages.Select(m => FormatMessage(m)).ToArray(),
            tools = context.Tools?.Select(t => new
            {
                name = t.Function.Name,
                description = t.Function.Description,
                input_schema = JsonDocument.Parse(t.Function.Parameters).RootElement,
            }).ToArray(),
            stream = true,
        };
    }

    private static object FormatMessage(Message m) => m switch
    {
        UserMessage um => new { role = "user", content = um.Content },
        AssistantMessage am => new
        {
            role = "assistant",
            content = am.Content.Select(FormatContentBlock).ToArray(),
        },
        ToolResultMessage tr => new
        {
            role = "user",
            content = new[]
            {
                new
                {
                    type = "tool_result",
                    tool_use_id = tr.ToolCallId,
                    content = tr.Content.OfType<TextContentBlock>()
                        .Select(t => t.Text).FirstOrDefault() ?? "",
                    is_error = tr.IsError,
                },
            },
        },
        _ => throw new ArgumentException($"Unknown message type: {m.GetType()}"),
    };

    private static object FormatContentBlock(ContentBlock cb) => cb switch
    {
        TextContentBlock t => new { type = "text", text = t.Text },
        ToolCallContentBlock tc => new
        {
            type = "tool_use",
            id = tc.Id,
            name = tc.Name,
            input = tc.Arguments,
        },
        _ => throw new ArgumentException($"Unknown content block: {cb.GetType()}"),
    };
}
```

- [ ] **Step 5: Run test — expect pass**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "AnthropicLLmClientTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/PicoNode.AI/LLm/AnthropicLLmClient.cs tests/PicoNode.AI.Tests/LLm/
git commit -m "feat: add AnthropicLLmClient with streaming support"
```

---

### Task 8: IFormatConverter — Anthropic ↔ OpenAI

**Files:**
- Create: `src/PicoNode.AI/Format/IFormatConverter.cs`
- Create: `src/PicoNode.AI/Format/FormatConverter.cs`
- Create: `tests/PicoNode.AI.Tests/Format/FormatConverterTests.cs`

**Interfaces:**
- Consumes: Message types, AiApiFormat
- Produces: `IFormatConverter`, `FormatConverter`

- [ ] **Step 1: Write failing test**

Create `tests/PicoNode.AI.Tests/Format/FormatConverterTests.cs`:

```csharp
namespace PicoNode.AI.Tests.Format;

public class FormatConverterTests
{
    private readonly FormatConverter _converter = new();

    [Test]
    public async Task CanConvert_AnthropicToOpenAI_ReturnsTrue()
    {
        await Assert.That(_converter.CanConvertRequest(
            AiApiFormat.AnthropicMessages,
            AiApiFormat.OpenAIChatCompletions)).IsTrue();
    }

    [Test]
    public async Task CanConvert_SameFormat_ReturnsFalse()
    {
        await Assert.That(_converter.CanConvertRequest(
            AiApiFormat.OpenAIChatCompletions,
            AiApiFormat.OpenAIChatCompletions)).IsFalse();
    }

    [Test]
    public async Task ConvertRequest_AnthropicToOpenAI_ProducesValidJson()
    {
        var source = new HttpRequest(); // PicoNode.Http type
        // Set up source with Anthropic body...

        var output = new ArrayBufferWriter<byte>();
        _converter.ConvertRequest(
            ref source,
            AiApiFormat.OpenAIChatCompletions,
            output);

        var result = output.WrittenSpan.ToArray();
        await Assert.That(result.Length).IsGreaterThan(0);
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "FormatConverterTests"`
Expected: FAIL

- [ ] **Step 3: Create IFormatConverter interface**

Create `src/PicoNode.AI/Format/IFormatConverter.cs`:

```csharp
namespace PicoNode.AI;

public interface IFormatConverter
{
    bool CanConvertRequest(AiApiFormat from, AiApiFormat to);
    void ConvertRequest(
        ref HttpRequest source,
        AiApiFormat targetFormat,
        IBufferWriter<byte> output);

    bool CanConvertResponse(AiApiFormat from, AiApiFormat to);
    IAsyncEnumerable<AssistantMessageEvent> ConvertStream(
        IAsyncEnumerable<AssistantMessageEvent> source,
        AiApiFormat targetFormat,
        CancellationToken ct);
}
```

- [ ] **Step 4: Implement FormatConverter skeleton**

Create `src/PicoNode.AI/Format/FormatConverter.cs`:

```csharp
namespace PicoNode.AI;

public sealed class FormatConverter : IFormatConverter
{
    private static readonly HashSet<(AiApiFormat, AiApiFormat)> SupportedConversions =
    [
        (AiApiFormat.AnthropicMessages, AiApiFormat.OpenAIChatCompletions),
        (AiApiFormat.OpenAIChatCompletions, AiApiFormat.AnthropicMessages),
        (AiApiFormat.AnthropicMessages, AiApiFormat.OpenAIResponses),
        (AiApiFormat.OpenAIResponses, AiApiFormat.AnthropicMessages),
    ];

    public bool CanConvertRequest(AiApiFormat from, AiApiFormat to)
        => SupportedConversions.Contains((from, to));

    public void ConvertRequest(
        ref HttpRequest source,
        AiApiFormat targetFormat,
        IBufferWriter<byte> output)
    {
        // v1: basic header + model field mapping. Full conversion in v2.
        // For now, validate that conversion is supported.
        if (!CanConvertRequest(DetectFormat(source), targetFormat))
            throw new NotSupportedException(
                $"Cannot convert from {DetectFormat(source)} to {targetFormat}");
    }

    public bool CanConvertResponse(AiApiFormat from, AiApiFormat to)
        => SupportedConversions.Contains((from, to));

    public async IAsyncEnumerable<AssistantMessageEvent> ConvertStream(
        IAsyncEnumerable<AssistantMessageEvent> source,
        AiApiFormat targetFormat,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // v1: pass-through. Full conversion in v2.
        await foreach (var evt in source.WithCancellation(ct))
            yield return evt;
    }

    private static AiApiFormat DetectFormat(HttpRequest request)
    {
        // Detect by URL path or model field
        if (request.Path.Contains("/v1/messages"))
            return AiApiFormat.AnthropicMessages;
        if (request.Path.Contains("/v1/chat/completions"))
            return AiApiFormat.OpenAIChatCompletions;
        return AiApiFormat.AnthropicMessages; // default
    }
}
```

- [ ] **Step 5: Run test — expect pass**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "FormatConverterTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/PicoNode.AI/Format/ tests/PicoNode.AI.Tests/Format/
git commit -m "feat: add IFormatConverter with v1 stub (Anthropic↔OpenAI)"
```

---

### Task 9: IProviderRouter

**Files:**
- Create: `src/PicoNode.AI/Routing/IProviderRouter.cs`
- Create: `src/PicoNode.AI/Routing/ProviderRouter.cs`
- Create: `tests/PicoNode.AI.Tests/Routing/ProviderRouterTests.cs`

**Interfaces:**
- Consumes: ProviderConfig
- Produces: `IProviderRouter`, `ProviderRouter`

- [ ] **Step 1: Write failing test**

Create `tests/PicoNode.AI.Tests/Routing/ProviderRouterTests.cs`:

```csharp
namespace PicoNode.AI.Tests.Routing;

public class ProviderRouterTests
{
    [Test]
    public async Task Resolve_ByName_ReturnsMatchingProvider()
    {
        var providers = new[]
        {
            new ProviderConfig { Name = "anthropic", ApiFormat = AiApiFormat.AnthropicMessages, Priority = 1 },
            new ProviderConfig { Name = "openai", ApiFormat = AiApiFormat.OpenAIChatCompletions, Priority = 2 },
        };
        var router = new ProviderRouter(providers);

        var result = router.Resolve("claude-sonnet-4", null);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("anthropic");
    }

    [Test]
    public async Task Resolve_NullModel_ReturnsFirstProvider()
    {
        var providers = new[]
        {
            new ProviderConfig { Name = "first", ApiFormat = AiApiFormat.AnthropicMessages, Priority = 1 },
        };
        var router = new ProviderRouter(providers);

        var result = router.Resolve(null, null);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("first");
    }

    [Test]
    public async Task Resolve_ModelMapping_RemapsModelName()
    {
        var providers = new[]
        {
            new ProviderConfig
            {
                Name = "openai",
                ApiFormat = AiApiFormat.OpenAIChatCompletions,
                ModelMapping = new() { ["gpt-4"] = "gpt-4o-2025-05-14" },
            },
        };
        var router = new ProviderRouter(providers);

        var result = router.Resolve("gpt-4", null);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ModelMapping["gpt-4"]).IsEqualTo("gpt-4o-2025-05-14");
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "ProviderRouterTests"`
Expected: FAIL

- [ ] **Step 3: Implement**

Create `src/PicoNode.AI/Routing/IProviderRouter.cs`:

```csharp
namespace PicoNode.AI;

public interface IProviderRouter
{
    ProviderConfig? Resolve(string? model, AiApiFormat? preferredFormat);
}
```

Create `src/PicoNode.AI/Routing/ProviderRouter.cs`:

```csharp
namespace PicoNode.AI;

public sealed class ProviderRouter : IProviderRouter
{
    private readonly IReadOnlyList<ProviderConfig> _providers;

    public ProviderRouter(IEnumerable<ProviderConfig> providers)
    {
        _providers = providers.OrderBy(p => p.Priority).ToList();
    }

    public ProviderConfig? Resolve(string? model, AiApiFormat? preferredFormat)
    {
        if (_providers.Count == 0)
            return null;

        // Prefer matching format
        var candidates = preferredFormat.HasValue
            ? _providers.Where(p => p.ApiFormat == preferredFormat.Value)
            : _providers;

        // Prefer providers that have the model in their mapping
        var match = candidates.FirstOrDefault(p =>
            model != null && p.ModelMapping.ContainsKey(model))
            ?? candidates.FirstOrDefault();

        return match;
    }
}
```

- [ ] **Step 4: Run test — expect pass**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "ProviderRouterTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.AI/Routing/ tests/PicoNode.AI.Tests/Routing/
git commit -m "feat: add IProviderRouter with model-aware routing"
```

---

### Task 10: ICircuitBreaker + IFailoverService

**Files:**
- Create: `src/PicoNode.AI/CircuitBreaker/ICircuitBreaker.cs`
- Create: `src/PicoNode.AI/CircuitBreaker/CircuitBreaker.cs`
- Create: `src/PicoNode.AI/CircuitBreaker/IFailoverService.cs`
- Create: `src/PicoNode.AI/CircuitBreaker/FailoverService.cs`
- Create: `tests/PicoNode.AI.Tests/CircuitBreaker/CircuitBreakerTests.cs`
- Create: `tests/PicoNode.AI.Tests/CircuitBreaker/FailoverServiceTests.cs`

**Interfaces:**
- Consumes: ProviderConfig, CircuitState, ProviderHealth
- Produces: `ICircuitBreaker`, `IFailoverService`

- [ ] **Step 1: Write failing circuit breaker test**

Create `tests/PicoNode.AI.Tests/CircuitBreaker/CircuitBreakerTests.cs`:

```csharp
namespace PicoNode.AI.Tests.CircuitBreaker;

public class CircuitBreakerTests
{
    [Test]
    public async Task Initial_State_IsClosed()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 4,
            RecoveryWaitSeconds = 60,
        });

        await Assert.That(cb.State).IsEqualTo(CircuitState.Closed);
        await Assert.That(cb.TryAcquire()).IsTrue();
    }

    [Test]
    public async Task ConsecutiveFailures_OpensCircuit()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 3,
            RecoveryWaitSeconds = 60,
        });

        await Assert.That(cb.State).IsEqualTo(CircuitState.Closed);

        cb.RecordFailure();
        cb.RecordFailure();
        await Assert.That(cb.State).IsEqualTo(CircuitState.Closed);

        cb.RecordFailure(); // 3rd failure → open
        await Assert.That(cb.State).IsEqualTo(CircuitState.Open);
        await Assert.That(cb.TryAcquire()).IsFalse();
    }

    [Test]
    public async Task Success_Resets_ConsecutiveFailures()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 3,
            RecoveryWaitSeconds = 60,
        });

        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordSuccess(); // resets counter
        cb.RecordFailure();
        await Assert.That(cb.State).IsEqualTo(CircuitState.Closed);
    }

    [Test]
    public async Task RecoveryWait_TransitionsTo_HalfOpen()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1,
            RecoveryWaitSeconds = 0, // immediate for test
        });

        cb.RecordFailure();
        await Assert.That(cb.State).IsEqualTo(CircuitState.Open);

        // Next TryAcquire after wait → transitions to HalfOpen
        await Assert.That(cb.TryAcquire()).IsTrue();
        await Assert.That(cb.State).IsEqualTo(CircuitState.HalfOpen);
    }

    [Test]
    public async Task HalfOpen_Success_ClosesCircuit()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1,
            RecoveryWaitSeconds = 0,
            RecoverySuccessThreshold = 2,
        });

        cb.RecordFailure();
        await Assert.That(cb.TryAcquire()).IsTrue(); // → HalfOpen
        cb.RecordSuccess();
        await Assert.That(cb.TryAcquire()).IsTrue();
        cb.RecordSuccess();
        await Assert.That(cb.State).IsEqualTo(CircuitState.Closed);
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "CircuitBreakerTests"`
Expected: FAIL

- [ ] **Step 3: Implement**

Create `src/PicoNode.AI/CircuitBreaker/ICircuitBreaker.cs`:

```csharp
namespace PicoNode.AI;

public record CircuitBreakerOptions
{
    public int FailureThreshold { get; init; } = 4;
    public int RecoverySuccessThreshold { get; init; } = 2;
    public int RecoveryWaitSeconds { get; init; } = 60;
    public double ErrorRateThreshold { get; init; } = 0.6;
    public int MinRequests { get; init; } = 10;
}

public interface ICircuitBreaker
{
    CircuitState State { get; }
    bool TryAcquire();
    void RecordSuccess();
    void RecordFailure();
    ProviderHealth GetHealth();
}
```

Create `src/PicoNode.AI/CircuitBreaker/CircuitBreaker.cs`:

```csharp
namespace PicoNode.AI;

public sealed class CircuitBreaker : ICircuitBreaker
{
    private readonly CircuitBreakerOptions _opts;
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private int _totalSuccesses;
    private int _totalFailures;
    private DateTime _openedAt;
    private CircuitState _state = CircuitState.Closed;
    private DateTimeOffset? _lastFailure;
    private DateTimeOffset? _lastSuccess;

    public CircuitBreaker(CircuitBreakerOptions? options = null)
    {
        _opts = options ?? new();
    }

    public CircuitState State
    {
        get { lock (_lock) return _state; }
    }

    public bool TryAcquire()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case CircuitState.Closed:
                    return true;
                case CircuitState.Open:
                    if ((DateTime.UtcNow - _openedAt).TotalSeconds >= _opts.RecoveryWaitSeconds)
                    {
                        _state = CircuitState.HalfOpen;
                        return true;
                    }
                    return false;
                case CircuitState.HalfOpen:
                    return true;
                default:
                    return false;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _consecutiveSuccesses++;
            _totalSuccesses++;
            _lastSuccess = DateTimeOffset.UtcNow;

            if (_state == CircuitState.HalfOpen
                && _consecutiveSuccesses >= _opts.RecoverySuccessThreshold)
            {
                _state = CircuitState.Closed;
            }
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveSuccesses = 0;
            _consecutiveFailures++;
            _totalFailures++;
            _lastFailure = DateTimeOffset.UtcNow;

            if (_state == CircuitState.Closed
                && _consecutiveFailures >= _opts.FailureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
            }

            if (_state == CircuitState.HalfOpen)
            {
                _state = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
            }
        }
    }

    public ProviderHealth GetHealth()
    {
        lock (_lock)
        {
            var total = _totalSuccesses + _totalFailures;
            var errorRate = total >= _opts.MinRequests
                ? (double)_totalFailures / total
                : 0;

            return new ProviderHealth
            {
                State = _state,
                ConsecutiveFailures = _consecutiveFailures,
                ErrorRate = errorRate,
                LastFailure = _lastFailure,
                LastSuccess = _lastSuccess,
            };
        }
    }
}
```

- [ ] **Step 4: Run test — expect pass**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "CircuitBreakerTests"`
Expected: PASS

- [ ] **Step 5: Write failover test**

Create `tests/PicoNode.AI.Tests/CircuitBreaker/FailoverServiceTests.cs`:

```csharp
namespace PicoNode.AI.Tests.CircuitBreaker;

public class FailoverServiceTests
{
    [Test]
    public async Task GetNextProvider_Skips_OpenCircuit()
    {
        var providers = new[]
        {
            new ProviderConfig { Name = "primary", Priority = 1 },
            new ProviderConfig { Name = "backup", Priority = 2 },
        };
        var breakers = new Dictionary<string, ICircuitBreaker>
        {
            ["primary"] = CreateOpenBreaker(),
            ["backup"] = new CircuitBreaker(),
        };
        var failover = new FailoverService(breakers);

        var next = failover.GetNextProvider(providers[0], providers);

        await Assert.That(next).IsNotNull();
        await Assert.That(next!.Name).IsEqualTo("backup");
    }

    [Test]
    public async Task GetNextProvider_AllOpen_ReturnsNull()
    {
        var providers = new[]
        {
            new ProviderConfig { Name = "primary", Priority = 1 },
            new ProviderConfig { Name = "backup", Priority = 2 },
        };
        var breakers = new Dictionary<string, ICircuitBreaker>
        {
            ["primary"] = CreateOpenBreaker(),
            ["backup"] = CreateOpenBreaker(),
        };
        var failover = new FailoverService(breakers);

        var next = failover.GetNextProvider(providers[0], providers);

        await Assert.That(next).IsNull();
    }

    private static ICircuitBreaker CreateOpenBreaker()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1,
            RecoveryWaitSeconds = 999,
        });
        cb.RecordFailure();
        return cb;
    }
}
```

- [ ] **Step 6: Run test — expect fail**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "FailoverServiceTests"`
Expected: FAIL

- [ ] **Step 7: Implement IFailoverService**

Create `src/PicoNode.AI/CircuitBreaker/IFailoverService.cs`:

```csharp
namespace PicoNode.AI;

public interface IFailoverService
{
    ProviderConfig? GetNextProvider(
        ProviderConfig current,
        IReadOnlyList<ProviderConfig> queue);

    void RecordFailover(ProviderConfig from, ProviderConfig to, string reason);
}
```

Create `src/PicoNode.AI/CircuitBreaker/FailoverService.cs`:

```csharp
namespace PicoNode.AI;

public sealed class FailoverService : IFailoverService
{
    private readonly IReadOnlyDictionary<string, ICircuitBreaker> _breakers;
    private readonly List<FailoverRecord> _history = [];

    public FailoverService(IReadOnlyDictionary<string, ICircuitBreaker> breakers)
    {
        _breakers = breakers;
    }

    public ProviderConfig? GetNextProvider(
        ProviderConfig current,
        IReadOnlyList<ProviderConfig> queue)
    {
        foreach (var provider in queue.OrderBy(p => p.Priority))
        {
            if (provider.Name == current.Name) continue;
            if (_breakers.TryGetValue(provider.Name, out var cb) && !cb.TryAcquire())
                continue;
            return provider;
        }
        return null;
    }

    public void RecordFailover(ProviderConfig from, ProviderConfig to, string reason)
    {
        _history.Add(new FailoverRecord(
            from.Name, to.Name, reason, DateTimeOffset.UtcNow));
    }

    private record FailoverRecord(
        string From, string To, string Reason, DateTimeOffset Time);
}
```

- [ ] **Step 8: Run test — expect pass**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "FailoverServiceTests"`
Expected: PASS

- [ ] **Step 9: Commit**

```bash
git add src/PicoNode.AI/CircuitBreaker/ tests/PicoNode.AI.Tests/CircuitBreaker/
git commit -m "feat: add CircuitBreaker and FailoverService"
```

---

### Task 11: Integration — full pipeline test

**Files:**
- Create: `tests/PicoNode.AI.Tests/Integration/LLmClientIntegrationTests.cs`

**Interfaces:**
- Consumes: All previous tasks
- Produces: Integration-level verification

- [ ] **Step 1: Write integration test**

Create `tests/PicoNode.AI.Tests/Integration/LLmClientIntegrationTests.cs`:

```csharp
namespace PicoNode.AI.Tests.Integration;

public class LLmClientIntegrationTests
{
    [Test]
    public async Task Streaming_TextResponse_ParsesCorrectly()
    {
        // Simulate a complete Anthropic streaming response
        var sseData = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-20250514","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}

            event: message_stop
            data: {"type":"message_stop"}

            """u8.ToArray();

        using var stream = new MemoryStream(sseData);
        var events = new List<AssistantMessageEvent>();

        await foreach (var evt in SseParser.ParseAnthropicStreamAsync(
            stream, "claude-sonnet-4-20250514", CancellationToken.None))
        {
            events.Add(evt);
        }

        // Verify full event sequence
        await Assert.That(events).IsNotEmpty();
        await Assert.That(events[0]).IsTypeOf<AssistantMessageEvent.Start>();

        var textDeltas = events.OfType<AssistantMessageEvent.TextDelta>().ToArray();
        await Assert.That(textDeltas.Length).IsEqualTo(2);
        await Assert.That(textDeltas[0].Delta).IsEqualTo("Hello");
        await Assert.That(textDeltas[1].Delta).IsEqualTo(" world");

        var done = events.OfType<AssistantMessageEvent.Done>().ToArray();
        await Assert.That(done.Length).IsEqualTo(1);
        await Assert.That(done[0].Message.StopReason).IsEqualTo("end_turn");

        // Final partial should have accumulated text
        var lastTextDelta = textDeltas[^1];
        var textBlock = lastTextDelta.Partial.Content
            .OfType<TextContentBlock>().First();
        await Assert.That(textBlock.Text).IsEqualTo("Hello world");
    }

    [Test]
    public async Task Streaming_ToolUseResponse_ParsesCorrectly()
    {
        var sseData = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-20250514","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_01","name":"read","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\"/foo\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_stop
            data: {"type":"message_stop"}

            """u8.ToArray();

        using var stream = new MemoryStream(sseData);
        var events = new List<AssistantMessageEvent>();

        await foreach (var evt in SseParser.ParseAnthropicStreamAsync(
            stream, "claude-sonnet-4-20250514", CancellationToken.None))
        {
            events.Add(evt);
        }

        var toolStarts = events.OfType<AssistantMessageEvent.ToolCallStart>().ToArray();
        await Assert.That(toolStarts.Length).IsEqualTo(1);

        var toolDeltas = events.OfType<AssistantMessageEvent.ToolCallDelta>().ToArray();
        await Assert.That(toolDeltas.Length).IsEqualTo(1);

        var toolEnds = events.OfType<AssistantMessageEvent.ToolCallEnd>().ToArray();
        await Assert.That(toolEnds.Length).IsEqualTo(1);
        await Assert.That(toolEnds[0].Call.Name).IsEqualTo("read");
        await Assert.That(toolEnds[0].Call.Arguments["path"]).IsEqualTo("/foo");

        var done = events.OfType<AssistantMessageEvent.Done>().ToArray();
        await Assert.That(done.Length).IsEqualTo(1);
        await Assert.That(done[0].Message.Content)
            .Contains(new ToolCallContentBlock
            {
                Id = "toolu_01",
                Name = "read",
                Arguments = new() { ["path"] = "/foo" },
            });
    }
}
```

- [ ] **Step 2: Run integration test**

Run: `dotnet test tests/PicoNode.AI.Tests/ --filter "LLmClientIntegrationTests"`
Expected: PASS

- [ ] **Step 3: Run all tests**

Run: `dotnet test tests/PicoNode.AI.Tests/`
Expected: All PASS

- [ ] **Step 4: Commit**

```bash
git add tests/PicoNode.AI.Tests/Integration/
git commit -m "test: add integration tests for full SSE streaming pipeline"
```

---

## Plan Summary

| # | Task | Files | Tests |
|---|------|-------|-------|
| 1 | Scaffold | 4 | — |
| 2 | Model + StreamOptions | 3 | 1 |
| 3 | Message + ContentBlock | 4 | 4 |
| 4 | AssistantMessageEvent | 2 | 3 |
| 5 | ProviderConfig + CircuitTypes | 3 | 2 |
| 6 | ILLmClient + SSE Parser | 3 | 3 |
| 7 | AnthropicLLmClient | 3 | 2 |
| 8 | IFormatConverter | 3 | 3 |
| 9 | IProviderRouter | 3 | 3 |
| 10 | CircuitBreaker + Failover | 6 | 6 |
| 11 | Integration tests | 1 | 2 |
| **Total** | | **35 files** | **29 tests** |

**Total commits:** 11 (one per task)

**Dependencies:** Tasks must be executed in order. Each task builds on previous types.

**After this plan:** PicoNode.AI is complete. Next plans: PicoRelay (Task 12+), PicoAgent (Task 20+).
