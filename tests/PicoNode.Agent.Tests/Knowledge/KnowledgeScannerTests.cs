namespace PicoNode.Agent.Tests.Knowledge;

using PicoNode.Agent;

public class KnowledgeScannerTests
{
    [Test]
    public async Task Scan_DiscoversSkillFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pico-knowledge-{Guid.NewGuid()}");
        var knowledgeDir = Path.Combine(root, "knowledge");
        Directory.CreateDirectory(knowledgeDir);

        // Create SKILL.md directly inside a subdirectory under knowledge/
        var skillDir = Path.Combine(knowledgeDir, "pdf-tools");
        Directory.CreateDirectory(skillDir);

        var skillPath = Path.Combine(skillDir, "SKILL.md");
        var lines = new[] { "---", "name: pdf-tools",
            "description: Extract text and tables from PDF files", "---", "", "# PDF Tools" };
        await File.WriteAllLinesAsync(skillPath, lines);

        try
        {
            var scanner = new KnowledgeScanner();
            var skills = scanner.Scan(root);

            await Assert.That(skills.Count).IsEqualTo(1);
            await Assert.That(skills[0].Name).IsEqualTo("pdf-tools");
            await Assert.That(skills[0].Description)
                .IsEqualTo("Extract text and tables from PDF files");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task ParseSkillMarkdown_ValidContent_ReturnsSkill()
    {
        var content = "---\nname: test-skill\ndescription: A test skill\n---\n# Body";
        var scanner = new KnowledgeScanner();

        var result = scanner.ParseForTest(content);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("test-skill");
        await Assert.That(result.Description).IsEqualTo("A test skill");
    }

    [Test]
    public async Task BuildSystemPrompt_IncludesSkills()
    {
        var skills = new List<SkillInfo>
        {
            new() { Name = "pdf-tools", Description = "Handle PDF files" },
            new() { Name = "web-search", Description = "Search the web" },
        };

        var prompt = KnowledgeScanner.BuildSkillsPrompt(skills);

        await Assert.That(prompt).Contains("<available_skills>");
        await Assert.That(prompt).Contains("<name>pdf-tools</name>");
        await Assert.That(prompt).Contains("<name>web-search</name>");
    }
}
