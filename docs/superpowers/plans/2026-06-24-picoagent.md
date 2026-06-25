# PicoAgent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build PicoAgent — AOT-first AI agent framework with dual-loop orchestration, capability runner, knowledge system, and Sidecar TUI.

**Architecture:** Agent Host (event loop + PicoNode.AI.ILLmClient + capability subprocess manager) + TUI Client (separate process, SSE consumer). Both in PicoNode repo under `src/PicoAgent/`.

**Tech Stack:** .NET 10, PicoNode.AI, PicoJetson, PicoDI, PicoNode.Web (for HTTP/SSE), TUnit

## Global Constraints

- Target: net10.0, `IsAotCompatible=true`, `IsTrimmable=true`
- `class` + `{ get; set; }` only — PicoJetson no record/init support
- No BCL HttpRequestMessage — use PicoNode.Http types
- No System.Text.Json for serialization — use PicoJetson
- No Microsoft.Extensions.* — use PicoDI, PicoLog, PicoCfg
- TDD: test first, see it fail, implement, see it pass
- Commit after each task

---

### Task 1: Scaffold PicoAgent projects

**Files:**
- Create: `src/PicoAgent/PicoAgent.csproj`
- Create: `src/PicoAgent/GlobalUsings.cs`
- Create: `tests/PicoAgent.Tests/PicoAgent.Tests.csproj`
- Create: `tests/PicoAgent.Tests/GlobalUsings.cs`
- Modify: `PicoNode.slnx`

**Interfaces:**
- Produces: Buildable solution skeleton

- [ ] **Step 1: Create src/PicoAgent/PicoAgent.csproj**

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
    <PackageId>PicoAgent</PackageId>
    <Description>PicoAgent — AOT-first AI agent framework for PicoHex ecosystem</Description>
    <PackageTags>PicoHex;PicoAgent;AI;Agent;AOT</PackageTags>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\PicoNode.AI\PicoNode.AI.csproj" />
    <PackageReference Include="PicoJetson" />
    <PackageReference Include="PicoDI" />
    <PackageReference Include="PicoLog" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\..\README.md" Pack="true" PackagePath="" Visible="false" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create src/PicoAgent/GlobalUsings.cs**

```csharp
global using System.Buffers;
global using System.Collections.Concurrent;
global using System.Diagnostics;
global using System.Runtime.CompilerServices;
global using System.Text;
global using System.Text.Json;
global using PicoDI;
global using PicoJetson;
global using PicoLog;
global using PicoNode.AI;
```

- [ ] **Step 3: Create tests/PicoAgent.Tests/PicoAgent.Tests.csproj**

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
    <ProjectReference Include="..\..\src\PicoAgent\PicoAgent.csproj" />
    <PackageReference Include="PicoJetson" />
    <PackageReference Include="TUnit" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create tests/PicoAgent.Tests/GlobalUsings.cs**

```csharp
global using System.Runtime.CompilerServices;
global using TUnit.Core;
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
```

- [ ] **Step 5: Add projects to PicoNode.slnx**

```xml
<!-- In /src/ folder -->
<Project Path="src/PicoAgent/PicoAgent.csproj" />

<!-- In /tests/ folder -->
<Project Path="tests/PicoAgent.Tests/PicoAgent.Tests.csproj" />
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/PicoAgent/PicoAgent.csproj`
Expected: Build succeeds

Run: `dotnet build tests/PicoAgent.Tests/PicoAgent.Tests.csproj`
Expected: Build succeeds

- [ ] **Step 7: Commit**

```bash
git add src/PicoAgent/ tests/PicoAgent.Tests/ PicoNode.slnx
git commit -m "feat: scaffold PicoAgent project"
```

---

### Task 2: Capability types

**Files:**
- Create: `src/PicoAgent/Capability/CapabilityTypes.cs`
- Create: `tests/PicoAgent.Tests/Capability/CapabilityTypesTests.cs`

**Interfaces:**
- Produces: `ICapability`, `CapabilityTrigger`, `TriggerKind`, `LifecycleKind`

- [ ] **Step 1: Write failing test**

Create `tests/PicoAgent.Tests/Capability/CapabilityTypesTests.cs`:

```csharp
namespace PicoAgent.Tests.Capability;

using PicoAgent;

public class CapabilityTypesTests
{
    [Test]
    public async Task CapabilityTrigger_Construction_Works()
    {
        var trigger = new CapabilityTrigger
        {
            Kind = TriggerKind.OnToolCall,
            ToolName = "bash",
        };

        await Assert.That(trigger.Kind).IsEqualTo(TriggerKind.OnToolCall);
        await Assert.That(trigger.ToolName).IsEqualTo("bash");
    }

    [Test]
    public async Task CapabilityTrigger_NullToolName_ForAllTools()
    {
        var trigger = new CapabilityTrigger
        {
            Kind = TriggerKind.OnToolCall,
            ToolName = null,
        };

        await Assert.That(trigger.ToolName).IsNull();
        // null means match all tools
    }

    [Test]
    public async Task TriggerKind_Has16Values()
    {
        var values = Enum.GetValues<TriggerKind>();
        await Assert.That(values.Length).IsEqualTo(16);
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet test --project tests/PicoAgent.Tests/PicoAgent.Tests.csproj`
Expected: FAIL

