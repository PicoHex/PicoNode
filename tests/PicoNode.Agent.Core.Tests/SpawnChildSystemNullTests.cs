using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// Regression tests for SpawnChildAsync silently no-op'ing when the actor's
/// System reference is null: it returned default without raising ChildSpawned
/// and without throwing, so the caller believed the spawn succeeded while the
/// child was never created. Desired: fail loudly with an exception.
/// </summary>
public sealed class SpawnChildSystemNullTests
{
    /// <summary>
    /// When System is null (actor not managed by an ActorSystem), SpawnChild
    /// must throw instead of silently dropping the request.
    /// </summary>
    [Test]
    [Timeout(10000)]
    public async Task SpawnChild_WhenSystemIsNull_ThrowsInsteadOfSilentNoOp()
    {
        // Construct an Agent directly (bypassing ActorSystem) so System stays null.
        var agent = new DomainAgent(
            new CreateAgent(
                [
                    new()
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "sk",
                    },
                ],
                "x",
                "y",
                Guid.CreateVersion7()
            )
        );
        agent.Id = Guid.CreateVersion7();
        agent.SignalReady();

        var childLlms = new List<Llm>
        {
            new()
            {
                ProviderName = "a",
                ModelId = "b",
                ApiKey = "sk",
            },
        };

        var tcs = new TaskCompletionSource<object?>();
        agent.Post(
            new Envelope { Command = new SpawnChildCmd(childLlms, "a", "b", []), Tcs = tcs }
        );

        // Must surface the failure (throw), not complete silently with null.
        await Assert.That(async () => await tcs.Task).Throws<InvalidOperationException>();

        // No child was recorded.
        await Assert.That(agent.ChildIds.Count).IsEqualTo(0);
    }
}
