# PicoNode Codebase Fix & Optimization Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix validated bugs, eliminate deadlock risks, and address the top performance and code-quality gaps identified during the source audit of PicoNode.

**Architecture:** Four phases ordered by risk: (1) null-validation gaps + sync-over-async deadlock fixes, (2) HPACK response encoding overhaul (the dominant performance gap), (3) `ConfigureAwait(false)` coverage in library hot paths, (4) structural hardening of `ConnectionRuntimeState` and exception-logging gaps. Each phase produces independently testable, buildable code.

**Tech Stack:** .NET 10, C# 13, System.IO.Pipelines, System.Buffers, System.Threading.Channels

---
## File Map (all phases)

| File | Phase | Change |
|------|-------|--------|
| `src/PicoNode/TcpNode.cs` | 1 | Add `ArgumentNullException.ThrowIfNull` for `ConnectionHandler` |
| `src/PicoNode/UdpNode.cs` | 1 | Add `ArgumentNullException.ThrowIfNull` for `DatagramHandler` |
| `src/PicoNode.Web/Internal/CompressedReadStream.cs` | 1 | Rework sync Read/Dispose to avoid `GetAwaiter().GetResult()` |
| `src/PicoNode.Web/MultipartFormDataParser.cs` | 1 | Rework sync `Parse` to avoid `GetAwaiter().GetResult()` |
| `src/PicoNode.Http/Internal/ConnectionRuntime/Http2StreamHandler.cs` | 2 | Replace `WriteResponseHeaders` with `HpackEncoder.Encode` |
| `src/PicoNode.Http/Internal/Hpack/HpackEncoder.cs` | 2 | Fix `StaticTableIndex` to index all entries; add Huffman encoding |
| `src/PicoNode.Http/Internal/Hpack/StaticTable.cs` | 2 | (Read-only, no change needed) |
| `src/PicoNode/TcpConnection.cs` | 3 | Add `ConfigureAwait(false)` to all awaits |
| `src/PicoNode/TcpConnectionLifecycle.cs` | 3 | Add `ConfigureAwait(false)` to all awaits |
| `src/PicoNode/TcpConnectionReceiveLoop.cs` | 3 | Add `ConfigureAwait(false)` to all awaits |
| `src/PicoNode/TcpNode.cs` | 3 | Add `ConfigureAwait(false)` to all awaits |
| `src/PicoNode/UdpNode.cs` | 3 | Complete existing single usage to all awaits |
| `src/PicoNode.Http/HttpConnectionHandler.cs` | 3 | Add `ConfigureAwait(false)` to all awaits |
| `src/PicoNode.Http/Internal/ConnectionRuntime/Http1ConnectionProcessor.cs` | 3 | Complete existing single usage to all awaits |
| `src/PicoNode.Http/Internal/ConnectionRuntime/Http2ConnectionProcessor.cs` | 3 | Add `ConfigureAwait(false)` to all awaits |
| `src/PicoNode.Http/Internal/ConnectionRuntime/Http2StreamHandler.cs` | 3 | Add `ConfigureAwait(false)` to all awaits |
| `src/PicoNode.Http/Internal/ConnectionRuntime/WebSocketMessageProcessor.cs` | 3 | Add `ConfigureAwait(false)` to all awaits |
| `src/PicoNode.Http/Internal/ConnectionRuntime/ConnectionRuntimeState.cs` | 4 | Replace `Dictionary<int, ...>` with `ConcurrentDictionary<int, ...>`; use `Interlocked` for counters |
| `src/PicoNode.Web/WebApp.cs` | — | (No changes needed, uses synchronous delegates) |
| `src/PicoWeb/WebServer.cs` | — | (No changes needed) |

---

## Phase 1 — Critical Fixes

### Task 1.1: `TcpNodeOptions.ConnectionHandler` null validation

**Files:**
- Modify: `src/PicoNode/TcpNode.cs:36` (insert after `Options = options;`)

TcpNodeOptions uses `= null!` to suppress NRT warnings but doesn't validate at construction time. Fix: either add `required` to the property (like `HttpConnectionHandlerOptions.RequestHandler`) or add an explicit null check in the `TcpNode` constructor.

- [ ] **Step 1: Add null check in TcpNode constructor**

```csharp
// src/PicoNode/TcpNode.cs — after line 37 (ArgumentNullException.ThrowIfNull(options))
ArgumentNullException.ThrowIfNull(options.ConnectionHandler);
```

Insert after:
```csharp
ArgumentNullException.ThrowIfNull(options);
Options = options;
ArgumentNullException.ThrowIfNull(options.ConnectionHandler);
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PicoNode/PicoNode.csproj -c Release`
Expected: Build succeeds with no errors.

- [ ] **Step 3: Run existing tests to verify no regression**

