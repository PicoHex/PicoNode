namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: HomeDir resolution — PICO_AGENT_HOME → ./data/ → ~/.pico-agent/
/// Uses internal ResolveCore(string?) overload to avoid env var mutation in tests.
/// </summary>
public sealed class HomeDirTests
{
    [Test]
    public async Task ResolveCore_PathExists_TakesPriority()
    {
        var custom = Path.Combine(
            Path.GetTempPath(),
            "picoagent-home-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(custom);
        try
        {
            var result = HomeDir.ResolveCore(custom);
            await Assert.That(result).IsEqualTo(Path.GetFullPath(custom));
        }
        finally
        {
            Directory.Delete(custom, true);
        }
    }

    [Test]
    public async Task ResolveCore_PathNotExists_FallsThrough()
    {
        var result = HomeDir.ResolveCore("/nonexistent/path");
        await Assert.That(result).DoesNotContain("nonexistent");
    }

    [Test]
    public async Task ResolveCore_Null_UsesFallback()
    {
        var result = HomeDir.ResolveCore(null);
        var hasDataDir = Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "data"));
        if (hasDataDir)
            await Assert.That(result).EndsWith("data");
        else
            await Assert.That(result).Contains(".pico-agent");
    }

    [Test]
    public async Task ResolveCore_Empty_UsesFallback()
    {
        var result = HomeDir.ResolveCore("");
        var hasDataDir = Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "data"));
        if (hasDataDir)
            await Assert.That(result).EndsWith("data");
        else
            await Assert.That(result).Contains(".pico-agent");
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
