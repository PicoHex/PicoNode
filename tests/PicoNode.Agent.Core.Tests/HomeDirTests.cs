namespace PicoNode.Agent.Core.Tests;

public sealed class HomeDirTests
{
    [Test]
    public async Task Resolve_Portable_WhenDataDirExists()
    {
        var portable = Path.Combine(Directory.GetCurrentDirectory(), "data");
        var existed = Directory.Exists(portable);
        if (!existed)
            Directory.CreateDirectory(portable);
        try
        {
            var result = HomeDir.Resolve();
            await Assert.That(result).EndsWith("data");
        }
        finally
        {
            if (!existed)
                Directory.Delete(portable, true);
        }
    }

    [Test]
    public async Task Resolve_ReturnsNonNullPath()
    {
        var result = HomeDir.Resolve();
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task EnsureCreated_CreatesDirectory()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "picoagent-ensure-" + Guid.NewGuid().ToString("N")[..8]
        );
        try
        {
            var home = new HomeDir(dir);
            home.EnsureCreated();
            await Assert.That(Directory.Exists(dir)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Subdirectories_CorrectPaths()
    {
        var home = new HomeDir(Path.Combine("/", "tmp", "picoagent"));
        await Assert
            .That(home.ConfigPath)
            .IsEqualTo(Path.Combine("/", "tmp", "picoagent", "settings.json"));
        await Assert.That(home.ToolsDir).IsEqualTo(Path.Combine("/", "tmp", "picoagent", "tools"));
    }
}
