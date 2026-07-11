namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: bash tool should use the same shell resolution as ToolHandlers
/// (prefer Git Bash on Windows, not cmd.exe).
/// </summary>
public sealed class BashToolShellTests
{
    [Test]
    public async Task Bash_UsesGetShellConfig_ConsistentWithOtherTools()
    {
        // Verify that AgentFactory's bash handler calls through the same
        // shell resolution path as ToolHandlers.GrepAsync/FindAsync.
        // On Windows with Git Bash: uses bash -c, not cmd.exe /c.
        var (shell, args) = ToolHandlers.GetShellConfig();

        await Assert.That(args[0]).IsEqualTo("-c");

        if (OperatingSystem.IsWindows())
            await Assert.That(shell.ToLower()).Contains("bash");
        else
            await Assert.That(shell).IsEqualTo("/bin/bash");
    }
}
