namespace PicoNode.Agent.Domain;

// LLM PM commands
public sealed record DeleteLlmCmd(Guid LlmId) : ICommand;

public sealed record SetSystemLlmCmd(Guid LlmId) : ICommand;

// Agent PM commands
public sealed record CreateAgentCmd(string Name, Guid LlmId) : ICommand;

public sealed record DeleteAgentCmd(Guid AgentId) : ICommand;

// Session PM commands
public sealed record CreateSessionCmd(string Name, Guid AgentId) : ICommand;

public sealed record DeleteSessionCmd(Guid SessionId) : ICommand;
