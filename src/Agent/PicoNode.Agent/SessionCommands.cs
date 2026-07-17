namespace PicoNode.Agent.Domain;

public sealed record StartSession(string Name) : ICommand;

public sealed record GetContextQuery : ICommand;

public sealed record GetSessionNameQuery : ICommand;

public sealed record AppendMessage(Message Message) : ICommand;

public sealed record RenameSession(string NewName) : ICommand;

public sealed record CompactSession(int Tag, string Summary) : ICommand;

public sealed record DeleteSession : ICommand;
