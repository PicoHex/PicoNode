# Spec: PicoNode.Actor — In-Memory Actor Framework

**Date:** 2026-07-08 | **Updated:** 2026-07-10
**Status:** production-ready (v1)

## 1. Design Philosophy

| 原则 | 说明 |
|------|------|
| **Actor 持有自己的 Mailbox** | `Channel<Envelope>`，构造时启动消费循环（被闸门阻塞直到就绪） |
| **send/ask 消息模型** | `IActorSystem.Send(id, cmd)` / `AskAsync<T>(id, cmd)` |
| **纯领域对象，无 DI 依赖** | Actor 只接收 `ICommand` 产生 `IDomainEvent`，构造只需 `new T()` |
| **Persist-then-Mutate** | 持久化成功后才会变更内存状态——参照 Akka Persistence |
| **AOT First** | 零反射、零 dynamic |
| **极简 Abs** | 无 SG 代理、无 Orleans 风格接口，IEventStore 也在 Abs 中定义 |

### 1.1 为什么 IEventStore 在 Abs 层

`EventSourcedActor.ProcessAsync` 需要调用 `IEventStore.AppendAsync`——持久化是 Actor 生命周期的内在组成部分。把 IEventStore 定义在 Abs 等同于 `System.Threading.Channels` 在 Abs 中——契约归 Abs，实现归运行时。

### 1.2 为什么 Actor 不依赖 DI

Actor 是纯领域对象。业务标识（orderId 等）是命令的属性，不属于框架。无 DI 意味着两条路径（工厂创建 vs 事件重建）收敛到同一组构造函数。

---

## 2. Target Structure

```
src/Actor/
├── PicoNode.Actor.Abs/            # 抽象层（netstandard2.0）
│   ├── ICommand.cs / IDomainEvent.cs / IEventStore.cs
│   ├── ConcurrencyException.cs / Envelope.cs
│   ├── IActor.cs / IEventSourcedActor.cs / IActorSystem.cs
│   └── Actor.cs / EventSourcedActor.cs
│
└── PicoNode.Actor/                # 运行时（net10.0, AOT-compatible）
    ├── ActorSystem.cs / InMemoryEventStore.cs
    ├── ActorConfig.cs / ActorSystemOptions.cs
    └── PicoActorDiExtensions.cs

tests/
├── PicoNode.Actor.Abs.Tests/      # 3 tests
└── PicoNode.Actor.Tests/          # 18 tests
```

### 为什么 netstandard2.0（Abs）

最大化包兼容性。Abs 层仅依赖 `System.Threading.Channels` 和 `Microsoft.Bcl.AsyncInterfaces`（`IAsyncDisposable` polyfill）。

---

## 3. Abs Layer — Interfaces & Base Classes

### 3.1 Type Catalog

| Type | Role |
|------|------|
| `ICommand` | 命令标记 |
| `IDomainEvent` | 领域事件标记 |
| `IEventStore` | 事件流持久化契约（AppendAsync + LoadAsync） |
| `ConcurrencyException` | 乐观并发冲突异常 |
| `IActor` | Actor 标识（Id） |
| `IEventSourcedActor` | ES 合约（Version + Commit + Replay） |
| `IActorSystem` | 运行时入口（Register/Create/Get/Send/Ask/Stop） |
| `Actor` | 操作型基类（双构造 + 闸门 + Channel 消费循环） |
| `EventSourcedActor` | ES 基类（RaiseEvent + Persist-then-Mutate + FlushEventsAsync） |
| `Envelope` | 命令信封（Command + 可选 TCS） |

### 3.2 Actor 基类核心设计

```
消费循环生命周期:
  new Actor() → Channel + RunAsync 启动（闸门阻塞）
     ↓
  SignalReady() → 闸门释放
     ↓
  OnReadyAsync() → EventSourcedActor flush 构造事件
     │  ├─ 成功 → _initCompleted.TrySetResult(true)
     │  └─ 失败 → _initCompleted.TrySetException(ex) + 循环终止
     ↓
  foreach envelope → ProcessAsync(envelope)
     ↓
  StopAsync() → _ready.TrySetResult + CTS.Cancel + await _loopTask
```

**双构造函数**：
- `Actor(ICommand)` — 创建路径，构造时同步处理创建命令（`OnMessageAsync`）
- `Actor()` — 重建路径，空壳，状态由 `ReplayEvents` 恢复

**为什么 StopAsync 需要 `_ready.TrySetResult`**：被丢弃的 Actor（重复 GetAsync 重建、构造失败）从未被 SignalReady。StopAsync 必须释放闸门，否则 `_loopTask` 永远等 `_ready.Task` 导致死锁。

**为什么 `Actor(ICommand)` 有 try-catch 清理**：构造函数内 `OnMessageAsync` 抛异常时，Channel + CTS 已创建但 `_loopTask` 在等闸门。catch 中释放闸门 + Cancel + Complete 防止资源泄漏。

### 3.3 InitCompletedTask — 初始化完成回传