- [ ] **Step 3: Implement types**

Create `src/PicoAgent/Capability/CapabilityTypes.cs`:

```csharp
namespace PicoAgent;

public interface ICapability
{
    string Name { get; }
    string Handler { get; }
    IReadOnlyList<CapabilityTrigger> Triggers { get; }
    LifecycleKind Lifecycle { get; }
    string? SchemaPath { get; }
    int Priority { get; }
}

public enum LifecycleKind { Persistent, Oneshot }

public sealed class CapabilityTrigger
{
    public TriggerKind Kind { get; set; }
    public string? ToolName { get; set; }
}

public enum TriggerKind
{
    OnToolCall, OnMessageStart, OnMessageEnd,
    OnCompaction, OnPreCompact,
    OnModelPreRequest, OnModelPostResponse, OnModelError,
    OnUserInput, OnBash,
    OnSessionStart, OnSessionEnd,
    OnTurnStart, OnTurnEnd,
    OnAgentStart, OnAgentEnd,
}
```

- [ ] **Step 4: Run test — expect pass**

Run: `dotnet test --project tests/PicoAgent.Tests/PicoAgent.Tests.csproj`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PicoAgent/Capability/ tests/PicoAgent.Tests/Capability/
git commit -m "feat: add Capability types (ICapability, TriggerKind, LifecycleKind)"
```

---

### Task 3: Manifest loader — discover capabilities from JSON

**Files:**
- Create: `src/PicoAgent/Capability/ManifestLoader.cs`
- Create: `tests/PicoAgent.Tests/Capability/ManifestLoaderTests.cs`
- Create: `tests/PicoAgent.Tests/Capability/TestData/` (test manifest files)

**Interfaces:**
- Produces: `ManifestLoader.LoadFromFile(string path) → ManifestData`

- [ ] **Step 1: Write failing test**

Create `tests/PicoAgent.Tests/Capability/ManifestLoaderTests.cs`:

```csharp
namespace PicoAgent.Tests.Capability;

using PicoAgent;

public class ManifestLoaderTests
{
    [Test]
    public async Task Load_ValidManifest_ReturnsCapabilities()
    {
        var json = """
            {
              "name": "test-pack",
              "version": "1.0.0",
              "capabilities": [
                {
                  "name": "bash",
                  "handler": "bash tools/runner.sh",
                  "triggers": [
                    { "kind": "OnToolCall", "toolName": "bash" }
                  ],
                  "lifecycle": "oneshot",
                  "priority": 50,
                  "description": "Run shell commands"
                }
              ]
            }
            """;

        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, json);
        try
        {
            var manifest = ManifestLoader.LoadFromFile(path);

            await Assert.That(manifest).IsNotNull();
            await Assert.That(manifest!.Name).IsEqualTo("test-pack");
            await Assert.That(manifest.Capabilities.Count).IsEqualTo(1);
            await Assert.That(manifest.Capabilities[0].Name).IsEqualTo("bash");
            await Assert.That(manifest.Capabilities[0].Handler)
                .IsEqualTo("bash tools/runner.sh");
            await Assert.That(manifest.Capabilities[0].Triggers[0].Kind)
                .IsEqualTo(TriggerKind.OnToolCall);
            await Assert.That(manifest.Capabilities[0].Triggers[0].ToolName)
                .IsEqualTo("bash");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Load_EmptyCapabilities_ReturnsEmptyList()
    {
        var json = """{"name":"minimal","capabilities":[]}""";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, json);
        try
        {
            var manifest = ManifestLoader.LoadFromFile(path);
            await Assert.That(manifest!.Capabilities.Count).IsEqualTo(0);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet test --project tests/PicoAgent.Tests/PicoAgent.Tests.csproj --filter "ManifestLoaderTests"`
Expected: FAIL

- [ ] **Step 3: Implement ManifestLoader**

Create `src/PicoAgent/Capability/ManifestLoader.cs`:

```csharp
namespace PicoAgent;

using System.Text.Json;

public sealed class ManifestData
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public List<CapabilityConfig> Capabilities { get; set; } = [];
    public List<string> Knowledge { get; set; } = [];
    public string? Setup { get; set; }
}

public sealed class CapabilityConfig
{
    public string Name { get; set; } = "";
    public string Handler { get; set; } = "";
    public List<CapabilityTrigger> Triggers { get; set; } = [];
    public LifecycleKind Lifecycle { get; set; }
    public string? SchemaPath { get; set; }
    public int Priority { get; set; } = 50;
    public string Description { get; set; } = "";
}

public static class ManifestLoader
{
    public static ManifestData? LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var bytes = Encoding.UTF8.GetBytes(json);
        return PicoJetson.JsonSerializer.Deserialize<ManifestData>(bytes);
    }
}
```

- [ ] **Step 4: Run test — expect pass**

Run: `dotnet test --project tests/PicoAgent.Tests/PicoAgent.Tests.csproj --filter "ManifestLoaderTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PicoAgent/Capability/ManifestLoader.cs tests/PicoAgent.Tests/Capability/ManifestLoaderTests.cs
git commit -m "feat: add ManifestLoader for JSON capability manifests"
```

---

### Task 4: Capability discovery — scan ~/.pico-agent/capabilities/

**Files:**
- Create: `src/PicoAgent/Capability/CapabilityRegistry.cs`
- Create: `tests/PicoAgent.Tests/Capability/CapabilityRegistryTests.cs`

**Interfaces:**
- Produces: `CapabilityRegistry.Scan(string root) → IReadOnlyList<CapabilityConfig>`

- [ ] **Step 1: Write failing test**

Create temp directory with manifest files, scan them:

```csharp
namespace PicoAgent.Tests.Capability;

using PicoAgent;

public class CapabilityRegistryTests
{
    [Test]
    public async Task Scan_DiscoversCapabilities()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pico-agent-test-{Guid.NewGuid()}");
        var capsDir = Path.Combine(root, "capabilities");
        var pkgDir = Path.Combine(capsDir, "github.com", "test", "my-pack");
        Directory.CreateDirectory(pkgDir);

