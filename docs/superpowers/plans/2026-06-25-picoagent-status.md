# PicoAgent 现状与待办

## 当前状态

### 已完成

- **CLI chat 模式** — stdin/stdout 流式 text_delta，可用 ✅
- **多 Provider 支持** — Anthropic + OpenAI 兼容（DeepSeek/Kimi/GLM/Groq），从 API 发现模型 ✅
- **配置** — `~/.pico-agent/settings.json`，`$XXX` 从环境变量展开 ✅
- **无配置文件时** — 创建模板 + 提示 ✅
- **ConfigLoader** — 改用 `System.Text.Json` 解析配置 ✅
- **PicoJetson 2026.2.7** — CS8785 hintName 冲突已修复，PicoNode.Agent SG 正常工作 ✅
- **CSharpier + usings 清理** — 907→908 测试，全 PASS ✅

### 未完成

#### SSE 流式（serve 模式）

当前 `POST /session/{id}/message` 收集完所有事件后一次性返回。目标是改为 Pipe 流式，逐条推送 text_delta。

**问题历史：**
1. Pipe + 后台任务 → `BufferStreamResponseAsync` 在 HTTP/1.0 下死锁（读取 pipe 前先缓冲整个 stream）
2. HTTP/1.1 下 `SendStreamingResponseAsync` 正确流式，但 pipe 数据未送达 curl（疑似 writer 未 complete 或 ct 被取消）
3. 最终改用 MemoryStream 收集后一次性返回——功能正常，非流式

**待修复：** HTTP/1.1 下 Pipe 方式未调通。需要排查 `SendStreamingResponseAsync` + Pipe 的写入时机。

#### serve 模式其他端点

- `GET /session/{id}/events` — 桩，返回 `{"type":"ready"}`
- `POST /reload` — 基本可用

#### 其他

- `/help` 帮助信息完善
- `/thinking` 命令（当前 chat 模式已接受但未传给 LLM）
- 会话管理（JSONL 持久化 + 恢复）
- 包管理 CLI（install/remove/list/update）
- 内置工具（bash/read/write）

## 优先顺序

| 优先级 | 项目 | 说明 |
|:---:|------|------|
| **P0** | SSE 流式修复 | serve 模式实用性——Pipe + SendStreamingResponseAsync |
| **P1** | /thinking 命令 | 切换思考深度 |
| **P1** | 会话管理 | 命令行恢复上次 session |
| **P2** | 包管理 CLI | install/remove/list/update |
| **P2** | 内置工具 | bash/read/write |