```
Actor._initCompleted (TaskCompletionSource<bool>):
  - OnReadyAsync 成功 → TrySetResult(true)
  - OnReadyAsync 失败 → TrySetException(ex)
  - 由 ActorSystem.CreateAsync 通过 InitCompletedTask 属性 consume
```

**为什么需要**：构造命令是同步的（C# 构造函数不能 await），但持久化是异步 I/O。`OnReadyAsync`（构造事件 flush）发生在 `SignalReady` 之后、首条消息之前。`CreateAsync` 需要等它完成才能安全返回——否则调用方拿到的是"持久化未完成的半初始化 Actor"。

**为什么只影响 CreateAsync**：`GetAsync`（重建路径）调用 `ReplayEvents`（同步 Mutate），`OnReadyAsync` 中 `_events` 为空，瞬间完成，不受影响。

### 3.4 EventSourcedActor — Persist-then-Mutate

```
ProcessAsync(envelope):
  1. OnMessageAsync(cmd)
     └─ RaiseEvent(e) → Add + Version++（状态不变）
  2. FlushEventsAsync:
     ├─ SaveAsync(events)    ← 持久化
     ├─ foreach Mutate(e)   ← 成功后变更状态
     └─ ClearEvents()
  3. Tcs.TrySetResult()      ← 成功后才回复
```

**为什么 EventStore 可为 null**：纯内存模式下跳过持久化，直接 Mutate + Commit。

---

## 4. Runtime Layer

### 4.1 ActorSystem

```
ActorSystem(IEventStore, ILogger?) : IActorSystem
  ┌─ _factories:   ConcurrentDictionary<Type, Func<ICommand, object>>
  ├─ _registry:    ConcurrentDictionary<Guid, ActorBase>
  ├─ _eventStore:  IEventStore
  └─ _logger:      ILogger?（PicoLog 集成）
```

**CreateAsync 流程（完整）**：
```
1. factory(cmd) → new Actor(cmd)
     └─ base(cmd): 构造时同步 OnMessageAsync → RaiseEvent
2. Id = CreateVersion7()
3. EventStore = _store（仅 ES）
4. _registry[id] = actor
5. SignalReady() → OnReadyAsync → FlushEventsAsync
6. await InitCompletedTask
     ├─ 成功 → logger.Info + return actor
     └─ 失败 → TryRemove registry + logger.Error + 异常传播
```

**GetAsync 流程**：
```
查 registry → 命中返回
  → 非 ES 类型 → return null
  → LoadAsync(id) → 空 → return null
  → new T() + Id + EventStore + ReplayEvents
  → TryAdd（失败则 StopAsync 丢弃 + 返回已有）→ SignalReady → return
```

### 4.2 InMemoryEventStore

```
ConcurrentDictionary<actorId, List<IDomainEvent>>
无锁设计：
  - AppendAsync: 同一 Actor 消费循环串行写 → 无并发
  - LoadAsync:   仅在被重建 Actor 不在内存时调用 → 无并发写
```

### 4.3 PicoDI 集成

```csharp
// 1. 零配置
container.AddPicoActor();

// 2. 自定义事件存储
container.AddPicoActor(new MyEventStore());

// 3. 从配置（可配合 PicoCfg.CfgBind）
var config = CfgBind.Bind<ActorConfig>(cfg, "Actor");
container.AddPicoActor(config);

// 4. 自动日志（如 PicoLog 已在 DI 中注册）
//    无需额外配置——AddPicoActor 自动 resolve ILoggerFactory
```

**ActorConfig**：
```csharp
public sealed class ActorConfig
{
    public EventStoreConfig? EventStore { get; set; }
}

public sealed class EventStoreConfig
{
    public string Type { get; set; } = "InMemory";   // "InMemory" only (v1)
    public string? ConnectionString { get; set; }     // unused in v1
}
```

### 4.4 并发模型

| 操作 | 并发来源 | 保障 |
|------|---------|------|
| CreateAsync | 多线程调用 | UUID v7 唯一 + InitCompletedTask 等待 |
| GetAsync | 多线程同一 ID | TryAdd 防止重复 |
| Send/Ask | 多线程 + 目标可能被 Stop | TryGetValue 原子查找 |
| StopAsync | 多线程 + 幂等 | TryRemove + Interlocked |

---

## 5. Actor 生命周期协议（完整）

### 5.1 CreateAsync

```
1. factory(cmd) → new Counter(CreateCounter(0))
2. Id = CreateVersion7()
3. EventStore = _store
4. _registry[id] = actor
5. SignalReady → OnReadyAsync → FlushEventsAsync → 持久化 + Mutate
6. await InitCompletedTask → 成功返回 / 失败注册表移除 + 异常
```

### 5.2 GetAsync（重建 — 不受 InitCompletedTask 影响）

```
1. _registry 命中 → return（已就绪）
2. 非 ES → return null
3. LoadAsync(id) → events
4. new T() + ReplayEvents（同步，无 I/O）
5. TryAdd → SignalReady → return
```

### 5.3 消息处理

```
Send/Ask → actor.Post(envelope)
  → ProcessAsync: OnMessageAsync → FlushEventsAsync → TCS
```

