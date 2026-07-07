# PicoAgent.Web

> Web-based AI agent chat application — beautiful UI, multi-provider, SSE streaming, with a full REST API.

PicoAgent.Web provides a complete web frontend + REST API for the PicoAgent engine. It features three color themes, Markdown rendering with Mermaid diagram support, SSE streaming for real-time token display, and full session management — all served by a single AOT-compiled binary with zero external dependencies.

## Quick Start

### 1. Create settings

```powershell
mkdir ~/.pico-agent
cp samples/PicoAgent.Cli/settings.example.json ~/.pico-agent/settings.json
# Edit ~/.pico-agent/settings.json with your API keys
```

Or set environment variables:

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
$env:OPENAI_API_KEY    = "sk-..."
```

### 2. Run

```powershell
dotnet run --project samples/PicoAgent.Web/PicoAgent.Web.csproj
```

Open **http://localhost:9000** in your browser.

### 3. AOT publish

```powershell
dotnet publish samples/PicoAgent.Web/PicoAgent.Web.csproj -c Release -r win-x64
./samples/PicoAgent.Web/bin/Release/net10.0/win-x64/publish/PicoAgent.Web.exe
```

## Features

### Web UI

- **Three color themes** — Warm Charcoal (default), Deep Space Amber, and Ivory Paper. Switch via the palette buttons in the sidebar.
- **Markdown rendering** — Responses are rendered with [marked](https://marked.js.org/), including code blocks, tables, and inline formatting.
- **Mermaid diagrams** — Flowcharts, sequence diagrams, and more rendered directly in chat.
- **Session sidebar** — View and switch between conversation sessions.
- **Model status** — Current model/provider displayed in the sidebar.

### REST API

All endpoints are available for programmatic integration. The Web UI uses the same API internally.

#### Chat & Streaming

```
POST /api/session/{id}/message
```
Send a message to a session. Returns `text/event-stream` (SSE) with these event types:

| Event | Shape | Description |
|-------|-------|-------------|
| `delta` | `{"type":"delta","content":"..."}` | Text token |
| `thinking` | `{"type":"thinking","content":"..."}` | Thinking/CoT stream |
| `done` | `{"type":"done","stopReason":"..."}` | Completion + stop reason |
| `error` | `{"type":"error","message":"..."}` | Error message |

Example with curl:

```powershell
curl -N -X POST "http://localhost:9000/api/session/default/message" \
  -H "Content-Type: text/plain" \
  -d "Hello, what can you do?"
```

#### Model & Provider Control

```powershell
# List available models
curl http://localhost:9000/api/models

# Switch model
curl -X POST http://localhost:9000/api/model/switch \
  -H "Content-Type: application/json" \
  -d '{"modelId":"claude-sonnet-4-20250514"}'

# Switch provider
curl -X POST http://localhost:9000/api/provider/switch \
  -H "Content-Type: application/json" \
  -d '{"provider":"openai"}'

# Toggle thinking
curl -X POST http://localhost:9000/api/thinking \
  -H "Content-Type: application/json" \
  -d '{"enabled":true,"level":"high"}'
```

#### Session Management

```powershell
# List sessions
curl http://localhost:9000/api/sessions

# Create session
curl -X POST http://localhost:9000/api/session/create/my-chat

# Get messages
curl http://localhost:9000/api/session/my-chat/messages

# Save session
curl -X POST http://localhost:9000/api/session/my-chat/save

# Delete session
curl -X POST http://localhost:9000/api/session/delete/my-chat
```

#### System

```powershell
# Health check
curl http://localhost:9000/api/health
# → {"status":"ok","model":"claude-sonnet-4-20250514","provider":"anthropic"}

# Reload capabilities
curl -X POST http://localhost:9000/api/reload
```

### SSE Streaming Architecture

PicoAgent.Web uses [Server-Sent Events](https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events) for real-time token streaming:

1. Browser sends `POST /api/session/{id}/message` with the user text
2. Server responds with `Content-Type: text/event-stream`
3. Token deltas stream as `data: {"type":"delta","content":"Hello"}` lines
4. Completion signaled with `data: {"type":"done","stopReason":"end_turn"}`
5. The frontend JavaScript client parses the SSE stream and renders incrementally

### Static File Serving

The `wwwroot/` directory is served as static files, providing the single-page app with no framework dependencies beyond CDN-hosted marked.js and mermaid.js.

| File | Purpose |
|------|---------|
| `index.html` | SPA shell with sidebar + chat area |
| `style.css` | Three-theme CSS with CSS custom properties |
| `app.js` | SSE client, Markdown/Mermaid rendering, theme switching |

## Architecture

```
Browser (localhost:9000)
    │
    ├─ Static files: wwwroot/index.html, style.css, app.js
    │
    └─ API calls → WebApp (PicoNode.Web)
                    │
                    ├─ GET  /api/health
                    ├─ GET  /api/models
                    ├─ POST /api/model/switch
                    ├─ POST /api/provider/switch
                    ├─ POST /api/thinking
                    ├─ GET  /api/sessions
                    ├─ POST /api/session/{id}/create
                    ├─ POST /api/session/{id}/delete
                    ├─ POST /api/session/{id}/save
                    ├─ GET  /api/session/{id}/messages
                    ├─ POST /api/session/{id}/message  ← SSE stream
                    └─ POST /api/reload
                           │
                           ▼
                    AgentHttpClient ──HTTP──► Agent (localhost:19998)
                                               │
                                               ├─ AgentHost → AgentLoop
                                               ├─ ProviderRouter → LLM providers
                                               └─ Session storage (~/.pico-agent)
```

The Web frontend launches an agent backend on port 19998 and communicates with it via `AgentHttpClient`. The WebApp at :9000 acts as a proxy, adding the UI and a REST API layer.

## Configuration

Same `settings.json` format as PicoAgent.Cli. See `samples/PicoAgent.Cli/settings.example.json`.

| Setting | Description |
|---------|-------------|
| `providers` | Provider name → {apiKey, baseUrl, apiFormat, thinking, models} |
| `model` | Model ID (`null` = auto-discover from default provider) |
| `thinkingEnabled` | Enable extended thinking (default: `true`) |
| `thinkingLevel` | `minimal` / `low` / `medium` / `high` / `xhigh` |
| `maxTokens` | Max output tokens (default: `4096`) |

## Dependencies

- [PicoAgent](https://github.com/PicoHex/PicoNode) — Agent hosting with SSE endpoints
- [PicoNode.Web](https://github.com/PicoHex/PicoNode) — HTTP middleware, routing, static files
- [PicoWeb](https://github.com/PicoHex/PicoNode) — Web server + hosting
- [PicoCfg](https://github.com/PicoHex/PicoCfg) — Source-generated config binding
- [PicoLog](https://github.com/PicoHex/PicoLog) — Structured logging
- [PicoDI](https://github.com/PicoHex/PicoDI) — AOT-first DI container
- [PicoJetson](https://github.com/PicoHex/PicoSerDe) — AOT-safe JSON serialization
