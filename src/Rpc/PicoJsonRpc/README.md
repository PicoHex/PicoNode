# PicoJsonRpc

**AOT-first JSON-RPC 2.0 protocol core** — NDJSON framing, child-process
lifecycle management, and an id-routed multiplexed client. The envelope is
hand-written UTF-8 JSON (`Utf8JsonWriter`); typed payloads are PicoJetson
compile-time shapes (zero reflection).

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
| `JsonRpcEnvelopes` | `JsonRpcFrame` request/response/notification/error shapes + `JsonRpcCodec.Serialize/Deserialize` |

## Wire

One JSON-RPC 2.0 object per line (NDJSON). Every frame carries
`"jsonrpc":"2.0"`, `params`/`result`/`error.data` are **inline JSON values**
(raw payload text in the API, inlined here — not a string containing JSON), and
member presence follows the frame kind:

```text
{"jsonrpc":"2.0","id":"req-1","method":"stream","params":{"messages":[]}}   request
{"jsonrpc":"2.0","method":"chunk","params":{"text":"a"}}                    notification — no id
{"jsonrpc":"2.0","id":"req-2","result":{"ok":true}}                        response — one of …
{"jsonrpc":"2.0","id":"req-2","error":{"code":-32601,"message":"no such method"}}   … result / error
```

- `params` is omitted when a caller passes `null` (JSON-RPC 2.0 requires a
  structured value; `"params":null` would be non-conforming).
- A response carries exactly one of `result`/`error`; a null result is written
  as `"result":null`.
- An `id` that IS a JSON number is echoed as a number (`"id":7`), so a peer that
  sends numeric ids is answered in kind. The rule is textual, not typed: any id
  text that parses as a JSON number is emitted as that number, so the STRING
  `"7"` also goes out as `7` (unobservable for this library's own GUID ids). An
  id with surrounding whitespace is normalised to the number's token, never
  re-emitted verbatim — no id can put a line break in the frame.
- **One line per frame, always.** Payload text is parsed once; text carrying a
  line break is re-emitted compactly, so a pretty-printed
  `params`/`result`/`error.data` is normalised rather than rejected (same JSON
  value, no insignificant whitespace) and a payload with a trailing newline
  cannot append a second line. Text already on one line keeps its exact bytes.
  The payload TEXT also decides the emitted JSON kind: `"7"` → `7`, `"true"` →
  `true`, `"null"` → `null`, `"ok"` → `"ok"`, and malformed text (`{"ok":true`)
  degrades to a quoted string instead of a broken line. See the `JsonRpcFrame`
  remarks.
- Payload nesting is inlined up to 256 levels; deeper text degrades to a JSON
  string. The reader's depth limit is derived from that constant, so a frame this
  writer can emit is always a frame it can read back.
- **Batching is unsupported.** A line that starts with `[` is not decoded: the
  framer yields a `-32600 Invalid Request` frame (null id), and the stream keeps
  flowing — the channel is not failed. Note the second half honestly: that frame
  has **no id, so `JsonRpcClient` drops it** — a peer that offers a batch gets no
  reply. Answering it would need a protocol-error sink, which is a separate API
  decision.

## Quick Start

```csharp
using PicoJsonRpc;

// Spawn the extension subprocess (stdio is the transport)
var lifecycle = new ProcessLifecycle(
    "my-extension",
    ["node", "extension.js"]
);
await using var client = new JsonRpcClient(lifecycle);

// Id-routed call: params/result are raw JSON text, inlined on the wire
// (typed payloads: JsonRpcCodec.Serialize/Deserialize<T>, compile-time shapes)
var frame = await client.CallAsync(
    "tools/list",
    paramsJson: null,
    CancellationToken.None
);
if (frame.Error is { } err)
    throw new JsonRpcException(err.Code, err.Message);
var result = JsonRpcCodec.Deserialize<ToolsListResult>(frame.Result!); // compile-time shape
```

## AOT Contract

Zero reflection, zero runtime code generation — the frame envelope is
hand-written `Utf8JsonWriter`/`JsonDocument` code and every typed payload is a
compile-time PicoJetson shape. No IL2xxx exception list.