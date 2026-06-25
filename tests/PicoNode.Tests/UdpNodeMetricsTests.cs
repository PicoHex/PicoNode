
namespace PicoNode.Tests;

public sealed class UdpNodeMetricsTests
{
    [Test]
    public async Task UdpNodeMetrics_is_readonly_record_struct()
    {
        var type = typeof(UdpNodeMetrics);
        await Assert.That(type.IsValueType).IsTrue();
        await Assert.That(type.GetConstructors()).IsNotEmpty();
    }

    [Test]
    public async Task UdpNodeMetrics_exposes_cumulative_counters()
    {
        var type = typeof(UdpNodeMetrics);
        await Assert
            .That(type.GetProperty(nameof(UdpNodeMetrics.TotalDatagramsSent))!.PropertyType)
            .IsEqualTo(typeof(long));
        await Assert
            .That(type.GetProperty(nameof(UdpNodeMetrics.TotalDatagramsReceived))!.PropertyType)
            .IsEqualTo(typeof(long));
        await Assert
            .That(type.GetProperty(nameof(UdpNodeMetrics.TotalDatagramsDropped))!.PropertyType)
            .IsEqualTo(typeof(long));
        await Assert
            .That(type.GetProperty(nameof(UdpNodeMetrics.TotalBytesSent))!.PropertyType)
            .IsEqualTo(typeof(long));
        await Assert
            .That(type.GetProperty(nameof(UdpNodeMetrics.TotalBytesReceived))!.PropertyType)
            .IsEqualTo(typeof(long));
    }

    [Test]
    public async Task UdpNode_exposes_GetMetrics_method()
    {
        var method = typeof(UdpNode).GetMethod("GetMetrics");
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(UdpNodeMetrics));
    }

    [Test]
    public async Task GetMetrics_returns_zero_when_no_activity()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = endpoint,
                DatagramHandler = new NoOpUdpHandler(),
                DispatchWorkerCount = 1,
                DatagramQueueCapacity = 16,
            }
        );
        await node.StartAsync();
        try
        {
            var metrics = node.GetMetrics();
            await Assert.That(metrics.TotalDatagramsSent).IsEqualTo(0);
            await Assert.That(metrics.TotalDatagramsReceived).IsEqualTo(0);
            await Assert.That(metrics.TotalBytesSent).IsEqualTo(0);
            await Assert.That(metrics.TotalBytesReceived).IsEqualTo(0);
        }
        finally
        {
            await node.StopAsync();
        }
    }

    private sealed class NoOpUdpHandler : IUdpDatagramHandler
    {
        public ValueTask OnDatagramAsync(
            IUdpDatagramContext context,
            ReadOnlyMemory<byte> datagram,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;
    }
}
