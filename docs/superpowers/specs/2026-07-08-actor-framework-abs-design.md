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
| **AOT First** | 零反射、零 dynamic |
| **极简 Abs** | 无 SG 代理、无 Orleans 风格接口 |
| **遵循 PicoHex 惯例** | PicoDI、PicoLog、PicoCfg 在运行时层集成 |

### 为什么 Actor 不依赖 DI

Actor 是纯领域对象。业务标识（orderId 等）是命令的属性，不属于框架。框架只关心技术标识（UUID v7）。无 DI 意味着两条路径（工厂创建 vs 事件重建）最终都收敛到同一组构造函数——消除了"重建时如何注入依赖"的复杂性。

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

### 3.2 Actor 接口

```csharp
public interface IActor
{
    /// <summary>UUID v7 标识。由 IActorSystem 在构造后分配。</summary>
    Guid Id { get; }
}

public interface IEventSourcedActor : IActor
{
    /// <summary>当前版本。由 Actor 自己在 RaiseEvent / ReplayEvents 中管理。</summary>
    ulong Version { get; }

    IReadOnlyList<IDomainEvent> GetUncommittedEvents();
    void CommitEvents();

    /// <summary>
    /// 从已持久化事件重建内存状态。
    /// 在第一条消息投递前调用一次。消息在重建期间排队但不投递。
    /// </summary>
    void ReplayEvents(IReadOnlyList<IDomainEvent> events);
}
```

### 3.3 IActorSystem

```csharp
public interface IActorSystem
{
    /// <summary>
    /// 注册创建工厂。工厂接收创建命令，返回构造完毕的 Actor。
    /// UUID v7 由 ActorSystem 内部生成并分配，不暴露给工厂。
    /// </summary>
    void Register<T>(Func<ICommand, T> factory) where T : IActor;

    /// <summary>
    /// 用注册的工厂创建 Actor。
    /// 1. factory(cmd) —— 构造 Actor，创建命令同步处理
    /// 2. 分配 UUID v7
    /// 3. 注册到系统
    /// 4. SignalReady() 释放消费循环
    /// </summary>
    ValueTask<T> CreateAsync<T>(ICommand command) where T : IActor;

    /// <summary>
    /// 按 UUID v7 查找 Actor。在内存中则直接返回；
    /// 否则从事件重建。未找到返回 null。
    /// 重建路径：new T() → 分配 Id → ReplayEvents → SignalReady。
    /// new() 约束是 AOT 安全的——编译器生成直接调用，零反射。
    /// </summary>
    ValueTask<T?> GetAsync<T>(Guid id) where T : IActor, new();

    void Send(Guid id, ICommand command);
    ValueTask<TResult> AskAsync<TResult>(Guid id, ICommand command);
    ValueTask StopAsync(Guid id);
}
```

**为什么工厂签名没有 Guid**：UUID v7 是框架内部的技术标识。把它暴露给工厂是信息泄露。

**为什么 Register<T> 和 CreateAsync<T> 分离**：注册是编译时/启动时的事，创建是运行时的事。分离支持批量注册 + 工厂闭包捕获外部上下文。

**为什么 GetAsync 有 new() 约束而非 InternalsVisibleTo**：
- `new()` 约束是 AOT 安全的——NativeAOT 编译器为每个 T 生成直接调用，零反射
- 用户体验同 ENode——两个 public 构造各司其职（创建构造 + 无参构造）
- 避免 InternalsVisibleTo 的"为什么需要 internal"困惑

### 3.4 Actor 基类（双构造函数 + 闸门）

