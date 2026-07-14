namespace PicoNode.Agent.Domain;

public sealed record NoOpCmd : ICommand;
public sealed record LoadAgentCmd(Guid AgentId) : ICommand;
public sealed record RunTurnCmd(string Message, Guid SessionId, SessionContext Context) : ICommand;
public sealed record GetLoadedConfigQuery : ICommand;
