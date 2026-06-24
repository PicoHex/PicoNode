namespace PicoNode.AI;

public abstract class AssistantMessageEvent
{
    public sealed class Start : AssistantMessageEvent
    {
        public Message Partial { get; set; } = new();
    }

    public sealed class TextDelta : AssistantMessageEvent
    {
        public int Index { get; set; }
        public string Delta { get; set; } = "";
        public Message Partial { get; set; } = new();
    }

    public sealed class ThinkingDelta : AssistantMessageEvent
    {
        public int Index { get; set; }
        public string Delta { get; set; } = "";
        public Message Partial { get; set; } = new();
    }

    public sealed class ToolCallStart : AssistantMessageEvent
    {
        public int Index { get; set; }
        public Message Partial { get; set; } = new();
    }

    public sealed class ToolCallDelta : AssistantMessageEvent
    {
        public int Index { get; set; }
        public string Delta { get; set; } = "";
        public Message Partial { get; set; } = new();
    }

    public sealed class ToolCallEnd : AssistantMessageEvent
    {
        public int Index { get; set; }
        public ContentBlock Call { get; set; } = new();
        public Message Partial { get; set; } = new();
    }

    public sealed class Done : AssistantMessageEvent
    {
        public Message Message { get; set; } = new();
    }

    public sealed class Error : AssistantMessageEvent
    {
        public Message Message { get; set; } = new();
    }
}
