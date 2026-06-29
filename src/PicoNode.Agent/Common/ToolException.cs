namespace PicoNode.Agent;

public sealed class ToolException : Exception
{
    public ToolErrorCode Code { get; }

    public ToolException(ToolErrorCode code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;
}
