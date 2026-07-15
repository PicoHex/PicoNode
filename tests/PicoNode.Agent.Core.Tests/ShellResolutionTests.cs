namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: shell resolution — prefer bash, fallback to cmd.exe on Windows.
/// </summary>
public sealed class ShellResolutionTests
{
    [Test]
    public async Task GetShellConfig_Windows_PrefersBash()
    {
        var (shell, args) = BashTool.GetShellConfig();

        if (OperatingSystem.IsWindows())
        {
            // Should prefer bash.exe over cmd.exe
            await Assert.That(shell.Contains("bash")).IsTrue();
            await Assert.That(args[0]).IsEqualTo("-c");
        }
        else
        {
            await Assert.That(shell).IsEqualTo("/bin/bash");
            await Assert.That(args[0]).IsEqualTo("-c");
        }
    }

    [Test]
    public async Task GetShellConfig_Fallback_ReturnsWorkingShell()
    {
        var (shell, _) = BashTool.GetShellConfig();

        // Shell must exist on disk
        await Assert
            .That(File.Exists(shell) || OperatingSystem.IsWindows() && shell == "cmd.exe")
            .IsTrue();
    }
}
