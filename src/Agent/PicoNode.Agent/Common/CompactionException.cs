namespace PicoNode.Agent;

public sealed class CompactionException : Exception
{
    public CompactionErrorCode Code { get; }

    public CompactionException(CompactionErrorCode code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;
}
