# Spec: PicoNode.Agent — Actor-Based Domain Refactoring

**Date:** 2026-07-10 | **Status:** implementing

## 1. Motivation

PicoNode.Agent.Core 当前有独立的 `AgentMailbox<T>` 和 `Agent` 领域对象，需要迁移到 `PicoNode.Actor` 基础架构上，消除重复的 Channel mailbox 实现，统一 Actor 生命周期管理。

## 2. Design Decisions

### 2.1 Agent 继承 EventSourcedActor

`Agent` 自身状态走 Event Sourcing，`Session` 作为附属数据自行管理：

```
Agent : EventSourcedActor
  ├─ ES 管理（RaiseEvent → Mutate → IEventStore）
  │   Status, Llms, CurrentLlm, Tools, ChildIds, ParentId
  │
  └─ 附属数据（Agent 自行管理）
      Session: append-only 对话记录 → JSONL 文件
```

**为什么 Session 不走 ES**：Session 是数据（输入/输出记录），不影响 Agent 行为。多一条消息、少一条消息，Agent 仍能处理命令。且 spec 已确定 JSONL 文件持久化。

### 2.2 双工厂注册（✅ 已实现）

```
Register<T>(createFactory, rebuildFactory?)
  ├─ CreateAsync → createFactory(ICommand) → 构造注入基础设施
  └─ GetAsync   → rebuildFactory()        → 构造注入基础设施
```

**为什么**：创建路径和重建路径都需要构造函数注入（ILLmClient、IToolRunner 等）。单工厂 + `new()` 约束无法满足——重建路径绕过工厂，基础设施缺失。双工厂收敛到同一构造函数，AOT 安全（编译器生成直接调用）。

### 2.3 基础设施通过构造函数注入

Agent 有三个构造路径，全部通过构造函数接收依赖：

| 构造 | 用途 | 签名 |
|------|------|------|
| 创建路径 | `CreateAsync` | `Agent(CreateAgent cmd, ILlmClient llm, IToolRunner runner)` |
| 创建路径（简化） | `CreateAsync`（无基础设施测试） | `Agent(CreateAgent cmd)` |
| 重建路径 | `GetAsync` | `Agent(ILlmClient llm, IToolRunner runner)` |

不再使用 `internal set` 属性或 `Initialize(ActorContext)`。编译期保证依赖完整。

## 3. Agent 设计

### 3.1 状态

| 字段 | ES? | 说明 |
|------|:---:|------|
| `Status` | ✅ | Pending → Running → Completed \| Failed |
| `Llms` | ✅ | 可用 LLM 列表 |
| `CurrentLlm` | ✅ | 当前 LLM 引用 |
| `Tools` | ✅ | 可用工具列表 |
| `ChildIds` | ✅ | 子 Agent ID 列表 |
| `ParentId` | ✅ | 父 Agent ID |
| `HomeDir` | ✅ | 数据目录 |
| `Packages` | ✅ | Skill 包声明 |
| `FailureReason` | ✅ | 失败原因 |
| `Session` | ❌ | 对话记录，JSONL 文件 |

### 3.2 命令 → 领域事件（✅ 原型已实现）

```csharp
// 命令
CreateAgent(Llms, Provider, Model, HomeDir, ParentId?, Packages?)  →  AgentCreated(...)
StartAgent                                                          →  AgentStarted
CompleteAgent                                                       →  AgentCompleted
FailAgent(reason)                                                   →  AgentFailed(reason)
SwitchLlmCmd(provider, model)                                       →  LlmSwitched(provider, model)
AddLlmCmd(llm)                                                      →  LlmAdded(llm)
RemoveLlmCmd(provider, model)                                       →  LlmRemoved(provider, model)
AddToolCmd(tool)                                                    →  ToolAdded(tool)
RemoveToolCmd(name)                                                 →  ToolRemoved(name)
SpawnChildCmd(llms, provider, model, tools)                         →  ChildSpawned(id)
RunTurn(message)                                                    →  ❌ 不产生事件
```

### 3.3 RunTurn — 嵌套循环（待实现）

```
OnMessageAsync:
  case RunTurn cmd:
    await RunTurnAsync(cmd.Message, ct);
    return default;

RunTurnAsync(message, ct):
  Session.Append(UserSaid(message))
  do {
    ctx = Session.BuildContext()
    response = await LlmClient.CompleteAsync(CurrentLlm, ctx, Tools, ct)
    Session.Append(AssistantReplied(response))
    for each tool_call:
      result = await ToolRunner.ExecuteAsync(tc, ct)
      Session.Append(ToolExecuted(tc, result))
  } while (hasTools && !ct.IsCancellationRequested)
```

**为什么 RunTurn 不 RaiseEvent**：对话记录是数据，不影响 Agent 行为。Session 自行管理。

