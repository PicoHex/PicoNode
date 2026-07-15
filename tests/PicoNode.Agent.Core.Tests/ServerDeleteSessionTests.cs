using PicoNode.Actor.Abs;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: DELETE /api/sessions/{id} must be idempotent — deleting a non-existent
/// or already-deleted session must return 200 OK, not 500.
/// </summary>
public sealed class ServerDeleteSessionTests
{
    [Test]
    public async Task DeleteSession_WhenActorNotFound_ReturnsOk()
    {
        // ── Arrange: a session system with no session actor for our GUID ──
        var sessionStore = new InMemoryEventStore();
        var sessionSystem = new ActorSystem(sessionStore);
        sessionSystem.Register<SessionActor>(cmd =>
            cmd switch
            {
                StartSession c => new SessionActor(c),
                _ => throw new InvalidOperationException(),
            }
        );

        // Create a session, then stop it (simulates already-deleted)
        var session = await sessionSystem.CreateAsync<SessionActor>(new StartSession("test", []));
        await sessionSystem.StopAsync(session.Id);

        // ── Arrange: Server with real sessionSystem, null for everything else
        var server = new PicoAgent.Server(
            null!, // Agent — not used by delete
            null!, // ActorSystem — not used by delete
            sessionSystem,
            null!, // RuntimeActor — not used by delete
            null!, // ILlmClient — not used by delete
            null! // IToolRunner — not used by delete
        );

        // ── Act: delete the already-stopped session ──
        var response = await server.DeleteSessionAsync(session.Id);

        // ── Assert: must return 200 OK, not throw ──
        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task DeleteSession_ActiveSession_ReturnsOk()
    {
        // ── Arrange ──
        var sessionStore = new InMemoryEventStore();
        var sessionSystem = new ActorSystem(sessionStore);
        sessionSystem.Register<SessionActor>(cmd =>
            cmd switch
            {
                StartSession c => new SessionActor(c),
                _ => throw new InvalidOperationException(),
            }
        );

        var session = await sessionSystem.CreateAsync<SessionActor>(
            new StartSession("active-session", [])
        );

        var server = new PicoAgent.Server(null!, null!, sessionSystem, null!, null!, null!);

        // ── Act ──
        var response = await server.DeleteSessionAsync(session.Id);

        // ── Assert ──
        await Assert.That(response.StatusCode).IsEqualTo(200);

        // Verify the actor was actually removed from the system
        await Assert
            .That(async () =>
            {
                await sessionSystem.AskAsync<object?>(session.Id, new NoOpCmd());
            })
            .Throws<KeyNotFoundException>();
    }
}
