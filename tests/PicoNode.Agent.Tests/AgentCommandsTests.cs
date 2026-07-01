namespace PicoNode.Agent.Tests;

public class AgentCommandsTests
{
    [Test]
    public async Task Execute_UnknownCommand_Throws()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        Assert.Throws<ArgumentException>(() =>
            AgentCommands.Execute("unknown_command", "", session)
        );
    }

    [Test]
    public async Task Execute_Thinking_TogglesWithEmptyArg()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        var result = AgentCommands.Execute("thinking", "", session);
        await Assert.That(result).IsEqualTo("thinking updated");
    }

    [Test]
    public async Task Execute_Save_ReturnsInfo()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        var result = AgentCommands.Execute("save", "", session);
        await Assert.That(result).IsNotNull();
    }
}
