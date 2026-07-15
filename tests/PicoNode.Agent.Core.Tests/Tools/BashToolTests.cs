namespace PicoNode.Agent.Core.Tests.Tools;

public sealed class BashToolTests
{
    [Test]
    public async Task Create_EchoHello()
    {
        var handler = BashTool.Create(Directory.GetCurrentDirectory());
        var result = await handler(
            new Dictionary<string, object?> { ["command"] = "echo hello" },
            CancellationToken.None
        );
        await Assert.That(result).Contains("hello");
    }

    [Test]
    public async Task Create_TimeoutKills()
    {
        var handler = BashTool.Create(Directory.GetCurrentDirectory());
        var cmd = OperatingSystem.IsWindows() ? "ping -n 10 127.0.0.1" : "sleep 10";
        var result = await handler(
            new Dictionary<string, object?> { ["command"] = cmd, ["timeout"] = 1 },
            CancellationToken.None
        );
        await Assert.That(result).Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task Create_TruncatesOutput()
    {
        var handler = BashTool.Create(Directory.GetCurrentDirectory());
        // Use shell-detected command syntax
        var (shell, _) = BashTool.GetShellConfig();
        var isBash = shell.Contains("bash");
        // Generate enough output to exceed 2000 lines
        var cmd = isBash
            ? "for i in $(seq 1 5000); do echo line$i; done"
            : "for /l %i in (1,1,5000) do @echo line%i";
        var result = await handler(
            new Dictionary<string, object?> { ["command"] = cmd },
            CancellationToken.None
        );
        await Assert.That(result).Contains("truncated", StringComparison.OrdinalIgnoreCase);
    }
}
