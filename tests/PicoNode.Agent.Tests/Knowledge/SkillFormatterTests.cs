namespace PicoNode.Agent.Tests.Knowledge;

public class SkillFormatterTests
{
    [Test]
    public async Task FormatSkillsPrompt_ShouldIncludeLocation()
    {
        var skills = new List<SkillInfo>
        {
            new()
            {
                Name = "pdf-tools",
                Description = "Handle PDF",
                Path = "/path/to/SKILL.md",
            },
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
            new()
            {
                Name = "hidden",
                Description = "Hidden skill",
                DisableModelInvocation = true,
            },
        };

        var prompt = SkillFormatter.FormatSkillsPrompt(skills);
        await Assert.That(prompt).Contains("visible");
        await Assert.That(prompt).DoesNotContain("hidden");
    }

    [Test]
    public async Task FormatSkillsPrompt_WithEmptySkills_ShouldReturnMinimalInstallHint()
    {
        // Zero installed skills: return a short one-liner (~100 chars)
        // so the LLM knows how to install skills, but don't waste tokens
        // with the full installation section.
        var prompt = SkillFormatter.FormatSkillsPrompt([]);

        // Must include the essential instruction
        await Assert.That(prompt).Contains("git clone");
        await Assert.That(prompt).Contains("skills/");
        // Must NOT bloat the prompt with headings or verbose text
        await Assert.That(prompt).DoesNotContain("##");
        await Assert.That(prompt).DoesNotContain("IMPORTANT");
        await Assert.That(prompt.Length).IsLessThan(300);
    }

    [Test]
    public async Task FormatSkillsPrompt_ShouldDistinguishRemoteFromLocal()
    {
        // Issue: "Never copy individual SKILL.md files" was too absolute —
        // it prevented the LLM from helping users create NEW local skills.
        // The prompt should distinguish remote repos (git clone) from local
        // skill creation (write SKILL.md to skills/<name>/).
        var skills = new List<SkillInfo>
        {
            new() { Name = "test", Description = "Test skill" },
        };

        var prompt = SkillFormatter.FormatSkillsPrompt(skills);

        // Must NOT contain absolute "Never copy" (which would block local creation)
        await Assert.That(prompt).DoesNotContain("Never copy");
        // Must contain guidance for BOTH remote and local
        await Assert.That(prompt).Contains("git clone");
        await Assert.That(prompt).Contains("Manual skills");
    }

    [Test]
    public async Task FormatSkillsPrompt_ShouldNotHardcodeGithub()
    {
        // Issue: hardcoded "github.com/<owner>/<repo>" as the ONLY pattern
        // may mislead LLM into thinking only GitHub repos work.
        // The instruction should use <host> as placeholder, with an example.
        var skills = new List<SkillInfo>
        {
            new() { Name = "test", Description = "A test skill" },
        };

        var prompt = SkillFormatter.FormatSkillsPrompt(skills);
        // The path pattern must use <host>, not hardcode github.com
        await Assert.That(prompt).Contains("<host>/<owner>/<repo>");
        await Assert.That(prompt).DoesNotContain("skills/github.com/<owner>");
    }

    [Test]
    public async Task FormatSkillsPrompt_WithBaseDir_ShouldOutputAbsolutePaths()
    {
        var skills = new List<SkillInfo>
        {
            new() { Name = "test", Description = "A test skill" },
        };
        var baseDir = OperatingSystem.IsWindows()
            ? @"C:\Users\user\.pico-agent"
            : "/home/user/.pico-agent";

        var prompt = SkillFormatter.FormatSkillsPrompt(skills, baseDir);

        // git clone path must be absolute
        await Assert.That(prompt).Contains(baseDir);
        await Assert.That(prompt).Contains("git clone");
    }

    [Test]
    public async Task FormatSkillsPrompt_WithBaseDir_EmptySkillsIncludesBasePath()
    {
        var baseDir = OperatingSystem.IsWindows()
            ? @"C:\Users\user\.pico-agent"
            : "/home/user/.pico-agent";

        var prompt = SkillFormatter.FormatSkillsPrompt([], baseDir);

        // Even the one-liner must include the absolute base path
        await Assert.That(prompt).Contains(baseDir);
        await Assert.That(prompt).Contains("git clone");
        await Assert.That(prompt.Length).IsLessThan(300);
    }

    [Test]
    public async Task FormatSkillsPrompt_NullBaseDir_FallsBackToRelativePaths()
    {
        var skills = new List<SkillInfo>
        {
            new() { Name = "test", Description = "A test skill" },
        };

        // null baseDir: keep relative paths (backward compat for tests)
        var prompt = SkillFormatter.FormatSkillsPrompt(skills, null);
        await Assert.That(prompt).Contains($"`git clone <url> {FileSystemConstants.SkillsDir}/");
    }

    [Test]
    public async Task FormatSkillsPrompt_ShowsPackageInstallInstruction()
    {
        var skills = new List<SkillInfo>
        {
            new() { Name = "test", Description = "Test skill" },
        };

        var prompt = SkillFormatter.FormatSkillsPrompt(skills, "/home/user/.pico-agent");
        await Assert.That(prompt).Contains("packages");
        await Assert.That(prompt).Contains("settings.json");
        await Assert.That(prompt).Contains("git:");
        await Assert.That(prompt).Contains("<host>/<owner>/<repo>");
    }

    [Test]
    public async Task FormatSkillInvocation_ShouldWrapFullContent()
    {
        var skill = new SkillInfo
        {
            Name = "pdf",
            Description = "PDF tools",
            Path = "/path/SKILL.md",
        };
        var fullContent = "# PDF Tools\n\nExtract with pdf-extract";
        var result = SkillFormatter.FormatSkillInvocation(skill, fullContent);

        await Assert.That(result).Contains("pdf");
        await Assert.That(result).Contains("/path/SKILL.md");
        await Assert.That(result).Contains("Extract with pdf-extract");
    }
}
