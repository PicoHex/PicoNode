using PicoCfg.Extensions;

namespace PicoNode.Tests;

public sealed class ConfigReloadTests
{
    [Test]
    public async Task TcpNode_ConfigReload_UpdatesMaxConnections_ProtectsEndpoint()
    {
        var initialEndpoint = new IPEndPoint(IPAddress.Loopback, 0);

        await using var root = await Cfg
            .CreateBuilder()
            .Add(new Dictionary<string, string>
            {
                ["MaxConnections"] = "100",
            })
            .BuildAsync();

        var options = new TcpNodeOptions
        {
            Endpoint = initialEndpoint,
            ConnectionHandler = null!,
            Config = root,
            MaxConnections = 50,
        };

        await Assert.That(options.MaxConnections).IsEqualTo(50);
        await Assert.That(options.Endpoint).IsEqualTo(initialEndpoint);

        _ = await root.ReloadAsync();

        ApplyTcpReload(root, options);
        await Assert.That(options.MaxConnections).IsEqualTo(100);
        await Assert.That(options.Endpoint).IsEqualTo(initialEndpoint);
    }

    [Test]
    public async Task UdpNode_ConfigReload_UpdatesBufferSize_ProtectsEndpoint()
    {
        var initialEndpoint = new IPEndPoint(IPAddress.Loopback, 0);

        await using var root = await Cfg
            .CreateBuilder()
            .Add(new Dictionary<string, string>
            {
                ["ReceiveSocketBufferSize"] = "4194304",
            })
            .BuildAsync();

        var options = new UdpNodeOptions
        {
            Endpoint = initialEndpoint,
            DatagramHandler = null!,
            Config = root,
            ReceiveSocketBufferSize = 1 << 20,
        };

        await Assert.That(options.ReceiveSocketBufferSize).IsEqualTo(1 << 20);
        await Assert.That(options.Endpoint).IsEqualTo(initialEndpoint);

        _ = await root.ReloadAsync();

        ApplyUdpReload(root, options);
        await Assert.That(options.ReceiveSocketBufferSize).IsEqualTo(4194304);
        await Assert.That(options.Endpoint).IsEqualTo(initialEndpoint);
    }

    private static void ApplyTcpReload(ICfg config, TcpNodeOptions options)
    {
        if (config.TryGetValue("MaxConnections", out var v) && int.TryParse(v, out var val))
            options.MaxConnections = val;
    }

    private static void ApplyUdpReload(ICfg config, UdpNodeOptions options)
    {
        if (config.TryGetValue("ReceiveSocketBufferSize", out var v) && int.TryParse(v, out var val))
            options.ReceiveSocketBufferSize = val;
    }
}
