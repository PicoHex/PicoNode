namespace PicoNode.Agent.Tests;

using DomainSession = Domain.Session;

public class ToolRunnerTests
{
    [Test]
    public async Task ExecuteAsync_KnownTool_ReturnsResult()
    {
        var runner = new ToolRunner();
        runner.Add(
            new Tool
            {
                Name = "echo",
                Description = "Echo",
                Kind = ToolKind.BuiltIn,
            },
            async (args, ct) =>
            {
                var msg = args.GetValueOrDefault("message", "") as string;
                return $"echo: {msg}";
            }
        );

        var result = await runner.ExecuteAsync(
            "echo",
            new Dictionary<string, object?> { ["message"] = "hello" },
            CancellationToken.None
        );

        await Assert.That(result).Contains("hello");
    }

    [Test]
    public async Task ExecuteAsync_UnknownTool_ReturnsError()
    {
        var runner = new ToolRunner();
        var result = await runner.ExecuteAsync(
            "nonexistent",
            new Dictionary<string, object?>(),
            CancellationToken.None
        );

        await Assert.That(result).Contains("Tool not found");
    }

    [Test]
    public async Task ExecuteAsync_HandlerError_ReturnsErrorWithToolName()
    {
        var runner = new ToolRunner();
        runner.Add(
            new Tool
            {
                Name = "crash",
                Description = "Crashes",
                Kind = ToolKind.BuiltIn,
            },
            (_, _) => throw new InvalidOperationException("boom")
        );

        var result = await runner.ExecuteAsync(
            "crash",
            new Dictionary<string, object?>(),
            CancellationToken.None
        );

        await Assert.That(result).Contains("crash");
        await Assert.That(result).Contains("boom");
    }
}