**为什么 ct 传播**：StopAsync → _cts.Cancel → ct 传播到 LLM/工具调用 → RunTurn 在下一个 await 处中断。

### 3.4 OnMessageAsync 命令分发（✅ 原型已实现）

```csharp
protected override ValueTask<object?> OnMessageAsync(ICommand command)
{
    switch (command)
    {
        case CreateAgent c:   Validate(c); RaiseEvent(new AgentCreated(...));
        case StartAgent:      Guard(Status==Pending); RaiseEvent(new AgentStarted());
        case CompleteAgent:   Guard(Status==Running); RaiseEvent(new AgentCompleted());
        case FailAgent f:     Guard(Status==Running); RaiseEvent(new AgentFailed(f.Reason));
        case SwitchLlmCmd s:  Guard(Llm exists);      RaiseEvent(new LlmSwitched(...));
        case AddLlmCmd a:     Guard(ApiKey non-empty); RaiseEvent(new LlmAdded(...));
        case RemoveLlmCmd r:   Guard(not last/not current); RaiseEvent(new LlmRemoved(...));
        case AddToolCmd a:    Guard(name unique);      RaiseEvent(new ToolAdded(...));
        case RemoveToolCmd r: RaiseEvent(new ToolRemoved(...));
    }
}
```

**不变性校验在 RaiseEvent 之前**，通过 `DomainInvariantException` 拒绝无效命令。

### 3.5 Mutate — 状态恢复

```
Mutate(IDomainEvent):
  case AgentCreated:   Llms.Clear+AddRange, CurrentLlm=findMatch, HomeDir, ParentId, Packages
  case AgentStarted:   Status = Running
  case AgentCompleted: Status = Completed
  case AgentFailed:    Status = Failed; FailureReason = e.Reason
  case LlmSwitched:    CurrentLlm = Llms.Find(e.Provider, e.Model)
  case LlmAdded:       Llms.Add(e.Llm)
  case LlmRemoved:     Llms.RemoveAll(match)
  case ToolAdded:      Tools.Add(e.Tool)
  case ToolRemoved:    Tools.RemoveAll(match)
  case ChildSpawned:   ChildIds.Add(e.ChildId)
```

**Mutate 不重复验证**——事件来自已成功处理的命令，数据已被确认有效。

## 4. IActorSystem 接口（✅ 已实现）

```csharp
public interface IActorSystem
{
    void Register<T>(
        Func<ICommand, T> createFactory,
        Func<T>? rebuildFactory = null
    ) where T : IActor;

    ValueTask<T> CreateAsync<T>(ICommand command) where T : IActor;
    ValueTask<T?> GetAsync<T>(Guid id) where T : IActor;

    void Send(Guid id, ICommand command);
    ValueTask<TResult> AskAsync<TResult>(Guid id, ICommand command);
    ValueTask StopAsync(Guid id);
}
```

**为什么去掉 `new()` 约束**：`GetAsync` 不再用 `new T()` 重建，改为 `rebuildFactory()`。约束从编译时移到注册时。

## 5. 实现进度

### 已完成

| 组件 | 状态 |
|------|------|
| `IActorSystem` 双工厂签名 | ✅ |
| `ActorSystem.Register` + `GetAsync` 适配 | ✅ |
| Agent 领域事件（9 个 + 1 个创建事件） | ✅ 原型 |
| Agent 命令（10 个） | ✅ 原型 |
| `OnMessageAsync` 命令分发 + 不变性校验 | ✅ 原型 |
| `Mutate` 状态恢复 | ✅ 原型 |
| TDD: Create + Start 生命周期 | ✅ 1 test |

### 待实现

| 组件 | 状态 |
|------|------|
| RunTurn 嵌套循环 | ❌ |
| SpawnChild 通过 ActorSystem | ❌ |
| Session 集成 | ❌ |
| 全部不变性 TDD 测试 | ❌ 1/11 |
| 替换旧 `Agent.cs` | ❌ |
| 删除 `AgentMailbox.cs` | ❌ |
| 删除 `AgentFactory.cs` | ❌ |

### 使用示例

```csharp
// 注册
var llmClient = new LlmClientAdapter(innerLlm);
var toolRunner = new ToolRunner();
system.Register<Agent>(
    create: cmd => {
        var c = (CreateAgent)cmd;
        return new Agent(c, llmClient, toolRunner);
    },
    rebuild: () => new Agent(llmClient, toolRunner)
);

// 创建 + 启动
var agent = await system.CreateAsync<Agent>(
    new CreateAgent(llms, "deepseek", "deepseek-chat", "/tmp")
);
system.Send(agent.Id, new StartAgent());

// RunTurn
system.Send(agent.Id, new RunTurn("帮我查天气"));
var ctx = await system.AskAsync<List<Message>>(agent.Id, new GetContext());
```
