using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Domain;

public static class AgentSystemExtensions
{
    /// <summary>
    /// Get an Agent by ID. Session mounting is gone — Agent no longer holds a Session.
    /// </summary>
    public static async ValueTask<T?> GetAgentAsync<T>(this IActorSystem system, Guid id)
        where T : IActor
    {
        return await system.GetAsync<T>(id);
    }
}