        var manifestPath = Path.Combine(pkgDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "name": "my-pack",
              "capabilities": [
                {
                  "name": "bash",
                  "handler": "bash tools/runner.sh",
                  "triggers": [{ "kind": "OnToolCall", "toolName": "bash" }],
                  "lifecycle": "oneshot",
                  "priority": 50
                },
                {
                  "name": "guard",
                  "handler": "node hooks/guard.js",
                  "triggers": [{ "kind": "OnToolCall" }],
                  "lifecycle": "persistent",
                  "priority": 0
                }
              ]
            }
            """);

        try
        {
            var registry = new CapabilityRegistry();
            registry.Scan(root);

            var all = registry.GetAll();
            await Assert.That(all.Count).IsEqualTo(2);

            var toolCaps = registry.GetByTrigger(TriggerKind.OnToolCall);
            await Assert.That(toolCaps.Count).IsEqualTo(2);

            var persistent = registry.GetByLifecycle(LifecycleKind.Persistent);
            await Assert.That(persistent.Count).IsEqualTo(1);
            await Assert.That(persistent[0].Name).IsEqualTo("guard");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Scan_EmptyDirectory_ReturnsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pico-agent-empty-{Guid.NewGuid()}");
        var capsDir = Path.Combine(root, "capabilities");
        Directory.CreateDirectory(capsDir);
        try
        {
            var registry = new CapabilityRegistry();
            registry.Scan(root);
            await Assert.That(registry.GetAll().Count).IsEqualTo(0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
```

- [ ] **Step 2: Run test — expect fail**

- [ ] **Step 3: Implement CapabilityRegistry**

```csharp
namespace PicoAgent;

public sealed class CapabilityRegistry
{
    private readonly List<CapabilityConfig> _capabilities = [];

    public void Scan(string root)
    {
        var capsDir = Path.Combine(root, "capabilities");
        if (!Directory.Exists(capsDir)) return;

        foreach (var dir in Directory.EnumerateDirectories(capsDir, "*", SearchOption.AllDirectories))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            var manifest = ManifestLoader.LoadFromFile(manifestPath);
            if (manifest?.Capabilities == null) continue;

            foreach (var cap in manifest.Capabilities)
            {
                // Resolve handler path relative to manifest directory
                cap.Handler = Path.GetFullPath(Path.Combine(dir, cap.Handler));
                _capabilities.Add(cap);
            }
        }
    }

    public IReadOnlyList<CapabilityConfig> GetAll() => _capabilities;

    public IReadOnlyList<CapabilityConfig> GetByTrigger(TriggerKind kind)
        => _capabilities.Where(c => c.Triggers.Any(t => t.Kind == kind)).ToList();

    public IReadOnlyList<CapabilityConfig> GetByLifecycle(LifecycleKind lifecycle)
        => _capabilities.Where(c => c.Lifecycle == lifecycle).ToList();
}
```

- [ ] **Step 4: Run test — expect pass**

- [ ] **Step 5: Commit**

---

### Task 5: Capability Runner — spawn and communicate with subprocesses

**Files:**
- Create: `src/PicoAgent/Capability/CapabilityRunner.cs`
- Create: `tests/PicoAgent.Tests/Capability/CapabilityRunnerTests.cs`

**Interfaces:**
- Produces: `CapabilityRunner.ExecuteAsync(config, contextKind, input, ct) → JsonElement`

- [ ] **Step 1: Write failing test — echo tool**

Create a test that spawns a simple echo tool and verifies stdin/stdout communication:

```csharp
namespace PicoAgent.Tests.Capability;

public class CapabilityRunnerTests
{
    [Test]
    public async Task ExecuteAsync_EchoTool_ReturnsResult()
    {
        var config = new CapabilityConfig
        {
            Name = "echo",
            Handler = OperatingSystem.IsWindows() ? "cmd.exe" : "bash",
            // On Windows: cmd /c echo, on Unix: bash -c echo
        };

        // Write a helper script for the test
        var scriptDir = Path.Combine(Path.GetTempPath(), "pico-test-" + Guid.NewGuid());
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir,
            OperatingSystem.IsWindows() ? "echo.bat" : "echo.sh");
        var scriptContent = OperatingSystem.IsWindows()
            ? "@echo off\r\nset /p line=\r\necho {\"content\":[{\"type\":\"text\",\"text\":\"got it\"}]}"
            : "#!/bin/bash\nread line\necho '{\"content\":[{\"type\":\"text\",\"text\":\"got it\"}]}'";
        await File.WriteAllTextAsync(scriptPath, scriptContent);

        if (!OperatingSystem.IsWindows())
            new Process { FileName = "chmod", Arguments = $"+x {scriptPath}" }.Start();

        config.Handler = OperatingSystem.IsWindows()
            ? $"cmd.exe /c {scriptPath}"
            : scriptPath;

        try
        {
            var runner = new CapabilityRunner();
            var input = new { kind = "tool_call", toolCallId = "1", toolName = "echo", args = new { } };
            var inputJson = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(input);

            var result = await runner.ExecuteAsync(
                config, "tool_call", inputJson, CancellationToken.None);

            await Assert.That(result.TryGetProperty("content", out _)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(scriptDir)) Directory.Delete(scriptDir, true);
        }
    }
}
```

- [ ] **Step 2: Run test — expect fail**

- [ ] **Step 3: Implement CapabilityRunner**

```csharp
namespace PicoAgent;

public sealed class CapabilityRunner
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    public async Task<JsonElement> ExecuteAsync(
        CapabilityConfig config,
        string contextKind,
        byte[] inputJson,
        CancellationToken ct)
    {
        var startInfo = ParseHandler(config.Handler);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        using var process = Process.Start(startInfo)!;

        // Write input line
        await process.StandardInput.BaseStream.WriteAsync(inputJson, ct);
        await process.StandardInput.BaseStream.WriteAsync(NewLine, ct);
        await process.StandardInput.FlushAsync(ct);

        // Read response via JSONL async enumerable
        await foreach (var response in PicoJetson.JsonSerializer
            .DeserializeAsyncEnumerable<JsonElement>(
                process.StandardOutput.BaseStream, topLevelValues: true, ct: ct))
        {
            return response;
        }

        return default;
    }

    private static ProcessStartInfo ParseHandler(string handler)
    {
        var parts = handler.Split(' ', 2);
        return parts.Length == 2
            ? new ProcessStartInfo(parts[0], parts[1])
            : new ProcessStartInfo(parts[0]);
    }
}
```

- [ ] **Step 4: Run test — expect pass**

- [ ] **Step 5: Commit**

---

### Task 6: Agent Loop — dual while loop

**Files:**
- Create: `src/PicoAgent/Agent/AgentLoop.cs`
- Create: `tests/PicoAgent.Tests/Agent/AgentLoopTests.cs`

**Interfaces:**
- Produces: `AgentLoop` — orchestrates LLM calls + tool execution + hook invocation

- [ ] **Step 1: Write failing test — single turn, no tools**

```csharp
namespace PicoAgent.Tests.Agent;

using PicoNode.AI;

public class AgentLoopTests
{
    [Test]
    public async Task RunAsync_SimpleMessage_ReturnsAssistantResponse()
    {
        var llmClient = new MockLLmClient();
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(llmClient, registry, runner);

        var messages = new List<Message>
        {
            new() { Role = "user", Content = "Hello", Timestamp = 1 },
        };

        var result = await loop.RunTurnAsync(messages, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsGreaterThan(0);
        var lastAssistant = result.LastOrDefault(m => m.Role == "assistant");
        await Assert.That(lastAssistant).IsNotNull();
    }
}

// Mock LLM client — returns a simple text response
public sealed class MockLLmClient : ILLmClient
{
    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model, ChatContext context, StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AssistantMessageEvent.Start
        {
            Partial = new Message { Role = "assistant" },
        };
        yield return new AssistantMessageEvent.TextDelta
        {
            Index = 0, Delta = "Hello!",
            Partial = new Message
            {
                Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "text", Text = "Hello!" }],
            },
        };
        yield return new AssistantMessageEvent.Done
        {
            Message = new Message
            {
                Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "text", Text = "Hello!" }],
                StopReason = "end_turn",
            },
        };
    }
}
```

- [ ] **Step 2: Run test — expect fail**

- [ ] **Step 3: Implement AgentLoop**

```csharp
namespace PicoAgent;

public sealed class AgentLoop
{
    private readonly ILLmClient _llm;
    private readonly CapabilityRegistry _registry;
    private readonly CapabilityRunner _runner;
    private const int MaxToolIterations = 20;

    public AgentLoop(ILLmClient llm, CapabilityRegistry registry, CapabilityRunner runner)
    {
        _llm = llm;
        _registry = registry;
        _runner = runner;
    }

    public async Task<List<Message>> RunTurnAsync(
        List<Message> messages, CancellationToken ct,
        List<Message>? followUpQueue = null)
    {
        var result = new List<Message>();
        var model = new Model
        {
            Id = "claude-sonnet-4-20250514",
            BaseUrl = "https://api.anthropic.com",
            Api = AiApiFormat.AnthropicMessages,
            Provider = "anthropic",
            MaxTokens = 4096,
        };

        // Outer loop: follow-up
        var pendingMessages = new List<Message>();
        if (followUpQueue != null && followUpQueue.Count > 0)
            pendingMessages.AddRange(followUpQueue);

        bool hasMore = true;
        while (hasMore)
        {
            // Inner loop: tool calls
            var iterations = 0;
            bool hasTools;

            do
            {
                iterations++;
                // Inject pending messages
                foreach (var pm in pendingMessages)
                {
                    messages.Add(pm);
                    result.Add(pm);
                }
                pendingMessages.Clear();

                // Call LLM
                var llmMessages = new Message[messages.Count];
                messages.CopyTo(llmMessages);

                var context = new ChatContext { Messages = llmMessages };
                var assistantMsg = await CallLLMAsync(model, context, result, ct);

                if (assistantMsg == null) break;

                messages.Add(assistantMsg);
                result.Add(assistantMsg);

                // Check for tool calls
                var toolCallBlocks = assistantMsg.ContentBlocks?
                    .Where(cb => cb.Type == "tool_call").ToArray();
                hasTools = toolCallBlocks is { Length: > 0 };

                if (hasTools)
                {
                    // Execute tool calls
                    foreach (var tc in toolCallBlocks!)
                    {
                        var capConfig = _registry.GetAll()
                            .FirstOrDefault(c => c.Name == tc.Name);

                        if (capConfig == null)
                        {
                            // Tool not found — create error result
                            messages.Add(new Message
                            {
                                Role = "toolResult",
                                ToolCallId = tc.Id,
                                ToolName = tc.Name,
                                ContentBlocks =
                                [
                                    new ContentBlock
                                    {
                                        Type = "text",
                                        Text = $"Tool not found: {tc.Name}",
                                    },
                                ],
                                IsError = true,
                                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            });
                            continue;
                        }

                        var toolInput = new
                        {
                            kind = "tool_call",
                            toolCallId = tc.Id,
                            toolName = tc.Name,
                            args = tc.Arguments,
                        };
                        var inputJson = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(toolInput);

                        var hookResult = await RunHooksAsync(
                            TriggerKind.OnToolCall, inputJson, ct);

                        if (hookResult is { } h && h.TryGetProperty("action", out var action))
                        {
                            if (action.GetString() == "block")
                                break;
                        }

                        var toolResponse = await _runner.ExecuteAsync(
                            capConfig, "tool_call", inputJson, ct);

                        var toolMsg = new Message
                        {
                            Role = "toolResult",
                            ToolCallId = tc.Id,
                            ToolName = tc.Name,
                            ContentBlocks =
                            [
                                new ContentBlock
                                {
                                    Type = "text",
                                    Text = toolResponse.TryGetProperty("content", out var c)
                                        ? c.ToString() : "",
                                },
                            ],
                            IsError = toolResponse.TryGetProperty("isError", out var isErr)
                                && isErr.GetBoolean(),
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        };
                        messages.Add(toolMsg);
                        result.Add(toolMsg);
                    }
                }

                if (iterations >= MaxToolIterations) break;
            }
            while (hasTools);

            hasMore = false; // v1: no follow-up support yet
        }

        return result;
    }

    private async Task<Message?> CallLLMAsync(
        Model model, ChatContext context, List<Message> result, CancellationToken ct)
    {
        Message? finalMessage = null;

        await foreach (var evt in _llm.StreamAsync(model, context, null, ct))
        {
            if (evt is AssistantMessageEvent.Done d)
                finalMessage = d.Message;
            else if (evt is AssistantMessageEvent.Error e)
                finalMessage = e.Message;
        }

        return finalMessage;
    }

    private async Task<JsonElement?> RunHooksAsync(
        TriggerKind trigger, byte[] input, CancellationToken ct)
    {
        var hooks = _registry.GetByTrigger(trigger)
            .OrderBy(c => c.Priority);

        foreach (var hook in hooks)
        {
            var result = await _runner.ExecuteAsync(hook, "hook", input, ct);
            if (result.TryGetProperty("action", out var action))
            {
                if (action.GetString() == "block")
                    return result;
            }
        }

        return null;
    }
}
```

- [ ] **Step 4: Run test — expect pass**

- [ ] **Step 5: Commit**

---

### Task 7: Knowledge system — SKILL.md scanning + system prompt

**Files:**
- Create: `src/PicoAgent/Knowledge/KnowledgeScanner.cs`
- Create: `tests/PicoAgent.Tests/Knowledge/KnowledgeScannerTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
namespace PicoAgent.Tests.Knowledge;

using PicoAgent;

public class KnowledgeScannerTests
{
    [Test]
    public async Task Scan_DiscoversSkillFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pico-knowledge-{Guid.NewGuid()}");
        var skillDir = Path.Combine(root, "knowledge", "pdf-tools");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: pdf-tools
            description: Extract text and tables from PDF files
            ---
            # PDF Tools
            Use this tool to work with PDFs.
            """);

