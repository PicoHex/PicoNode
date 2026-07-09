# Spec: PicoNode.Actor — In-Memory Actor Framework (Abs Layer)

**Date:** 2026-07-08 | **Updated:** 2026-07-09
**Status:** implemented

## Motivation

PicoNode 需要一个基于 PicoHex 生态的 in-memory Actor 框架。当前 repo 已按职责域重组（Network/Http/Web/Agent），Actor 作为独立域加入。

---

## 1. Design Philosophy

| 原则 | 说明 |
|------|------|
| **Actor 持有自己的 Mailbox** | `Channel<Envelope>`，构造时启动消费循环（被闸门阻塞直到就绪） |
| **send/ask 消息模型** | `IActorSystem.Send(id, cmd)` / `AskAsync<T>(id, cmd)` |
| **纯领域对象，无 DI 依赖** | Actor 只接收 `ICommand` 产生 `IDomainEvent`，构造只需 `new T()` |
| **Persist-then-Mutate** | 持久化成功后才会变更内存状态——参照 Akka Persistence |
| **AOT First** | 零反射、零 dynamic |
| **极简 Abs** | 无 SG 代理、无 Orleans 风格接口，IEventStore 也在 Abs 中定义 |

### 为什么 IEventStore 在 Abs 层

`EventSourcedActor.ProcessAsync` 需要调用 `IEventStore.AppendAsync`——持久化是 Actor 生命周期的内在组成部分，不是外部关注点。把 IEventStore 定义在 Abs 等同于 `System.Threading.Channels` 在 Abs 中——Abstract（契约）归 Abs，Implementation（具体存储）归运行时。

### 为什么 Actor 不依赖 DI

Actor 是纯领域对象。业务标识（orderId 等）是命令的属性，不属于框架。框架只关心技术标识（UUID v7）。无 DI 意味着两条路径（工厂创建 vs 事件重建）最终都收敛到同一组构造函数。

---

## 2. Target Structure

```
src/
  Actor/
    PicoNode.Actor.Abs/     # 抽象层（netstandard2.0）
    PicoNode.Actor/          # 运行时实现（net10.0, AOT-compatible）
```

### 为什么 netstandard2.0

最大化包兼容性。Abs 层仅依赖 `System.Threading.Channels` 和 `Microsoft.Bcl.AsyncInterfaces`（`IAsyncDisposable` polyfill），无任何 PicoHex 特定依赖。

---

## 3. Abs API Design（当前实现）

### 3.1 标记接口

```csharp
public interface ICommand { }
public interface IDomainEvent { }
```

### 3.2 IEventStore（事件流持久化契约）

```csharp
/// <summary>
/// Event stream persistence contract. Append-only with optimistic concurrency.
/// Defined in Abs because EventSourcedActor.ProcessAsync owns the persist step.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Append events to an actor's event stream.
    /// expectedVersion is the version before these events — for optimistic concurrency.
    /// Throws ConcurrencyException if the stream has diverged.
    /// </summary>
    ValueTask<ulong> AppendAsync(Guid actorId, ulong expectedVersion, IReadOnlyList<IDomainEvent> events);

    /// <summary>Load all events for an actor, ordered by version ascending.</summary>
    ValueTask<IReadOnlyList<IDomainEvent>> LoadAsync(Guid actorId);
}
```

### 3.3 Actor 接口

```csharp
public interface IActor
{
    Guid Id { get; }
}

public interface IEventSourcedActor : IActor
{
    ulong Version { get; }
    IReadOnlyList<IDomainEvent> GetUncommittedEvents();
    void CommitEvents();
    void ReplayEvents(IReadOnlyList<IDomainEvent> events);
}
```

### 3.4 IActorSystem

```csharp
public interface IActorSystem
{
    void Register<T>(Func<ICommand, T> factory) where T : IActor;
    ValueTask<T> CreateAsync<T>(ICommand command) where T : IActor;
    ValueTask<T?> GetAsync<T>(Guid id) where T : IActor, new();

    void Send(Guid id, ICommand command);
    ValueTask<TResult> AskAsync<TResult>(Guid id, ICommand command);
    ValueTask StopAsync(Guid id);
}
```

**为什么工厂签名没有 Guid**：UUID v7 是框架内部的技术标识。把它暴露给工厂是信息泄露。

**为什么 GetAsync 有 new() 约束而非 InternalsVisibleTo**：
- `new()` 约束是 AOT 安全的——NativeAOT 编译器为每个 T 生成直接调用
- 用户体验同 ENode——两个 public 构造各司其职

