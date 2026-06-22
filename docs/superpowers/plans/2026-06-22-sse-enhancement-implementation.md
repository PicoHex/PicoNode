# SSE Enhancement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add typed event methods (`WriteEventAsync`, `WriteErrorAsync`) to the existing `SseConnection` class.

**Architecture:** Two new methods on `SseConnection.cs` with input validation, `\r\n` normalization, multi-line data splitting, and JSON escaping for error messages. No new files.

**Tech Stack:** .NET 10, `TUnit` for tests, `PicoNode.Web` library

## Global Constraints

- TDD: test first, watch fail, implement minimal code, watch pass, commit
- AOT-safe: no reflection, no `dynamic`, no `Type.GetType`
- Follow existing patterns: `sealed class`, `static Create()` factory, `async ValueTask` return types
- `init`-only properties for models

---

### Task 1: Write failing test for WriteEventAsync

**Files:**
- Create: `tests/PicoNode.Web.Tests/SseConnectionTests.cs`

**Interfaces:**
- Produces: `WriteEventAsync(string eventType, string data, CancellationToken ct)` — writes `event: <type>\ndata: ...\n\n` to PipeWriter

- [ ] **Step 1: Write the failing test**

```csharp
namespace PicoNode.Web.Tests;

public sealed class SseConnectionTests
{
    [Test]
    public async Task WriteEventAsync_emits_event_and_data_lines()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("text_delta", "hello", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: text_delta\ndata: hello\n\n");
    }

    [Test]
    public async Task WriteEventAsync_splits_multiline_data()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("code", "line1\nline2", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: code\ndata: line1\ndata: line2\n\n");
    }

    [Test]
    public async Task WriteEventAsync_normalizes_crlf()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("text", "a\r\nb", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: text\ndata: a\ndata: b\n\n");
    }

    [Test]
    public async Task WriteEventAsync_null_data_emits_empty_data_line()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("ping", null!, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: ping\ndata: \n\n");
    }

    [Test]
    public async Task WriteEventAsync_empty_data_emits_empty_data_line()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("ping", "", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: ping\ndata: \n\n");
    }

    [Test]
    public async Task WriteEventAsync_throws_on_null_event_type()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await Assert
            .That(async () => await sse.WriteEventAsync(null!, "data", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task WriteEventAsync_throws_on_empty_event_type()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await Assert
            .That(async () => await sse.WriteEventAsync("", "data", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task WriteEventAsync_throws_on_event_type_with_newline()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await Assert
            .That(async () => await sse.WriteEventAsync("a\nb", "data", CancellationToken.None))
            .Throws<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: Build error — `'SseConnection' does not contain a definition for 'WriteEventAsync'`

- [ ] **Step 3: Implement WriteEventAsync in SseConnection.cs**

File: `src/PicoNode.Web/SseConnection.cs`

Add method after the existing `WriteJsonAsync`:

```csharp
/// <summary>
/// Writes a typed SSE event. Event type must not be null/empty or contain newlines.
/// Data is split on newlines and each line prefixed with "data: ".
/// Immediately flushes to the underlying writer.
/// </summary>
public async Task WriteEventAsync(string eventType, string data, CancellationToken ct)
{
    if (string.IsNullOrEmpty(eventType))
        throw new ArgumentException("Event type required", nameof(eventType));
    if (eventType.Contains('\n'))
        throw new ArgumentException("Event type must not contain newlines", nameof(eventType));

    data ??= "";

    var sb = new StringBuilder();
    sb.Append("event: ").Append(eventType).Append('\n');

    var normalized = data.Replace("\r\n", "\n").Replace("\r", "");
    if (normalized.Length > 0)
    {
        foreach (var line in normalized.Split('\n'))
            sb.Append("data: ").Append(line).Append('\n');
    }
    else
    {
        sb.Append("data: \n");
    }
    sb.Append('\n');

    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
    await _writer.WriteAsync(bytes, ct);
    await _writer.FlushAsync(ct);
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: All `SseConnectionTests` pass

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --solution PicoNode.slnx`
Expected: All tests pass, no regressions

- [ ] **Step 6: Commit**

```bash
git add src/PicoNode.Web/SseConnection.cs tests/PicoNode.Web.Tests/SseConnectionTests.cs
git commit -m "feat: add WriteEventAsync to SseConnection with typed event support"
```

---

### Task 2: Implement WriteErrorAsync

**Files:**
- Modify: `src/PicoNode.Web/SseConnection.cs`
- Modify: `tests/PicoNode.Web.Tests/SseConnectionTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `tests/PicoNode.Web.Tests/SseConnectionTests.cs`:

```csharp
[Test]
public async Task WriteErrorAsync_emits_error_event_with_json()
{
    var pipe = new Pipe();
    var sse = new SseConnection(pipe.Writer);

    await sse.WriteErrorAsync("timeout", CancellationToken.None);
    await pipe.Writer.CompleteAsync();

    var reader = pipe.Reader;
    var result = await reader.ReadAsync(CancellationToken.None);
    var output = Encoding.UTF8.GetString(result.Buffer);

    await Assert.That(output).IsEqualTo(
        "event: error\ndata: {\"message\":\"timeout\"}\n\n");
}

[Test]
public async Task WriteErrorAsync_escapes_quotes_in_message()
{
    var pipe = new Pipe();
    var sse = new SseConnection(pipe.Writer);

    await sse.WriteErrorAsync("unknown model \"gpt-5\"", CancellationToken.None);
    await pipe.Writer.CompleteAsync();

    var reader = pipe.Reader;
    var result = await reader.ReadAsync(CancellationToken.None);
    var output = Encoding.UTF8.GetString(result.Buffer);

    await Assert.That(output).IsEqualTo(
        "event: error\ndata: {\"message\":\"unknown model \\\"gpt-5\\\"\"}\n\n");
}

[Test]
public async Task WriteErrorAsync_replaces_newlines_with_spaces()
{
    var pipe = new Pipe();
    var sse = new SseConnection(pipe.Writer);

    await sse.WriteErrorAsync("a\nb\rc\u2028d", CancellationToken.None);
    await pipe.Writer.CompleteAsync();

    var reader = pipe.Reader;
    var result = await reader.ReadAsync(CancellationToken.None);
    var output = Encoding.UTF8.GetString(result.Buffer);

    await Assert.That(output).IsEqualTo(
        "event: error\ndata: {\"message\":\"a b c d\"}\n\n");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: Build error — `'SseConnection' does not contain a definition for 'WriteErrorAsync'`

- [ ] **Step 3: Implement WriteErrorAsync**

Add method to `src/PicoNode.Web/SseConnection.cs` after `WriteEventAsync`:

```csharp
/// <summary>
/// Convenience: writes an error event with JSON payload.
/// The message is JSON-escaped and newlines are replaced with spaces.
/// </summary>
public Task WriteErrorAsync(string message, CancellationToken ct)
{
    var escaped = message
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r\n", " ")
        .Replace("\r", " ")
        .Replace("\n", " ");
    return WriteEventAsync("error", $$"""{"message":"{{escaped}}"}""", ct);
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: All tests pass

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --solution PicoNode.slnx`
Expected: All tests pass, no regressions

- [ ] **Step 6: Commit**

```bash
git add src/PicoNode.Web/SseConnection.cs tests/PicoNode.Web.Tests/SseConnectionTests.cs
git commit -m "feat: add WriteErrorAsync to SseConnection for error event streaming"
```