        try
        {
            var scanner = new KnowledgeScanner();
            var skills = scanner.Scan(root);

            await Assert.That(skills.Count).IsEqualTo(1);
            await Assert.That(skills[0].Name).IsEqualTo("pdf-tools");
            await Assert.That(skills[0].Description)
                .IsEqualTo("Extract text and tables from PDF files");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task BuildSystemPrompt_IncludesSkills()
    {
        var skills = new List<SkillInfo>
        {
            new() { Name = "pdf-tools", Description = "Handle PDF files" },
            new() { Name = "web-search", Description = "Search the web" },
        };

        var prompt = KnowledgeScanner.BuildSkillsPrompt(skills);

        await Assert.That(prompt).Contains("<available_skills>");
        await Assert.That(prompt).Contains("<name>pdf-tools</name>");
        await Assert.That(prompt).Contains("<name>web-search</name>");
    }
}
```

- [ ] **Step 2: Run test — expect fail**

- [ ] **Step 3: Implement**

```csharp
namespace PicoAgent;

public sealed class SkillInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class KnowledgeScanner
{
    public List<SkillInfo> Scan(string root)
    {
        var skills = new List<SkillInfo>();
        var knowledgeDir = Path.Combine(root, "knowledge");
        if (!Directory.Exists(knowledgeDir)) return skills;

        foreach (var dir in Directory.EnumerateDirectories(
            knowledgeDir, "*", SearchOption.AllDirectories))
        {
            var skillPath = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillPath)) continue;

