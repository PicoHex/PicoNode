# Spec: PicoNode.Agent — Actor-Based Domain Refactoring

**Date:** 2026-07-10 | **Status:** design

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

### 2.2 双工厂注册

```
Register<T>(createFactory, rebuildFactory?)
  ├─ CreateAsync → createFactory(ICommand) → 构造注入基础设施
  └─ GetAsync   → rebuildFactory()        → 构造注入基础设施
```

**为什么**：创建路径和重建路径都需要构造函数注入（ILLmClient、IToolRunner 等）。单工厂 + `new()` 约束无法满足——重建路径绕过工厂，基础设施缺失。双工厂收敛到同一构造函数，AOT 安全（编译器生成直接调用）。

### 2.3 基础设施通过构造函数注入

```
Agent(CreateAgent cmd, ILlmClient llmClient, IToolRunner toolRunner)
```

不再使用 `internal set` 属性或 `Initialize(ActorContext)`。Agent 的依赖从构造函数获得，编译期保证完整。

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

### 3.2 命令 → 领域事件

| 命令 | 领域事件 | 说明 |
|------|---------|------|
| `Start` | `AgentStarted` | Pending → Running |
| `Complete` | `AgentCompleted` | Running → Completed |
| `Fail(reason)` | `AgentFailed(reason)` | Running → Failed |
| `SwitchLlm(p, m)` | `LlmSwitched(p, m)` | 切换 CurrentLlm |
| `AddLlm(llm)` | `LlmAdded(llm)` | 新增 LLM |
| `RemoveLlm(p, m)` | `LlmRemoved(p, m)` | 移除 LLM |
| `AddTool(tool)` | `ToolAdded(tool)` | 新增工具 |
| `RemoveTool(name)` | `ToolRemoved(name)` | 移除工具 |
| `SpawnChild(...)` | `ChildSpawned(id)` | 添加子 Agent |
| `RunTurn(message)` | ❌ | 对话记录在 Session，不影响 Agent 行为 |

### 3.3 RunTurn — 嵌套循环

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

**为什么 RunTurn 不 RaiseEvent**：对话记录是数据，不影响 Agent 行为。Session 自行管理 Append + JSONL 持久化。

**为什么 ct 传播**：StopAsync → _cts.Cancel → ct 传播到 LLM/工具调用 → RunTurn 在下一个 await 处中断。不需要系统消息优先机制。

### 3.4 Mutate — 状态恢复

```
Mutate(IDomainEvent):
  case AgentStarted:       Status = Running
  case AgentCompleted:     Status = Completed
  case AgentFailed(e):     Status = Failed; FailureReason = e.Reason
  case LlmSwitched(e):     CurrentLlm = Llms.Find(e.Provider, e.Model)
  case LlmAdded(e):        Llms.Add(e.Llm)
  case LlmRemoved(e):      Llms.Remove(e.Provider, e.Model)
  case ToolAdded(e):       Tools.Add(e.Tool)
  case ToolRemoved(e):     Tools.Remove(e.Name)
  case ChildSpawned(e):    ChildIds.Add(e.Id)
```

### 3.5 不变性校验

不变性校验在 `OnMessageAsync` 中（命令验证）和 `Mutate` 中（事件恢复）都需要。通过提取私有 validate 方法复用：

```
SwitchLlm(cmd):
  // 验证
  var match = Llms.Find(cmd.Provider, cmd.Model);
  if (match == null) throw DomainInvariantException;

  // 事件
  RaiseEvent(new LlmSwitched(cmd.Provider, cmd.Model));
```

`Mutate` 不重复验证——事件来自已成功处理的命令，不需要二次校验。

## 4. IActorSystem 接口变更

```csharp
public interface IActorSystem
{
    // 新增可选 rebuildFactory 参数
    void Register<T>(
        Func<ICommand, T> createFactory,
        Func<T>? rebuildFactory = null
    ) where T : IActor;

    ValueTask<T> CreateAsync<T>(ICommand command) where T : IActor;

    // GetAsync 不再需要 new() 约束
    ValueTask<T?> GetAsync<T>(Guid id) where T : IActor;

    void Send(Guid id, ICommand command);
    ValueTask<TResult> AskAsync<TResult>(Guid id, ICommand command);
    ValueTask StopAsync(Guid id);
}
```

**为什么去掉 `new()` 约束**：`GetAsync` 不再用 `new T()` 重建，改为 `rebuildFactory()`。约束从编译时（`new()`）移到注册时（工厂闭包）。

## 5. AgentFactory 适配

```csharp
// 旧：手工创建，独立的 AgentMailbox
var agent = AgentFactory.Build(config, homeDir);

// 新：通过 ActorSystem 创建
var system = new ActorSystem(new InMemoryEventStore());

// 注册 + 封闭基础设施
system.Register<Agent>(
    create: cmd => {
        var c = (CreateAgent)cmd;
        return new Agent(c, llmClient, toolRunner);
    },
    rebuild: () => new Agent(llmClient, toolRunner)
);

var createCmd = new CreateAgent(config, homeDir);
var agent = await system.CreateAsync<Agent>(createCmd);
agent.Start();
```

## 6. 删除清单

| 文件 | 原因 |
|------|------|
| `AgentMailbox.cs` | PicoNode.Actor 的 `Actor.Post()` + Channel 替代 |
| `AgentFactory.cs` | `Register<T>` + `CreateAsync` 替代 |
| 部分 `Agent.cs` 方法 | 迁移为 `OnMessageAsync` 命令分发 + `Mutate` |

## 7. Self-Review

- [x] Agent 继承 EventSourcedActor
- [x] 双工厂注册：创建/重建均构造注入
- [x] RunTurn 不产生领域事件，Session 自行管理
- [x] ct 传播到内层循环，Stop 可中断 RunTurn
- [x] 不变性在命令处理时验证，Mutate 不重复
- [x] AOT 安全：无反射、无 `new()` 约束硬编码
- [x] 删除 AgentMailbox + AgentFactory
