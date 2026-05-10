namespace PicoNode.Tests;

public sealed class NodeFaultLogLevelMapperTests
{
    [Test]
    public async Task Verify_All_11_FaultCodes_Have_Mapping()
    {
        var codes = Enum.GetValues<NodeFaultCode>();
        foreach (var code in codes)
        {
            var level = NodeFaultLogLevelMapper.GetLevel(code);
            await Assert.That(level).IsNotEqualTo(LogLevel.None);
        }
    }

    [Test]
    public async Task Verify_StartFailed_Maps_To_Error()
    {
        var level = NodeFaultLogLevelMapper.GetLevel(NodeFaultCode.StartFailed);
        await Assert.That(level).IsEqualTo(LogLevel.Error);
    }

    [Test]
    public async Task Verify_SessionRejected_Maps_To_Warning()
    {
        var level = NodeFaultLogLevelMapper.GetLevel(NodeFaultCode.SessionRejected);
        await Assert.That(level).IsEqualTo(LogLevel.Warning);
    }

    [Test]
    public async Task Verify_All_Codes_Explicitly_Mapped()
    {
        var codes = Enum.GetValues<NodeFaultCode>();
        foreach (var code in codes)
        {
            var level = NodeFaultLogLevelMapper.GetLevel(code);
            await Assert.That(level == LogLevel.Error || level == LogLevel.Warning).IsTrue();
        }
    }
}