            var content = File.ReadAllText(skillPath);
            var skill = ParseSkillMarkdown(content);
            if (skill != null)
            {
                skill.Path = skillPath;
                skills.Add(skill);
            }
        }

        return skills.OrderBy(s => s.Name).ToList();
    }

    private static SkillInfo? ParseSkillMarkdown(string content)
    {
        // Parse YAML frontmatter between --- markers
        var lines = content.Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---") return null;

        var skill = new SkillInfo();
        bool inFrontmatter = false;

        foreach (var line in lines.Skip(1))
        {
            var trimmed = line.Trim();
            if (trimmed == "---")
            {
                if (!inFrontmatter) { inFrontmatter = true; continue; }
                break;
            }

            if (!inFrontmatter) continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) continue;

            var key = trimmed[..colonIdx].Trim();
            var value = trimmed[(colonIdx + 1)..].Trim();

            if (key == "name") skill.Name = value;
            else if (key == "description") skill.Description = value;
        }

        return string.IsNullOrEmpty(skill.Name) ? null : skill;
    }

    public static string BuildSkillsPrompt(List<SkillInfo> skills)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<available_skills>");
        foreach (var skill in skills)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{skill.Name}</name>");
            sb.AppendLine($"    <description>{skill.Description}</description>");
            sb.AppendLine("  </skill>");
        }
        sb.AppendLine("</available_skills>");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run test — expect pass**

