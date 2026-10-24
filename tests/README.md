# tests

PicoNode test projects:

| Project | Description | Count |
|---|---|---|
| `PicoNode.Tests` | PicoNode.Abs + PicoNode unit tests and coding convention checks | 266 |
| `PicoNode.Http.Tests` | HTTP/1.1, HTTP/2, WebSocket protocol tests | 479 |
| `PicoNode.Web.Tests` | Web middleware tests (routing, CORS, compression, SSE, rate limiting) | 470 |
| `PicoJsonRpc.Tests` | JSON-RPC 2.0 envelopes, framing, routing and lifecycle tests | 73 |
| `PicoWeb.Tests` | WebServer integration tests | 79 |
| `PicoWeb.DI.Tests` | DI container integration tests | 16 |
| `PicoWeb.Integration.Tests` | End-to-end HTTP integration tests | 55 |
| `PicoNode.Smoke` | Smoke tests (TCP echo, TLS, HTTP GET) | 33 |
| `PicoNode.PerfHarness` | Performance regression tests | 9 |
| `PicoNode.Docs.Tests` | README sample compilation, code-block coverage and doc-number guards | 15 |
| `PicoNode.E2E` | End-to-end tests (SSE streaming) | Python |
| `PicoWeb.AotVerify` | AOT publish + run verifier (native binary, ephemeral port) | n/a (exe) |

Counts are `[Test]` methods in each project's own sources, re-derived on every run by the docs guard
(1,495 across the project table above). A `[Arguments]`-driven method runs once per argument set, so
a `dotnet test` total can be higher than its row. `PicoNode.E2E` is a Python suite and
`PicoWeb.AotVerify` is an executable check, so neither has a test count.
When you add tests, refresh the affected row — the guard stays red until you do.
