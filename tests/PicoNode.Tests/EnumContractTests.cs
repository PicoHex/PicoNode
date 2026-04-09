namespace PicoNode.Tests;

public sealed class EnumContractTests
{
    [Test]
    public async Task NodeFaultCode_contains_expected_members_in_order()
    {
        var names = Enum.GetNames<NodeFaultCode>();

        await Assert
            .That(names)
            .IsEquivalentTo(

                [
                    nameof(NodeFaultCode.StartFailed),
                    nameof(NodeFaultCode.StopFailed),
                    nameof(NodeFaultCode.AcceptFailed),
                    nameof(NodeFaultCode.SessionRejected),
                    nameof(NodeFaultCode.ReceiveFailed),
                    nameof(NodeFaultCode.SendFailed),
                    nameof(NodeFaultCode.HandlerFailed),
                    nameof(NodeFaultCode.DatagramReceiveFailed),
                    nameof(NodeFaultCode.DatagramDropped),
                    nameof(NodeFaultCode.DatagramHandlerFailed),
                    nameof(NodeFaultCode.TlsFailed),
                ]
            );
    }

    [Test]
    public async Task NodeState_contains_expected_members_in_order()
    {
        var values = Enum.GetValues<NodeState>();

        await Assert
            .That(values)
            .IsEquivalentTo(

                [
                    NodeState.Created,
                    NodeState.Starting,
                    NodeState.Running,
                    NodeState.Stopping,
                    NodeState.Stopped,
                    NodeState.Disposed,
                ]
            );
    }

    [Test]
    public async Task TcpCloseReason_contains_expected_members_in_order()
    {
        var values = Enum.GetValues<TcpCloseReason>();

        await Assert
            .That(values)
            .IsEquivalentTo(

                [
                    TcpCloseReason.LocalClose,
                    TcpCloseReason.RemoteClosed,
                    TcpCloseReason.IdleTimeout,
                    TcpCloseReason.HandlerFault,
                    TcpCloseReason.ReceiveFault,
                    TcpCloseReason.SendFault,
                    TcpCloseReason.NodeStopping,
                    TcpCloseReason.Rejected,
                ]
            );
    }

    [Test]
    public async Task UdpOverflowMode_contains_expected_members_in_order()
    {
        var values = Enum.GetValues<UdpOverflowMode>();

        await Assert
            .That(values)
            .IsEquivalentTo([UdpOverflowMode.DropNewest, UdpOverflowMode.Wait,]);
    }
}