- [ ] **Step 5: Commit**

---

### Task 8: Session persistence — JSONL read/write

**Files:**
- Create: `src/PicoAgent/Session/SessionStore.cs`
- Create: `tests/PicoAgent.Tests/Session/SessionStoreTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
namespace PicoAgent.Tests.Session;

using PicoNode.AI;

public class SessionStoreTests
{
    [Test]
    public async Task SaveAndLoad_RoundTrip_PreservesMessages()
    {
        var messages = new List<Message>
        {
            new() { Role = "user", Content = "Hello", Timestamp = 1 },
            new()
            {
                Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "text", Text = "Hi!" }],
                StopReason = "end_turn",
                Timestamp = 2,
            },
        };

        var dir = Path.Combine(Path.GetTempPath(), "pico-session-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "test-session.jsonl");

        try
        {
            await SessionStore.SaveAsync(path, messages);

            await Assert.That(File.Exists(path)).IsTrue();

            var loaded = await SessionStore.LoadAsync(path);

            await Assert.That(loaded.Count).IsEqualTo(2);
            await Assert.That(loaded[0].Role).IsEqualTo("user");
            await Assert.That(loaded[0].Content).IsEqualTo("Hello");
            await Assert.That(loaded[1].ContentBlocks![0].Text).IsEqualTo("Hi!");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run test — expect fail**

- [ ] **Step 3: Implement**

```csharp
namespace PicoAgent;

using PicoNode.AI;

public static class SessionStore
{
    public static async Task SaveAsync(string path, List<Message> messages, CancellationToken ct = default)
    {
        var jsonl = PicoJetson.JsonSerializer.SerializeLines(messages);
        await File.WriteAllBytesAsync(path, jsonl, ct);
    }

