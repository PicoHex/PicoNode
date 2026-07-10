namespace PicoNode.Agent.Tests.BuiltIn;

public class BashToolTests
{
    [Test]
    public async Task EchoCommandWithQuotes_ReturnsOutput()
    {
        var tool = new BashTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["command"] = "echo \"hello world\"" },
            Directory.GetCurrentDirectory(),
            CancellationToken.None
        );

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Content).Contains("hello world");
    }

    [Test]
    public async Task EchoCommand_ReturnsOutput()
    {
        var tool = new BashTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["command"] = "echo hello" },
            Directory.GetCurrentDirectory(),
            CancellationToken.None
        );

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Content).Contains("hello");
    }

    [Test]
    public async Task NonexistentCommand_ReturnsError()
    {
        var tool = new BashTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["command"] = "this_command_does_not_exist_12345" },
            Directory.GetCurrentDirectory(),
            CancellationToken.None
        );

        await Assert.That(result.IsError).IsTrue();
    }

    [Test]
    public async Task MissingCommand_ReturnsError()
    {
        var tool = new BashTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>(),
            Directory.GetCurrentDirectory(),
            CancellationToken.None
        );

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).Contains("command is required");
    }

    [Test]
    [Timeout(15000)]
    public async Task FastTimeout_ReturnsTimeoutMessage()
    {
        var tool = new BashTool();
        var cmd = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["command"] = cmd, ["timeout"] = 1L },
            Directory.GetCurrentDirectory(),
            CancellationToken.None
        );

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).Contains("timed out");
    }

    [Test]
    [Timeout(15000)]
    public async Task Timeout_PreservesBufferedOutput()
    {
        var tool = new BashTool();
        // Command that prints immediately then hangs
        var cmd = OperatingSystem.IsWindows()
            ? "echo BUFFERED_OUTPUT && ping -n 30 127.0.0.1 > nul"
            : "echo BUFFERED_OUTPUT && sleep 30";

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["command"] = cmd, ["timeout"] = 1L },
            Directory.GetCurrentDirectory(),
            CancellationToken.None
        );

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).Contains("BUFFERED_OUTPUT");
        await Assert.That(result.Content).Contains("timed out");
    }
}
