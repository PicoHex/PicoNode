# Release notes — audit remediation (2026-10-25)

Draft for the release that ships branch `fix/audit-remediation`. Every API line below was produced by
`scripts/api-surface diff` against `api/*.public.txt`; every behaviour claim is backed by a test named in the plan ledgers
under `.superpowers/sdd/`. **The API baselines in `api/` have deliberately not been refreshed** — refresh them once, at
release, together with the version bump below.

## Version

`x` bump (not `y`): the public surface changed in four packages, one change is source-breaking, and the JSON-RPC wire
format changed. Numbering follows the existing scheme; the point is that a `y` bump would be a lie about all three.

## Breaking changes

| Change | Impact | Where |
|---|---|---|
| `ISessionStore.RegenerateAsync(string oldId, CancellationToken)` added as **abstract** | **Source-breaking for anyone implementing `ISessionStore`** — the type gains a required member. `InMemorySessionStore` implements it. | `PicoNode.Web.Session.Abs` |
| `JsonRpcChannel.Stderr` added as **`required`** | **Source-breaking for external code that constructs `JsonRpcChannel`** — the initializer must now be supplied. | `PicoJsonRpc` |
| `InMemoryRateLimitStore(in RateLimitBudget, TimeSpan?)` **replaced** by `(in RateLimitBudget, TimeSpan?, int maxBuckets = 100000)` | Binary-breaking; source-compatible unless the ctor is referenced by method group or reflection. | `PicoNode.Web` |
| JSON-RPC wire format is now standard 2.0 | **Wire-breaking for peers.** See below. | `PicoJsonRpc` |

## JSON-RPC wire format (the only change that moves bytes)

A peer now sees JSON-RPC 2.0 objects instead of a serializer's dump of a C# class:

```
{"jsonrpc":"2.0","id":"req-1","method":"stream","params":{"messages":[]}}
{"jsonrpc":"2.0","method":"chunk","params":{"text":"a"}}            // notification: no "id" member at all
{"jsonrpc":"2.0","id":"req-2","result":{"ok":true}}                 // exactly one of result/error
```

- `params`/`result`/`error.data` are **inlined as JSON values** instead of string-embedded JSON.
- A response carries **exactly one** of `result`/`error` (the old shape emitted both, one of them `null`).
- The nested error is camelCase (`code`/`message`/`data`).
- An inbound numeric `id` is echoed back **as a number**.
- A batch line (`[`) is answered with `-32600` and the channel stays usable.
- Every emitted frame is **exactly one line**: a pretty-printed payload is normalised, because a frame carrying a newline
  would corrupt NDJSON framing (this was a real defect found in review and is pinned by a golden snapshot).

**Consumers must move in lockstep.** `PicoAgent.Binding` (same family) must ship the matching change: it now has to send
`params._streamId` for multi-stream routing, and the old string-embedded payload shape fails at deserialisation rather than
mis-parsing. The repository pins the new shape in `tests/PicoJsonRpc.Tests/Fixtures/wire-v1.ndjson`; a future shape change
must update that file in the same commit as the encoder, and `release.ps1 -DryRun` prints the wire hash and whether it
changed since the last tag.

## Behaviour changes (no API change, but observable)

- **`RemoteCloseToken` now fires on every close** (not only peer-initiated) and stays readable afterwards.
- **A naked RST no longer closes silently**: it surfaces as `RemoteClosed` + `ReceiveFailed`, so `OnFault` fires per RST.
- **`AcceptFaultBackoff = 0` now means 1 ms**, not "retry immediately" (which spun the accept loop).
- **`MaxTotalSizeBytes` now counts header bytes**, not just body bytes.
- **`StopAsync` is idempotent**, and it drains before releasing members; UDP handlers have their own lifetime, so a stop can
  drain them. A drain that gives up after 2 s is **reported if a fault sink is wired** (`Options.Logger`/`OnFault`) and is
  silent otherwise — this is now what the docs say, instead of the stronger claim they used to make.
- **`StopAsync` on a node whose drain times out no longer leaks the internal timeout to the caller.**
- **WebSocket protocol errors now send the RFC 6455 close code** (`1002` protocol error, `1007` invalid UTF-8, `1009` too
  big). Previously every protocol-error path sent a **code-less** close frame.