    public static async Task<List<Message>> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return [];

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var entries = PicoJetson.JsonSerializer.DeserializeLines<Message>(bytes);
        return entries.Where(e => e != null).Select(e => e!).ToList();
    }
}
```

- [ ] **Step 4: Run test — expect pass**

- [ ] **Step 5: Commit**

---

### Task 9: Agent Host HTTP API

**Files:**
- Create: `src/PicoAgent.Host/PicoAgent.Host.csproj`
- Create: `src/PicoAgent.Host/AgentHost.cs`
- Create: `src/PicoAgent.Host/SseEventEndpoint.cs`
- Create: `tests/PicoAgent.Host.Tests/PicoAgent.Host.Tests.csproj`

**Interfaces:**
- Produces: `pico-agent serve` — HTTP server with SSE events + session API

- [ ] **Step 1: Scaffold host project** (depends on PicoNode.Web for HTTP)

- [ ] **Step 2: Implement SSE event endpoint** — `GET /session/{id}/events`

- [ ] **Step 3: Implement session endpoints** — `POST /session/{id}/message`

- [ ] **Step 4: Integration test** — start host, send message, verify SSE response

- [ ] **Step 5: Commit**

---

### Task 10: CLI — serve + chat commands

**Files:**
- Create: `src/PicoAgent.Cli/PicoAgent.Cli.csproj`
- Create: `src/PicoAgent.Cli/Program.cs`

- [ ] **Step 1: Scaffold CLI project** (depends on PicoAgent.Host)

- [ ] **Step 2: Implement `serve` command** — starts AgentHost on specified port

- [ ] **Step 3: Implement `chat` command** — basic stdin/stdout, no TUI

- [ ] **Step 4: Implement `install` command** — `git clone → ~/.pico-agent/packages/ → symlink capabilities + knowledge`

- [ ] **Step 5: Implement `remove` command** — `rm -rf packages/<name> + unlink`

- [ ] **Step 6: Implement `list` command** — `ls ~/.pico-agent/packages/`

- [ ] **Step 7: Implement `update` command** — `git pull` for each installed package

- [ ] **Step 8: Implement `reload` command** — `HTTP POST /reload` → Agent rescans

- [ ] **Step 9: Implement `status` command**

- [ ] **Step 10: Commit**

---

### Task 11: Persistent hook lifecycle management

**Files:**
- Modify: `src/PicoAgent/Capability/CapabilityRunner.cs`
- Create: `src/PicoAgent/Capability/PersistentProcessManager.cs`
- Create: `tests/PicoAgent.Tests/Capability/PersistentProcessManagerTests.cs`

**Interfaces:**
- Produces: Persistent process health checks (ping/pong), crash restart (max 3), timeout handling

- [ ] **Step 1: Write failing test** — spawn persistent hook, verify ping/pong round-trip

```csharp
[Test]
public async Task PersistentHook_PingPong_Works()
{
    // Create a persistent hook script that responds to ping
    var scriptDir = Path.Combine(Path.GetTempPath(), "pico-persist-" + Guid.NewGuid());
    Directory.CreateDirectory(scriptDir);
    var scriptPath = Path.Combine(scriptDir, OperatingSystem.IsWindows() ? "hook.bat" : "hook.sh");
    var content = OperatingSystem.IsWindows()
        ? "@echo off\r\n:loop\r\nset /p line=\r\nif \"%line%\"==\"{\"kind\":\"ping\"}\" echo {\"kind\":\"pong\"}\r\ngoto loop"
        : "#!/bin/bash\nwhile read line; do\n  if [ \"$line\" = '{\"kind\":\"ping\"}' ]; then\n    echo '{\"kind\":\"pong\"}'\n  fi\ndone";
    await File.WriteAllTextAsync(scriptPath, content);

    try
    {
        var config = new CapabilityConfig
        {
            Name = "test-hook",
            Handler = scriptPath,
            Lifecycle = LifecycleKind.Persistent,
        };
        var mgr = new PersistentProcessManager();

        var result = await mgr.SendPingAsync(config, CancellationToken.None);

        await Assert.That(result).IsTrue();
        mgr.StopAll();
    }
    finally { Directory.Delete(scriptDir, true); }
}
```

- [ ] **Step 2: Run test — expect fail**

- [ ] **Step 3: Implement PersistentProcessManager**

```csharp
public sealed class PersistentProcessManager
{
    private readonly Dictionary<string, Process> _processes = [];
    private readonly Dictionary<string, int> _restartCounts = [];
    private const int MaxRestarts = 3;
    private const int TimeoutSeconds = 30;

