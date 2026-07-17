namespace PicoNode.Agent.Domain;

public sealed record InitRuntimeCmd(Guid AgentId, Guid SessionId) : ICommand;

public sealed record RunTurnCmd(string Message) : ICommand;

public sealed record GetLoadedConfigQuery : ICommand;