### 3.5 Actor 基类（双构造函数 + 闸门 + 生命周期钩子）

```csharp
public abstract class Actor : IActor, IAsyncDisposable
{
    private readonly Channel<Envelope> _mailbox;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _ready = new();
    private readonly Task _loopTask;

    public Guid Id { get; internal set; }

    // 创建路径：构造时同步处理创建命令
    protected Actor(ICommand creationCommand) : this()
    {
        var task = OnMessageAsync(creationCommand);
        if (!task.IsCompleted)
            throw new InvalidOperationException(
                "OnMessageAsync must complete synchronously for creation commands.");
    }

    // 重建路径：无业务逻辑，状态后续由 ReplayEvents 恢复
    protected Actor()
    {
        _mailbox = Channel.CreateUnbounded<Envelope>();
        _loopTask = RunAsync(_cts.Token);
    }

    internal void SignalReady() => _ready.TrySetResult(true);
    internal void Post(Envelope envelope) => _mailbox.Writer.TryWrite(envelope);

    private async Task RunAsync(CancellationToken ct)
    {
        // 闸门：等待 ActorSystem 完成注册 + ReplayEvents
        await _ready.Task.ConfigureAwait(false);

        // 生命周期钩子：EventSourcedActor 在此处 flush 构造事件
        await OnReadyAsync().ConfigureAwait(false);

        // 消费循环
        await foreach (var envelope in _mailbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try { await ProcessAsync(envelope).ConfigureAwait(false); }
            catch (Exception ex) { envelope.Tcs?.TrySetException(ex); }
        }
    }

    protected virtual async ValueTask ProcessAsync(Envelope envelope)
    {
        var result = await OnMessageAsync(envelope.Command).ConfigureAwait(false);
        envelope.Tcs?.TrySetResult(result);
    }

    protected abstract ValueTask<object?> OnMessageAsync(ICommand command);

    /// <summary>
    /// Lifecycle hook called after SignalReady() and before the first mailbox message.
    /// EventSourcedActor overrides this to flush uncommitted construction events.
    /// </summary>
    protected virtual ValueTask OnReadyAsync() => default;
}
```

**为什么有 OnReadyAsync 钩子**：
构造路径中创建命令在构造函数里同步处理，产生的事件（RaiseEvent）暂存在 `_events` 列表中。消费循环直到闸门释放后才会运行。OnReadyAsync 是闸门和第一条消息之间的桥梁——EventSourcedActor 在此处 flush 构造事件（持久化 → Mutate → Commit）。

### 3.6 EventSourcedActor（Persist-then-Mutate）

```csharp
public abstract class EventSourcedActor : Actor, IEventSourcedActor
{
    private readonly List<IDomainEvent> _events = new();

    public ulong Version { get; protected set; }

    /// <summary>Set by IActorSystem after construction, before SignalReady.</summary>
    internal IEventStore? EventStore { get; set; }

    protected EventSourcedActor(ICommand creationCommand) : base(creationCommand) { }
    protected EventSourcedActor() { }

    /// <summary>
    /// Record a domain event and increment Version. Does NOT mutate state.
    /// State is mutated later, after successful persistence, by FlushEventsAsync.
    /// </summary>
    protected void RaiseEvent<T>(T @event) where T : notnull, IDomainEvent
    {
        _events.Add(@event);
        Version++;
    }

    /// <summary>
    /// Mutate in-memory state from a domain event.
    /// Called by FlushEventsAsync (after persistence) and ReplayEvents (recovery).
    /// Must be a pure function — no validation, no side effects.
    /// </summary>
    protected abstract void Mutate(IDomainEvent @event);

    protected override async ValueTask OnReadyAsync()
        => await FlushEventsAsync().ConfigureAwait(false);

    protected override async ValueTask ProcessAsync(Envelope envelope)
    {
        var result = await OnMessageAsync(envelope.Command).ConfigureAwait(false);
        await FlushEventsAsync().ConfigureAwait(false);
        envelope.Tcs?.TrySetResult(result);
    }

    private async ValueTask FlushEventsAsync()
    {
        if (_events.Count == 0) return;

        if (EventStore is not null)
            await EventStore.AppendAsync(Id, Version - (ulong)_events.Count, _events)
                .ConfigureAwait(false);

        foreach (var e in _events) Mutate(e);
        ClearEvents();
    }

    private void ClearEvents() => _events.Clear();

    void IEventSourcedActor.ReplayEvents(IReadOnlyList<IDomainEvent> events)
    {
        foreach (var e in events) Mutate(e);
        Version = (ulong)events.Count;
    }
}
```

