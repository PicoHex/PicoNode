namespace PicoNode.AI.Tests.LLm;

public sealed class MapResolverTests
{
    [Test]
    public async Task Exact_match_returns_value()
    {
        var map = new Dictionary<string, string> { ["medium"] = "16000" };
        var result = MapResolver.Resolve(ThinkingLevel.Medium, map);
        await Assert.That(result).IsEqualTo("16000");
    }

    [Test]
    public async Task Missing_downward_finds_nearest()
    {
        var map = new Dictionary<string, string> { ["low"] = "8000", ["medium"] = "16000" };
        var result = MapResolver.Resolve(ThinkingLevel.High, map);
        await Assert.That(result).IsEqualTo("16000");
    }

    [Test]
    public async Task Missing_upward_finds_nearest()
    {
        var map = new Dictionary<string, string> { ["low"] = "8000", ["high"] = "32000" };
        var result = MapResolver.Resolve(ThinkingLevel.Minimal, map);
        await Assert.That(result).IsEqualTo("8000");
    }

    [Test]
    public async Task Empty_map_returns_null()
    {
        var map = new Dictionary<string, string>();
        var result = MapResolver.Resolve(ThinkingLevel.Medium, map);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Null_map_returns_null()
    {
        var result = MapResolver.Resolve(ThinkingLevel.Medium, null);
        await Assert.That(result).IsNull();
    }
}
