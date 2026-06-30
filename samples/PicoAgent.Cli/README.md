# PicoAgent.Cli

> Terminal-based AI agent multi-provider chat client — powered by PicoNode + PicoAgent.

PicoAgent.Cli is a command-line chat client that connects to multiple LLM providers (Anthropic, OpenAI, DeepSeek, Kimi, GLM, Groq) with streaming SSE output, model switching, thinking mode control, and automatic skill/knowledge loading from your local `~/.pico-agent` directory.

## Quick Start

### 1. Create a settings file

Copy the example config and edit it with your API keys:

```powershell
mkdir ~/.pico-agent
cp samples/PicoAgent.Cli/settings.example.json ~/.pico-agent/settings.json
```

Edit `~/.pico-agent/settings.json` — replace `$ANTHROPIC_API_KEY`, `$OPENAI_API_KEY`, etc. with your actual keys, or set environment variables:

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
$env:OPENAI_API_KEY    = "sk-..."
```

### 2. Run

```powershell
dotnet run --project samples/PicoAgent.Cli/PicoAgent.Cli.csproj
```

### 3. AOT publish (standalone binary)

```powershell
dotnet publish samples/PicoAgent.Cli/PicoAgent.Cli.csproj -c Release -r win-x64
./samples/PicoAgent.Cli/bin/Release/net10.0/win-x64/publish/PicoAgent.Cli.exe
```

## Commands

Once running, type your message and press Enter. The response streams token-by-token via SSE.

| Command | Description |
|---------|-------------|
| `/help` | Show all available commands |
| `/model <id>` | Switch to a specific model (e.g. `/model claude-sonnet-4-20250514`) |
| `/provider <name>` | Switch provider (e.g. `/provider openai`) |
| `/thinking <level>` | Set thinking level: `minimal`, `low`, `medium`, `high`, `xhigh` |
| `/list-models` | List available models from the current provider |
| `/save` | Save the current session to disk |
| `/exit` | Exit and auto-save session |

## Features

### Multi-Provider Support

Configure multiple providers in `settings.json`. The client auto-discovers available models and routes requests based on the selected provider.

```json
{
  "providers": {
    "anthropic": {
      "apiKey": "$ANTHROPIC_API_KEY",
      "baseUrl": "https://api.anthropic.com",
      "apiFormat": "anthropic",
      "thinking": { "minimal": "2000", "low": "8000", "medium": "16000", "high": "32000", "xhigh": "64000" }
    },
    "openai": {
      "apiKey": "$OPENAI_API_KEY",
      "baseUrl": "https://api.openai.com/v1",
      "apiFormat": "openai"
    }
  },
  "model": null,
  "maxTokens": 4096,
  "thinkingEnabled": true,
  "thinkingLevel": "medium"
}
```

Supported API formats:
- **anthropic** — Anthropic Messages API (native Claude thinking budget control)
- **openai** — OpenAI-compatible Chat Completions API (OpenAI, DeepSeek, Kimi, GLM, Groq, Ollama, etc.)

### Skill & Knowledge Auto-Loading

Place `SKILL.md` files inside `~/.pico-agent/knowledge/` — they are automatically discovered and loaded at startup via YAML frontmatter parsing:

```markdown
---
name: my-skill
description: A custom skill for doing X
---

# My Skill

Skill content here...
```

The scanner validates skill names (`^[a-z0-9]+(-[a-z0-9]+)*$`, max 64 chars) and descriptions (max 1024 chars).

### Session Persistence

Sessions are saved as JSONL files in `~/.pico-agent/sessions/`. Use `/save` to persist the current conversation, or `/exit` for auto-save. Sessions survive restarts if a `default.jsonl` exists.

### AOT-Native Binary

The project targets `net10.0` with `<PublishAot>true</PublishAot>` and `<TrimMode>full</TrimMode>`. Published binaries are self-contained and require no .NET runtime installation.

### Serve Mode

Run the agent as a standalone HTTP server for programmatic access:

```powershell
dotnet run --project samples/PicoAgent.Cli -- serve 8080
```

This exposes the full PicoAgent REST API (SSE streaming, model/provider switching, session management) on `http://localhost:8080`.

## Configuration Reference

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `providers` | object | *(required)* | Provider name → config (apiKey, baseUrl, apiFormat, thinking, models) |
| `model` | string? | auto-discover | Model ID to use; `null` = auto-discover first available |
| `thinkingEnabled` | bool | `true` | Whether extended thinking is enabled |
| `thinkingLevel` | string | `"medium"` | `minimal`, `low`, `medium`, `high`, `xhigh` |
| `maxTokens` | int? | `4096` | Maximum output tokens |

Environment variables with `PICO_` prefix override settings (e.g. `PICO_providers__anthropic__apiKey`).

## Dependencies

- [PicoAgent](https://github.com/PicoHex/PicoNode) — Agent hosting with built-in SSE endpoints
- [PicoNode.Web](https://github.com/PicoHex/PicoNode) — HTTP web framework
- [PicoCfg](https://github.com/PicoHex/PicoCfg) — Source-generated config binding
- [PicoLog](https://github.com/PicoHex/PicoLog) — Structured logging
- [PicoDI](https://github.com/PicoHex/PicoDI) — AOT-first DI container
- [PicoJetson](https://github.com/PicoHex/PicoSerDe) — AOT-safe JSON serialization
