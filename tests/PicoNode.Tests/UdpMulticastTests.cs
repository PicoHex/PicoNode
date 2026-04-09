namespace PicoNode.Tests;

public sealed class UdpMulticastTests
{
    [Test]
    public async Task MulticastGroup_option_defaults_to_null()
    {
        var options = new UdpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Any, 0),
            DatagramHandler = new NoOpUdpHandler(),
        };

        await Assert.That(options.MulticastGroup).IsNull();
    }

    [Test]
    public async Task MulticastTtl_defaults_to_one()
    {
        var options = new UdpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Any, 0),
            DatagramHandler = new NoOpUdpHandler(),
        };

        await Assert.That(options.MulticastTtl).IsEqualTo(1);
    }

    [Test]
    public async Task MulticastLoopback_defaults_to_true()
    {
        var options = new UdpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Any, 0),
            DatagramHandler = new NoOpUdpHandler(),
        };

        await Assert.That(options.MulticastLoopback).IsTrue();
    }

    [Test]
    public async Task JoinMulticastGroup_rejects_null_address()
    {
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Any, 0),
                DatagramHandler = new NoOpUdpHandler(),
            }
        );

        await node.StartAsync();

        await Assert.That(() => node.JoinMulticastGroup(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task LeaveMulticastGroup_rejects_null_address()
    {
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Any, 0),
                DatagramHandler = new NoOpUdpHandler(),
            }
        );

        await node.StartAsync();

        await Assert.That(() => node.LeaveMulticastGroup(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task JoinMulticastGroup_succeeds_with_valid_multicast_address()
    {
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Any, 0),
                DatagramHandler = new NoOpUdpHandler(),
            }
        );

        await node.StartAsync();

        // 239.x.x.x is a valid multicast address range
        node.JoinMulticastGroup(IPAddress.Parse("239.0.0.1"));
        node.LeaveMulticastGroup(IPAddress.Parse("239.0.0.1"));
    }

    [Test]
    public async Task StartAsync_auto_joins_configured_multicast_group()
    {
        var multicastAddress = IPAddress.Parse("239.0.0.2");
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Any, 0),
                DatagramHandler = new NoOpUdpHandler(),
                MulticastGroup = multicastAddress,
            }
        );

        await node.StartAsync();

        await Assert.That(node.State).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task MulticastGroup_option_stores_address()
    {
        var address = IPAddress.Parse("239.1.2.3");
        var options = new UdpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Any, 0),
            DatagramHandler = new NoOpUdpHandler(),
            MulticastGroup = address,
        };

        await Assert.That(options.MulticastGroup).IsEqualTo(address);
    }

    private sealed class NoOpUdpHandler : IUdpDatagramHandler
    {
        public Task OnDatagramAsync(
            IUdpDatagramContext context,
            ArraySegment<byte> datagram,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