```csharp
public abstract class Actor : IActor, IAsyncDisposable
{
    private readonly Channel<Envelope> _mailbox;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _ready = new();
    private readonly Task _loopTask;
    private int _stopped;

    public Guid Id { get; internal set; }

    /// <summary>
    /// 创建路径。构造 Actor 并同步处理创建命令，保证原子初始化。
    /// 消费循环在构造函数中启动但被闸门阻塞，SignalReady() 后释放。
    /// </summary>
    protected Actor(ICommand creationCommand) : this()
    {
        var task = OnMessageAsync(creationCommand);
        if (!task.IsCompleted)
            throw new InvalidOperationException(
                "OnMessageAsync must complete synchronously for creation commands. " +
                "Use RaiseEvent or return a value directly; do not await.");
    }

    /// <summary>
    /// 重建路径。构造 Actor 不做任何业务逻辑。
    /// 状态由调用方通过 ReplayEvents 恢复。
    /// 必须可被子类的 public 无参构造链入（GetAsync 的 new() 约束要求）。
    /// </summary>
    protected Actor()
    {
        _mailbox = Channel.CreateUnbounded<Envelope>();
        _loopTask = RunAsync(_cts.Token);
    }

    /// <summary>
    /// ActorSystem 在 Actor 完全初始化后调用（Id 已分配、已注册、事件已重放）。
    /// 释放闸门，消费循环开始处理消息。
    /// </summary>
    internal void SignalReady() => _ready.TrySetResult(true);

    internal void Post(Envelope envelope) => _mailbox.Writer.TryWrite(envelope);

    private async Task RunAsync(CancellationToken ct)
    {
        // 闸门：等待 ActorSystem 调用 SignalReady()。
        // 保证 Id 已分配、Actor 已注册、ReplayEvents（如有）已完成。
        // 等待期间 Post() 的消息在 Channel 中安全排队。
        await _ready.Task.ConfigureAwait(false);

        try
        {
            await foreach (var envelope in _mailbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await ProcessAsync(envelope).ConfigureAwait(false); }
                catch (Exception ex) { envelope.Tcs?.TrySetException(ex); }
            }
        }
        catch (OperationCanceledException) { /* Stop 时预期 */ }
    }

    /// <summary>可被运行时层重写以注入持久化。</summary>
    protected virtual async ValueTask ProcessAsync(Envelope envelope)
    {
        var result = await OnMessageAsync(envelope.Command).ConfigureAwait(false);
        envelope.Tcs?.TrySetResult(result);
    }

    protected abstract ValueTask<object?> OnMessageAsync(ICommand command);

    internal async ValueTask StopAsync() { /* 幂等差止逻辑 */ }

    async ValueTask IAsyncDisposable.DisposeAsync() => await StopAsync();
}
```

### 3.5 EventSourcedActor（版本自管理）

```csharp
public abstract class EventSourcedActor : Actor, IEventSourcedActor
{
    private readonly List<IDomainEvent> _events = new();

    public ulong Version { get; protected set; }

    /// <summary>创建路径。</summary>
    protected EventSourcedActor(ICommand creationCommand) : base(creationCommand) { }

    /// <summary>重建路径。</summary>
    protected EventSourcedActor() { }

    /// <summary>
    /// 记录事件、变更状态、递增 Version。
    /// 子类在 OnMessageAsync 中调用。
    /// Actor 是自身版本号的唯一权威——每个 RaiseEvent = 一次版本递增。
    /// </summary>
    protected void RaiseEvent<T>(T @event) where T : notnull, IDomainEvent
    {
        _events.Add(@event);
        Mutate(@event);
        Version++;
    }

    /// <summary>
    /// 从领域事件变更内存状态。
    /// 由 RaiseEvent（正常流程）和 ReplayEvents（恢复流程）调用。
    /// </summary>
    protected abstract void Mutate(IDomainEvent @event);

    IReadOnlyList<IDomainEvent> IEventSourcedActor.GetUncommittedEvents() => _events.AsReadOnly();
    void IEventSourcedActor.CommitEvents() => _events.Clear();
    void IEventSourcedActor.ReplayEvents(IReadOnlyList<IDomainEvent> events)
    {
        foreach (var e in events) Mutate(e);
        Version = (ulong)events.Count;
    }
}
```