Run: `dotnet test tests/PicoNode.Tests/PicoNode.Tests.csproj -c Release`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/PicoNode/TcpNode.cs
git commit -m "fix: add null validation for TcpNodeOptions.ConnectionHandler"
```

### Task 1.2: `UdpNodeOptions.DatagramHandler` null validation

**Files:**
- Modify: `src/PicoNode/UdpNode.cs`

Same pattern as Task 1.1 but for UdpNode.

- [ ] **Step 1: Add null check in UdpNode constructor**

```csharp
// src/PicoNode/UdpNode.cs — after options null check
ArgumentNullException.ThrowIfNull(options);
Options = options;
ArgumentNullException.ThrowIfNull(options.DatagramHandler);
```

Insert after the existing `ArgumentNullException.ThrowIfNull(options);` at approximately line 35.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PicoNode/PicoNode.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 3: Run existing tests**

Run: `dotnet test tests/PicoNode.Tests/PicoNode.Tests.csproj -c Release`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/PicoNode/UdpNode.cs
git commit -m "fix: add null validation for UdpNodeOptions.DatagramHandler"
```

### Task 1.3: `CompressedReadStream` — Remove sync-over-async deadlock risk

**Files:**
- Modify: `src/PicoNode.Web/Internal/CompressedReadStream.cs`

The sync `Read(byte[], int, int)` and `Dispose(bool)` methods call `.AsTask().GetAwaiter().GetResult()`, which can deadlock in synchronization contexts. The class extends `Stream` so it must implement sync Read. Fix: use `Task.Run` to offload the async work to the thread pool and block there, avoiding the sync context capture.

- [ ] **Step 1: Replace sync Read implementation**

Current (lines 42-49):
```csharp
public override int Read(byte[] buffer, int offset, int count)
{
    ArgumentNullException.ThrowIfNull(buffer);
    return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
        .AsTask()
        .GetAwaiter()
        .GetResult();
}
```

Replace with:
```csharp
public override int Read(byte[] buffer, int offset, int count)
{
    ArgumentNullException.ThrowIfNull(buffer);
    // Offload to thread pool to avoid deadlock from sync-context capture.
    return Task.Run(
        () => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
    ).GetAwaiter().GetResult();
}
```

- [ ] **Step 2: Replace Dispose(bool) implementation**

Current (lines 153-160):
```csharp
protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    base.Dispose(disposing);
}
```

Replace with:
```csharp
protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        Task.Run(async () => await DisposeAsync().ConfigureAwait(false))
            .GetAwaiter().GetResult();
    }
    base.Dispose(disposing);
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 4: Run CompressionMiddleware tests**

Run: `dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj -c Release --filter "Compression" --no-build`
Expected: All compression-related tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.Web/Internal/CompressedReadStream.cs
git commit -m "fix: avoid sync-over-async deadlock in CompressedReadStream"
```

### Task 1.4: `MultipartFormDataParser.Parse` — Remove sync-over-async deadlock risk

**Files:**
- Modify: `src/PicoNode.Web/MultipartFormDataParser.cs:37`

The sync `Parse` calls `.GetAwaiter().GetResult()` directly, which can deadlock.

- [ ] **Step 1: Replace the deadlock-prone call**

Current (line 37):
```csharp
return StreamingMultipartParser.ParseAsync(
    bodyStream, boundary).GetAwaiter().GetResult();
```

Replace with:
```csharp
return Task.Run(
    () => StreamingMultipartParser.ParseAsync(bodyStream, boundary)
).GetAwaiter().GetResult();
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 3: Run Multipart tests**

Run: `dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj -c Release --filter "Multipart" --no-build`
Expected: All multipart-related tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/PicoNode.Web/MultipartFormDataParser.cs
git commit -m "fix: avoid sync-over-async deadlock in MultipartFormDataParser.Parse"
```

---

## Phase 2 — HTTP/2 HPACK Response Encoding

### Task 2.1: Fix `HpackEncoder.StaticTableIndex` to index all entries

**Files:**
- Modify: `src/PicoNode.Http/Internal/Hpack/HpackEncoder.cs:19-32`

The current `StaticTableIndex` dictionary stores only the first static-table entry per header name. This causes `:method: POST`, `:path: /index.html`, `:scheme: https`, and `:status` values other than 200 to miss indexed encoding. Fix: store a list of entries per name, and check all of them during `TryEncodeIndexed`.

- [ ] **Step 1: Change `StaticTableIndex` from single-entry to multi-entry lookup**

Replace the static constructor and field declaration (lines 14-32):

```csharp
// Static table: name_lower -> list of (index, value_or_null)
private static readonly Dictionary<string, List<(int Index, string? Value)>> StaticTableIndex;

static HpackEncoder()
{
    StaticTableIndex = new Dictionary<string, List<(int, string?)>>(StringComparer.OrdinalIgnoreCase);
    for (int i = 1; i <= StaticTable.EntryCount; i++)
    {
        var entry = StaticTable.Entries[i];
        if (!StaticTableIndex.TryGetValue(entry.Name, out var list))
        {
            list = new List<(int, string?)>(capacity: 2);
            StaticTableIndex[entry.Name] = list;
        }
        list.Add((i, string.IsNullOrEmpty(entry.Value) ? null : entry.Value));
    }
}
```

