namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: HomeDir resolution — PICO_AGENT_HOME → ./data/ → ~/.pico-agent/
/// Uses internal Resolve(string?) overload to avoid env var mutation in tests.
/// </summary>
public sealed class HomeDirTests
{
    [Test]
    public async Task Resolve_EnvVar_TakesPriority()
    {
        var result = HomeDir.Resolve("/custom/path");
        await Assert.That(result).IsEqualTo(Path.GetFullPath("/custom/path"));
    }

    [Test]
    public async Task Resolve_NullEnvVar_UsesFallback()
    {
        var result = HomeDir.Resolve(null);
        // Either portable data dir or user profile (depending on test environment)
        var hasDataDir = Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "data"));
        if (hasDataDir)
            await Assert.That(result).EndsWith("data");
        else
            await Assert.That(result).Contains(".pico-agent");
    }

    [Test]
    public async Task Resolve_EmptyEnvVar_UsesFallback()
    {
        var result = HomeDir.Resolve("");
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
        await Assert
            .That(home.SessionsDir)
            .IsEqualTo(Path.Combine("/", "tmp", "picoagent", "sessions"));
        await Assert.That(home.PackagesDir).IsEqualTo(Path.Combine("/", "tmp", "picoagent", "git"));
        await Assert
            .That(home.SkillsDir)
            .IsEqualTo(Path.Combine("/", "tmp", "picoagent", "skills"));
    }
}