    public async Task<bool> SendPingAsync(CapabilityConfig config, CancellationToken ct)
    {
        var process = GetOrSpawn(config);
        var pingJson = "{\"kind\":\"ping\"}\n"u8;
        await process.StandardInput.BaseStream.WriteAsync(pingJson, ct);
        await process.StandardInput.FlushAsync(ct);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var line = await process.StandardOutput.ReadLineAsync(linked.Token);
            return line?.Contains("\"pong\"") == true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // v1: basic ping/pong only. Full health/crash/timeout in v2.
    private Process GetOrSpawn(CapabilityConfig config) { ... }
    public void StopAll() { ... }
}
```

- [ ] **Step 4: Run test — expect pass**
- [ ] **Step 5: Commit**

---

### Task 12: Compaction

**Files:**
- Create: `src/PicoAgent/Agent/Compactor.cs`
- Create: `tests/PicoAgent.Tests/Agent/CompactorTests.cs`

**Interfaces:**
- Produces: `Compactor.Compact(messages, maxTokens) → List<Message>` — v1 truncation

- [ ] **Step 1: Write failing test**

```csharp
[Test]
public async Task Compact_ExceedsThreshold_TruncatesOldest()
{
    var messages = new List<Message>();
    for (int i = 0; i < 30; i++)
        messages.Add(new Message { Role = "user", Content = $"msg{i}", Timestamp = i });

    var compacted = Compactor.Compact(messages, keepLast: 20);

    await Assert.That(compacted.Count).IsEqualTo(20);
    await Assert.That(compacted[0].Content).IsEqualTo("msg10");
}

[Test]
public async Task Compact_UnderThreshold_ReturnsOriginal()
{
    var messages = new List<Message>
    {
        new() { Role = "user", Content = "hi", Timestamp = 1 },
    };
    var compacted = Compactor.Compact(messages, keepLast: 20);
    await Assert.That(compacted.Count).IsEqualTo(1);
}
```

- [ ] **Step 2: Run test — expect fail**

- [ ] **Step 3: Implement**

```csharp
public static class Compactor
{
    public static List<Message> Compact(List<Message> messages, int keepLast = 20)
    {
        if (messages.Count <= keepLast) return [..messages];
        return messages.Skip(messages.Count - keepLast).ToList();
    }
}
```

- [ ] **Step 4: Run test — expect pass**
- [ ] **Step 5: Commit**

---

### Task 13: Hot reload — HTTP endpoint

**Files:**
- Create: `src/PicoAgent.Host/ReloadEndpoint.cs`
- Create: `tests/PicoAgent.Host.Tests/ReloadEndpointTests.cs`

**Interfaces:**
- Produces: `POST /reload` → rescans capabilities + knowledge + config

- [ ] **Step 1: Write failing test** — POST /reload, verify capability count changed after manifest added

- [ ] **Step 2: Implement** — signal AgentHost to rescan on POST /reload

- [ ] **Step 3: Commit**

---

### Task 14: TUI client — SSE consumer (separate executable)

**Files:**
- Create: `src/PicoAgent.Tui/PicoAgent.Tui.csproj`
- Create: `src/PicoAgent.Tui/TuiClient.cs`

**Interfaces:**
- Produces: `pico-agent chat` — connects to AgentHost SSE, renders events, sends user input

- [ ] **Step 1: Scaffold TUI project** (net10.0, console app, no PicoNode.Web dependency)

- [ ] **Step 2: Implement SSE consumer** — `await foreach` over AgentHost events stream

```csharp
public async Task ConnectAsync(string hostUrl, CancellationToken ct)
{
    var client = new HttpClient();
    var stream = await client.GetStreamAsync($"{hostUrl}/session/default/events", ct);
    using var reader = new StreamReader(stream);

    while (!ct.IsCancellationRequested)
    {
        var line = await reader.ReadLineAsync(ct);
        if (line == null) break;
        if (line.StartsWith("data: "))
        {
            var json = line[6..];
            // Parse and render event
            Console.WriteLine(json);
        }
    }
}
```

- [ ] **Step 3: Implement reconnect** — `?since={lastEventIndex}` on reconnect

- [ ] **Step 4: Implement heartbeat detection** — 10s timeout, auto-reconnect

- [ ] **Step 5: Integration test** — start AgentHost, start TUI, send message, verify output

- [ ] **Step 6: Commit**

---

## Plan Summary

| # | Task | Files | Tests |
|---|------|-------|-------|
| 1 | Scaffold | 5 | — |
| 2 | Capability types | 2 | 3 |
| 3 | Manifest loader | 2 | 2 |
| 4 | Capability discovery | 2 | 2 |
| 5 | Capability Runner | 2 | 1 |
| 6 | Agent Loop | 2 | 1 |
| 7 | Knowledge system | 2 | 2 |
| 8 | Session persistence | 2 | 1 |
| 9 | Agent Host HTTP API | 4 | 1 |
| 10 | CLI (serve/chat/install/remove/list/update/reload/status) | 3 | 1 |
| 11 | Persistent hook lifecycle | 3 | 1 |
| 12 | Compaction | 2 | 2 |
| 13 | Hot reload endpoint | 2 | 1 |
| 14 | TUI client | 3 | 1 |
| **Total** | | **36 files** | **19+ tests** |

**Dependencies:** Tasks 1→2→3→4→5→6→7→8→9→10 in order. Task 11 after 5. Task 12 after 6. Task 13 after 9. Task 14 after 10.
