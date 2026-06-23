namespace PicoNode.Tests;

public sealed class NodeFaultLogLevelMapperTests
{
    [Test]
    public async Task Unknown_fault_code_returns_error_level()
    {
        var level = NodeFaultLogLevelMapper.GetLevel((NodeFaultCode)11);

        await Assert.That(level).IsEqualTo(LogLevel.Error);
    }

    [Test]
    public async Task All_named_codes_return_a_level()
    {
        var allCodes = Enum.GetValues<NodeFaultCode>();
        foreach (var code in allCodes)
        {
            var level = NodeFaultLogLevelMapper.GetLevel(code);
            await Assert.That(level).IsNotEqualTo(default(LogLevel));
        }
    }
}
