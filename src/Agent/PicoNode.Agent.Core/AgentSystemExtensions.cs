using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Domain;

public static class AgentSystemExtensions
{
    /// <summary>
    /// Get an Agent by ID with persistent session storage automatically injected.
    /// Use this instead of raw GetAsync for Agents that need session persistence.
    /// </summary>
    public static async ValueTask<T?> GetAgentAsync<T>(
        this IActorSystem system,
        Guid id,
        string? sessionsDir = null
    )
        where T : IActor
    {
        var actor = await system.GetAsync<T>(id);
        if (actor is Agent agent && agent.SessionId is { } sessionId)
        {
            agent.Session = new Session(
                sessionId,
                storage: new JsonlSessionStorage(sessionId, baseDir: sessionsDir ?? "data/sessions")
            );
        }
        return actor;
    }
}