- [ ] **Step 2: Update `TryEncodeIndexed` to iterate all entries per name**

Current (lines 62-80):
```csharp
if (StaticTableIndex.TryGetValue(name, out var entry))
{
    if (entry.Value is not null && entry.Value == value)
    {
        EncodeIntegerWithPrefix(ms, entry.Index, 7, 0x80);
        return true;
    }

    if (entry.Value is null)
    {
        EncodeIntegerWithPrefix(ms, entry.Index, 6, 0x40);
        EncodeString(ms, value);
        _dynamicTable.Add(name, value);
        return true;
    }
}
```

Replace with:
```csharp
if (StaticTableIndex.TryGetValue(name, out var entries))
{
    // 1) Try exact value match first (indexed representation, 1 byte).
    foreach (var (idx, val) in entries)
    {
        if (val is not null && val == value)
        {
            EncodeIntegerWithPrefix(ms, idx, 7, 0x80);
            return true;
        }
    }

    // 2) No exact match — use the first name-only entry (val is null)
    //    for literal-with-indexing (name reference + string value).
    foreach (var (idx, val) in entries)
    {
        if (val is null)
        {
            EncodeIntegerWithPrefix(ms, idx, 6, 0x40);
            EncodeString(ms, value);
            _dynamicTable.Add(name, value);
            return true;
        }
    }

    // 3) Every entry for this name has a non-null value, none matched.
    //    Use the first entry's name index for literal-with-indexing.
    var (firstIdx, _) = entries[0];
    EncodeIntegerWithPrefix(ms, firstIdx, 6, 0x40);
    EncodeString(ms, value);
    _dynamicTable.Add(name, value);
    return true;
}
```

- [ ] **Step 3: Build and run existing HpackEncoder tests**

Run: `dotnet build src/PicoNode.Http/PicoNode.Http.csproj -c Release`
Run: `dotnet test tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release --filter "HpackEncoder" --no-build`
Expected: All HpackEncoder tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/PicoNode.Http/Internal/Hpack/HpackEncoder.cs
git commit -m "fix: index all static-table entries in HPACK encoder, not just first per name"
```

### Task 2.2: Wire `HpackEncoder` into HTTP/2 response path

**Files:**
- Modify: `src/PicoNode.Http/Internal/ConnectionRuntime/Http2StreamHandler.cs`
- Modify: `src/PicoNode.Http/Internal/ConnectionRuntime/ConnectionRuntimeState.cs` (add `ResponseHpackEncoder` property)

Currently, `WriteResponseHeaders` encodes headers as literal-without-indexing, name index 0 (full ASCII strings). Replace it with `HpackEncoder.Encode` which uses static/dynamic table references, Huffman encoding, and incremental indexing.

The `HpackEncoder` maintains its own `HpackDynamicTable` for response headers sent TO the client. This is separate from the `ConnectionRuntimeState.HpackTable` which is the decoder's dynamic table (tracks request headers received FROM the client). These MUST be independent per HPACK RFC §2.3. Each `TcpConnection` accumulates entries in its response encoder's dynamic table.

- [ ] **Step 1: Add `HpackEncoder` instance to `ConnectionRuntimeState`**

In `src/PicoNode.Http/Internal/ConnectionRuntime/ConnectionRuntimeState.cs`, add a property:

```csharp
// After existing HpackTable line (line 20):
// HPACK encoder for OUTGOING response headers. Uses its own dynamic table
// (independent from HpackTable which tracks incoming request headers).
public HpackEncoder ResponseHpackEncoder { get; } = new();
```

Add `using PicoNode.Http.Internal.Hpack;` if not already present at top of file (check GlobalUsings.cs — it already has `using PicoNode.Http.Internal.Hpack;`).

- [ ] **Step 2: Replace `WriteResponseHeaders` with `HpackEncoder.Encode` in `Http2StreamHandler`**

There are 6 call sites of `WriteResponseHeaders` / `MeasureResponseHeadersSize` in the file. Replace all of them with `EncodeResponseHeadersHpack`, then delete the obsolete methods in the same commit.

Add a helper method at the top of the `// ── Minimal HPACK encoder ──` section:

```csharp
// ── HPACK response encoder (wraps HpackEncoder with connection's dynamic table) ──

private static byte[] EncodeResponseHeadersHpack(
    ITcpConnectionContext connection,
    List<(string Name, string Value)> headers)
{
    var state = connection.UserState as ConnectionRuntimeState;
    var encoder = state?.ResponseHpackEncoder ?? new HpackEncoder();
    // Convert (string, string)[] to IReadOnlyList<(string, string)>
    return encoder.Encode(headers);
}
```

Then replace each occurrence of:
```csharp
var hpackSize = MeasureResponseHeadersSize(responseHeaders);
// ... rent buffer ...
WriteResponseHeaders(frameRented.AsSpan(...), responseHeaders);
```

