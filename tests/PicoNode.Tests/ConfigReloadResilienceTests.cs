using System.Threading.Channels;

namespace PicoNode.Tests;

/// <summary>
/// A transient failure while reloading configuration must not kill the
/// hot-reload loop for the lifetime of the node: the next published change
/// still has to be applied.
/// </summary>
public sealed class ConfigReloadResilienceTests
{
    [Test]
    public async Task TcpNode_reload_loop_continues_after_transient_failure()
    {
        await using var config = new ScriptedCfgRoot();
        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = new NoopHandler(),
                Config = config,
                MaxConnections = 50,
            }
        );
        await node.StartAsync();

        config.Publish("MaxConnections", "123");
        await Task.Delay(200); // let the loop observe the failing reload

        config.Publish("MaxConnections", "123");
        await WaitUntilAsync(() => node.Options.MaxConnections == 123, TimeSpan.FromSeconds(3));

        await Assert.That(node.Options.MaxConnections).IsEqualTo(123);
    }

    [Test]
    public async Task UdpNode_reload_loop_continues_after_transient_failure()
    {
        await using var config = new ScriptedCfgRoot();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = new NoopDatagramHandler(),
                Config = config,
                ReceiveSocketBufferSize = 1 << 20,
            }
        );
        await node.StartAsync();

        config.Publish("ReceiveSocketBufferSize", "4194304");
        await Task.Delay(200);

        config.Publish("ReceiveSocketBufferSize", "4194304");
        await WaitUntilAsync(
            () => node.Options.ReceiveSocketBufferSize == 4194304,
            TimeSpan.FromSeconds(3)
        );

        await Assert.That(node.Options.ReceiveSocketBufferSize).IsEqualTo(4194304);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// ICfgRoot whose first ReloadAsync throws (transient failure) and whose
    /// later reloads succeed; changes are published through a channel.
    /// </summary>
    private sealed class ScriptedCfgRoot : ICfgRoot
    {
        private readonly Channel<string> _changes = Channel.CreateUnbounded<string>();
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private int _reloadCalls;

        public void Publish(string key, string value)
        {
            _values[key] = value;
            _changes.Writer.TryWrite(key);
        }

        public bool TryGetValue(string key, out string? value) =>
            _values.TryGetValue(key, out value);

        public async ValueTask WaitForChangeAsync(CancellationToken cancellationToken) =>
            await _changes.Reader.ReadAsync(cancellationToken);

        public ValueTask<bool> ReloadAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _reloadCalls);
            if (call == 1)
            {
                return ValueTask.FromException<bool>(
                    new InvalidOperationException("transient reload failure")
                );
            }

            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopHandler : ITcpConnectionHandler
    {
        public Task OnConnectedAsync(ITcpConnectionContext connection, CancellationToken ct) =>
            Task.CompletedTask;

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken ct
        ) => ValueTask.CompletedTask;

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken ct
        ) => ValueTask.FromResult(buffer.End);
    }

    private sealed class NoopDatagramHandler : IUdpDatagramHandler
    {
        public ValueTask OnDatagramAsync(
            IUdpDatagramContext context,
            ReadOnlyMemory<byte> datagram,
            CancellationToken ct
        ) => ValueTask.CompletedTask;
    }
}
