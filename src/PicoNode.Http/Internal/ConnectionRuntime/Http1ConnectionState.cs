namespace PicoNode.Http.Internal.ConnectionRuntime;

/// <summary>
/// HTTP/1.1 connection-specific state. Created on demand from
/// <see cref="ConnectionRuntimeState.Http1State"/> so HTTP/2-only
/// connections never allocate it.
/// </summary>
internal sealed class Http1ConnectionState
{
    /// <summary>Whether a 100 Continue response has been sent for the current request.</summary>
    public bool ContinueSent { get; set; }

    /// <summary>UTC timestamp when the current request's headers started arriving.</summary>
    public DateTime RequestParsingStartedAtUtc { get; set; }
}
