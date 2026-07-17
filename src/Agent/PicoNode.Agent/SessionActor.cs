namespace PicoNode.Agent.Domain;

public sealed class SessionActor : EventSourcedActor
{
    private string _name = "";
    private readonly List<Message> _messages = [];
    private readonly List<CompactionEntry> _compactions = [];

    public SessionActor(StartSession cmd) : base(cmd) { }
    public SessionActor() { }

    protected override ValueTask<object?> OnMessageAsync(ICommand command)
    {
        switch (command)
        {
            case StartSession c:
                RaiseEvent(new SessionStarted(c.Name));
                return default;

            case GetContextQuery:
                return new ValueTask<object?>(BuildContext());

            case GetSessionNameQuery:
                return new ValueTask<object?>(_name);

            case AppendMessage c:
                ValidateMessage(c.Message);
                RaiseEvent(new MessageAppended(c.Message));
                return default;

            case RenameSession c:
                RaiseEvent(new SessionRenamed(c.NewName));
                return default;

            case CompactSession c:
                ValidateCompaction(c.Tag);
                RaiseEvent(new CompactionExecuted(c.Tag, c.Summary));
                return default;

            case DeleteSession:
                RaiseEvent(new SessionDeleted());
                return default;

            default:
                throw new DomainInvariantException($"Unknown command: {command.GetType().Name}");
        }
    }

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case SessionStarted e: _name = e.Name; break;
            case MessageAppended e: _messages.Add(e.Message); break;
            case SessionRenamed e: _name = e.NewName; break;
            case CompactionExecuted e:
                _compactions.Add(new CompactionEntry(e.Tag, e.Summary)); break;
            case SessionDeleted: break;
        }
    }

    private SessionContext BuildContext()
    {
        if (_compactions.Count == 0)
            return new SessionContext(new List<Message>(_messages), null);

        var ctx = new List<Message>();
        foreach (var c in _compactions)
        {
            ctx.Add(new Message
            {
                Role = "system",
                Content = c.Summary,
                ContentBlocks = [new ContentBlock { Type = "text", Text = c.Summary }],
                Timestamp = 0
            });
        }
        ctx.AddRange(_messages.Skip(_compactions.Last().Tag));
        return new SessionContext(ctx, null);
    }

    private static void ValidateMessage(Message msg)
    {
        switch (msg.Role)
        {
            case "user":
                if (msg.Sender is not null)
                    throw new DomainInvariantException("User messages must have null Sender");
                break;
            case "assistant":
            case "toolResult":
                if (msg.Sender is null)
                    throw new DomainInvariantException($"'{msg.Role}' messages must have non-null Sender");
                break;
            default:
                throw new DomainInvariantException($"Unknown Role: {msg.Role}");
        }
    }

    private void ValidateCompaction(int tag)
    {
        if (tag <= 0 || tag > _messages.Count)
            throw new DomainInvariantException($"Invalid tag: {tag}");
        if (_compactions.Count > 0 && tag <= _compactions.Last().Tag)
            throw new DomainInvariantException("Tag must be greater than previous compaction tag");
    }
}
