using System.Text;
using PicoNode.AI;

namespace PicoNode.Agent.Domain;

/// <summary>
/// Pure accumulator that turns a stream of StreamEvent into one assistant Message.
/// Handles text/thinking/tool_call deltas. Does NOT handle "done"/"error" — those
/// are control-flow concerns of StreamSingleTurnAsync. Replaces the inline
/// contentAccum/thinkingAccum/argAccum state machine in RunTurnAsync.
/// </summary>
public sealed class TurnResponseAssembler
{
    private readonly string _turnLabel;
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _thinking = new();
    private readonly Dictionary<int, StringBuilder> _args = new();
    private readonly List<ContentBlock> _blocks = new();

    public bool HasToolCalls { get; private set; }

    public TurnResponseAssembler(string turnLabel) => _turnLabel = turnLabel;

    public void Handle(StreamEvent evt)
    {
        switch (evt.Type)
        {
            case "text":
                _content.Append(evt.Content);
                break;
            case "thinking":
                _thinking.Append(evt.Content);
                break;
            case "tool_call_delta":
                if (int.TryParse(evt.ToolCallId, out var di))
                {
                    if (!_args.TryGetValue(di, out var sb))
                        _args[di] = sb = new StringBuilder();
                    sb.Append(evt.Content);
                }
                break;
            case "tool_call_end":
                if (int.TryParse(evt.ToolCallId, out var ei) && evt.ToolName is { Length: > 0 })
                {
                    var args = _args.TryGetValue(ei, out var a) ? a.ToString() : "{}";
                    _blocks.Add(
                        new ContentBlock
                        {
                            Id = $"{_turnLabel}-{evt.ToolCallId}",
                            Type = "tool_call",
                            Name = evt.ToolName,
                            Arguments = ParseSimpleJson(args),
                        }
                    );
                    HasToolCalls = true;
                }
                break;
        }
    }

    public Message Build()
    {
        if (_content.Length > 0)
            _blocks.Add(new ContentBlock { Type = "text", Text = _content.ToString() });

        return new Message
        {
            Role = "assistant",
            ContentBlocks = _blocks.ToArray(),
            ReasoningContent = _thinking.Length > 0 ? _thinking.ToString() : null,
            StopReason = "stop",
        };
    }

    private static Dictionary<string, object?> ParseSimpleJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json is not { Length: > 2 })
            return [];
        try
        {
            var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
            return PicoElementConverter.ObjectToDict(doc.RootElement);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