**为什么 Version 由 Actor 管理（而非持久化层）**：
Version 的本质是"已形成事件的序号"，不是"已持久化事件的序号"。Actor 产生了事件就应该知道自己的版本号。这和 ENode 一致——聚合根追踪自己的版本。

**为什么 RaiseEvent 立即 Mutate**：
- 性能：事件形成和状态变更是同一个逻辑步骤，拆开反而增加复杂度
- 一致性：Actor 是自身状态的唯一写入者，不存在并发
- 持久化失败：新创建 Actor 的失败意味着整个 Actor 被丢弃；运行时消息的失败由 TCS 传播异常，调用方决定重试或放弃

### 3.6 Envelope

```csharp
/// <summary>
/// 包装命令和可选 TaskCompletionSource 的消息信封。
/// 不面向用户代码——框架和持久化层使用。
/// public 是因为 protected ProcessAsync 暴露给了外部程序集（C# 可访问性约束）。
/// </summary>
public sealed class Envelope
{
    public ICommand Command { get; init; } = null!;
    public TaskCompletionSource<object?>? Tcs { get; init; }
}
```

### 3.7 兼容性文件

```csharp
// GlobalUsings.cs
global using System.Threading.Channels;

// IsExternalInit.cs — netstandard2.0 init 关键字 polyfill
namespace System.Runtime.CompilerServices;
internal class IsExternalInit { }
```

---

## 4. Actor 生命周期协议

### 4.1 CreateAsync（创建 + 初始化）

```
CreateAsync<Counter>(new CreateCounter(0)):
  1. counter = factory(cmd)
     └─ new Counter(CreateCounter(0))
        ├─ base(cmd):
        │  ├─ base()                            ← Channel + 消费循环启动（闸门阻塞）
        │  └─ OnMessageAsync(CreateCounter(0))  ← 同步处理
        │     └─ RaiseEvent(CounterCreated(0))  ← 记录 + Mutate + Version=1
        └─ 无额外子类逻辑
  2. counter.Id = CreateVersion7()
  3. _registry[id] = counter
  4. counter.SignalReady()                      ← 释放闸门
  5. return counter                             ← 调用方拿到已就绪的 Actor
```

### 4.2 GetAsync（查找 + 重建）

```
GetAsync<Counter>(id):
  1. 查 _registry → 命中则直接返回
  2. 未命中 + typeof(T) : IEventSourcedActor:
     a. events = await _eventStore.LoadAsync(id)
     b. events 为空 → return null
     c. counter = new Counter()                 ← public 无参构造
     d. counter.Id = id
     e. counter.ReplayEvents(events)            ← 重放事件流
     f. _registry[id] = counter
     g. counter.SignalReady()
     h. return counter
```

### 4.3 Send / Ask

```
Send(id, cmd):
  actor = _registry[id] ?? throw
  actor.Post(new Envelope { Command = cmd })    ← TCS=null

AskAsync<T>(id, cmd):
  actor = _registry[id] ?? throw
  tcs = new TaskCompletionSource<object?>()
  actor.Post(new Envelope { Command = cmd, Tcs = tcs })
  return (T)await tcs.Task
```

### 4.4 Stop

```
StopAsync(id):
  1. 原子移除 _registry[id]（不存则幂等返回）
  2. await actor.StopAsync()
     └─ _cts.Cancel() → Channel 停止读取
     └─ await _loopTask → 等待当前消息处理完毕
     └─ _cts.Dispose()
  二次调用：Interlocked 保证幂等
```

---

## 5. 双构造函数模式 vs ENode

