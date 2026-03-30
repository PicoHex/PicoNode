namespace Pico.Node.Abs;

public interface ITcpConnectionHandler
{
    Task OnConnectedAsync(ITcpConnectionContext connection, CancellationToken cancellationToken);
    ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    );
    Task OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    );
}