**为什么 Persist-then-Mutate**：

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

**为什么 Version 在每个 RaiseEvent 时递增**：
Version 的本质是"已形成事件的序号"，不是"已持久化事件的序号"。Actor 产生了事件就应该知道自己的版本号——与 ENode 一致。

**为什么 EventStore 可为 null**：
纯内存模式下（测试、无持久化场景）不需要持久化。此时 `FlushEventsAsync` 跳过 SaveAsync，直接 Mutate + Commit。

### 3.7 Envelope

```csharp
public sealed class Envelope
{
    public ICommand Command { get; init; } = null!;
    public TaskCompletionSource<object?>? Tcs { get; init; }
}
```

### 3.8 兼容性文件

```csharp
// GlobalUsings.cs
global using System.Threading.Channels;

// IsExternalInit.cs — netstandard2.0 init 关键字 polyfill
namespace System.Runtime.CompilerServices;
internal class IsExternalInit { }
```

---

## 4. Actor 生命周期协议

### 4.1 CreateAsync（创建路径）

```
CreateAsync<Counter>(new CreateCounter(0)):
  1. counter = factory(cmd)
     └─ new Counter(CreateCounter(0))
        ├─ base(cmd):
        │  ├─ base()                            ← Channel + 消费循环（闸门）
        │  └─ OnMessageAsync(CreateCounter(0))  ← 同步：RaiseEvent 只记录，不改状态
        └─ 无额外子类逻辑
  2. counter.Id = CreateVersion7()
  3. counter.EventStore = _store                 ← 设置持久化
  4. _registry[id] = counter
  5. counter.SignalReady()                       ← 释放闸门
     └─ RunAsync:
        ├─ OnReadyAsync()
        │  └─ FlushEventsAsync:
        │     ├─ SaveAsync([CounterCreated])
        │     ├─ foreach Mutate(e)              ← 状态第一次变更
        │     └─ ClearEvents()
        └─ 进入消费循环，等待 mailbox 消息
  6. return counter                              ← 已就绪
```

### 4.2 GetAsync（重建路径）

```
GetAsync<Counter>(id):
  1. 查 _registry → 命中则返回
  2. 未命中 + typeof(T) : IEventSourcedActor:
     a. events = await _store.LoadAsync(id)
     b. events 为空 → return null
     c. counter = new Counter()                 ← public 无参构造
     d. counter.Id = id
     e. counter.EventStore = _store
     f. counter.ReplayEvents(events)            ← 直接 Mutate，不走持久化
     g. _registry[id] = counter
     h. counter.SignalReady()
        └─ OnReadyAsync() → _events 为空 → no-op
     i. return counter
```

### 4.3 正常消息处理

```
Send / AskAsync:
  actor.Post(new Envelope { Command = cmd, Tcs = tcs })
    └─ ProcessAsync(envelope):
       1. OnMessageAsync(cmd)
          └─ RaiseEvent(e) × N → 记录事件，不改状态
       2. FlushEventsAsync:
          ├─ SaveAsync(events)   ← 持久化
          ├─ foreach Mutate(e)  ← 成功后变更状态
          └─ ClearEvents()
       3. TCS.TrySetResult()
```

### 4.4 Stop

```
StopAsync(id):
  1. 原子移除 _registry[id]
  2. await actor.StopAsync()
     └─ _cts.Cancel() + Writer.Complete() + await _loopTask
```

---

## 5. 开发者视角——三步分离

```csharp
public sealed class Counter : EventSourcedActor
{
    private int _value;

    public Counter(CreateCounter cmd) : base(cmd) { }
    public Counter() { }

    // Step 1: 构建领域事件
    protected override ValueTask<object?> OnMessageAsync(ICommand command) => command switch
    {
        CreateCounter c => { RaiseEvent(new CounterCreated(c.InitialValue)); return default; },
        Increment i     => { RaiseEvent(new CounterIncremented(i.Delta));       return default; },
        GetValue        => new ValueTask<object?>(_value),
        _               => default,
    };

    // Step 2: 框架自动持久化（ProcessAsync → FlushEventsAsync）

    // Step 3: 变更状态
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

### 复杂聚合示例：OnMessageAsync 内的级联事件

```csharp
// 添加订单明细 + 检查阈值
case AddOrderDetail cmd:
    // 验证用当前状态（_details 是已持久化的干净状态）
    if (_details.Any(d => d.Sku == cmd.Sku))
        throw new InvalidOperationException("Duplicate SKU");

    // 计算——用局部变量追踪中间结果
    var newTotal = TotalPrice + cmd.UnitPrice * cmd.Quantity;

    // 事件 1
    RaiseEvent(new OrderDetailAdded(cmd.Sku, cmd.Quantity, cmd.UnitPrice, newTotal));

    // 事件 2——不要读 TotalPrice（它还是旧值），用局部变量
    if (newTotal > 1000)
        RaiseEvent(new HighValueOrderDetected(Id, newTotal));

    return default;