With:
```csharp
var encodedHeaders = EncodeResponseHeadersHpack(connection, responseHeaders);
var hpackSize = encodedHeaders.Length;
// ... rent buffer of FrameHeaderSize + hpackSize ...
encodedHeaders.CopyTo(frameRented.AsSpan(Http2FrameCodec.FrameHeaderSize));
```

The first call site is at `ProcessHeadersFrame` around line 214. Replace the block:
```csharp
var hpackSize = MeasureResponseHeadersSize(responseHeaders);
var headersFlags = Http2FrameFlags.EndHeaders;

if (response.Body.Length == 0 && response.BodyStream is null)
{
    headersFlags |= Http2FrameFlags.EndStream;
    var frameRented = ArrayPool<byte>.Shared.Rent(
        Http2FrameCodec.FrameHeaderSize + hpackSize
    );
    try
    {
        Http2FrameCodec.WriteFrameHeader(
            frameRented,
            hpackSize,
            Http2FrameType.Headers,
            headersFlags,
            frame.StreamId
        );
        WriteResponseHeaders(
            frameRented.AsSpan(Http2FrameCodec.FrameHeaderSize),
            responseHeaders
        );
        await connection.SendAsync(...);
    }
    finally { ArrayPool<byte>.Shared.Return(frameRented); }
    return false;
}
```

With:
```csharp
var encodedHeaders = EncodeResponseHeadersHpack(connection, responseHeaders);
var headersFlags = Http2FrameFlags.EndHeaders;

if (response.Body.Length == 0 && response.BodyStream is null)
{
    headersFlags |= Http2FrameFlags.EndStream;
    var frameRented = ArrayPool<byte>.Shared.Rent(
        Http2FrameCodec.FrameHeaderSize + encodedHeaders.Length
    );
    try
    {
        Http2FrameCodec.WriteFrameHeader(
            frameRented,
            encodedHeaders.Length,
            Http2FrameType.Headers,
            headersFlags,
            frame.StreamId
        );
        encodedHeaders.CopyTo(frameRented.AsSpan(Http2FrameCodec.FrameHeaderSize));
        await connection.SendAsync(
            new ReadOnlySequence<byte>(
                frameRented.AsMemory(0, Http2FrameCodec.FrameHeaderSize + encodedHeaders.Length)
            ),
            ct
        );
    }
    finally { ArrayPool<byte>.Shared.Return(frameRented); }
    return false;
}
```

Apply the same transformation to the remaining 5 call sites. Each follows the same pattern:
- Calculate: `var encodedHeaders = EncodeResponseHeadersHpack(connection, responseHeaders);`
- Allocate: `ArrayPool<byte>.Shared.Rent(Http2FrameCodec.FrameHeaderSize + encodedHeaders.Length)`
- Write header: `Http2FrameCodec.WriteFrameHeader(frameRented, encodedHeaders.Length, ...)`
- Copy payload: `encodedHeaders.CopyTo(frameRented.AsSpan(Http2FrameCodec.FrameHeaderSize))`
- Send: `connection.SendAsync(new ReadOnlySequence<byte>(frameRented.AsMemory(0, FrameHeaderSize + encodedHeaders.Length)))`

The 5 remaining call sites are:
1. **ProcessHeadersFrame — body case** (around line 263): Same structure as above but no EndStream flag (body follows in DATA frames). The chunked DATA frame sending logic below remains unchanged.
2. **ProcessHeadersFrame — BodyStream case** (line 254): Delegates to `SendResponseAsync`. Change is inside that method.
3. **ProcessWebSocketOverHttp2** (around line 445): Response is `:status: 200`. Replace the inline `WriteResponseHeaders` + `MeasureResponseHeadersSize` with `EncodeResponseHeadersHpack`.
4. **SendResponseAsync — empty body case** (around line 892): Replace `MeasureResponseHeadersSize` + `WriteResponseHeaders` with `EncodeResponseHeadersHpack`.
5. **SendResponseAsync — body present case** (around line 942): Same as case 4 but HEADERS frame doesn't have EndStream.

Delete the now-unused methods from the file: `WriteResponseHeaders`, `MeasureResponseHeadersSize`, `EncodeResponseHeaders` (the byte[] overload), `MeasureRawStringSize`, and `WriteRawString`.

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/PicoNode.Http/PicoNode.Http.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 4: Run HTTP/2 tests**

Run: `dotnet test tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release`
Expected: All tests pass (existing tests use the public API, not the internal encoder).

- [ ] **Step 5: Run smoke tests (HTTP/2 round-trip)**

