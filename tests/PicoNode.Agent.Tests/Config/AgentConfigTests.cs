namespace PicoNode.Agent.Tests.Config;

public sealed class AgentConfigTests
{
    [Test]
    public async Task ParseLevel_returns_correct_enum()
    {
        await Assert.That(AgentConfig.ParseLevel("high")).IsEqualTo(ThinkingLevel.High);
        await Assert.That(AgentConfig.ParseLevel("MEDIUM")).IsEqualTo(ThinkingLevel.Medium);
        await Assert.That(AgentConfig.ParseLevel(null)).IsNull();
        await Assert.That(AgentConfig.ParseLevel("invalid")).IsNull();
        await Assert.That(AgentConfig.ParseLevel("")).IsNull();
    }

    [Test]
    public async Task ParseLevel_all_levels_roundtrip()
    {
        foreach (var level in Enum.GetValues<ThinkingLevel>())
        {
            var name = level.ToString().ToLower();
            var parsed = AgentConfig.ParseLevel(name);
            await Assert.That(parsed).IsEqualTo(level);
        }
    }
}
