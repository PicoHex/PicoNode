using PicoNode.Actor.Abs;

namespace PicoNode.Agent.Domain;

public sealed record CreateAgent(
    List<Llm> Llms,
    string CurrentProvider,
    string CurrentModel,
    string HomeDir,
    Guid? ParentId = null,
    List<string>? Packages = null
) : ICommand;

public sealed record RunTurn(string Message) : ICommand;

public sealed record StartAgent : ICommand;

public sealed record CompleteAgent : ICommand;

public sealed record FailAgent(string Reason) : ICommand;

public sealed record SwitchLlmCmd(string ProviderName, string ModelId) : ICommand;

public sealed record AddLlmCmd(Llm Llm) : ICommand;

public sealed record RemoveLlmCmd(string ProviderName, string ModelId) : ICommand;

public sealed record AddToolCmd(Tool Tool) : ICommand;

public sealed record RemoveToolCmd(string Name) : ICommand;

public sealed record SpawnChildCmd(
    List<Llm> Llms,
    string CurrentProvider,
    string CurrentModel,
    List<Tool> Tools
) : ICommand;

public sealed record SetThinkingLevelCmd(string Level) : ICommand;
