namespace PicoNode.Agent.Domain;

public sealed class SessionActor : EventSourcedActor
{
    private string _name = "";
    private List<Participant> _participants = [];
    private readonly List<SessionTreeEntryBase> _entries = [];
    private string? _leafId;

    public SessionActor(StartSession cmd) : base(cmd) { }
    public SessionActor() { }

    protected override ValueTask<object?> OnMessageAsync(ICommand command)
    {
        return command switch
        {
            StartSession c => HandleStartSession(c),
            GetContextQuery => HandleGetContext(),
            GetSessionNameQuery => new ValueTask<object?>(_name),
            GetEntriesQuery => new ValueTask<object?>(_entries.ToArray()),
            AppendMessage c => HandleAppendMessage(c),
            MoveLeaf c => HandleMoveLeaf(c),
            RenameSession c => HandleRenameSession(c),
            DeleteSession => HandleDeleteSession(),
            _ => throw new DomainInvariantException($"Unknown command: {command.GetType().Name}")
        };
    }

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case SessionStarted e:
                _name = e.Name;
                _participants = e.Participants;
                break;
            case MessageAppended e:
                _entries.Add(e.Entry);
                _leafId = e.Entry.Id;
                break;
            case LeafMoved e:
                _leafId = e.TargetId;
                break;
            case SessionRenamed e:
                _name = e.NewName;
                break;
            case SessionDeleted:
                break;
        }
    }

    private ValueTask<object?> HandleStartSession(StartSession c)
    {
        RaiseEvent(new SessionStarted(c.Name, c.Participants));
        return default;
    }

    private ValueTask<object?> HandleGetContext()
    {
        var path = BuildPathToRoot(_leafId, _entries);
        var messages = path.OfType<MessageEntry>().Select(me => me.Message).ToList();
        return new ValueTask<object?>(new SessionContext(messages, _leafId));
    }

    private ValueTask<object?> HandleAppendMessage(AppendMessage c)
    {
        var entry = c.Entry;
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.CreateVersion7().ToString("N");
        if (string.IsNullOrEmpty(entry.ParentId))
            entry.ParentId = _leafId;
        if (string.IsNullOrEmpty(entry.Timestamp))
            entry.Timestamp = DateTime.UtcNow.ToString("O");
        RaiseEvent(new MessageAppended(entry));
        return default;
    }

    private ValueTask<object?> HandleMoveLeaf(MoveLeaf c)
    {
        if (!_entries.Any(e => e.Id == c.TargetId))
            throw new DomainInvariantException($"Target entry not found: {c.TargetId}");
        RaiseEvent(new LeafMoved(c.TargetId));
        return default;
    }

    private ValueTask<object?> HandleRenameSession(RenameSession c)
    {
        RaiseEvent(new SessionRenamed(c.NewName));
        return default;
    }

    private ValueTask<object?> HandleDeleteSession()
    {
        RaiseEvent(new SessionDeleted());
        return default;
    }

    private static SessionTreeEntryBase[] BuildPathToRoot(
        string? leafId, List<SessionTreeEntryBase> entries)
    {
        if (leafId is null) return [];
        var path = new List<SessionTreeEntryBase>();
        var current = entries.Find(e => e.Id == leafId);
        while (current is not null)
        {
            path.Add(current);
            if (current.ParentId is null) break;
            current = entries.Find(e => e.Id == current.ParentId);
        }
        path.Reverse();
        return path.ToArray();
    }
}
