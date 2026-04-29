namespace PicoNode.Tests;

public sealed class InterfaceContractTests
{
    [Test]
    public async Task INode_exposes_expected_members()
    {
        var properties = typeof(INode)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToArray();
        var methods = typeof(INode)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => !x.IsSpecialName)
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToArray();

        await Assert
            .That(properties)
            .IsEquivalentTo([nameof(INode.LocalEndPoint), nameof(INode.State)]);
        await Assert
            .That(methods)
            .IsEquivalentTo([nameof(INode.StartAsync), nameof(INode.StopAsync)]);
    }

    [Test]
    public async Task ITcpConnectionContext_exposes_expected_contract_shape()
    {
        var type = typeof(ITcpConnectionContext);

        await Assert
            .That(type.GetProperty(nameof(ITcpConnectionContext.ConnectionId))?.PropertyType)
            .IsEqualTo(typeof(long));
        await Assert
            .That(type.GetProperty(nameof(ITcpConnectionContext.RemoteEndPoint))?.PropertyType)
            .IsEqualTo(typeof(IPEndPoint));
        await Assert
            .That(type.GetProperty(nameof(ITcpConnectionContext.ConnectedAtUtc))?.PropertyType)
            .IsEqualTo(typeof(DateTimeOffset));
        await Assert
            .That(type.GetProperty(nameof(ITcpConnectionContext.LastActivityUtc))?.PropertyType)
            .IsEqualTo(typeof(DateTimeOffset));
        await Assert
            .That(type.GetMethod(nameof(ITcpConnectionContext.SendAsync))?.ReturnType)
            .IsEqualTo(typeof(Task));
        await Assert
            .That(type.GetMethod(nameof(ITcpConnectionContext.Close))?.ReturnType)
            .IsEqualTo(typeof(void));
    }

    [Test]
    public async Task ITcpConnectionHandler_exposes_expected_contract_shape()
    {
        var type = typeof(ITcpConnectionHandler);

        await Assert
            .That(type.GetMethod(nameof(ITcpConnectionHandler.OnConnectedAsync))?.ReturnType)
            .IsEqualTo(typeof(Task));
        await Assert
            .That(type.GetMethod(nameof(ITcpConnectionHandler.OnReceivedAsync))?.ReturnType)
            .IsEqualTo(typeof(ValueTask<SequencePosition>));
        await Assert
            .That(type.GetMethod(nameof(ITcpConnectionHandler.OnClosedAsync))?.ReturnType)
            .IsEqualTo(typeof(ValueTask));
    }

    [Test]
    public async Task IUdpDatagramContext_exposes_expected_contract_shape()
    {
        var type = typeof(IUdpDatagramContext);

        await Assert
            .That(type.GetProperty(nameof(IUdpDatagramContext.RemoteEndPoint))?.PropertyType)
            .IsEqualTo(typeof(IPEndPoint));
        await Assert
            .That(type.GetMethod(nameof(IUdpDatagramContext.SendAsync))?.ReturnType)
            .IsEqualTo(typeof(ValueTask));
    }

    [Test]
    public async Task IUdpDatagramHandler_exposes_expected_contract_shape()
    {
        var type = typeof(IUdpDatagramHandler);

        await Assert
            .That(type.GetMethod(nameof(IUdpDatagramHandler.OnDatagramAsync))?.ReturnType)
            .IsEqualTo(typeof(ValueTask));
    }
}

