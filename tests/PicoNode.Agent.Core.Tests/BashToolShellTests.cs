namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: bash tool should use BashTool.GetShellConfig()
/// (prefer Git Bash on Windows, not cmd.exe).
/// </summary>
public sealed class BashToolShellTests
{
    [Test]
    public async Task Bash_UsesGetShellConfig_ConsistentWithOtherTools()
    {
        // Verify that BashTool uses the same shell resolution as grep/find.
        // On Windows with Git Bash: uses bash -c, not cmd.exe /c.
        var (shell, args) = BashTool.GetShellConfig();

        await Assert.That(args[0]).IsEqualTo("-c");

        if (OperatingSystem.IsWindows())
            await Assert.That(shell.ToLower()).Contains("bash");
        else
            await Assert.That(shell).IsEqualTo("/bin/bash");
    }
}
