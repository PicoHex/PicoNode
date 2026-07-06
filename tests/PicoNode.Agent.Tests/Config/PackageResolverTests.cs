namespace PicoNode.Agent.Tests.Config;

public class PackageResolverTests
{
    [Test]
    public async Task Resolve_GitEntry_ReturnsGitPackage()
    {
        var homeDir = OperatingSystem.IsWindows()
            ? @"C:\Users\test\.pico-agent"
            : "/home/test/.pico-agent";
        var packages = new List<string> { "git:github.com/anthropics/skills" };

        var result = PackageResolver.Resolve(homeDir, packages);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].IsGit).IsTrue();
        await Assert.That(result[0].GitUrl).IsEqualTo("https://github.com/anthropics/skills.git");
        await Assert.That(result[0].GitRef).IsNull();
    }

    [Test]
    public async Task Resolve_GitEntryWithRef_ParsesRef()
    {
        var homeDir = "/home/test/.pico-agent";
        var packages = new List<string> { "git:github.com/anthropics/skills@v1.0" };

        var result = PackageResolver.Resolve(homeDir, packages);

        await Assert.That(result[0].GitRef).IsEqualTo("v1.0");
    }

    [Test]
    public async Task Resolve_GitEntryDeepPath_PreservesSubpath()
    {
        var homeDir = "/home/test/.pico-agent";
        var packages = new List<string> { "git:github.com/obra/superpowers" };

        var result = PackageResolver.Resolve(homeDir, packages);

        await Assert
            .That(result[0].DisplayPath)
            .Contains(Path.Combine("git", "github.com", "obra", "superpowers"));
    }

    [Test]
    public async Task Resolve_LocalEntry_ReturnsLocalPackage()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pico_pkg_" + Guid.NewGuid().ToString("N")[..8]);
        var localPath = Path.Combine(tmp, "local-src", "my-tools");
        Directory.CreateDirectory(localPath);
        try
        {
            var packages = new List<string> { "local-src/my-tools" };
            var result = PackageResolver.Resolve(tmp, packages);

            await Assert.That(result.Count).IsEqualTo(1);
            await Assert.That(result[0].IsGit).IsFalse();
            await Assert.That(result[0].GitUrl).IsNull();
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_NullPackages_ReturnsEmpty()
    {
        var result = PackageResolver.Resolve("/tmp", null);
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_EmptyPackages_ReturnsEmpty()
    {
        var result = PackageResolver.Resolve("/tmp", []);
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_MalformedGit_Skipped()
    {
        var result = PackageResolver.Resolve("/tmp", ["git:only-one"]);
        await Assert.That(result.Count).IsEqualTo(0);
    }
}
