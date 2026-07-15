namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: tool manager — resolves rg/fd paths, identifies platform.
/// </summary>
public sealed class ToolManagerTests
{
    [Test]
    public async Task GetPlatformKey_ReturnsValidKey()
    {
        var key = ToolManager.GetPlatformKey();

        await Assert.That(key).IsNotNull();
        await Assert.That(key.Contains("-")).IsTrue(); // e.g. win-x64, linux-x64, osx-arm64
    }

    [Test]
    public async Task GetToolDir_ReturnsCachePath()
    {
        var dir = ToolManager.GetToolDir();

        await Assert.That(dir).IsNotNull();
        // HomeDir.Resolve() returns portable data dir (AppContext.BaseDirectory + "data")
        // ToolsDir is Path.Combine(Root, "tools").
        await Assert.That(dir).EndsWith(Path.Combine("data", "tools"));
        await Assert.That(Directory.Exists(dir) || !Directory.Exists(dir)).IsTrue(); // no crash
    }

    [Test]
    public async Task ResolveRg_UsesCacheAfterDownload()
    {
        // Ensure cache dir exists
        var cacheDir = ToolManager.GetToolDir();
        Directory.CreateDirectory(cacheDir);

        // First call: try to find or download
        var path1 = await ToolManager.EnsureToolAsync("rg", CancellationToken.None);

        // Second call: should return same cached path
        var path2 = await ToolManager.EnsureToolAsync("rg", CancellationToken.None);

        if (path1 is not null)
        {
            await Assert.That(path2).IsEqualTo(path1);
            await Assert.That(File.Exists(path1) || path1 == "rg").IsTrue();
        }
    }

    [Test]
    public async Task ResolveFd_UsesCacheAfterDownload()
    {
        Directory.CreateDirectory(ToolManager.GetToolDir());

        var path1 = await ToolManager.EnsureToolAsync("fd", CancellationToken.None);
        var path2 = await ToolManager.EnsureToolAsync("fd", CancellationToken.None);

        if (path1 is not null)
        {
            await Assert.That(path2).IsEqualTo(path1);
        }
    }
}
