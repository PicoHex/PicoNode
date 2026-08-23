using System.Net;
using PicoNode;

namespace PicoWeb.Tests;

public sealed class WebServerOptionsMappingTests
{
    [Test]
    public async Task ToTcpNodeOptions_maps_all_properties()
    {
        var options = new WebServerOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
            MaxConnections = 500,
            ReceiveSocketBufferSize = 8192,
            SendSocketBufferSize = 16384,
            NoDelay = false,
            IdleTimeout = TimeSpan.FromMinutes(5),
            DrainTimeout = TimeSpan.FromSeconds(10),
            AcceptFaultBackoff = TimeSpan.FromMilliseconds(250),
            EnableDualMode = false,
        };

        var handler = options.ToTcpNodeOptions(new DummyConnectionHandler());

        await Assert.That(handler.Endpoint).IsEqualTo(options.Endpoint);
        await Assert.That(handler.ConnectionHandler).IsNotNull();
        await Assert.That(handler.MaxConnections).IsEqualTo(500);
        await Assert.That(handler.ReceiveSocketBufferSize).IsEqualTo(8192);
        await Assert.That(handler.SendSocketBufferSize).IsEqualTo(16384);
        await Assert.That(handler.NoDelay).IsFalse();
        await Assert.That(handler.IdleTimeout).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(handler.DrainTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(handler.AcceptFaultBackoff).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(handler.EnableDualMode).IsFalse();
        await Assert.That(handler.SslOptions).IsNull();
        await Assert.That(handler.Logger).IsNull();
    }

    private sealed class DummyConnectionHandler : ITcpConnectionHandler
    {
        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(buffer.End);

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;
    }
}
