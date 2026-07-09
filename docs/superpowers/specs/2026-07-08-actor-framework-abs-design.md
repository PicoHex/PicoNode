# Spec: PicoNode.Actor — In-Memory Actor Framework

**Date:** 2026-07-08 | **Updated:** 2026-07-09
**Status:** implemented (Abs + Runtime + Tests)

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
├── PicoNode.Actor.Abs/           # 抽象层（netstandard2.0）
└── PicoNode.Actor/               # 运行时（net10.0, AOT-compatible）

tests/
├── PicoNode.Actor.Abs.Tests/     # 3 tests
└── PicoNode.Actor.Tests/         # 5 tests
```

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
Actor 构造函数:
  Actor(ICommand) → 创建路径：同步处理创建命令，原子初始化
  Actor()        → 重建路径：空壳，状态由 ReplayEvents 恢复

消费循环生命周期:
  new Actor() → Channel + RunAsync 启动（闸门阻塞）
     ↓
  SignalReady() → 闸门释放
     ↓
  OnReadyAsync() → EventSourcedActor 在此 flush 构造事件
     ↓
  foreach envelope → ProcessAsync(envelope)
     ↓
  StopAsync() → _ready.TrySetResult + CTS.Cancel + await _loopTask
```

**为什么 StopAsync 需要 _ready.TrySetResult**：被丢弃的 Actor（重复 GetAsync 重建、工厂构造失败）从未被 SignalReady。StopAsync 必须释放闸门，否则 `_loopTask` 永远等 `_ready.Task`，导致死锁。这是 TDD 中发现的关键修复。

**为什么 Actor(ICommand) 有 try-catch 清理**：构造函数内 factory → OnMessageAsync 抛异常时，Channel 和 CTS 已创建但 _loopTask 在等闸门。catch 中释放闸门 + Cancel + Complete 防止资源泄漏。

### 3.3 EventSourcedActor — Persist-then-Mutate

```
ProcessAsync(envelope):
  1. OnMessageAsync(cmd)
     └─ RaiseEvent(e) → Add + Version++（状态不变）
  2. FlushEventsAsync:
     ├─ SaveAsync(events)    ← 持久化
     ├─ foreach Mutate(e)   ← 成功后变更状态
     └─ ClearEvents()
  3. Tcs.TrySetResult()      ← 成功后才回复

持久化失败 → 异常 → TCS 异常 → 状态未被 Mutate → 下条消息基于干净状态重试
```

**为什么 EventStore 可为 null**：纯内存模式下跳过持久化，直接 Mutate + Commit。

---

## 4. Runtime Layer — ActorSystem & InMemoryEventStore

### 4.1 ActorSystem

```
ActorSystem(IEventStore) : IActorSystem
  ┌─ _factories:   ConcurrentDictionary<Type, Func<ICommand, object>>  ← 工厂注册
  ├─ _registry:    ConcurrentDictionary<Guid, ActorBase>               ← 活跃 Actor
  └─ _eventStore:  IEventStore                                          ← 持久化
```

**CreateAsync 流程**：
```
factory(cmd) → new Actor(cmd) → Id = CreateVersion7()
  → es.EventStore = _store → _registry[id] = → SignalReady() → return
```

**GetAsync 流程**：
```
查 registry → 命中返回
  → 非 ES 类型 → return null（ephemeral，不能重建）
  → LoadAsync(id) → 空 → return null
  → new T() → Id + EventStore + ReplayEvents
  → TryAdd（失败则 StopAsync 丢弃 + 返回已有）→ SignalReady → return
```

**为什么 GetAsync 用 TryAdd 而非赋值**：并发 GetAsync 同一 ID 时，后到者会覆盖先到者，导致先到 Actor 泄漏（消费循环运行但无人能路由）。TryAdd 失败时 StopAsync 丢弃重建的，返回注册表中的。

**为什么非 ES Actor 的 GetAsync 返回 null**：非 ES Actor 纯内存、无持久化。Stop 后事件流不存在，无法重建。这是设计决策而非遗漏。

### 4.2 InMemoryEventStore

```
InMemoryEventStore : IEventStore
  ConcurrentDictionary<actorId, List<IDomainEvent>>

  无锁设计：
    - AppendAsync: 同一 Actor 的消费循环串行写 → 无并发
    - LoadAsync:   仅在被重建 Actor 不在内存时调用 → 无并发写
    - 不同 Actor:  ConcurrentDictionary 保障
```