- **WebSocket masking is enforced**: an unmasked client frame is rejected with `1002` (the server previously accepted it).
- **Multipart**: parts are bounded (`MaxPartCount`, `MaxHeaderBytesPerPart`), an empty part can no longer swallow the next
  one, and `SafeFileName` is exposed.
- **Rate limiting**: buckets are bounded (`MaxBuckets`), eviction no longer refills a bucket's budget, and
  `RateLimitKeys.UntrustedHeader` exists for keys that must not trust the header ordering.
- **Sessions**: the store is bounded (`MaxSessions`, `MaxBytesPerSession`), idle expiry is enforced on load, and rotation
  failures surface instead of being swallowed. `SessionMiddleware.Create` overloads now accept a logger.
- **Caching**: `CacheMiddleware` will not advertise public caching for identity-carrying requests (`IdentityHeaders`,
  `ShouldCache`), and `AuthOptions.RequireIdentity` sends `WWW-Authenticate` on the 401.
- **CORS**: wildcard origin + credentials is now a startup error rather than a silent misconfiguration.
- **HTTP/2**: closed-stream table is bounded, padded DATA is charged to flow control, a stale deferred header-table resize
  can no longer be re-applied, header blocks are written in one send (nothing can interleave), and HPACK integers/header
  lists are validated rather than overflowing.
- **`Http2Frame.Payload` lifetime is now the caller's**: it is a view valid until the caller's `AdvanceTo`, or a pooled
  buffer owned by the frame and returned through the internal `ReturnPayload`. Split payloads (>4 KB frames) still take
  the old pooled copy. Anyone storing `Payload` beyond its sequence must copy.
- **Controller generator**: a `generic` controller is now a **`PCW002` error** (it used to emit code that did not compile);
  `PCW001`/`PCW003` are warnings with real source locations; `[Route("api/x")]` without a leading slash no longer throws at
  startup; stub type names are namespace-qualified. **A generic controller now fails an upgrading build** — this is
  deliberate (silently dropping endpoints is worse), and it is the one change most likely to surprise a consumer.
- **AOT**: `PicoNode.Abs` and `PicoNode.Web.Session.Abs` are now `IsAotCompatible`/`IsTrimmable`, and the AOT gate
  publishes and runs generated controller code.

## Performance (measured, not estimated)

| Change | Before → after |
|---|---|
| HTTP/1 request header parsing | **1688 → 864 B/request (−49 %)** |
| HTTP/2 frame payload | header block **424 → 0 B/frame** |
| SSE event write | **≈624 → 0 B/event**, 6 → 0 objects |
| Controller generator | now genuinely incremental (step reasons `Cached`/`Unchanged`), output byte-identical |

## Test-infrastructure changes a contributor will notice

- `PicoJsonRpc.Tests` needs **node** (CI now installs it); without node, 21 tests **skip loudly** and name themselves.
- A **missing fixture now fails** instead of skipping — "no node on this machine" and "the fixture was not copied" are
  different failures on purpose.
- Perf floors are **scaled by a measured machine calibration**, so a busy CI box no longer reads as a slow product. The
  floor is a bound relative to machine capability, not an absolute throughput claim.
- Docs are guarded by measurement, not a ban-word list: test counts, block coverage and claims are checked against the
  tree.

## Security

**Rotate the key that `e2cb3fe6` removed.** The one-shot script is gone from the tree, but the value is still reachable in
Git history; removing it from the working tree is not the same as revoking it.

## Known limitations left open (tracked, not fixed)

- A UDP datagram still in the socket buffer at stop time is abandoned (only *queued* datagrams drain).
- `Http2Frame` split payloads (> 4 KB frames) still take the old pooled copy — removing it needs a public type change.
- The JSON-RPC client attaches the stderr tail only on the EOF path; a write to an already-dead child surfaces a bare
  `IOException` with no tail (untested, not proven absent).
- `IsComplexType` treats `List<T>`/`T[]`/`Nullable<T>` as complex, so collections resolve through DI rather than body
  binding (compiles; semantically suspect — needs its own audit id), and a generic **action method** still emits `CS0411`.
- `PCW003` over-promises when user code serialises the type elsewhere; `PCW001` has no "controller-shaped" heuristic yet.
- The wire version guard forces a file edit and a visible diff; it does **not** force a version bump. That last mile is a
  human-checked discipline, stated in `AGENTS.md`.
