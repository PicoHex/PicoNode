# SSE Enhancement for PicoNode.Web

> 2026-06-22 | Spec | Typed event streaming for Server-Sent Events

## 1. Overview

Enhance the existing `SseConnection` class with typed event support for AI Agent
streaming scenarios (text deltas, tool calls, errors).

### Motivation

The current `SseConnection` can write raw text and JSON to an SSE stream, but lacks:

- **Typed events** — AI agents need to distinguish `text_delta`, `tool_call`, `error`,
  and `done` event types for client-side routing (show thinking indicator vs. render
  output vs. display error).
- **Multi-line data** — Markdown code blocks, stack traces, and other content containing
  newlines must be correctly encoded per the SSE protocol (`data:` on every line).

### Non-Goals

- **WebSocket upgrade** — SSE is a one-way server→client channel. Bidirectional
  communication is handled by the existing WebSocket support in `PicoNode.Http`.
- **Event ID / retry** — `id:` and `retry:` fields are not needed for AI response
  streaming (events are consumed in order, no reconnection recovery logic).
- **Changing `SseEndpoint.Create()`** — the factory method is feature-complete for simple
  SSE endpoints and does not need modification.

---

## 2. Architecture

```
Handler (e.g. AI Agent)
    │
    ├─ sse.WriteEventAsync("text_delta", chunk, ct)   ← NEW
    ├─ sse.WriteEventAsync("tool_call", json, ct)     ← NEW
    ├─ sse.WriteErrorAsync("timeout", ct)             ← NEW
    ├─ sse.WriteAsync("data: ...\n\n", ct)            ← EXISTING
    ├─ sse.WriteJsonAsync(json, ct)                   ← EXISTING
    └─ sse.CompleteAsync(ct)                          ← EXISTING
    │
    ▼
SseConnection → PipeWriter → HttpResponse.BodyStream → Client
```

**Design decisions:**
- All new methods are added to the existing `SseConnection` class. No new files.
- `SseEndpoint.Create()` is unchanged — it already returns a `WebRequestHandler` with
  a Pipe-based streaming response.
- No generic or typed-overload API — the caller provides pre-serialized JSON strings.
  This avoids coupling `SseConnection` to any serialization library.

---

## 3. Files

| File | Change |
|------|--------|
| `src/PicoNode.Web/SseConnection.cs` | Add `WriteEventAsync`, `WriteErrorAsync` methods |
| `src/PicoNode.Web/SseEndpoint.cs` | No changes |

---

## 4. API Design

### 4.1 Existing Methods (unchanged)

```csharp
namespace PicoNode.Web;

public sealed class SseConnection
{
    public Task WriteAsync(string text, CancellationToken ct);
    public Task WriteJsonAsync(string json, CancellationToken ct);
    public Task PingAsync(CancellationToken ct);
    public Task CompleteAsync(CancellationToken ct);
}
```

### 4.2 New Methods

```csharp
/// <summary>
/// Writes a typed SSE event.
/// Event type must not be null/empty or contain newlines.
/// Data is split on newlines and each line prefixed with "data: ".
/// Immediately flushes to the underlying writer.
/// </summary>
public async Task WriteEventAsync(string eventType, string data, CancellationToken ct);

/// <summary>
/// Convenience: writes an "error" event with JSON payload.
/// The message is JSON-escaped and newlines are replaced with spaces.
/// </summary>
public Task WriteErrorAsync(string message, CancellationToken ct);
```

### 4.3 Output Examples

```
WriteEventAsync("text_delta", "hello")
→ event: text_delta\ndata: hello\n\n

WriteEventAsync("text_delta", "```python\nprint('hi')\n```")
→ event: text_delta\ndata: ```python\ndata: print('hi')\ndata: ```\n\n

WriteErrorAsync("connection refused")
→ event: error\ndata: {"message":"connection refused"}\n\n

WriteErrorAsync("unknown model \"gpt-5\"")
→ event: error\ndata: {"message":"unknown model \"gpt-5\""}\n\n
```

### 4.4 Usage (AI Agent handler)

```csharp
app.MapPost("/api/agent/chat", SseEndpoint.Create(static async (sse, ct) =>
{
    await sse.WriteEventAsync("thinking", "Analyzing request...", ct);

    await foreach (var chunk in aiClient.ChatStreamAsync(prompt, ct))
    {
        await sse.WriteEventAsync("text_delta", chunk.Content, ct);
    }

    await sse.WriteEventAsync("done", "{}", ct);
    await sse.CompleteAsync(ct);
}));
```

---

## 5. Input Normalization

### 5.1 Data field

```
Input: "line1\r\nline2\rline3"
  → Replace("\r\n", "\n")  → "line1\nline2\rline3"
   → Replace("\r", "")      → "line1\nline2line3"
    → Split('\n')           → ["line1", "line2line3"]
     → "data: line1\ndata: line2line3\n"
```

### 5.2 Null data

```
data = null → data = "" → "data: \n" (one empty data line)
```

### 5.3 Event type validation

| Input | Behavior |
|-------|----------|
| `null` | `ArgumentException` |
| `""` | `ArgumentException` |
| `"a\nb"` | `ArgumentException` |
| `"text_delta"` | ✅ |
| `"status_update"` | ✅ |

### 5.4 Error message

The error message undergoes three transformations:
1. JSON-escaping: `\` → `\\`, `"` → `\"`
2. Newline normalization: `\r\n` / `\r` / `\n` → space
3. Wrapped in `{"message":"..."}`

---

## 6. Edge Cases

| Scenario | Behavior |
|----------|----------|
| Empty data | Outputs `data: \n` (one empty data line) |
| Data with embedded `\r\n`, `\r`, `\n` | Normalized to `\n`, each line prefixed with `data: ` |
| Event type with newline | `ArgumentException` |
| Event type null/empty | `ArgumentException` |
| Multi-line in error message | Newlines replaced with spaces |
| `CancellationToken` triggered | I/O operation throws `OperationCanceledException` |

---

## 7. Thread Safety

- `SseConnection` wraps a single `PipeWriter`. It is designed for sequential use by one
  handler. Concurrent writes from multiple tasks will interleave and produce corrupt
  SSE output. Caller must serialize access.

---

## 8. AOT Compatibility

| Element | Status | Notes |
|---------|:------:|-------|
| `StringBuilder` | ✅ | BCL native AOT |
| `string.Replace`, `string.Split` | ✅ | |
| `Encoding.UTF8` | ✅ | |
| `PipeWriter.WriteAsync` | ✅ | Already in use by existing code |
| No reflection / `dynamic` / `Type.GetType` | ✅ | |

---

## 9. Testing Strategy

| Test | What |
|------|------|
| Single-line data → correct output | `WriteEventAsync("text", "hi")` → `event: text\ndata: hi\n\n` |
| Multi-line data → each line prefixed | Python code block with newlines → correct `data:` prefix on each line |
| `\r\n` normalization | Windows-style newlines → treated as `\n` |
| `\r` stripping | Bare CR → removed before processing |
| Empty data → `data: \n` | `WriteEventAsync("x", "")` → one empty `data:` line |
| Null data → `data: \n` | `WriteEventAsync("x", null)` → one empty `data:` line |
| Error with special chars | `"a\"b\\c"` → `{"message":"a\"b\\c"}` |
| Error with newlines | `"a\nb"` → `{"message":"a b"}` |
| Event type validation | null / empty / contains `\n` → `ArgumentException` |
| Completes after write | Output ends with `\n\n` |
