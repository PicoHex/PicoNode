# PicoNode.Actor — In-Memory Actor Framework

## Packages

| Package | Target | Description |
|---------|--------|-------------|
| `PicoNode.Actor.Abs` | netstandard2.0 | Interfaces, base classes, IEventStore contract |
| `PicoNode.Actor` | net10.0 | ActorSystem, InMemoryEventStore, DI integration |

## Quick Start

```csharp
using PicoNode.Actor;
using PicoNode.Actor.Abs;

// Define commands and events
public sealed record CreateCounter(int Value) : ICommand;
public sealed record Increment(int Delta) : ICommand;
public sealed record GetValue : ICommand;
public sealed record CounterCreated(int Value) : IDomainEvent;
public sealed record CounterIncremented(int Delta) : IDomainEvent;

// Define the actor
public sealed class Counter : EventSourcedActor
{
    private int _value;
    public Counter(CreateCounter cmd) : base(cmd) { }
    public Counter() { }

    protected override ValueTask<object?> OnMessageAsync(ICommand command)
    {
        switch (command)
        {
            case CreateCounter c: RaiseEvent(new CounterCreated(c.Value)); return default;
            case Increment i:     RaiseEvent(new CounterIncremented(i.Delta)); return default;
            case GetValue:        return new ValueTask<object?>(_value);
        }
        return default;
    }

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case CounterCreated e:     _value = e.Value; break;
            case CounterIncremented e: _value += e.Delta; break;
        }
    }
}

// Usage
var system = new ActorSystem(new InMemoryEventStore());
system.Register<Counter>(cmd => cmd switch
{
    CreateCounter c => new Counter(c),
    _               => throw new InvalidOperationException(),
});

var counter = await system.CreateAsync<Counter>(new CreateCounter(0));
system.Send(counter.Id, new Increment(5));
var value = await system.AskAsync<int>(counter.Id, new GetValue());
```

## Design

- **Persist-then-Mutate**: Events are persisted before in-memory state changes
- **AOT-first**: Zero reflection, `new()` constraint instead of `Activator`
- **Pure domain objects**: Actors need no DI — `new T()` always suffices
- **Channel mailbox**: Sequentially processes messages without polling

See `docs/superpowers/specs/2026-07-08-actor-framework-design.md` for full design documentation.