Run: `dotnet test tests/PicoNode.Smoke/PicoNode.Smoke.csproj -c Release`
Expected: All smoke tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/PicoNode.Http/Internal/ConnectionRuntime/Http2StreamHandler.cs
git add src/PicoNode.Http/Internal/ConnectionRuntime/ConnectionRuntimeState.cs
git commit -m "perf: wire HpackEncoder into HTTP/2 response path, enable static/dynamic table references"
```

### Task 2.3: Add Huffman encoding to `HpackEncoder`

**Files:**
- Modify: `src/PicoNode.Http/Internal/Hpack/HpackEncoder.cs:161-167`

Current `EncodeString` uses "Non-Huffman string (bit 7 = 0)". Add Huffman encoding support to reduce HPACK string sizes.

Ref: RFC 7541 Appendix B (Huffman code table). The code table is already defined in `HpackDecoder.cs`. Extract it to a shared helper, or duplicate it in the encoder.

- [ ] **Step 1: Add Huffman encoding helper to HpackEncoder**

Create a private static method:

```csharp
private static byte[] HuffmanEncode(ReadOnlySpan<byte> data)
{
    // Huffman code table from RFC 7541 Appendix B
    // (codes and bit-lengths matching HpackDecoder.InitHuffmanCodes)
    // Returns the Huffman-encoded bytes.
}
```

The `HpackDecoder.cs` already defines the full Huffman code table as `private static readonly (uint Code, int BitLength)[] HuffmanCodes` (257 entries, lines ~21-280) and builds a binary decode tree (`HuffLeft`/`HuffRight`/`HuffSym`, lines ~282-314) from it in `BuildHuffmanTree()`. Extract the FORWARD table (encoding: symbol → code+bitlen) AND the decode tree building into a shared `HuffmanCodec` class.

- [ ] **Step 1a: Create shared `HuffmanCodec`**

Create `src/PicoNode.Http/Internal/Hpack/HuffmanCodec.cs`:

```csharp
namespace PicoNode.Http.Internal.Hpack;

internal static class HuffmanCodec
{
    /// <summary>257-entry table: symbol → (code, bitLength). Indices 0-255 = byte values, index 256 = EOS.</summary>
    internal static readonly (uint Code, int BitLength)[] Codes = new (uint, int)[257];

    // Decode tree (built from Codes at static init)
    private static readonly int[] HuffLeft;
    private static readonly int[] HuffRight;
    private static readonly short[] HuffSym;

    static HuffmanCodec()
    {
        // Copy the full 257-entry table from HpackDecoder.InitHuffmanCodes()
        var t = Codes;
        t[0] = (0x1FF8, 13);  t[1] = (0x7FFFD8, 23);
        // ... all 256 entries, copied verbatim from HpackDecoder.cs lines ~21-280 ...
        t[256] = (0x3FFFFFFF, 30);

        // Build decode tree (same logic as HpackDecoder.BuildHuffmanTree)
        BuildDecodeTree(out HuffLeft, out HuffRight, out HuffSym);
    }

    private static void BuildDecodeTree(out int[] left, out int[] right, out short[] sym)
    {
        // Same implementation as HpackDecoder.BuildHuffmanTree
        // Copy verbatim from HpackDecoder.cs lines ~282-314
    }

    /// <summary>Huffman-encodes data. Returns the encoded bytes.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return [];

        // 1. Count total bits
        long totalBits = 0;
        for (int i = 0; i < data.Length; i++)
            totalBits += Codes[data[i]].BitLength;

        // 2. Allocate
        var result = new byte[(totalBits + 7) / 8];
        long bitBuffer = 0;
        int bitsInBuffer = 0;
        int pos = 0;

        // 3. Pack bits
        for (int i = 0; i < data.Length; i++)
        {
            var (code, bitLen) = Codes[data[i]];
            bitBuffer = (bitBuffer << bitLen) | code;
            bitsInBuffer += bitLen;
            while (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                result[pos++] = (byte)(bitBuffer >> bitsInBuffer);
            }
        }

        // 4. Pad final byte with 1-bits (EOS prefix per RFC 7541 §5.2)
        if (bitsInBuffer > 0)
            result[pos] = (byte)((bitBuffer << (8 - bitsInBuffer)) | (0xFF >> bitsInBuffer));

        return result;
    }

    /// <summary>Huffman-decode helpers used by HpackDecoder. Returns -1 if decode fails.</summary>
    internal static bool TryDecode(ReadOnlySpan<byte> data, out string? result)
    {
        // Same implementation as HpackDecoder.TryHuffmanDecode, using HuffLeft/HuffRight/HuffSym
        // Copy verbatim
    }
}
```

- [ ] **Step 1b: Simplify `HpackDecoder` to delegate Huffman to `HuffmanCodec`**

In `HpackDecoder.cs`:
- Remove the private `HuffmanCodes` array and `InitHuffmanCodes()` method
- Remove the `HuffLeft`/`HuffRight`/`HuffSym` arrays and `BuildHuffmanTree()` method
- Replace `TryHuffmanDecode` body with a call to `HuffmanCodec.TryDecode(...)`

```csharp
// Before (line ~367):
// private static bool TryHuffmanDecode(ReadOnlySpan<byte> data, out string? result)
// {
//     ... 50 lines of decode tree walking ...
// }

