using PicoNode.Actor.Abs;

namespace PicoNode.Actor.Abs.Tests;

internal sealed record NoOpCommand : ICommand;

/// <summary>Actor that does nothing — for testing lifecycle.</summary>
internal sealed class NoOpActor : Actor
{
    public NoOpActor(Guid id) : base(id) { }

    protected override ValueTask<object?> OnMessageAsync(ICommand command) => default;
}

public sealed class ActorLifecycleTests
{
    /// <summary>
    /// BUG: _cts is never disposed. Actor should implement IDisposable
    /// and dispose CancellationTokenSource on Stop.
    /// </summary>
    [Test]
    public async Task Actor_implements_IDisposable()
    {
        var actor = new NoOpActor(Guid.CreateVersion7());

        await Assert.That(actor).IsAssignableTo<IDisposable>();
    }

    [Test]
    public async Task Stop_disposes_CancellationTokenSource()
    {
        var actor = new NoOpActor(Guid.CreateVersion7());
        actor.Post(new Envelope { Command = new NoOpCommand() });

        // Small delay to let the message process
        await Task.Delay(100);

        actor.Stop();

        // After Stop + Dispose, calling Stop again should not throw ObjectDisposedException
        // If _cts was disposed, a second Stop would throw — which proves disposal happened
        await Assert.That(() => actor.Stop()).Throws<ObjectDisposedException>();
    }
}
