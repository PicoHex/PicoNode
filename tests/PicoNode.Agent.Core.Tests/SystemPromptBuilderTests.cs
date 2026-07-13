namespace PicoNode.Agent.Core.Tests;

public sealed class SystemPromptBuilderTests
{
    [Test]
    public async Task GetSkillsDir_WhenBaseDirIsNull_ReturnsResolvedPathNotPlaceholder()
    {
        // Arrange: baseDir = null simulates no explicit base directory
        var tools = new List<Tool>();

        // Act
        var prompt = SystemPromptBuilder.Build(tools, baseDir: null);

        // Assert: The literal placeholder "{homeDir}" must NOT appear
        await Assert.That(prompt).DoesNotContain("{homeDir}");
    }

    [Test]
    public async Task GetSkillsDir_WhenBaseDirIsNull_ReturnsPathFromHomeDirResolve()
    {
        // Arrange: baseDir = null
        var tools = new List<Tool>();
        var expectedDir = Path.Combine(HomeDir.Resolve(), "git");

        // Act
        var prompt = SystemPromptBuilder.Build(tools, baseDir: null);

        // Assert: Output contains the resolved home directory path
        await Assert.That(prompt).Contains(expectedDir);
    }

    [Test]
    public async Task GetSkillsDir_WhenBaseDirProvided_ReturnsCombinedPath()
    {
        // Arrange
        var tools = new List<Tool>();
        var baseDir = "/tmp/test-base";
        var expectedDir = Path.Combine(baseDir, "git");

        // Act
        var prompt = SystemPromptBuilder.Build(tools, baseDir: baseDir);

        // Assert: Uses the provided baseDir, not HomeDir.Resolve()
        await Assert.That(prompt).Contains(expectedDir);
        await Assert.That(prompt).DoesNotContain("{homeDir}");
    }
}