// After:
private static bool TryHuffmanDecode(ReadOnlySpan<byte> data, out string? result)
{
    return HuffmanCodec.TryDecode(data, out result);
}
```

- [ ] **Step 2: Update `HpackEncoder.EncodeString` to use Huffman**

```csharp
private static void EncodeString(MemoryStream ms, string value)
{
    var bytes = DefaultEncoder.GetBytes(value);
    var huffBytes = HuffmanCodec.Encode(bytes);

    // Use Huffman if it saves space, otherwise fall back to plain.
    if (huffBytes.Length < bytes.Length)
    {
        // Huffman string (bit 7 = 1)
        EncodeInteger(ms, huffBytes.Length, 7);
        ms.Write(huffBytes);
    }
    else
    {
        // Non-Huffman string (bit 7 = 0)
        EncodeInteger(ms, bytes.Length, 7);
        ms.Write(bytes);
    }
}
```

- [ ] **Step 3: Build and run tests**

Run: `dotnet build src/PicoNode.Http/PicoNode.Http.csproj -c Release`
Run: `dotnet test tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release`
Expected: All tests pass.

- [ ] **Step 4: Benchmark to measure improvement**

Run: `dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick`
Expected: Observe reduced response header sizes in benchmark output.

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.Http/Internal/Hpack/HuffmanCodec.cs
git add src/PicoNode.Http/Internal/Hpack/HpackEncoder.cs
git add src/PicoNode.Http/Internal/Hpack/HpackDecoder.cs
git commit -m "perf: add Huffman encoding to HPACK encoder with shared HuffmanCodec"
```

---

## Phase 3 — `ConfigureAwait(false)` Coverage

### Task 3.1: Add `ConfigureAwait(false)` to transport layer

**Files:**
- Modify: `src/PicoNode/TcpNode.cs`
- Modify: `src/PicoNode/TcpConnection.cs`
- Modify: `src/PicoNode/TcpConnectionLifecycle.cs`
- Modify: `src/PicoNode/TcpConnectionReceiveLoop.cs`
- Modify: `src/PicoNode/UdpNode.cs`

For every `await` in these files (except where the await immediately follows `Task.Run` or `Task.Delay` where sync-context capture is irrelevant), append `.ConfigureAwait(false)`.

- [ ] **Step 1: Add `ConfigureAwait(false)` in `TcpNode.cs`**

Apply `.ConfigureAwait(false)` to each `await` in:
- `StopAsync` (2 awaits: `_acceptTask.WaitAsync`, `_idleMonitorTask.WaitAsync`, `drained.Task.WaitAsync`)
- `AcceptLoopAsync` (1 await: `acceptArgs.AcceptAsync`)
- `ProcessAcceptedSocketAsync` (no await)
- `TrackAndRunAsync` (1 await: `NegotiateTlsAsync`)
- `NegotiateTlsAsync` (1 await: `sslStream.AuthenticateAsServerAsync`)
- `MonitorIdleConnectionsAsync` (1 await: `Task.Delay`)
- `ConfigReloadLoopAsync` (2 awaits: `config.WaitForChangeAsync`, `config.ReloadAsync`)
- `DisposeAsync` (1 await: `StopAsync`)

- [ ] **Step 2: Add `ConfigureAwait(false)` in `TcpConnection.cs`**

Apply to:
- `SendCoreAsync`: 2 awaits (`_sendLock.WaitAsync`, `_sendPath.SendSequenceAsync`)

- [ ] **Step 3: Add `ConfigureAwait(false)` in `TcpConnectionLifecycle.cs`**

Apply to:
- `RunAsync`: 2 awaits (`_receiveLoop.ExecuteReceiveLoopAsync` in try, `CloseCoreAsync` in finally)
- `CloseCoreAsync`: 3 awaits (`CancelConnectionAsync`, `InvokeClosedHandlerAsync`, `FinalizeCloseAsync` + `DisposeAsync`)
- `DisposeAsync`: 1 await (`DisposeStreamAndSocketAsync`)

- [ ] **Step 4: Add `ConfigureAwait(false)` in `TcpConnectionReceiveLoop.cs`**

Apply to:
- `ExecuteReceiveLoopAsync`: 2 awaits (`InvokeConnectedAsync`, `ProcessPipeAsync`, `PumpSocketToPipeAsync`)
- `ProcessPipeAsync`: 2 awaits (`_pipe.Reader.ReadAsync`, `InvokeOnReceivedAsync`)
- `PumpSocketToPipeAsync`: 3 awaits (`ReceiveIntoPipeBufferAsync`, `FlushReceivedBytesAsync`)
- `ReceiveIntoPipeBufferAsync`: sync path with `Memory<byte>` — no `await` for `_socket.ReceiveAsync` if using sync Result path, but add to the async fallback path.

- [ ] **Step 5: Add `ConfigureAwait(false)` in `UdpNode.cs`**

Already has one usage at line 344; add to all remaining awaits:
- `StopAsync`: 3 awaits (`_receiveTask.WaitAsync`, `Task.WhenAll(...).WaitAsync`)
- `SendAsync`: 1 await (`_socket.SendToAsync`)
- `ReceiveLoopAsync`: 2 awaits (`_socket.ReceiveFromAsync`, `queue.Writer.WriteAsync`)
- `ProcessQueueAsync`: 2 awaits (`reader.WaitToReadAsync`, handler invocation)
- `ConfigReloadLoopAsync`: 2 awaits