### 5.4 Stop

```
TryRemove → StopAsync:
  _ready.TrySetResult + CTS.Cancel + Writer.Complete + await _loopTask
```

---

## 6. 开发者视角

### 6.1 三步分离（完整示例）

```csharp
public sealed class Counter : EventSourcedActor
{
    private int _value;
    public Counter(CreateCounter cmd) : base(cmd) { }   // 创建构造
    public Counter() { }                                  // 重建构造（new() 约束）

    // Step 1: 构建事件
    protected override ValueTask<object?> OnMessageAsync(ICommand command) => command switch
    {
        CreateCounter c => { RaiseEvent(new CounterCreated(c.InitialValue)); return default; },
        Increment i     => { RaiseEvent(new CounterIncremented(i.Delta));       return default; },
        GetValue        => new ValueTask<object?>(_value),
        _               => default,
    };

    // Step 2: 框架自动持久化（ProcessAsync → FlushEventsAsync）— 透明

    // Step 3: 变更状态（纯函数，持久化成功后调用）
    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case CounterCreated e:     _value = e.InitialValue; break;
            case CounterIncremented e: _value += e.Delta;      break;
        }
    }
}
```

### 6.2 OnMessageAsync 内级联事件的规则

事件间的依赖存在于事件携带的数据中，不存在于聚合状态中。用局部变量追踪中间结果：

```csharp
case AddOrderDetail cmd:
    var newTotal = TotalPrice + cmd.UnitPrice * cmd.Quantity;  // 局部变量
    RaiseEvent(new OrderDetailAdded(..., newTotal));
    if (newTotal > 1000)                                        // 用局部变量
        RaiseEvent(new HighValueOrderDetected(Id, newTotal));
```

---

## 7. 测试矩阵（21 passing）

```
Abs 层 (3):
  ActorExceptionPropagation — OnMessageAsync抛异常→TCS设为faulted
  ActorLifecycle — IAsyncDisposable / StopAsync 幂等

ActorSystem (18):
  获取 (2)    非 ES 返回 null / ES 从事件重建
  创建 (2)    工厂异常传播 / 异常后系统仍可用
  并发 (1)    GetAsync 同一 ID 返回同一实例
  配置 (4)    InMemory/null/不支持/无配置 → 默认 InMemory
  API (7)     Id 非空 / Send/Ask 不存在 Actor / 连续消息有序 / Stop 幂等
  初始化 (2)  持久化失败传播 / 失败后新创建成功
```

---

## 8. Key Design Decisions

| 决策 | 为什么 |
|------|--------|
| Channel 做 Mailbox | 空队列时挂起 Task 不占线程，Write 唤醒 Read |
| `new()` 约束替代反射 | AOT 安全，编译器生成直接调用 |
| 工厂不暴露 Guid | UUID v7 是框架内部的技术标识 |
| IEventStore 在 Abs | 持久化是 EventSourcedActor.ProcessAsync 的内在步骤 |
| RaiseEvent 不改状态 | 持久化成功后才 Mutate，失败时状态干净 |
| Version 自管理 | 版本号是"已形成事件"的序号，归 Actor 所有 |
| SignalReady 闸门 | ENode 式"重建完毕前不投递" |
| OnReadyAsync 钩子 | 构造事件在首条消息前 flush |
| InitCompletedTask | 让 CreateAsync 能等待异步持久化完成 |
| CreateAsync await 初始化 | 持久化失败 → 注册表移除 + 异常传播，无僵尸 Actor |
| TryAdd in GetAsync | 防止并发重建时的 Actor 泄漏 |
| StopAsync 释放闸门 | 防止未 SignalReady 的 Actor 死锁 |
| Actor(ICommand) try-catch | 构造失败时释放资源，防止泄漏 |
| EventStore 可为 null | 支持纯内存模式（测试/开发） |
| ActorConfig + DI 扩展 | 声明式配置 + 零样板代码 |

---

## 9. Non-Scope (v2)

- 监督策略
- 快照 / Snapshot
- 分布式 / Virtual Actor
- SG 代理 / Orleans 风格接口
- `AskAsync` 超时重载
- Actor 枚举（调试/运维面板）

---

## 10. Self-Review

- [x] Abs: 6 接口 + 1 异常 + 2 基类 + 1 信封
- [x] Runtime: ActorSystem + InMemoryEventStore + ActorConfig + DI 扩展
- [x] Tests: 21 passing
- [x] Persist-then-Mutate 完整性
- [x] 并发安全（TryAdd / TryRemove / TryGetValue）
- [x] 资源泄漏防护（构造清理 + StopAsync 闸门 + InitCompletedTask）
- [x] 初始化失败处理（CreateAsync await 初始化 → 注册表移除 + 异常传播）
- [x] PicoDI 集成（4 种注册方式 + 自动日志）
- [x] PicoLog 集成（ActorSystem 生命周期日志）
- [x] ActorConfig 配置支持（PicoCfg 可绑定）
- [x] AOT 安全：零反射、零 dynamic
- [x] netstandard2.0（Abs）+ net10.0（Runtime）
