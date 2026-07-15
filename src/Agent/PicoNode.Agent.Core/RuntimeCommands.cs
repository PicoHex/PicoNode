using System.Threading.Channels;

namespace PicoNode.Agent.Domain;

public sealed record NoOpCmd : ICommand;

public sealed record LoadAgentCmd(Guid AgentId) : ICommand;

public sealed record RunTurnCmd(
    string Message,
    Guid SessionId,
    SessionContext Context,
    ChannelWriter<ActorOutputEvent>? OutputWriter = null
) : ICommand;

public sealed record GetLoadedConfigQuery : ICommand;
