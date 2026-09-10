# PicoJsonRpc

**AOT-first JSON-RPC 2.0 protocol core** — NDJSON framing, child-process
lifecycle management, and an id-routed multiplexed client. Serialized with
PicoJetson (compile-time shapes only, zero reflection).

## Positioning

- **Protocol core only** — no transport opinion beyond stdio/sockets/pipes:
  `ProcessLifecycle` spawns a child process and manages its stream pair;
  `NdjsonFramer` frames any duplex stream. Higher-level bindings
  (provider/store/tool JSON-RPC extensions, MCP v2) live in their consuming
  products.
- **AOT** — `<IsAotCompatible>true</IsAotCompatible>` + `<IsTrimmable>true</IsTrimmable>`;
  every wire DTO is a compile-time shape (PicoJetson contract).
- **RFC 2.0 discipline** — id-routed request/response correlation, error
  objects, out-of-order response handling, bounded in-flight calls.

## Components

| Piece | Purpose |
|---|---|
| `NdjsonFramer` | Newline-delimited JSON framing over a stream (`Encode` / `ReadAsync`) |
| `JsonRpcClient` | Multiplexed client: unique id per call, concurrent in-flight (bounded), response routing |
| `ProcessLifecycle` | Child-process lifecycle over stdio (spawn, idle recycling, crash backoff) |
| `JsonRpcEnvelopes` | `JsonRpcFrame` request/response/notification/error shapes + PicoJetson `Serialize/Deserialize` |

## Quick Start

```csharp
using PicoJsonRpc;

// Spawn the extension subprocess (stdio is the transport)
var lifecycle = new ProcessLifecycle(
    "my-extension",
    ["node", "extension.js"]
);
await using var client = new JsonRpcClient(lifecycle);

// Id-routed call: params/result are raw JSON (PicoJetson shapes via
// JsonRpcFrame.Serialize/Deserialize<T>)
var frame = await client.CallAsync(
    "tools/list",
    paramsJson: null,
    CancellationToken.None
);
if (frame.Error is { } err)
    throw new JsonRpcException(err.Code, err.Message);
var result = JsonRpcFrame.Deserialize<ToolsListResult>(frame.Result!); // compile-time shape
```

## AOT Contract

Zero reflection, zero runtime code generation — all serialization is
compile-time PicoJetson shapes. No IL2xxx exception list.