namespace PicoNode.Agent.Tests;

public class ExceptionTests
{
    [Test]
    public async Task SessionException_ShouldThrowWithCode()
    {
        var ex = new SessionException(SessionErrorCode.NotFound, "entry not found");
        await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.NotFound);
        await Assert.That(ex.Message).IsEqualTo("entry not found");
    }

    [Test]
    public async Task ToolException_ShouldThrowWithCode()
    {
        var ex = new ToolException(ToolErrorCode.SchemaValidationFailed, "missing param: path");
        await Assert.That(ex.Code).IsEqualTo(ToolErrorCode.SchemaValidationFailed);
    }

    [Test]
    public async Task CompactionException_ShouldThrowWithCode()
    {
        var ex = new CompactionException(CompactionErrorCode.Aborted, "cancelled by user");
        await Assert.That(ex.Code).IsEqualTo(CompactionErrorCode.Aborted);
    }
}
