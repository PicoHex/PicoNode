using PicoNode.Actor.Abs;

namespace PicoNode.Actor.Abs.Tests;

internal sealed record TestCommand : ICommand;

/// <summary>Actor that always throws — for testing exception propagation.</summary>
internal sealed class ThrowingActor : Actor
{
    public ThrowingActor(Guid id) : base(id) { }

    protected override ValueTask<object?> OnMessageAsync(ICommand command)
        => throw new InvalidOperationException("boom");
}

public sealed class ActorExceptionPropagationTests
{
    /// <summary>
    /// BUG: When OnMessageAsync throws, the TCS must be set to faulted.
    /// Otherwise AskAsync callers hang forever.
    /// </summary>
    [Test]
    [Timeout(5000)]
    public async Task Post_when_OnMessageAsync_throws_sets_Tcs_to_faulted()
    {
        var actor = new ThrowingActor(Guid.CreateVersion7());
        var tcs = new TaskCompletionSource<object?>();

        actor.Post(new Envelope { Command = new TestCommand(), Tcs = tcs });

        // Must NOT hang — must receive the exception
        await Assert.That(async () => await tcs.Task).Throws<InvalidOperationException>();
    }
}