### 4.3 并发模型

| 操作 | 并发来源 | 保障 |
|------|---------|------|
| CreateAsync | 多线程调用 | UUID v7 唯一，无冲突 |
| GetAsync | 多线程同一 ID | TryAdd 防止重复 |
| Send/Ask | 多线程 + 目标可能被 Stop | TryGetValue 原子查找 |
| StopAsync | 多线程 + 幂等 | TryRemove + Interlocked |

---

## 5. Actor 生命周期协议（完整）

### 5.1 CreateAsync

```
1. factory(cmd) → new Counter(CreateCounter(0))
   └─ base(cmd): Channel + RunAsync（闸门）+ OnMessageAsync → RaiseEvent
2. Id = CreateVersion7()
3. EventStore = _store（仅 ES）
4. _registry[id] = actor
5. SignalReady() → OnReadyAsync() → FlushEventsAsync → 进入循环
```

### 5.2 GetAsync（重建）

```
1. _registry 命中 → return
2. 非 ES → return null
3. LoadAsync(id) → null → return null
4. new T() + Id + EventStore + ReplayEvents
5. TryAdd → 失败则 StopAsync + return 已有
6. SignalReady → return
```

### 5.3 消息处理

```
Send/Ask → actor.Post(envelope)
  → ProcessAsync: OnMessageAsync → FlushEventsAsync → TCS
```

### 5.4 Stop

```
TryRemove → StopAsync:
  _ready.TrySetResult（确保 RunAsync 能退出）
  → CTS.Cancel + Writer.Complete + await _loopTask
```

---

## 6. 开发者视角

### 6.1 三步分离

```csharp
public sealed class Counter : EventSourcedActor
{
    private int _value;
    public Counter(CreateCounter cmd) : base(cmd) { }   // 创建构造
    public Counter() { }                                  // 重建构造（new() 约束）

    // Step 1: 构建事件。可以读当前状态做验证和计算
    protected override ValueTask<object?> OnMessageAsync(ICommand command) => command switch
    {
        CreateCounter c => { RaiseEvent(new CounterCreated(c.InitialValue)); return default; },
        Increment i     => { RaiseEvent(new CounterIncremented(i.Delta));       return default; },
        GetValue        => new ValueTask<object?>(_value),
        _               => default,
    };

    // Step 2: 框架自动持久化（ProcessAsync → FlushEventsAsync）

    // Step 3: 变更状态（纯函数）
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

```csharp
// 事件间依赖存在于事件携带的数据中，不存在于聚合状态中
case AddOrderDetail cmd:
    if (_details.Any(d => d.Sku == cmd.Sku))
        throw new InvalidOperationException("Duplicate SKU");
    var newTotal = TotalPrice + cmd.UnitPrice * cmd.Quantity;  // 局部变量
    RaiseEvent(new OrderDetailAdded(cmd.Sku, cmd.Quantity, cmd.UnitPrice, newTotal));
    if (newTotal > 1000)                                        // 用局部变量，不用 TotalPrice
        RaiseEvent(new HighValueOrderDetected(Id, newTotal));
```

---

## 7. Key Design Decisions

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
| TryAdd in GetAsync | 防止并发重建时的 Actor 泄漏 |
| StopAsync 释放闸门 | 防止未 SignalReady 的 Actor 死锁 |
| EventStore 可为 null | 支持纯内存模式（测试/开发） |

---

## 8. Non-Scope

- SG 代理 / Orleans 风格接口
- 分布式 / 跨进程 / Virtual Actor
- 监督策略（后续版本）

---

## 9. Self-Review

- [x] Abs: 6 接口 + 1 异常 + 2 基类 + 1 信封
- [x] Runtime: ActorSystem + InMemoryEventStore
- [x] Tests: 8 passing（3 Abs + 5 Actor）
- [x] Persist-then-Mutate 完整性
- [x] 并发安全（TryAdd / TryRemove / TryGetValue）
- [x] 资源泄漏防护（构造函数清理 + StopAsync 闸门释放）
- [x] AOT 安全：零反射、零 dynamic
- [x] netstandard2.0（Abs）+ net10.0（Runtime）
