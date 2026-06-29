namespace PicoNode.Agent.Tests.Agent;

public class SystemPromptBuilderTests
{
    [Test]
    public async Task FormatToolsPrompt_ShouldListTools()
    {
        var caps = new List<ManifestCapability>
        {
            new() { Name = "read", Description = "Read files" },
            new() { Name = "write", Description = "Write files" },
        };

        var prompt = SystemPromptBuilder.FormatToolsPrompt(caps);
        await Assert.That(prompt).Contains("read");
        await Assert.That(prompt).Contains("Read files");
        await Assert.That(prompt).Contains("write");
    }

    [Test]
    public async Task BuildSystemPrompt_ShouldConcatenate()
    {
        var skills = new List<SkillInfo>();
        var caps = new List<ManifestCapability>
        {
            new() { Name = "read", Description = "Read files" },
        };

        var prompt = SystemPromptBuilder.Build(skills, caps, "AGENTS content");
        await Assert.That(prompt).Contains("AGENTS content");
        await Assert.That(prompt).Contains("read");
    }
}
