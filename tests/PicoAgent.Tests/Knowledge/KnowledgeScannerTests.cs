namespace PicoAgent.Tests.Knowledge;

using PicoAgent;

public class KnowledgeScannerTests
{
    [Test]
    public async Task Scan_DiscoversSkillFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pico-knowledge-{Guid.NewGuid()}");
        var skillDir = Path.Combine(root, "knowledge", "pdf-tools");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: pdf-tools
            description: Extract text and tables from PDF files
            ---
            # PDF Tools
            Use this tool to work with PDFs.
            """);

        try
        {
            // Debug: verify file exists
            var skillMdPath = Path.Combine(skillDir, "SKILL.md");
            await Assert.That(File.Exists(skillMdPath)).IsTrue();

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
