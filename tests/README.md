# tests

PicoNode test projects:

| Project | Description | Count |
|---|---|---|
| `PicoNode.Tests` | PicoNode.Abs + PicoNode unit tests and coding convention checks | 212 |
| `PicoNode.Http.Tests` | HTTP/1.1, HTTP/2, WebSocket protocol tests | 435 |
| `PicoNode.Web.Tests` | Web middleware tests (routing, CORS, compression, SSE, rate limiting) | 379 |
| `PicoJsonRpc.Tests` | JSON-RPC 2.0 envelopes, framing and lifecycle tests | 15 |
| `PicoWeb.Tests` | WebServer integration tests | 62 |
| `PicoWeb.DI.Tests` | DI container integration tests | 9 |
| `PicoWeb.Integration.Tests` | End-to-end HTTP integration tests | 51 |
| `PicoNode.Smoke` | Smoke tests (TCP echo, TLS, HTTP GET) | 33 |
| `PicoNode.PerfHarness` | Performance regression tests | 7 |
| `PicoNode.E2E` | End-to-end tests (SSE streaming) | Python |
| `PicoWeb.AotVerify` | AOT publish + run verifier (native binary, ephemeral port) | n/a (exe) |

Counts are the per-project `dotnet test` totals (1203 across the project table above).
`PicoNode.E2E` is a Python suite and `PicoWeb.AotVerify` is an executable check, so neither has a
test count. When you add tests, refresh the affected row.
