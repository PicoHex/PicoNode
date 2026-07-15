using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class SystemPromptBuilderTests
{
    [Test]
    public async Task GetSkillsDir_WhenBaseDirIsNull_ReturnsResolvedPathNotPlaceholder()
    {
        // Arrange: baseDir = null simulates no explicit base directory.
        // Provide an empty skills list so FormatSkillsPrompt is called.
        var tools = new List<Tool>();
        var skills = new List<SkillInfo>();

        // Act
        var prompt = SystemPromptBuilder.Build(tools, skills: skills, baseDir: null);

        // Assert: The literal placeholder "{homeDir}" must NOT appear
        await Assert.That(prompt).DoesNotContain("{homeDir}");
    }

    [Test]
    public async Task GetSkillsDir_WhenBaseDirIsNull_ReturnsPathFromHomeDirResolve()
    {
        // Arrange: baseDir = null
        var tools = new List<Tool>();
        var skills = new List<SkillInfo>();

        // Act
        var prompt = SystemPromptBuilder.Build(tools, skills: skills, baseDir: null);

        // Assert: Output contains the skills directory path resolved via HomeDir
        // When baseDir is null, SkillFormatter uses FileSystemConstants.SkillsDir ("skills")
        // as a relative path.
        await Assert.That(prompt).Contains("skills/");
    }

    [Test]
    public async Task GetSkillsDir_WhenBaseDirProvided_ReturnsCombinedPath()
    {
        // Arrange
        var tools = new List<Tool>();
        var skills = new List<SkillInfo>();
        var baseDir = "/tmp/test-base";
        var expectedDir = Path.Combine(baseDir, "skills");

        // Act
        var prompt = SystemPromptBuilder.Build(tools, skills: skills, baseDir: baseDir);

        // Assert: Uses the provided baseDir, not HomeDir.Resolve()
        await Assert.That(prompt).Contains(expectedDir);
        await Assert.That(prompt).DoesNotContain("{homeDir}");
    }
}
