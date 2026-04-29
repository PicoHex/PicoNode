namespace PicoNode.Abs;

/// <summary>Handles lifecycle callbacks for a single TCP connection.</summary>
public interface ITcpConnectionHandler
{
    /// <summary>Called when a new TCP connection is established.</summary>
    Task OnConnectedAsync(ITcpConnectionContext connection, CancellationToken cancellationToken);

    /// <summary>
    /// Called when data is available to read from the connection.
    /// Returns the consumed <see cref="SequencePosition"/> — the caller advances the read buffer past this position.
    /// Return <c>buffer.End</c> to consume all data, or a earlier position to leave data for the next call.
    /// </summary>
    ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    );

    /// <summary>Called when the connection is closed, for any reason.</summary>
    ValueTask OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    );
}

