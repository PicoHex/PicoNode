namespace PicoNode.Agent.Tests.BuiltIn;

public class SkillsIntegrationTests
{
    [Test]
    public async Task SkillFormat_MatchesPiFormat()
    {
        var skills = new List<SkillInfo>
        {
            new()
            {
                Name = "pdf",
                Description = "PDF manipulation",
                Path = "/skills/pdf/SKILL.md",
            },
            new()
            {
                Name = "xlsx",
                Description = "Spreadsheet tools",
                Path = "/skills/xlsx/SKILL.md",
            },
        };

        var prompt = SkillFormatter.FormatSkillsPrompt(skills);

        await Assert.That(prompt).Contains("<available_skills>");
        await Assert.That(prompt).Contains("</available_skills>");
        await Assert.That(prompt).Contains("<name>pdf</name>");
        await Assert.That(prompt).Contains("<description>PDF manipulation</description>");
        await Assert.That(prompt).Contains("<location>/skills/pdf/SKILL.md</location>");
    }

    [Test]
    public async Task SkillWithDisableModelInvocation_Excluded()
    {
        var skills = new List<SkillInfo>
        {
            new() { Name = "visible", Description = "V" },
            new()
            {
                Name = "hidden",
                Description = "H",
                DisableModelInvocation = true,
            },
        };

        var prompt = SkillFormatter.FormatSkillsPrompt(skills);

        await Assert.That(prompt).Contains("visible");
        await Assert.That(prompt).DoesNotContain("hidden");
    }

    [Test]
    public async Task ScanFromDir_FindsSkills()
    {
        var tmpDir = Path.Combine(
            Path.GetTempPath(),
            "pico_skills_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmpDir);
        File.WriteAllText(
            Path.Combine(tmpDir, "SKILL.md"),
            """
            ---
            name: test-skill
            description: A test skill for TDD
            ---

            # Test Skill
            Content here.
            """
        );

        try
        {
            var scanner = new KnowledgeScanner();
            var skills = scanner.ScanFromDir(tmpDir);

            await Assert.That(skills.Count).IsEqualTo(1);
            await Assert.That(skills[0].Name).IsEqualTo("test-skill");
            await Assert.That(skills[0].Description).IsEqualTo("A test skill for TDD");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
