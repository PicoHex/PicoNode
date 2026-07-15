using PicoAgent;

namespace PicoNode.Agent.Core.Tests;

public sealed class BootstrapCwdTests
{
    [Test]
    public async Task DetectWorkingDirectory_FindsGitRoot()
    {
        // Find a .git directory by walking up from the current directory
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                break;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir || parent is null)
            {
                dir = null;
                break;
            }
            dir = parent;
        }

        if (dir is not null)
        {
            var home = new HomeDir(HomeDir.Resolve());
            var result = Bootstrap.DetectWorkingDirectory(home);
            await Assert.That(result).IsEqualTo(dir);
        }
        else
        {
            // No .git found, verify fallback to HomeDir.Root
            var home = new HomeDir(HomeDir.Resolve());
            var result = Bootstrap.DetectWorkingDirectory(home);
            await Assert.That(result).IsEqualTo(home.Root);
        }
    }
}
