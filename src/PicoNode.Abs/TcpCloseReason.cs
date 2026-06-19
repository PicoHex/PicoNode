namespace PicoNode.Abs;

/// <summary>Reason why a TCP connection was closed.</summary>
public enum TcpCloseReason
{
    /// <summary>Local side initiated close via <c>Close()</c> or shutdown.</summary>
    LocalClose,

    /// <summary>Remote peer closed the connection (FIN or RST received).</summary>
    RemoteClosed,

    /// <summary>Connection exceeded the configured idle timeout without activity.</summary>
    IdleTimeout,

    /// <summary>User-provided connection handler threw an unhandled exception.</summary>
    HandlerFailed,

    /// <summary>Socket receive operation failed irrecoverably.</summary>
    ReceiveFailed,

    /// <summary>Socket send operation failed irrecoverably.</summary>
    SendFailed,

    /// <summary>Node is stopping; connection closed as part of graceful shutdown drain.</summary>
    NodeStopping,

    /// <summary>Connection rejected during accept (e.g. max connections reached).</summary>
    Rejected,
}
