namespace PicoNode.Agent.Tests.Knowledge;

public class SkillFormatterTests
{
    [Test]
    public async Task FormatSkillsPrompt_ShouldIncludeLocation()
    {
        var skills = new List<SkillInfo>
        {
            new() { Name = "pdf-tools", Description = "Handle PDF", Path = "/path/to/SKILL.md" },
        };

        var prompt = SkillFormatter.FormatSkillsPrompt(skills);
        await Assert.That(prompt).Contains("pdf-tools");
        await Assert.That(prompt).Contains("Handle PDF");
        await Assert.That(prompt).Contains("/path/to/SKILL.md");
    }

    [Test]
    public async Task FormatSkillsPrompt_ShouldSkipDisabledSkills()
    {
        var skills = new List<SkillInfo>
        {
            new() { Name = "visible", Description = "Visible skill" },
            new() { Name = "hidden", Description = "Hidden skill", DisableModelInvocation = true },
        };

        var prompt = SkillFormatter.FormatSkillsPrompt(skills);
        await Assert.That(prompt).Contains("visible");
        await Assert.That(prompt).DoesNotContain("hidden");
    }

    [Test]
    public async Task FormatSkillInvocation_ShouldWrapFullContent()
    {
        var skill = new SkillInfo { Name = "pdf", Description = "PDF tools", Path = "/path/SKILL.md" };
        var fullContent = "# PDF Tools\n\nExtract with pdf-extract";
        var result = SkillFormatter.FormatSkillInvocation(skill, fullContent);

        await Assert.That(result).Contains("pdf");
        await Assert.That(result).Contains("/path/SKILL.md");
        await Assert.That(result).Contains("Extract with pdf-extract");
    }
}
