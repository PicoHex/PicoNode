namespace PicoNode.Tests;

public sealed class GlobalUsingsNamespaceTests
{
    [Test]
    public async Task ILogger_Type_Is_Available_In_PicoNode()
    {
        PicoLog.Abs.ILogger? logger = null;
        await Assert.That(logger).IsNull();
    }

    [Test]
    public async Task LogLevel_Type_Is_Available()
    {
        var level = PicoLog.Abs.LogLevel.Info;
        await Assert.That(level).IsEqualTo(PicoLog.Abs.LogLevel.Info);
    }
}