- [ ] **Step 6: Build and run transport tests**

Run: `dotnet build src/PicoNode/PicoNode.csproj -c Release`
Run: `dotnet test tests/PicoNode.Tests/PicoNode.Tests.csproj -c Release`
Expected: All tests pass.

- [ ] **Step 7: Build and run smoke tests**

Run: `dotnet test tests/PicoNode.Smoke/PicoNode.Smoke.csproj -c Release`
Expected: All smoke tests pass (confirms TCP/UDP behavior unchanged).

- [ ] **Step 8: Commit**

```bash
git add src/PicoNode/
git commit -m "perf: add ConfigureAwait(false) to all transport-layer awaits"
```

### Task 3.2: Add `ConfigureAwait(false)` to HTTP layer

**Files:**
- Modify: `src/PicoNode.Http/HttpConnectionHandler.cs`
- Modify: `src/PicoNode.Http/Internal/ConnectionRuntime/Http1ConnectionProcessor.cs`
- Modify: `src/PicoNode.Http/Internal/ConnectionRuntime/Http2ConnectionProcessor.cs`
- Modify: `src/PicoNode.Http/Internal/ConnectionRuntime/Http2StreamHandler.cs`
- Modify: `src/PicoNode.Http/Internal/ConnectionRuntime/WebSocketMessageProcessor.cs`

- [ ] **Step 1: `HttpConnectionHandler.cs`**

Apply `.ConfigureAwait(false)` to:
- `OnReceivedAsync` (no awaits in the main dispatch — all ValueTask returns)
- `SendInitialSettingsAsync` (1 await: `connection.SendAsync`)
- `ProcessWebSocketFrameAsync` (1 await)

- [ ] **Step 2: `Http1ConnectionProcessor.cs`**

Already has one at line 407 (`await using (stream.ConfigureAwait(false))`). Apply to all remaining:
- `HandleRequestAsync`: Response sending, `BufferStreamResponseAsync`
- `SendStreamingResponseAsync`: Chunked send loop
- `SendResponseAsync`
- `HandleProtocolErrorAsync`
- `HandleH2cUpgradeAsync`

- [ ] **Step 3: `Http2ConnectionProcessor.cs`**

Apply to all awaits:
- `ProcessAsync`: `SendInitialSettingsAsync` path, `connection.SendAsync`, `HandleFrameAsync`
- `HandleFrameAsync`: `SendGoAwayAndCloseAsync`, `SendSettingsAck`, `Http2StreamHandler.ProcessHeadersFrame`/`ProcessDataFrame`
- `SendGoAwayAndCloseAsync`

- [ ] **Step 4: `Http2StreamHandler.cs`**

Apply to all awaits:
- `ProcessHeadersFrame`: `SendRstStreamAsync`, `SendGoAwayAndCloseAsync`, `requestHandler`, `connection.SendAsync`
- `CompleteDeferredRequest`: `requestHandler`, `SendResponseAsync`
- `ProcessDataFrame` (no awaits except in `CompleteDeferredRequest`)
- `ProcessWindowUpdateFrame`: `FlushPendingDataAsync`
- `FlushPendingDataAsync`: `connection.SendAsync` loop
- `SendResponseAsync`: `connection.SendAsync` + stream read loop
- `ProcessWebSocketOverHttp2`
- `SendRstStreamAsync`

- [ ] **Step 5: `WebSocketMessageProcessor.cs`**

Apply to all awaits:
- `ProcessAsync`: `connection.SendAsync` for Ping/Pong/Close responses, `handler` invocation

- [ ] **Step 6: Build and run HTTP tests**

Run: `dotnet build src/PicoNode.Http/PicoNode.Http.csproj -c Release`
Run: `dotnet test tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/PicoNode.Http/
git commit -m "perf: add ConfigureAwait(false) to all HTTP-layer awaits"
```

---

## Phase 4 — Structural Hardening

### Task 4.1: Harden `ConnectionRuntimeState` synchronization

**Files:**
- Modify: `src/PicoNode.Http/Internal/ConnectionRuntime/ConnectionRuntimeState.cs`
- Modify: `src/PicoNode.Http/Internal/ConnectionRuntime/Http2ConnectionProcessor.cs`
- Modify: `src/PicoNode.Http/Internal/ConnectionRuntime/Http2StreamHandler.cs`

The current architecture is sequentially safe because `OnReceivedAsync` calls are serialized. This hardening adds explicit synchronization as defensive protection for future parallel evolution.

- [ ] **Step 1: Replace `Dictionary` with `ConcurrentDictionary` for `Http2Streams`**

In `ConnectionRuntimeState.cs`:

```csharp
// Before:
public Dictionary<int, Http2StreamState>? Http2Streams { get; set; }

// After:
public ConcurrentDictionary<int, Http2StreamState>? Http2Streams { get; set; }
```

- [ ] **Step 2: Make `HighestProcessedStreamId` and `ConnectionSendWindow` use `Interlocked`**

