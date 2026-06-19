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

    /// <summary>User-provided connection handler threw an unhandled exception.</summary>
    HandlerFailed,

    /// <summary>UDP socket receive failed.</summary>
    DatagramReceiveFailed,

    /// <summary>UDP datagram dropped due to channel overflow (<see cref="UdpOverflowMode.DropNewest"/>).</summary>
    DatagramDropped,

    /// <summary>UDP datagram handler threw an unhandled exception.</summary>
    DatagramHandlerFailed,

    /// <summary>TLS handshake or authentication failed.</summary>
    TlsFailed,
}
