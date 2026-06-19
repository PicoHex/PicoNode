using PicoLog.Abs;

namespace PicoNode.Tests;

[NotInParallel]
public sealed class NodeFaultLogLevelMapperTests
{
    /// <summary>Reset all overrides between tests to prevent parallel interference.</summary>
    [Before(Test)]
    public void ResetAll() => NodeFaultLogLevelMapper.ResetAll();

    [Test]
    public async Task GetLevel_returns_default_for_code_without_override()
    {
        var level = NodeFaultLogLevelMapper.GetLevel(NodeFaultCode.StartFailed);
        await Assert.That(level).IsEqualTo(LogLevel.Error);
    }

    [Test]
    public async Task Override_changes_level_for_specific_code()
    {
        NodeFaultLogLevelMapper.Override(NodeFaultCode.TlsFailed, LogLevel.Warning);
        var level = NodeFaultLogLevelMapper.GetLevel(NodeFaultCode.TlsFailed);
        NodeFaultLogLevelMapper.Reset(NodeFaultCode.TlsFailed);
        await Assert.That(level).IsEqualTo(LogLevel.Warning);
    }

    [Test]
    public async Task Override_after_Reset_reverts_to_default()
    {
        NodeFaultLogLevelMapper.Override(NodeFaultCode.TlsFailed, LogLevel.Warning);
        NodeFaultLogLevelMapper.Reset(NodeFaultCode.TlsFailed);
        var level = NodeFaultLogLevelMapper.GetLevel(NodeFaultCode.TlsFailed);
        await Assert.That(level).IsEqualTo(LogLevel.Debug);
    }

    [Test]
    public async Task Override_does_not_affect_other_codes()
    {
        NodeFaultLogLevelMapper.Override(NodeFaultCode.TlsFailed, LogLevel.Warning);
        var startLevel = NodeFaultLogLevelMapper.GetLevel(NodeFaultCode.StartFailed);
        NodeFaultLogLevelMapper.Reset(NodeFaultCode.TlsFailed);
        await Assert.That(startLevel).IsEqualTo(LogLevel.Error);
    }

    [Test]
    public async Task Multiple_overrides_work_independently()
    {
        NodeFaultLogLevelMapper.Override(NodeFaultCode.ReceiveFailed, LogLevel.Critical);
        NodeFaultLogLevelMapper.Override(NodeFaultCode.SendFailed, LogLevel.None);
        var recvLevel = NodeFaultLogLevelMapper.GetLevel(NodeFaultCode.ReceiveFailed);
        var sendLevel = NodeFaultLogLevelMapper.GetLevel(NodeFaultCode.SendFailed);
        NodeFaultLogLevelMapper.Reset(NodeFaultCode.ReceiveFailed);
        NodeFaultLogLevelMapper.Reset(NodeFaultCode.SendFailed);
        await Assert.That(recvLevel).IsEqualTo(LogLevel.Critical);
        await Assert.That(sendLevel).IsEqualTo(LogLevel.None);
    }

    [Test]
    public async Task Override_non_default_code_preserves_original_mapping()
    {
        NodeFaultLogLevelMapper.Override(NodeFaultCode.StartFailed, LogLevel.Debug);
        NodeFaultLogLevelMapper.Reset(NodeFaultCode.StartFailed);
        var level = NodeFaultLogLevelMapper.GetLevel(NodeFaultCode.StartFailed);
        await Assert.That(level).IsEqualTo(LogLevel.Error);
    }
}
