// The README "TCP Echo Server" block: the exact text shipped in all ten READMEs, compiled here so
// it cannot rot. tests/PicoNode.Docs.Tests asserts every README matches this region.

// #region readme:echo
using System.Buffers;
using System.Net;
using PicoNode;
using PicoNode.Abs;

var node = new TcpNode(
    new TcpNodeOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
        ConnectionHandler = new EchoHandler(),
    }
);

await node.StartAsync();
Console.ReadLine();
await node.DisposeAsync();

sealed class EchoHandler : ITcpConnectionHandler
{
    public Task OnConnectedAsync(ITcpConnectionContext c, CancellationToken ct) =>
        Task.CompletedTask;

    public ValueTask OnClosedAsync(
        ITcpConnectionContext c,
        TcpCloseReason r,
        Exception? e,
        CancellationToken ct
    ) => ValueTask.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken ct
    )
    {
        _ = connection.SendAsync(buffer, ct);
        return ValueTask.FromResult(buffer.End);
    }
}
// #endregion
