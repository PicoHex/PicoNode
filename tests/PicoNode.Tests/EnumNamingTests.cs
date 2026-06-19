namespace PicoNode.Tests;

/// <summary>
/// Verifies that TcpCloseReason uses consistent naming (-Failed suffix)
/// aligned with NodeFaultCode convention.
/// </summary>
public sealed class EnumNamingTests
{
    [Test]
    public async Task TcpCloseReason_HandlerFault_is_renamed_to_HandlerFailed()
    {
        // NEW name must exist after renaming
        var values = Enum.GetValues<TcpCloseReason>();
        await Assert.That(values).Contains(TcpCloseReason.HandlerFailed);
    }

    [Test]
    public async Task TcpCloseReason_ReceiveFault_is_renamed_to_ReceiveFailed()
    {
        var values = Enum.GetValues<TcpCloseReason>();
        await Assert.That(values).Contains(TcpCloseReason.ReceiveFailed);
    }

    [Test]
    public async Task TcpCloseReason_SendFault_is_renamed_to_SendFailed()
    {
        var values = Enum.GetValues<TcpCloseReason>();
        await Assert.That(values).Contains(TcpCloseReason.SendFailed);
    }
}
