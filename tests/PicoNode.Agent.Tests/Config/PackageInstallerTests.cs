namespace PicoNode.Agent.Tests.Config;

public class PackageInstallerTests
{
    [Test]
    public async Task EnsureAsync_RepoAlreadyExists_Skips()
    {
        var tmpDir = Path.Combine(
            Path.GetTempPath(),
            "pico_inst_" + Guid.NewGuid().ToString("N")[..8]
        );
        var gitPath = Path.Combine(tmpDir, "git", "github.com", "test", "repo");
        Directory.CreateDirectory(gitPath);
        Directory.CreateDirectory(Path.Combine(gitPath, ".git"));
        try
        {
            var entries = new List<PackageEntry>
            {
                new()
                {
                    DisplayPath = gitPath,
                    IsGit = true,
                    GitUrl = "https://github.com/test/repo.git",
                },
            };
            await PackageInstaller.EnsureAsync(entries, null, CancellationToken.None);
            // No exception — directory already exists, clone skipped
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Test]
    public async Task EnsureAsync_MissingRepoWithBogusUrl_SkipsWithoutThrowing()
    {
        var tmpDir = Path.Combine(
            Path.GetTempPath(),
            "pico_inst_" + Guid.NewGuid().ToString("N")[..8]
        );
        var gitPath = Path.Combine(tmpDir, "git", "github.com", "nonexistent", "repo");
        try
        {
            var entries = new List<PackageEntry>
            {
                new()
                {
                    DisplayPath = gitPath,
                    IsGit = true,
                    GitUrl = "https://github.com/nonexistent-does-not-exist-99999/repo.git",
                },
            };
            await PackageInstaller.EnsureAsync(entries, null, CancellationToken.None);
            await Assert.That(Directory.Exists(gitPath)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Test]
    public async Task EnsureAsync_NonGitEntries_Ignored()
    {
        var entries = new List<PackageEntry>
        {
            new() { DisplayPath = "/some/local/path", IsGit = false },
        };
        await PackageInstaller.EnsureAsync(entries, null, CancellationToken.None);
    }

    [Test]
    public async Task EnsureAsync_EmptyList_NoOp()
    {
        await PackageInstaller.EnsureAsync([], null, CancellationToken.None);
    }
}
