namespace PicoNode.Agent;

public sealed class SessionException : Exception
{
    public SessionErrorCode Code { get; }

    public SessionException(SessionErrorCode code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;
}