```

**规则**：`OnMessageAsync` 内所有 `RaiseEvent` 都在同一快照——状态是命令到达瞬间的版本。事件间的级联依赖存在于事件携带的数据中，不存在于聚合状态中。

---

## 6. 双构造函数模式 vs ENode

| | ENode | PicoNode.Actor |
|---|---|---|
| 创建构造 | `Order(CreateOrder cmd)` | `Counter(CreateCounter cmd) : base(cmd)` |
| 重建构造 | `Order()`（框架用反射调） | `Counter()`（框架用 `new()` 约束调） |
| 重建机制 | `Activator.CreateInstance<T>()` | `new T()` —— AOT 安全 |
| 持久化 | Repository 外部调用 | EventSourcedActor.ProcessAsync 内置 |
| 用户感知 | 两个构造 + 外部 Save | 两个构造，持久化透明 |

---

## 7. 为什么用 Channel 而非 ConcurrentQueue

Erlang 通过轻量进程实现"无消息时暂停"，不消耗 OS 线程。.NET 没有等价机制：
- `ConcurrentQueue` 轮询 → 空转 CPU
- `Thread.Sleep` → 引入延迟

`Channel.ReadAllAsync` 在队列空时把 Task 挂起（不占线程），新消息入队时 Write 端自动唤醒 Read 端。

---

## 8. 依赖关系

```
PicoNode.Actor.Abs (netstandard2.0)
  ├─ System.Threading.Channels          ← Channel mailbox
  └─ Microsoft.Bcl.AsyncInterfaces      ← IAsyncDisposable polyfill

PicoNode.Actor (net10.0, AOT-compatible)
  ├─ PicoNode.Actor.Abs
  ├─ PicoDI                             ← 单例注册 IActorSystem
  ├─ PicoLog.Abs                        ← 结构化日志
  └─ PicoCfg.Abs                        ← 配置热加载
```

---

## 9. Non-Scope

- SG 代理 / Orleans 风格接口
- 分布式 / 跨进程 / Virtual Actor
- 监督策略（后续版本）

---

## 10. 文件清单

```
src/Actor/PicoNode.Actor.Abs/
├── PicoNode.Actor.Abs.csproj    ← netstandard2.0
├── GlobalUsings.cs              ← System.Threading.Channels
├── IsExternalInit.cs            ← netstandard2.0 init polyfill
├── ICommand.cs                  ← 命令标记
├── IDomainEvent.cs              ← 事件标记
├── IEventStore.cs               ← 事件流持久化契约
├── IActor.cs                    ← Id getter
├── IEventSourcedActor.cs        ← Version + ES 合约
├── IActorSystem.cs              ← Register/Create/Get/Send/Ask/Stop
├── Actor.cs                     ← 双构造 + 闸门 + OnReadyAsync + 消费循环
├── EventSourcedActor.cs         ← RaiseEvent + Persist-then-Mutate + 版本自管理
└── Envelope.cs                  ← 命令信封
```

---

## 11. Self-Review

- [x] 无 TBD/TODO
- [x] Abs: 6 接口（ICommand, IDomainEvent, IEventStore, IActor, IEventSourcedActor, IActorSystem）+ 2 基类 + 1 信封
- [x] IEventStore 在 Abs——EventSourcedActor.ProcessAsync 内置持久化
- [x] Persist-then-Mutate：持久化成功后才 Mutate，失败时状态保持干净
- [x] 双构造函数：创建（atomic via ctor） vs 重建（ReplayEvents）
- [x] `new()` 约束替代反射：AOT 安全
- [x] Version 由 Actor 在 RaiseEvent 中自管理
- [x] SignalReady 闸门 + OnReadyAsync 钩子：构造事件在首条消息前 flush
- [x] FlushEventsAsync 统一所有持久化代码路径
- [x] EventStore 可为 null——支持纯内存模式
- [x] AOT 安全：零反射、零 dynamic
- [x] netstandard2.0 兼容
