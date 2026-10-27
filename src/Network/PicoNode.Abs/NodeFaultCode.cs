namespace PicoNode.Abs;

/// <summary>Classification of runtime faults reported via <see cref="NodeFault"/>.</summary>
public enum NodeFaultCode
{
    /// <summary>Node failed to bind or start listening.</summary>
    StartFailed,

    /// <summary>Node failed to drain in-flight work during shutdown.</summary>
    StopFailed,

    /// <summary>Socket accept threw an unrecoverable exception.</summary>
    AcceptFailed,

    /// <summary>Connection rejected by config policy (e.g. max connections limit).</summary>
    SessionRejected,

    /// <summary>Socket receive operation failed.</summary>
    ReceiveFailed,

    /// <summary>Socket send operation failed.</summary>
    SendFailed,

    /// <summary>User-provided connection handler or close-sequence callback threw an unhandled exception.</summary>
    /// <remarks>
    /// A close-sequence callback is the case this code must not hide: a completion the handler
    /// registered on the remote-close token that throws when the token is fired, or a faulting
    /// <c>OnClosedAsync</c>. Both run while a connection is being CLOSED rather than served, and
    /// both are still the user's own code — so they are classified here instead of inventing a
    /// separate code for them.
    /// </remarks>
    HandlerFailed,

    /// <summary>UDP socket receive failed.</summary>
    DatagramReceiveFailed,

    /// <summary>
    /// UDP datagram dropped instead of delivered: the dispatch queue was full
    /// (<see cref="UdpOverflowMode.DropNewest"/>) or a shutdown had already closed it.
    /// </summary>
    DatagramDropped,

    /// <summary>UDP datagram handler threw an unhandled exception.</summary>
    DatagramHandlerFailed,

    /// <summary>TLS handshake or authentication failed.</summary>
    TlsFailed,
}