```csharp
// Before:
public int HighestProcessedStreamId { get; set; }
public int ConnectionSendWindow { get; set; } = 65535;

// After:
private int _highestProcessedStreamId;
public int HighestProcessedStreamId
{
    get => Volatile.Read(ref _highestProcessedStreamId);
    set => Interlocked.Exchange(ref _highestProcessedStreamId, value);
}

private int _connectionSendWindow = 65535;
public int ConnectionSendWindow
{
    get => Volatile.Read(ref _connectionSendWindow);
    set => Interlocked.Exchange(ref _connectionSendWindow, value);
}
```

- [ ] **Step 3: Add `AddConnectionSendWindow` helper for atomic increments**

```csharp
public int AddConnectionSendWindow(int increment) =>
    Interlocked.Add(ref _connectionSendWindow, increment);
```

- [ ] **Step 4: Update call sites in `Http2ConnectionProcessor` and `Http2StreamHandler`**

Replace direct assignments and compound assignments:
- `state.ConnectionSendWindow += increment` → `state.AddConnectionSendWindow(increment)`
- `state.ConnectionSendWindow -= toSend` → `state.AddConnectionSendWindow(-toSend)`
- `state.HighestProcessedStreamId = streamId` → works via property setter
- `streamCount >= runtimeStateForLimit.RemoteMaxConcurrentStreams` → no change needed
- `state.Http2Streams?[key]` → still works with `ConcurrentDictionary`

- [ ] **Step 5: Build and run tests**

Run: `dotnet build src/PicoNode.Http/PicoNode.Http.csproj -c Release`
Run: `dotnet test tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release`
Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/PicoNode.Http/Internal/ConnectionRuntime/ConnectionRuntimeState.cs
git add src/PicoNode.Http/Internal/ConnectionRuntime/Http2ConnectionProcessor.cs
git add src/PicoNode.Http/Internal/ConnectionRuntime/Http2StreamHandler.cs
git commit -m "refactor: add explicit synchronization to ConnectionRuntimeState for future parallel safety"
```

### Task 4.2: Improve `ConfigReloadLoopAsync` exception logging

**Files:**
- Modify: `src/PicoNode/TcpNode.cs`
- Modify: `src/PicoNode/UdpNode.cs`

The empty `catch { }` in config reload loops makes debugging difficult.

- [ ] **Step 1: Add logging to TcpNode's catch block**

Current:
```csharp
catch
{ /* config reload is best-effort */ }
```

Replace with:
```csharp
catch (Exception ex)
{
    Options.Logger?.Log(
        LogLevel.Warning,
        new EventId(0),
        "Config reload failed (best-effort, continuing)",
        ex
    );
}
```

- [ ] **Step 2: Apply same pattern to UdpNode's catch block**

Same change in `UdpNode.cs`.

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/PicoNode/PicoNode.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/PicoNode/TcpNode.cs src/PicoNode/UdpNode.cs
git commit -m "refactor: log config reload exceptions instead of swallowing silently"
```

### Task 4.3: Verify no dead code remains after Phase 2

**Files:**
- (No modifications — pure verification)

The obsolete methods (`WriteResponseHeaders`, `MeasureResponseHeadersSize`, `EncodeResponseHeaders` byte[] overload, `WriteRawString`, `MeasureRawStringSize`) should have been removed in Task 2.2. This task confirms they are gone.

- [ ] **Step 1: Scan for remaining dead methods**

```bash
grep -rn "WriteResponseHeaders\|MeasureResponseHeadersSize\|EncodeResponseHeaders\|WriteRawString\|MeasureRawStringSize" src/ --include="*.cs" | grep -v obj/
```

Expected: no hits (all obsolete methods were removed in Task 2.2).

- [ ] **Step 2: Confirm HpackEncoder.cs is in use**

```bash
grep -rn "HpackEncoder" src/ --include="*.cs" | grep -v obj/ | grep -v "HpackEncoder.cs$"
```

Expected: hits in `Http2StreamHandler.cs` and `ConnectionRuntimeState.cs`, confirming the encoder is wired into the hot path.

- [ ] **Step 3: Build and run tests**

Run: `dotnet build src/PicoNode.Http/PicoNode.Http.csproj -c Release`
Run: `dotnet test tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release`
Expected: All tests pass.

- [ ] **Step 4: Commit (no file changes — informational checkpoint)**

```bash
git commit --allow-empty -m "chore: verify HpackEncoder migration complete, no dead code remains"
```

---

## Build & Validation (end-to-end)

- [ ] **Final build check**

```bash
dotnet build PicoNode.slnx -c Release
```

Expected: Clean build, no warnings.

- [ ] **Full test suite**

```bash
dotnet test tests/PicoNode.Tests/PicoNode.Tests.csproj -c Release
dotnet test tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj -c Release
dotnet test tests/PicoNode.Smoke/PicoNode.Smoke.csproj -c Release
```

Expected: All tests pass.

- [ ] **Benchmark (regression check)**

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

Expected: No regression in throughput; HTTP/2 response size benchmarks should show improvement.

