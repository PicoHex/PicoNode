namespace PicoNode.Agent.Domain;

public sealed record StartSession(string Name, List<Participant> Participants) : ICommand;
public sealed record GetContextQuery : ICommand;
public sealed record GetSessionNameQuery : ICommand;
public sealed record GetEntriesQuery : ICommand;
public sealed record AppendMessage(SessionTreeEntryBase Entry) : ICommand;
public sealed record MoveLeaf(string TargetId) : ICommand;
public sealed record RenameSession(string NewName) : ICommand;
public sealed record DeleteSession : ICommand;