| | ENode | PicoNode.Actor |
|---|---|---|
| 创建构造 | `Order(CreateOrder cmd)` | `Counter(CreateCounter cmd) : base(cmd)` |
| 重建构造 | `Order()`（框架用反射调） | `Counter()`（框架用 `new()` 约束调） |
| 重建机制 | `Activator.CreateInstance<T>()` | `new T()` —— AOT 安全 |
| 用户感知 | 需写两个构造，无参构造"为什么需要"不透明 | 需写两个构造，`new()` 约束显式化 |

---

## 6. 为什么用 Channel 而非 ConcurrentQueue

Erlang 通过轻量进程实现"无消息时暂停"，不消耗 OS 线程。.NET 没有等价机制：
- `ConcurrentQueue` 轮询 → 空转 CPU
- `Thread.Sleep` → 引入延迟

`Channel.ReadAllAsync` 在队列空时把 Task 挂起（不占线程），新消息入队时 Write 端自动唤醒 Read 端——等价于 Erlang 的 `receive` 阻塞语义。

---

## 7. 使用示例

```csharp
// ── 命令 ──
public sealed record CreateCounter(int InitialValue) : ICommand;
public sealed record Increment(int Delta) : ICommand;
public sealed record GetValue : ICommand;

// ── 事件 ──
public sealed record CounterCreated(int InitialValue) : IDomainEvent;
public sealed record CounterIncremented(int Delta) : IDomainEvent;

// ── ES Actor ──
public sealed class Counter : EventSourcedActor
{
    private int _value;

    // 创建路径 —— 构造时原子初始化
    public Counter(CreateCounter cmd) : base(cmd) { }

    // 重建路径 —— GetAsync 的 new() 约束需要
    public Counter() { }

    protected override ValueTask<object?> OnMessageAsync(ICommand command) => command switch
    {
        CreateCounter c => { RaiseEvent(new CounterCreated(c.InitialValue)); return default; },
        Increment i     => { RaiseEvent(new CounterIncremented(i.Delta)); return default; },
        GetValue        => new ValueTask<object?>(_value),
        _               => default,
    };

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case CounterCreated e:     _value = e.InitialValue; break;
            case CounterIncremented e: _value += e.Delta;      break;
        }
    }
}

// ── 消费 ──
system.Register<Counter>(cmd => cmd switch
{
    CreateCounter c => new Counter(c),
    _               => throw new InvalidOperationException(),
});

var counter = await system.CreateAsync<Counter>(new CreateCounter(0));
system.Send(counter.Id, new Increment(5));
var value = await system.AskAsync<int>(counter.Id, new GetValue());
```

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
├── PicoNode.Actor.Abs.csproj    ← netstandard2.0, Channels + AsyncInterfaces
├── GlobalUsings.cs              ← System.Threading.Channels
├── IsExternalInit.cs            ← netstandard2.0 init polyfill
├── ICommand.cs                  ← 命令标记
├── IDomainEvent.cs              ← 事件标记
├── IActor.cs                    ← Id getter
├── IEventSourcedActor.cs        ← Version + ES 合约
├── IActorSystem.cs              ← Register/Create/Get/Send/Ask/Stop
├── Actor.cs                     ← 双构造 + 闸门 + Channel 消费循环
├── EventSourcedActor.cs         ← RaiseEvent + Mutate + 版本自管理
└── Envelope.cs                  ← 命令信封（public，技术约束）
```

---

## 11. Self-Review

- [x] 无 TBD/TODO
- [x] 双构造函数：创建（atomic via ctor） vs 重建（ReplayEvents）
- [x] `new()` 约束替代 InternalsVisibleTo + 反射：AOT 安全
- [x] Version 由 Actor 在 RaiseEvent 中自管理
- [x] SignalReady 闸门：ENode 式"重建完毕前不投递"
- [x] 工厂不暴露 Guid：技术 ID 不泄露到业务层
- [x] ProcessAsync 为 protected：运行时层可重写
- [x] AOT 安全：零反射、零 dynamic
- [x] netstandard2.0 兼容：TaskCompletionSource<bool> + IsExternalInit
