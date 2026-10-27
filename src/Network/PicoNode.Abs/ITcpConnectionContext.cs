namespace PicoNode.Abs;

public interface ITcpConnectionContext
{
    /// <summary>Unique connection identifier within this transport node.</summary>
    long ConnectionId { get; }

    /// <summary>Remote endpoint of the connected peer.</summary>
    EndPoint RemoteEndPoint { get; }

    /// <summary>UTC timestamp when the connection was accepted.</summary>
    DateTimeOffset ConnectedAtUtc { get; }

    /// <summary>UTC timestamp of the last read or write activity on this connection.</summary>
    DateTimeOffset LastActivityUtc { get; }

    /// <summary>Arbitrary user state object. Use to attach protocol-level state (e.g. HTTP/1.1 parser state).</summary>
    object? UserState { get; set; }

    /// <summary>Sends data to the connected peer. The buffer is consumed asynchronously.</summary>
    Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Initiates a graceful close of the connection. Fire-and-forget; the actual close is asynchronous.</summary>
    void Close();

    /// <summary>ALPN-negotiated protocol, e.g. "h2", "http/1.1". Null when not negotiated.</summary>
    string? NegotiatedProtocol { get; }

    /// <summary>
    /// Cancelled when the connection closes, whichever side closed it: the peer's graceful FIN
    /// or aborted RST, or a local close (an explicit <see cref="Close()"/> from a handler, an
    /// idle timeout, a receive or send fault, or the node stopping). It is the deterministic
    /// disconnect signal for responses that outlive their request (e.g. SSE), and it fires
    /// before the read pipe is completed and the socket is torn down.
    /// </summary>
    /// <remarks>
    /// Unlike the request cancellation token — which stays alive while a half-closed client's
    /// buffered responses drain — this token fires on every close path, so a long-lived response
    /// cannot outlive its connection. It is safe to read (and to register on) at any time,
    /// including after the connection has been disposed.
    /// </remarks>
    CancellationToken RemoteCloseToken { get; }
}

/// <summary>Type-safe accessors for <see cref="ITcpConnectionContext.UserState"/>.</summary>
public static class TcpConnectionContextExtensions
{
    /// <summary>Gets the user state cast to <typeparamref name="T"/>. Returns null if state is not of that type.</summary>
    public static T? GetUserState<T>(this ITcpConnectionContext ctx)
        where T : class => ctx.UserState as T;

    /// <summary>Sets the user state. Prefer this over directly assigning <c>UserState</c> for consistency.</summary>
    public static void SetUserState<T>(this ITcpConnectionContext ctx, T? state)
        where T : class => ctx.UserState = state;
}
