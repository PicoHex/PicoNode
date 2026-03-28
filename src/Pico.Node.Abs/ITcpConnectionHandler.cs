namespace Pico.Node.Abs;

public interface ITcpConnectionHandler
{
    Task OnConnectedAsync(ITcpConnectionContext connection, CancellationToken cancellationToken);
    Task OnReceivedAsync(
        ITcpConnectionContext connection,
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken
    );
    Task OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    );
}
